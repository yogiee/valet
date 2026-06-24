using System.Windows.Forms;
using Valet.Logging;

namespace Valet.Power;

internal sealed class PowerActions
{
    private CancellationTokenSource? _pendingCts;
    private DateTimeOffset? _pendingFireAt;

    public bool IsPending => _pendingCts is not null;

    public int? PendingSecondsRemaining => _pendingFireAt is null
        ? null
        : Math.Max(0, (int)Math.Ceiling((_pendingFireAt.Value - DateTimeOffset.UtcNow).TotalSeconds));

    public void SuspendNow()
    {
        CancelPendingSuspend();
        Log.Info("SuspendNow → SetSuspendState(Suspend)");
        Application.SetSuspendState(PowerState.Suspend, force: true, disableWakeEvent: true);
    }

    public void BeginDelayedSuspend(int seconds)
    {
        if (seconds <= 0)
        {
            SuspendNow();
            return;
        }

        CancelPendingSuspend();
        var cts = new CancellationTokenSource();
        _pendingCts = cts;
        _pendingFireAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        Log.Info($"BeginDelayedSuspend({seconds}s)");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token).ConfigureAwait(false);
                if (Interlocked.CompareExchange(ref _pendingCts, null, cts) == cts)
                {
                    _pendingFireAt = null;
                    Log.Info("Delayed suspend firing");
                    Application.SetSuspendState(PowerState.Suspend, force: true, disableWakeEvent: true);
                }
            }
            catch (OperationCanceledException)
            {
                Log.Info("Delayed suspend cancelled");
            }
            finally
            {
                cts.Dispose();
            }
        });
    }

    public bool CancelPendingSuspend()
    {
        var cts = Interlocked.Exchange(ref _pendingCts, null);
        _pendingFireAt = null;
        if (cts is null) return false;
        cts.Cancel();
        return true;
    }
}
