using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;
using Valet.Logging;

namespace Valet.Kodi;

/// <summary>
/// Polls HKCU\Software\Valve\Steam\RunningAppID and looks up the display name from
/// store.steampowered.com (public, anonymous endpoint — no API key needed).
/// </summary>
internal sealed class SteamRunningGame : IDisposable
{
    private const string RegPath = @"Software\Valve\Steam";
    private const string RegValue = "RunningAppID";
    private const string AppDetailsUrl = "https://store.steampowered.com/api/appdetails?appids={0}&filters=basic";

    private readonly System.Threading.Timer _timer;
    private readonly HttpClient _http;
    private readonly Dictionary<int, string?> _nameCache = new();
    private readonly object _sync = new();

    private volatile int _currentAppId;
    private string? _currentName;
    private volatile bool _disposed;

    public int CurrentAppId => _currentAppId;

    public string? CurrentName
    {
        get { lock (_sync) return _currentName; }
    }

    public SteamRunningGame()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Valet/1.0 (yogiee/valet)");

        // Poll cadence: every 2s. Registry read is cheap; reacting within 2s of a game
        // launch/exit is plenty fast for the lifecycle handoff.
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    private void Poll()
    {
        if (_disposed) return;
        try
        {
            var appId = ReadRunningAppId();
            if (appId == _currentAppId) return; // unchanged

            var previous = _currentAppId;
            _currentAppId = appId;

            if (appId == 0)
            {
                lock (_sync) _currentName = null;
                Log.Info($"SteamRunningGame: app {previous} stopped");
                return;
            }

            Log.Info($"SteamRunningGame: app {appId} started — resolving name");
            _ = ResolveAndSetNameAsync(appId);
        }
        catch (Exception ex)
        {
            Log.Error("SteamRunningGame.Poll", ex);
        }
    }

    private static int ReadRunningAppId()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath);
        if (key is null) return 0;
        return key.GetValue(RegValue) switch
        {
            int i => i,
            long l => (int)l,
            _ => 0,
        };
    }

    private async Task ResolveAndSetNameAsync(int appId)
    {
        string? cachedName;
        bool cached;
        lock (_sync)
        {
            cached = _nameCache.TryGetValue(appId, out cachedName);
        }

        if (cached)
        {
            lock (_sync) if (_currentAppId == appId) _currentName = cachedName;
            Log.Info($"SteamRunningGame: app {appId} → '{cachedName ?? "<unknown>"}' (cached)");
            return;
        }

        string? resolved = null;
        try
        {
            var url = string.Format(AppDetailsUrl, appId);
            using var resp = await _http.GetAsync(url).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty(appId.ToString(), out var entry) &&
                entry.TryGetProperty("success", out var success) && success.GetBoolean() &&
                entry.TryGetProperty("data", out var data) &&
                data.TryGetProperty("name", out var nameEl))
            {
                resolved = nameEl.GetString();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"SteamRunningGame name lookup failed for app {appId}: {ex.Message}");
        }

        lock (_sync)
        {
            _nameCache[appId] = resolved; // cache even null to avoid retrying the lookup
            if (_currentAppId == appId) _currentName = resolved;
        }
        Log.Info($"SteamRunningGame: app {appId} → '{resolved ?? "<unknown>"}'");
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _http.Dispose();
    }
}
