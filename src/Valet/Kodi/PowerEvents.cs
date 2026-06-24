using Microsoft.Win32;
using Valet.Logging;

namespace Valet.Kodi;

internal sealed class PowerEvents : IDisposable
{
    public event Action? Suspending;
    public event Action? Resuming;

    public PowerEvents()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // Fires on a SystemEvents helper thread, not the UI thread.
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Log.Info("PowerMode: Suspend");
                try { Suspending?.Invoke(); }
                catch (Exception ex) { Log.Error("Suspending handler", ex); }
                break;

            case PowerModes.Resume:
                Log.Info("PowerMode: Resume");
                try { Resuming?.Invoke(); }
                catch (Exception ex) { Log.Error("Resuming handler", ex); }
                break;
        }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
