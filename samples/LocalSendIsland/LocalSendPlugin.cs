using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace LocalSendIsland;

public sealed class LocalSendPlugin : IslandPluginBase
{
    private CancellationTokenSource? _lifetime;
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override Task OnInitializeAsync()
    {
        _lifetime = new();
        Context.Island.AddDropTarget(new IslandDropTarget
        {
            Id = "share", Title = "LocalSend 分享", Hint = "文件 · 文件夹 · 文本 · 图片",
            Glyph = "\uE72D", Kinds = IslandDropKind.All, Handler = ShareAsync
        });
        Context.Island.AddSettingsPage(new SettingsPageDescriptor("localsend", "LocalSend 分享", "\uE72D", BuildSettings));
        return Task.CompletedTask;
    }

    private async Task<string?> ShareAsync(IslandDropContext drop)
    {
        var token = _lifetime!.Token;
        var configured = Settings.Get("executable", "");
        var nativeText = Settings.Get("nativeText", false);
        try
        {
            await _gate.WaitAsync(token);
            try
            {
                return await Task.Run(async () =>
                {
                    var exe = LocalSendBridge.FindExecutable(configured);
                    if (exe == null) return "未找到 LocalSend，请在设置 → LocalSend 分享中指定程序路径";
                    var args = await LocalSendBridge.PrepareAsync(drop.Kind.ToString(), drop.Paths, drop.Text, drop.ImageBytes, nativeText, token);
                    token.ThrowIfCancellationRequested();
                    await LocalSendBridge.LaunchAsync(exe, args, token);
                    return "已交给 LocalSend，请选择接收设备";
                }, token);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { return "分享已取消"; }
        catch (Exception ex)
        {
            Log.Error("调用 LocalSend 失败", ex);
            return "分享失败：" + ex.Message;
        }
    }

    private UIElement BuildSettings()
    {
        var panel = new StackPanel { Spacing = 14, Padding = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "LocalSend 分享", FontSize = 24 });
        panel.Children.Add(new TextBlock { Text = "将内容拖到岛上的 LocalSend 分享卡片，随后在 LocalSend 中选择接收设备。", TextWrapping = TextWrapping.Wrap });
        var path = new TextBox { Header = "LocalSend 程序路径（留空自动查找）", PlaceholderText = "例如 D:\\LocalSend\\localsend_app.exe", Text = Settings.Get("executable", "") };
        void Save()
        {
            try
            {
                var value = path.Text.Trim().Trim('"');
                if (value != Settings.Get("executable", "")) Settings.Set("executable", value);
            }
            catch (Exception ex) { Log.Error("保存 LocalSend 路径失败", ex); }
        }
        path.LostFocus += (_, _) => Save();
        path.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Save(); };
        panel.Children.Add(path);
        var native = new ToggleSwitch { Header = "原生文本分享（需要 LocalSend 1.18 或更新版本）", IsOn = Settings.Get("nativeText", false) };
        native.Toggled += (_, _) => { try { Settings.Set("nativeText", native.IsOn); } catch (Exception ex) { Log.Error("保存设置失败", ex); } };
        panel.Children.Add(native);
        panel.Children.Add(new TextBlock { Text = "默认把文本保存成 UTF-8 .txt 文件，兼容旧版并保留首尾空白。图片保留原始字节。临时文件保留 7 天，之后在下一次分享时清理。文件夹由 LocalSend 递归发送，空文件夹通常不会传输。", TextWrapping = TextWrapping.Wrap });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var test = new Button { Content = "保存并打开 LocalSend" };
        test.Click += async (_, _) =>
        {
            try
            {
                Save();
                var exe = LocalSendBridge.FindExecutable(Settings.Get("executable", ""));
                if (exe == null) { status.Text = "未找到程序，请填写 localsend_app.exe 的完整路径。"; return; }
                await LocalSendBridge.LaunchAsync(exe, []);
                status.Text = "已打开：" + exe;
            }
            catch (Exception ex) { Log.Error("打开 LocalSend 失败", ex); status.Text = ex.Message; }
        };
        panel.Children.Add(test);
        panel.Children.Add(status);
        return new ScrollViewer { Content = panel };
    }

    protected override Task OnShutdownAsync()
    {
        _lifetime?.Cancel();
        // 已交给 LocalSend 的临时文件不能在停用时删除。
        return Task.CompletedTask;
    }
}
