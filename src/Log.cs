// The troubleshooting log (0.4.0 step 6, 2026-11-09): what sfmodcheck read, where it found each master, which caches
// it used or rebuilt, how long each stage took, and the full error when it fails. Off unless asked for:
//   --verbose      log lines on stderr, each starting "log: "
//   --log <file>   the same lines, with times, written to that file
// It never writes to stdout, so the ERROR / WARNING / HINT / RESULT lines are the same with or without it.
using System.Diagnostics;
using System.Text;

namespace SfModCheck;

public static class Log
{
    static bool toStderr;
    static StreamWriter? file;
    static readonly Stopwatch clock = Stopwatch.StartNew();

    public static bool On => toStderr || file != null;

    public static void ToStderr() => toStderr = true;

    /// <summary>Opens the log file (overwrites it). Returns an error text naming the file and the fix, or null.</summary>
    public static string? ToFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (dir != null && !Directory.Exists(dir))
                return $"can't write the log file {full}: the folder {dir} doesn't exist. Make the folder, or give --log a path in a folder that exists.";
            file = new StreamWriter(full, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"can't write the log file {path}: {e.Message} Give --log a path you can write to, such as a file in your Documents folder.";
        }
    }

    public static void Write(string text)
    {
        if (!On) return;
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (toStderr) Console.Error.WriteLine($"log: {line}");
            file?.WriteLine($"{clock.Elapsed.TotalSeconds,8:F2}s  {line}");
        }
    }

    /// <summary>A file's size and modified time (UTC), for the log.</summary>
    public static string Describe(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? $"{fi.FullName} ({fi.Length:N0} bytes, modified {fi.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC)" : $"{path} (not found)";
        }
        catch (Exception e) { return $"{path} ({e.GetType().Name}: {e.Message})"; }
    }

    public static void Close()
    {
        file?.Dispose();
        file = null;
    }
}
