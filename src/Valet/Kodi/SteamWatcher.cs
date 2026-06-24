using System.Diagnostics;
using System.Text;
using Valet.Logging;
using Valet.Native;

namespace Valet.Kodi;

internal sealed class SteamWatcher : IDisposable
{
    private const string SteamProcessName = "steam";
    private const string BigPictureTitleHint = "Big Picture";

    private readonly System.Threading.Timer _timer;
    private volatile bool _isActive;
    private volatile bool _disposed;

    public bool IsBigPictureActive => _isActive;

    public SteamWatcher()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
    }

    private void Poll()
    {
        if (_disposed) return;
        try
        {
            var nowActive = FindBigPictureWindow();
            if (nowActive != _isActive)
            {
                Log.Info($"SteamWatcher: BPM {(nowActive ? "appeared" : "gone")}");
                _isActive = nowActive;
            }
        }
        catch (Exception ex)
        {
            Log.Error("SteamWatcher poll", ex);
        }
    }

    private static bool FindBigPictureWindow()
    {
        var steamPids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(SteamProcessName))
        {
            steamPids.Add((uint)p.Id);
            p.Dispose();
        }
        if (steamPids.Count == 0) return false;

        var found = false;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!steamPids.Contains(pid)) return true;

            var len = NativeMethods.GetWindowTextLength(hwnd);
            if (len <= 0) return true;

            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.Contains(BigPictureTitleHint, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
