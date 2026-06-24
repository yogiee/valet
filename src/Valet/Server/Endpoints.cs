using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using Valet.Kodi;
using Valet.Native;
using Valet.Notify;
using Valet.Power;

namespace Valet.Server;

internal sealed class Endpoints
{
    private static readonly string Version =
        typeof(Endpoints).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static readonly JsonSerializerOptions PayloadJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly PowerActions _power;
    private readonly LifecycleStateMachine? _lifecycle;
    private readonly KodiJsonRpc? _kodiRpc;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    public Endpoints(PowerActions power, LifecycleStateMachine? lifecycle = null, KodiJsonRpc? kodiRpc = null)
    {
        _power = power;
        _lifecycle = lifecycle;
        _kodiRpc = kodiRpc;
    }

    public async Task RouteAsync(HttpListenerContext ctx, Auth auth)
    {
        var (tokenValid, path) = auth.CheckToken(ctx.Request);
        var method = ctx.Request.HttpMethod.ToUpperInvariant();

        if (method == "GET")
        {
            switch (path)
            {
                case "/":
                    await HttpServer.WriteJsonAsync(ctx.Response, 200,
                        new { app = "Valet", online = true }).ConfigureAwait(false);
                    return;

                case "/status":
                    await HandleStatusAsync(ctx).ConfigureAwait(false);
                    return;

                case "/version":
                    await HttpServer.WriteJsonAsync(ctx.Response, 200,
                        new { version = Version }).ConfigureAwait(false);
                    return;
            }
        }

        if (!tokenValid)
        {
            await HttpServer.WriteJsonAsync(ctx.Response, 401,
                new { error = "unauthorized" }).ConfigureAwait(false);
            return;
        }

        switch (path)
        {
            case "/sleep":
            case "/suspend":
                if (method != "POST" && method != "GET") { await Reply405(ctx).ConfigureAwait(false); return; }
                await HandleSleepAsync(ctx).ConfigureAwait(false);
                return;

            case "/sleep/cancel":
                if (method != "POST") { await Reply405(ctx).ConfigureAwait(false); return; }
                var cancelled = _power.CancelPendingSuspend();
                await HttpServer.WriteJsonAsync(ctx.Response, 200,
                    new { cancelled }).ConfigureAwait(false);
                return;

            case "/notify":
                if (method != "POST") { await Reply405(ctx).ConfigureAwait(false); return; }
                await HandleNotifyAsync(ctx).ConfigureAwait(false);
                return;
        }

        await HttpServer.WriteJsonAsync(ctx.Response, 404,
            new { error = "not_found", path }).ConfigureAwait(false);
    }

    private async Task HandleStatusAsync(HttpListenerContext ctx)
    {
        var uptime = (int)(DateTime.UtcNow - _startedUtc).TotalSeconds;
        var state = _lifecycle?.State.ToString().ToLowerInvariant() ?? "unknown";
        var kodiRunning = _lifecycle?.IsKodiRunning ?? false;
        var foreground = GetForeground();

        string activity;
        KodiActivityDetail? activityDetail = null;

        if (state == "gaming")
        {
            activity = "gaming";
        }
        else if (!kodiRunning || _kodiRpc is null)
        {
            activity = "idle";
        }
        else
        {
            (activity, activityDetail) = await _kodiRpc.ProbeActivityAsync().ConfigureAwait(false);
        }

        await HttpServer.WriteJsonAsync(ctx.Response, 200, new
        {
            app = "Valet",
            online = true,
            state,
            kodiRunning,
            activity,
            activityDetail,
            foreground,
            uptimeSec = uptime,
            sleepPendingSec = _power.PendingSecondsRemaining,
        }).ConfigureAwait(false);
    }

    private static string GetForeground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "desktop";
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return "other";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var name = p.ProcessName.ToLowerInvariant();
            return name switch
            {
                "kodi" => "kodi",
                "steam" or "steamwebhelper" => "steam",
                "explorer" => "desktop",
                _ => "other",
            };
        }
        catch
        {
            return "other";
        }
    }

    private async Task HandleSleepAsync(HttpListenerContext ctx)
    {
        var delayStr = ctx.Request.QueryString["delay"];

        if (int.TryParse(delayStr, out var delay) && delay > 0)
        {
            _power.BeginDelayedSuspend(delay);
            await HttpServer.WriteJsonAsync(ctx.Response, 202,
                new { action = "sleep", delay }).ConfigureAwait(false);
            return;
        }

        await HttpServer.WriteJsonAsync(ctx.Response, 202,
            new { action = "sleep", delay = 0 }).ConfigureAwait(false);

        // Delay slightly so the response actually flushes before the system suspends.
        _ = Task.Run(async () =>
        {
            await Task.Delay(500).ConfigureAwait(false);
            _power.SuspendNow();
        });
    }

    private static async Task HandleNotifyAsync(HttpListenerContext ctx)
    {
        NotifyPayload? payload;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                await HttpServer.WriteJsonAsync(ctx.Response, 400,
                    new { error = "empty_body" }).ConfigureAwait(false);
                return;
            }
            payload = JsonSerializer.Deserialize<NotifyPayload>(body, PayloadJsonOpts);
        }
        catch (JsonException ex)
        {
            await HttpServer.WriteJsonAsync(ctx.Response, 400,
                new { error = "bad_json", message = ex.Message }).ConfigureAwait(false);
            return;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Title))
        {
            await HttpServer.WriteJsonAsync(ctx.Response, 400,
                new { error = "title_required" }).ConfigureAwait(false);
            return;
        }

        var shown = Toast.Show(payload.Title!, payload.Body, payload.Icon, payload.Scenario, payload.Image);
        await HttpServer.WriteJsonAsync(ctx.Response, shown ? 200 : 500,
            new { shown }).ConfigureAwait(false);
    }

    private static Task Reply405(HttpListenerContext ctx) =>
        HttpServer.WriteJsonAsync(ctx.Response, 405, new { error = "method_not_allowed" });

    private sealed class NotifyPayload
    {
        public string? Title { get; set; }
        public string? Body { get; set; }
        public string? Icon { get; set; }
        public string? Scenario { get; set; }
        public string? Image { get; set; } // hero image URL (http/https/file://)
    }
}
