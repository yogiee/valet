using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Valet.Native;

namespace Valet.Osd;

public partial class VolumeOverlayWindow : Window
{
    // Codepoints from Segoe Fluent Icons / Segoe MDL2 Assets.
    private const string IconMute   = ""; // Speaker mute
    private const string IconVolume = ""; // Volume

    public VolumeOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Flip extended style so the window is layered, top-most, doesn't activate, and is
        // click-through (mouse events pass through to whatever is underneath — Kodi, games, etc.).
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_TRANSPARENT
            | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_TOPMOST
            | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    public void UpdateContent(int level, string? label, bool muted)
    {
        level = Math.Clamp(level, 0, 100);
        IconGlyph.Text = muted ? IconMute : IconVolume;

        var clean = StripDbSuffix(label);
        var displayLabel = !string.IsNullOrWhiteSpace(clean) ? clean : (muted ? "Mute" : $"{level}%");
        LabelText.Text = displayLabel;

        // Chrome breakdown: 12 left + 18 icon + 10 + 50 label + 10 + 12 right = 112.
        var w = (ActualWidth > 0 ? ActualWidth : Width) - 112;
        if (w < 1) w = 1;
        var target = muted ? 0 : w * (level / 100.0);

        var anim = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BarFill.BeginAnimation(System.Windows.Shapes.Rectangle.WidthProperty, anim);
    }

    public void FadeIn(double durationMs = 120)
    {
        if (!IsVisible) Show();

        // Re-assert HWND_TOPMOST every time we show — Kodi (and other fullscreen apps that came
        // up after Valet) sit in the same TOPMOST tier and can outrank our Z-order. Forcing the
        // overlay to the front of the topmost tier on each show makes it visible above them.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        var anim = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, anim);
    }

    // Strip a trailing "dB" or " dB" suffix (case-insensitive) since the OSD shows just the number.
    private static string? StripDbSuffix(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return label;
        var s = label.TrimEnd();
        if (s.EndsWith("dB", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^2].TrimEnd();
        }
        return s;
    }

    public void FadeOut(double durationMs = 280, Action? onCompleted = null)
    {
        var anim = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, _) =>
        {
            if (Math.Abs(Opacity) < 0.01) Hide();
            onCompleted?.Invoke();
        };
        BeginAnimation(OpacityProperty, anim);
    }
}
