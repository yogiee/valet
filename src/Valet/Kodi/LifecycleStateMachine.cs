using Valet.App;
using Valet.Logging;

namespace Valet.Kodi;

internal enum LifecycleState
{
    Booting,
    Media,
    Gaming,
}

internal sealed class LifecycleStateMachine : IDisposable
{
    private readonly Config _config;
    private readonly KodiController _kodi;
    private readonly SteamWatcher _steam;
    private readonly PowerEvents _power;
    private readonly System.Threading.Timer _tickTimer;
    private readonly object _sync = new();

    private LifecycleState _state = LifecycleState.Booting;
    private DateTime _readyAtUtc;
    private volatile bool _disposed;

    public LifecycleStateMachine(Config config, KodiController kodi, SteamWatcher steam, PowerEvents power)
    {
        _config = config;
        _kodi = kodi;
        _steam = steam;
        _power = power;

        _readyAtUtc = DateTime.UtcNow.AddSeconds(_config.BootDelaySec);
        _power.Suspending += OnSuspend;
        _power.Resuming += OnResume;

        _tickTimer = new System.Threading.Timer(_ => Tick(), null,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));

        Log.Info($"Lifecycle started; bootDelay={_config.BootDelaySec}s wakeDelay={_config.WakeDelaySec}s");
    }

    public LifecycleState State
    {
        get { lock (_sync) return _state; }
    }

    public bool IsKodiRunning => _kodi.IsRunning();

    private void OnSuspend()
    {
        // Synchronous — Windows force-suspends after a few seconds regardless.
        _kodi.CloseGracefully(TimeSpan.FromSeconds(2));
    }

    private void OnResume()
    {
        lock (_sync)
        {
            _state = LifecycleState.Booting;
            _readyAtUtc = DateTime.UtcNow.AddSeconds(_config.WakeDelaySec);
        }
        Log.Info($"Resume → wake delay {_config.WakeDelaySec}s");
    }

    private void Tick()
    {
        if (_disposed) return;

        LifecycleState previous;
        LifecycleState next;
        bool launchKodi = false;
        bool closeKodi = false;
        string transitionReason = "";

        lock (_sync)
        {
            if (DateTime.UtcNow < _readyAtUtc) return;

            previous = _state;
            var bpm = _steam.IsBigPictureActive;

            if (previous == LifecycleState.Booting)
            {
                if (bpm) { next = LifecycleState.Gaming; closeKodi = true; transitionReason = "boot/BPM"; }
                else { next = LifecycleState.Media; launchKodi = true; transitionReason = "boot"; }
            }
            else if (previous == LifecycleState.Media && bpm)
            {
                next = LifecycleState.Gaming; closeKodi = true; transitionReason = "BPM appeared";
            }
            else if (previous == LifecycleState.Gaming && !bpm)
            {
                next = LifecycleState.Media; launchKodi = true; transitionReason = "BPM gone";
            }
            else
            {
                next = previous;
            }

            _state = next;
        }

        if (next == previous) return;

        Log.Info($"State {previous} → {next} ({transitionReason})");

        // Side effects outside the lock — these can block (CloseGracefully waits up to 5s).
        if (closeKodi && _kodi.IsRunning())
        {
            _kodi.CloseGracefully(TimeSpan.FromSeconds(5));
        }
        if (launchKodi && !_kodi.IsRunning())
        {
            _kodi.Launch();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _tickTimer.Dispose();
        _power.Suspending -= OnSuspend;
        _power.Resuming -= OnResume;
    }
}
