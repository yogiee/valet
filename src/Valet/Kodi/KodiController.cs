using System.Diagnostics;
using System.IO;
using Valet.Logging;
using Valet.Native;

namespace Valet.Kodi;

internal sealed class KodiController
{
    private const string KodiProcessName = "kodi";
    private const string KodiWindowClass = "Kodi";
    private const string XbmcWindowClass = "XBMC";

    private readonly Func<string> _kodiPathResolver;

    public KodiController(Func<string> kodiPathResolver)
    {
        _kodiPathResolver = kodiPathResolver;
    }

    public bool IsRunning()
    {
        var procs = Process.GetProcessesByName(KodiProcessName);
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    public bool Launch()
    {
        var path = ResolvePath();
        if (string.IsNullOrEmpty(path))
        {
            Log.Warn("Kodi launch skipped — no kodi.exe found (set kodiPath in config)");
            return false;
        }
        if (!File.Exists(path))
        {
            Log.Warn($"Kodi launch skipped — file not found at {path}");
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
            };
            using var p = Process.Start(psi);
            Log.Info($"Kodi launched: {path} (pid={p?.Id.ToString() ?? "?"})");
            return p is not null;
        }
        catch (Exception ex)
        {
            Log.Error($"Kodi launch failed: {path}", ex);
            return false;
        }
    }

    public void CloseGracefully(TimeSpan timeout)
    {
        if (!IsRunning()) return;

        var hwnd = NativeMethods.FindWindow(KodiWindowClass, null);
        if (hwnd == IntPtr.Zero)
        {
            hwnd = NativeMethods.FindWindow(XbmcWindowClass, null);
        }

        if (hwnd != IntPtr.Zero)
        {
            Log.Info("Posting WM_CLOSE to Kodi window");
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        else
        {
            Log.Warn("Kodi process running but no window found — going straight to kill");
            ForceKill();
            return;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning())
            {
                Log.Info("Kodi closed gracefully");
                return;
            }
            Thread.Sleep(100);
        }

        Log.Warn($"Kodi did not exit within {timeout.TotalSeconds:F1}s — force killing");
        ForceKill();
    }

    private static void ForceKill()
    {
        foreach (var p in Process.GetProcessesByName(KodiProcessName))
        {
            try { p.Kill(); } catch (Exception ex) { Log.Error($"Kill kodi pid={p.Id}", ex); }
            finally { p.Dispose(); }
        }
    }

    private string ResolvePath()
    {
        var configured = _kodiPathResolver();
        if (!string.IsNullOrWhiteSpace(configured) &&
            !string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Kodi", "kodi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Kodi", "kodi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Kodi", "kodi.exe"),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return string.Empty;
    }
}
