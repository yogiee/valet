using System.Windows;
using System.Windows.Threading;
using Valet.App;
using Valet.Logging;

namespace Valet.Osd;

internal sealed class OsdController : IDisposable
{
    private readonly Config _config;
    private readonly Dispatcher _dispatcher;
    private VolumeOverlayWindow? _window;
    private DispatcherTimer? _hideTimer;
    private volatile bool _disposed;

    public OsdController(Config config)
    {
        _config = config;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>
    /// Show or update the volume OSD. Safe to call from any thread — marshals to the WPF dispatcher.
    /// </summary>
    public void Show(int level, string? label, bool muted)
    {
        if (_disposed) return;
        if (!_config.OsdEnabled) return;

        _dispatcher.BeginInvoke(new Action(() => ShowOnDispatcher(level, label, muted)));
    }

    private void ShowOnDispatcher(int level, string? label, bool muted)
    {
        try
        {
            _window ??= CreateWindow();
            _window.UpdateContent(level, label, muted);
            _window.FadeIn();
            ResetHideTimer();
        }
        catch (Exception ex)
        {
            Log.Error("OsdController.Show", ex);
        }
    }

    private VolumeOverlayWindow CreateWindow()
    {
        var w = new VolumeOverlayWindow();
        ApplyScale(w);
        PositionWindow(w);
        return w;
    }

    private void ApplyScale(VolumeOverlayWindow w)
    {
        var scale = Math.Clamp(_config.OsdScale, 0.5, 3.0);
        if (Math.Abs(scale - 1.0) > 0.001)
        {
            w.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
            w.Width = 380 * scale;
            w.Height = 84 * scale;
        }
    }

    private void PositionWindow(VolumeOverlayWindow w)
    {
        // WorkArea excludes the taskbar — positioning relative to its bottom keeps
        // the OSD above the taskbar regardless of taskbar placement/size.
        var screen = SystemParameters.WorkArea;
        const double edgeMargin = 16.0;   // small consistent gap from screen edges
        const double topMargin = 80.0;    // larger gap from top for top-* positions

        switch ((_config.OsdPosition ?? "bottom-right").ToLowerInvariant())
        {
            case "top-center":
                w.Left = screen.Left + (screen.Width - w.Width) / 2;
                w.Top  = screen.Top  + topMargin;
                break;
            case "bottom-center":
                w.Left = screen.Left + (screen.Width - w.Width) / 2;
                w.Top  = screen.Top  + screen.Height - w.Height - edgeMargin;
                break;
            case "top-right":
                w.Left = screen.Left + screen.Width - w.Width - edgeMargin;
                w.Top  = screen.Top  + topMargin;
                break;
            case "bottom-right":
            default:
                w.Left = screen.Left + screen.Width - w.Width - edgeMargin;
                w.Top  = screen.Top  + screen.Height - w.Height - edgeMargin;
                break;
        }
    }

    private void ResetHideTimer()
    {
        _hideTimer?.Stop();
        _hideTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _hideTimer.Tick -= OnHideTick;
        _hideTimer.Tick += OnHideTick;
        _hideTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, _config.OsdTimeoutMs));
        _hideTimer.Start();
    }

    private void OnHideTick(object? sender, EventArgs e)
    {
        _hideTimer?.Stop();
        _window?.FadeOut();
    }

    public void Dispose()
    {
        _disposed = true;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _hideTimer?.Stop();
            _window?.Close();
            _window = null;
        }));
    }
}
