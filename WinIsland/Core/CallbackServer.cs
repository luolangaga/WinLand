using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace WinIsland.Core;

public sealed class CallbackServer : IAsyncDisposable, ICallbackServer
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private int _port;
    private bool _running;

    private readonly ConcurrentDictionary<string, List<Func<JsonElement, Task>>> _routes = new();

    public int Port => _port;
    public bool IsRunning => _running;
    public string BaseUrl => $"http://127.0.0.1:{_port}";

    public event Action<string, JsonElement>? OnEventReceived;

    public async Task StartAsync(int preferredPort = 52741, CancellationToken ct = default)
    {
        if (_running) return;

        Exception? lastError = null;
        for (int port = preferredPort; port < preferredPort + 100; port++)
        {
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                _listener = listener;
                _port = port;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try { _listener?.Close(); } catch { }
                _listener = null;
            }
        }

        if (_listener == null)
            throw new InvalidOperationException($"Cannot find available port for CallbackServer: {lastError?.Message}");

        _cts = new CancellationTokenSource();
        _running = true;
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public void RegisterRoute(string path, Func<JsonElement, Task> handler)
    {
        var normalized = NormalizePath(path);
        if (!_routes.TryGetValue(normalized, out var list))
        {
            list = new List<Func<JsonElement, Task>>();
            _routes[normalized] = list;
        }
        lock (list)
            list.Add(handler);
    }

    public void UnregisterRoute(string path, Func<JsonElement, Task> handler)
    {
        var normalized = NormalizePath(path);
        if (_routes.TryGetValue(normalized, out var list))
        {
            lock (list)
                list.Remove(handler);
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();

                if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url?.AbsolutePath == "/ping")
                {
                    Respond(ctx, 200, new { status = "ok", port = _port });
                    continue;
                }

                if (ctx.Request.HttpMethod == "POST")
                {
                    await HandlePostAsync(ctx);
                }
                else
                {
                    Respond(ctx, 404, new { error = "not found" });
                }
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task HandlePostAsync(HttpListenerContext ctx)
    {
        string body;
        using (var ms = new MemoryStream())
        {
            await ctx.Request.InputStream.CopyToAsync(ms);
            body = Encoding.UTF8.GetString(ms.ToArray());
        }

        JsonElement json;
        try
        {
            json = JsonDocument.Parse(body).RootElement;
        }
        catch
        {
            Respond(ctx, 400, new { error = "invalid json" });
            return;
        }

        var path = NormalizePath(ctx.Request.Url?.AbsolutePath ?? "/");

        OnEventReceived?.Invoke(path, json);

        if (_routes.TryGetValue(path, out var handlers))
        {
            List<Func<JsonElement, Task>> snapshot;
            lock (handlers)
                snapshot = new List<Func<JsonElement, Task>>(handlers);

            foreach (var handler in snapshot)
            {
                try { await handler(json); }
                catch { }
            }
            Respond(ctx, 200, new { ok = true });
        }
        else
        {
            Respond(ctx, 200, new { ok = true, warning = "no handler" });
        }
    }

    private static void Respond(HttpListenerContext ctx, int statusCode, object payload)
    {
        try
        {
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch { }
    }

    private static string NormalizePath(string path) =>
        string.IsNullOrEmpty(path) ? "/" : (path.StartsWith("/") ? path : "/" + path);

    public async ValueTask DisposeAsync()
    {
        _running = false;
        _cts?.Cancel();

        if (_listenTask != null)
        {
            try { await _listenTask; } catch { }
        }

        _listener?.Close();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _routes.Clear();
    }
}
