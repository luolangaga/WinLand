using System.Net;
using System.Text.Json;
using WinIsland.Core;

namespace WinIsland.Modules.AiMonitor;

public sealed class AiMonitorHttpListener : IDisposable
{
    private const int Port = 47321;

    private readonly AiMonitorViewModel _vm;
    private readonly IPluginContext _context;
    private readonly AiMonitorPlugin _plugin;
    private HttpListener? _listener;
    private bool _disposed;

    public AiMonitorHttpListener(AiMonitorViewModel vm, IPluginContext context, AiMonitorPlugin plugin)
    {
        _vm = vm;
        _context = context;
        _plugin = plugin;
    }

    public void Start()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = ListenLoop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
    }

    private async Task ListenLoop()
    {
        while (_listener != null && !_disposed)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = HandleRequest(context);
            }
            catch (HttpListenerException) when (_disposed) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                ProcessEvent(JsonSerializer.Deserialize<JsonElement>(body));
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/plain";
            var buffer = System.Text.Encoding.UTF8.GetBytes("ok");
            await context.Response.OutputStream.WriteAsync(buffer);
            context.Response.Close();
        }
        catch
        {
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private void ProcessEvent(JsonElement payload)
    {
        var type = payload.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (type == null) return;

        var message = payload.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : "";
        var detail = payload.TryGetProperty("detail", out var detailElement) ? detailElement.GetString() : "";
        var subDetail = payload.TryGetProperty("subDetail", out var subDetailElement) ? subDetailElement.GetString() : "";
        var progress = payload.TryGetProperty("progress", out var progressElement) ? progressElement.GetDouble() : 0;

        _context.RunOnUI(() =>
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
                    _plugin.SetLive(true);

                    _context.Island.ShowMessage(new IslandMessage
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

                    _context.Island.ShowMessage(new IslandMessage
                    {
                        Title = "AI 任务完成",
                        Text = detail ?? message ?? "任务已完成",
                        Glyph = "\uE73E",
                        AccentColor = Windows.UI.Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F),
                        Duration = TimeSpan.FromSeconds(4),
                    });

                    _plugin.ScheduleHide();
                    break;

                case "task_error":
                    _vm.Status = "出错";
                    _vm.Detail = detail ?? "任务出错";
                    _vm.SubDetail = "";
                    _vm.IsRunning = false;

                    _context.Island.ShowMessage(new IslandMessage
                    {
                        Title = "AI 任务出错",
                        Text = detail ?? "任务出错",
                        Glyph = "\uE711",
                        AccentColor = Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x4D, 0x3D),
                        Duration = TimeSpan.FromSeconds(4),
                    });

                    _plugin.ScheduleHide();
                    break;

                case "task_progress":
                    _vm.Detail = detail ?? "任务执行中";
                    if (!string.IsNullOrEmpty(subDetail))
                    {
                        _vm.SubDetail = subDetail;
                    }

                    if (progress > 0 && progress <= 1)
                    {
                        _vm.Progress = progress;
                    }
                    break;
            }
        });
    }
}
