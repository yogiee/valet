using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Valet.App;
using Valet.Logging;

namespace Valet.Kodi;

internal sealed record KodiActivityDetail(string? Title, string? Type);

internal sealed class KodiJsonRpc : IDisposable
{
    private const string Endpoint = "http://localhost:8080/jsonrpc";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public KodiJsonRpc(Config config)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };

        // Kodi v19+ requires Basic auth by default. Defaults are user="kodi", password="" —
        // matches the box defaults exactly so most installs work without further config.
        if (!string.IsNullOrEmpty(config.KodiHttpUsername))
        {
            var creds = $"{config.KodiHttpUsername}:{config.KodiHttpPassword}";
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(creds));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", b64);
        }
    }

    public async Task<(string Activity, KodiActivityDetail? Detail)> ProbeActivityAsync()
    {
        try
        {
            var players = await CallAsync("Player.GetActivePlayers", null).ConfigureAwait(false);
            if (players.ValueKind != JsonValueKind.Array || players.GetArrayLength() == 0)
            {
                return ("idle", null);
            }

            var first = players[0];
            var type = first.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            var playerId = first.TryGetProperty("playerid", out var idEl) ? idEl.GetInt32() : -1;

            if (playerId < 0)
            {
                return ("idle", null);
            }

            var activity = type switch
            {
                "audio" => "playing_audio",
                "video" => "playing_video",
                _ => "idle",
            };

            if (activity == "idle") return ("idle", null);

            var detail = await GetItemDetailAsync(playerId).ConfigureAwait(false);
            return (activity, detail);
        }
        catch (HttpRequestException ex)
        {
            // Connection refused / Kodi off / wrong port — common, not an error.
            Log.Info($"Kodi JSON-RPC unreachable: {ex.Message}");
            return ("unknown", null);
        }
        catch (TaskCanceledException)
        {
            Log.Warn("Kodi JSON-RPC timed out");
            return ("unknown", null);
        }
        catch (Exception ex)
        {
            Log.Error("Kodi JSON-RPC probe", ex);
            return ("unknown", null);
        }
    }

    private async Task<KodiActivityDetail?> GetItemDetailAsync(int playerId)
    {
        var parameters = new
        {
            playerid = playerId,
            properties = new[] { "title", "showtitle" },
        };
        var result = await CallAsync("Player.GetItem", parameters).ConfigureAwait(false);
        if (!result.TryGetProperty("item", out var item)) return null;

        string? title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(title) && item.TryGetProperty("label", out var l))
        {
            title = l.GetString();
        }
        var type = item.TryGetProperty("type", out var ty) ? ty.GetString() : null;
        return new KodiActivityDetail(title, type);
    }

    private async Task<JsonElement> CallAsync(string method, object? parameters)
    {
        var req = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
            id = 1,
        };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(Endpoint, content).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("result", out var result))
        {
            throw new InvalidOperationException("JSON-RPC response missing 'result'");
        }
        return result.Clone(); // detach from the disposing JsonDocument
    }

    public void Dispose() => _http.Dispose();
}
