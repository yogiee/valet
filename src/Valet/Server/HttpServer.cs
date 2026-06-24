using System.Net;
using System.Text;
using System.Text.Json;
using Valet.Logging;

namespace Valet.Server;

internal sealed class HttpServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Endpoints _endpoints;
    private readonly Auth _auth;
    private readonly int _port;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public HttpServer(int port, Endpoints endpoints, Auth auth)
    {
        _port = port;
        _endpoints = endpoints;
        _auth = auth;
    }

    public bool Start()
    {
        if (TryBind($"http://+:{_port}/")) return true;

        Log.Warn($"HttpListener Access Denied on http://+:{_port}/ — falling back to localhost only. " +
                 $"Run `netsh http add urlacl url=http://+:{_port}/ user=Everyone` (admin) to expose on LAN.");

        return TryBind($"http://localhost:{_port}/");
    }

    private bool TryBind(string prefix)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            Log.Info($"HTTP server bound: {prefix}");
            return true;
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            try { ((IDisposable)listener).Dispose(); } catch { }
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"HttpListener failed to bind {prefix}", ex);
            try { ((IDisposable)listener).Dispose(); } catch { }
            return false;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var remote = ctx.Request.RemoteEndPoint?.Address;
            if (remote is null || !_auth.IsAllowedFrom(remote))
            {
                Log.Warn($"403 from {remote}");
                await WriteJsonAsync(ctx.Response, 403, new { error = "forbidden" }).ConfigureAwait(false);
                return;
            }

            await _endpoints.RouteAsync(ctx, _auth).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("request handler", ex);
            try { await WriteJsonAsync(ctx.Response, 500, new { error = "internal" }).ConfigureAwait(false); }
            catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    internal static async Task WriteJsonAsync(HttpListenerResponse res, int statusCode, object body)
    {
        res.StatusCode = statusCode;
        res.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(body, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Stop();
        if (_listener is not null)
        {
            try { ((IDisposable)_listener).Dispose(); } catch { }
        }
        _cts?.Dispose();
    }
}
