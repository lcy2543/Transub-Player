using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TransubPlayer.Services;

/// <summary>
/// whisper.cpp's Windows model loader crashes (SEH → "External component has thrown an
/// exception") when the model path contains non-ASCII characters — e.g. CJK usernames
/// (<c>C:\Users\喵喵\AppData\…</c>) or Chinese folders (<c>I:\Python项目\…</c>).
/// <see cref="WhisperFactory.FromPath"/> is the only call that hands a filesystem path to
/// native Whisper, so resolve an ASCII-safe alias before loading:
/// 8.3 short path → junction under ProgramData (no admin rights needed) → cached copy.
/// </summary>
internal static class NativePathSafety
{
    private const int MaxPathChars = 2048;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> JunctionByParent = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsPureAscii(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return true;
        foreach (var c in text)
        {
            if (c > 127)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns an ASCII-safe path that points at the same file as <paramref name="path"/>.
    /// ASCII paths and missing files pass through unchanged; non-ASCII paths get the first
    /// alias that works. The original path is returned when every alias fails (callers log
    /// the native failure anyway).
    /// </summary>
    public static string SafeExistingFile(string path, Action<string>? log = null)
    {
        if (IsPureAscii(path) || !File.Exists(path))
            return path;

        var shortPath = TryShortPath(path);
        if (shortPath is not null && IsPureAscii(shortPath))
        {
            log?.Invoke("ASR 模型路径含非 ASCII 字符 · 用 8.3 短路径加载: " + shortPath);
            return shortPath;
        }

        var junction = EnsureJunctionAlias(path, log);
        if (junction is not null)
        {
            log?.Invoke("ASR 模型路径含非 ASCII 字符 · 用 junction 别名加载: " + junction);
            return junction;
        }

        var copy = EnsureCachedCopy(path, log);
        if (copy is not null)
        {
            log?.Invoke("ASR 模型路径含非 ASCII 字符 · 用 ASCII 缓存副本加载: " + copy);
            return copy;
        }

        log?.Invoke("ASR 模型路径含非 ASCII 字符，且无法生成兼容别名 · 请在设置中把模型目录改为纯英文路径（如 D:\\transub-models）");
        return path;
    }

    private static string? TryShortPath(string path)
    {
        try
        {
            var sb = new StringBuilder(MaxPathChars);
            var len = GetShortPathName(path, sb, (uint)sb.Capacity);
            if (len <= 0 || len >= sb.Capacity)
                return null;
            return sb.ToString(0, (int)len);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One junction per models parent dir, under <c>%ProgramData%\TransubPlayer\native-ascii\</c>
    /// (always an ASCII root; users may create folders there without elevation). Junctions need
    /// no admin rights and resolve live, so model updates need no invalidation.
    /// </summary>
    private static string? EnsureJunctionAlias(string path, Action<string>? log)
    {
        lock (Gate)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var parent = Path.GetDirectoryName(full);
                var fileName = Path.GetFileName(full);
                if (string.IsNullOrEmpty(parent) || !IsPureAscii(fileName))
                    return null;

                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "TransubPlayer",
                    "native-ascii");
                Directory.CreateDirectory(root);

                var key = parent.TrimEnd(Path.DirectorySeparatorChar);
                if (!JunctionByParent.TryGetValue(key, out var junction))
                {
                    junction = Path.Combine(root, "asr-" + Hash8(key));
                    JunctionByParent[key] = junction;
                }

                if (!IsJunctionFor(junction, key))
                {
                    if (Directory.Exists(junction))
                        Directory.Delete(junction);
                    if (!CreateJunction(junction, key, log))
                        return null;
                }

                var alias = Path.Combine(junction, fileName);
                return File.Exists(alias) ? alias : null;
            }
            catch (Exception ex)
            {
                log?.Invoke("ASR junction 别名创建失败 · " + ex.Message);
                return null;
            }
        }
    }

    private static bool IsJunctionFor(string junction, string target)
    {
        try
        {
            if (!Directory.Exists(junction))
                return false;
            var link = new DirectoryInfo(junction).LinkTarget;
            if (string.IsNullOrEmpty(link))
                return false;
            return string.Equals(link.TrimEnd(Path.DirectorySeparatorChar), target, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool CreateJunction(string junction, string target, Action<string>? log)
    {
        try
        {
            // mklink /J needs no elevation; spawn hidden and check the result.
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/d /c mklink /J \"{junction}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("cmd.exe 启动失败");
            proc.WaitForExit(10_000);

            if (proc.ExitCode == 0 && Directory.Exists(junction))
                return true;
            var err = proc.StandardError.ReadToEnd().Trim();
            if (err.Length > 0)
                log?.Invoke("mklink /J 失败 · " + err);
            return false;
        }
        catch (Exception ex)
        {
            log?.Invoke("mklink /J 异常 · " + ex.Message);
            return false;
        }
    }

    /// <summary>Last-resort fallback: a plain ASCII copy, reused while size + mtime match.</summary>
    private static string? EnsureCachedCopy(string path, Action<string>? log)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "TransubPlayer",
                "native-ascii");
            Directory.CreateDirectory(root);
            var alias = Path.Combine(root, "asr-copy-" + Hash8(Path.GetFullPath(path)) + Path.GetExtension(path));
            var src = new FileInfo(path);
            if (File.Exists(alias))
            {
                var dst = new FileInfo(alias);
                if (dst.Length == src.Length
                    && dst.LastWriteTimeUtc == src.LastWriteTimeUtc)
                    return alias;
                dst.Delete();
            }

            log?.Invoke($"ASR ASCII 缓存副本复制中 · {src.Length / 1024.0 / 1024.0:0} MB…");
            File.Copy(path, alias);
            new FileInfo(alias).LastWriteTimeUtc = src.LastWriteTimeUtc;
            return File.Exists(alias) ? alias : null;
        }
        catch (Exception ex)
        {
            log?.Invoke("ASR ASCII 缓存副本失败 · " + ex.Message);
            return null;
        }
    }

    private static string Hash8(string text)
    {
        unchecked
        {
            var h = 14695981039346656037UL;
            foreach (var c in text.ToUpperInvariant())
            {
                h ^= c;
                h *= 1099511628211UL;
            }

            return h.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);
}
