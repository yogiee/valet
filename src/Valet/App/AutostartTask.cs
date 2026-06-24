using System.Diagnostics;
using Valet.Logging;

namespace Valet.App;

internal static class AutostartTask
{
    public const string TaskName = "Valet";

    public static bool IsInstalled()
    {
        var (exit, _, _) = Run("schtasks", $"/Query /TN \"{TaskName}\"");
        return exit == 0;
    }

    public static void Install()
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine current executable path");

        var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /F";
        var (exit, _, stderr) = Run("schtasks", args);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"schtasks create failed (exit {exit}): {stderr.Trim()}. " +
                "Highest-privilege tasks require admin to register.");
        }
        Log.Info($"AutostartTask installed: {exe}");
    }

    public static void Uninstall()
    {
        var (exit, _, stderr) = Run("schtasks", $"/Delete /TN \"{TaskName}\" /F");
        if (exit != 0 && !stderr.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
                      && !stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"schtasks delete failed (exit {exit}): {stderr.Trim()}");
        }
        Log.Info("AutostartTask uninstalled");
    }

    private static (int exitCode, string stdout, string stderr) Run(string filename, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = filename,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {filename}");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }
}
