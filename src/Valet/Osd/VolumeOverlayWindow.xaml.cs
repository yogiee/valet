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

        var displayLabel = !string.IsNullOrWhiteSpace(label) ? label : (muted ? "Mute" : $"{level}%");
        LabelText.Text = displayLabel;

        // Bar track lives between the icon column (22 + 16 margin) and the label column
        // (60 min + 16 margin). Total chrome: 20 left + 22 icon + 16 + 60 label + 16 + 20 right = 154.
        var w = (ActualWidth > 0 ? ActualWidth : Width) - 154;
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
        var anim = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, anim);
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
