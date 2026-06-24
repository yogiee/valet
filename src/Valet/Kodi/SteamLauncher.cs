using System.Diagnostics;
using System.IO;
using Valet.Logging;

namespace Valet.Kodi;

internal static class SteamLauncher
{
    private const string Args = "-bigpicture";

    public static bool LaunchBigPicture(string configuredPath)
    {
        var path = Resolve(configuredPath);
        if (string.IsNullOrEmpty(path))
        {
            Log.Warn("Steam launch skipped — no steam.exe found (set steamPath in config)");
            return false;
        }
        if (!File.Exists(path))
        {
            Log.Warn($"Steam launch skipped — file not found at {path}");
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = Args,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
            };
            using var p = Process.Start(psi);
            Log.Info($"Steam launched (Big Picture): {path} (pid={p?.Id.ToString() ?? "?"})");
            return p is not null;
        }
        catch (Exception ex)
        {
            Log.Error($"Steam launch failed: {path}", ex);
            return false;
        }
    }

    private static string Resolve(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) &&
            !string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steam.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Steam", "steam.exe"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return string.Empty;
    }
}
