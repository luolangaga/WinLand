using System.Net;
using System.Text.Json;
using WinIsland.Core;

namespace WinIsland.Modules.AiMonitor;

public sealed class AiMonitorHttpListener : IDisposable
{
    private const int Port = 47321;
    private HttpListener? _listener;
    private readonly AiMonitorViewModel _vm;
    private readonly IDynamicIslandApi _api;
    private readonly AiMonitorModule _module;
    private bool _disposed;

    public AiMonitorHttpListener(AiMonitorViewModel vm, IDynamicIslandApi api, AiMonitorModule module)
    {
        _vm = vm;
        _api = api;
        _module = module;
    }

    public void Start()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = ListenLoop();
    }

    private async Task ListenLoop()
    {
        while (_listener != null && !_disposed)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = HandleRequest(ctx);
            }
            catch (HttpListenerException) when (_disposed) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                var payload = JsonSerializer.Deserialize<JsonElement>(body);
                ProcessEvent(payload);
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/plain";
            var buf = System.Text.Encoding.UTF8.GetBytes("ok");
            await ctx.Response.OutputStream.WriteAsync(buf);
            ctx.Response.Close();
        }
        catch
        {
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch { }
        }
    }

    private void ProcessEvent(JsonElement payload)
    {
        var type = payload.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == null) return;

        var message = payload.TryGetProperty("message", out var m) ? m.GetString() : "";
        var detail = payload.TryGetProperty("detail", out var d) ? d.GetString() : "";
        var subDetail = payload.TryGetProperty("subDetail", out var sd) ? sd.GetString() : "";
        var progressVal = payload.TryGetProperty("progress", out var p) ? p.GetDouble() : 0;

        RunOnUI(() =>
        {
            switch (type)
            {
                case "task_start":
                    _vm.TaskName = message ?? "OpenCode";
                    _vm.Status = "运行中";
                    _vm.Detail = detail ?? "任务执行中";
                    _vm.SubDetail = subDetail ?? "";
                    _vm.IsRunning = true;
                    _vm.Progress = 0;
                    _module.SetLive(true);

                    _api.SendMessage(new IslandMessage
                    {
                        Title = "AI 任务开始",
                        Text = message ?? "OpenCode",
                        Glyph = "\uE965",
                        AccentColor = Windows.UI.Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F),
                        Duration = TimeSpan.FromSeconds(3),
                    });
                    break;

                case "task_end":
                    _vm.Status = "已完成";
                    _vm.Detail = detail ?? "任务已完成";
                    _vm.SubDetail = subDetail ?? "";
                    _vm.IsRunning = false;

                    _api.SendMessage(new IslandMessage
                    {
                        Title = "AI 任务完成",
                        Text = detail ?? message ?? "任务已完成",
                        Glyph = "\uE73E",
                        AccentColor = Windows.UI.Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F),
                        Duration = TimeSpan.FromSeconds(4),
                    });

                    ScheduleHide();
                    break;

                case "task_error":
                    _vm.Status = "出错";
                    _vm.Detail = detail ?? "任务出错";
                    _vm.SubDetail = "";
                    _vm.IsRunning = false;

                    _api.SendMessage(new IslandMessage
                    {
                        Title = "AI 任务出错",
                        Text = detail ?? "任务出错",
                        Glyph = "\uE711",
                        AccentColor = Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x4D, 0x3D),
                        Duration = TimeSpan.FromSeconds(4),
                    });

                    ScheduleHide();
                    break;

                case "task_progress":
                    _vm.Detail = detail ?? "任务执行中";
                    if (!string.IsNullOrEmpty(subDetail))
                        _vm.SubDetail = subDetail;
                    if (progressVal > 0 && progressVal <= 1)
                        _vm.Progress = progressVal;
                    break;
            }
        });
    }

    private void ScheduleHide()
    {
        var timer = _api.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(5);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _module.SetLive(false);
        timer.Start();
    }

    private void RunOnUI(Action action)
    {
        if (_api.Dispatcher.HasThreadAccess)
            action();
        else
            _api.Dispatcher.TryEnqueue(() => action());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
    }
}
