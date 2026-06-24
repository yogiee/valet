using System.Diagnostics;
using System.IO;

namespace Valet.Logging;

internal static class Log
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Valet", "logs");

    private static readonly string LogFile = Path.Combine(LogDir, "valet.log");
    private static readonly Lock Sync = new();

    static Log()
    {
        Directory.CreateDirectory(LogDir);
    }

    public static void Info(string msg) => Write("INFO", msg);

    public static void Warn(string msg) => Write("WARN", msg);

    public static void Error(string msg, Exception? ex = null) =>
        Write("ERROR", ex is null ? msg : $"{msg}: {ex}");

    public static string Folder => LogDir;

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        lock (Sync)
        {
            try { File.AppendAllText(LogFile, line + Environment.NewLine); }
            catch { /* logging must not throw */ }
        }
        Debug.WriteLine(line);
    }
}
