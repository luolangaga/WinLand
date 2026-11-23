#if DEBUG
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Numerics;
using Windows.Foundation;
using WinIsland.Core;
using WinIsland.Core.Plugins;
using WinIsland.Island;
using WinIsland.Settings;
using WinIsland.Modules.Battery;
using WinIsland.Modules.Media;

namespace WinIsland.Diagnostics;

/// <summary>Deterministic, opt-in UI regression surface. Uses isolated settings and no live plugins.</summary>
internal sealed class MotionPreview
{
    internal static bool Requested => Environment.GetCommandLineArgs().Contains("--motion-preview");
    private readonly IslandWindow _island;
    private readonly IslandService _service;
    private readonly Window _controls = new() { Title = "WinIsland Motion Preview" };
    private readonly BatterySpotlightView _battery;
    private readonly MediaSpotlightView _media;
    private readonly TextBlock _status = new() { Text = "Ready", Margin = new Thickness(0, 12, 0, 0) };
    private readonly SettingsService _settings;
    private int _closed;
    private SettingsWindow? _settingsWindow;

    internal MotionPreview(PluginLogService logs)
    {
        _settings = new SettingsService(Path.Combine(AppContext.BaseDirectory, "motion-preview"));
        _settings.Set("island.position", "top");
        _settings.Set("island.bounce", true);
        _settings.Set("island.style", "apple");
        _settings.Set("island.queueTail", "");
        _settings.Set("island.display", "auto");
        _settings.Set("island.fullscreen", "normal");
        _island = new IslandWindow(_settings, logs);
        SystemTheme.Start(_island.DispatcherQueue);
        _service = new IslandService(_island, _settings, logs.Host);
        _service.AddSettingsPage(new SettingsPageDescriptor("general", "通用", "\uE713", () => new GeneralSettingsPage(_settings), 0));
        var theme = new PreviewTheme(_settings);
        var battery = new BatteryViewModel { Percent = 76, IsCharging = true, ChargePower = 35.4, StatusText = "正在充电" };
        var media = new MediaViewModel { Title = "Quiet mornings", Artist = "WinIsland Studio", AlbumTitle = "Motion study",
            SourceAppName = "动画预览", IsPlaying = true, HasProgress = true, Duration = TimeSpan.FromSeconds(240), Position = TimeSpan.FromSeconds(88) };
        media.TogglePlayPause = () => media.IsPlaying = !media.IsPlaying;
        var artwork = new[] { CreateArtwork(36, 113, 192), CreateArtwork(203, 115, 52), CreateArtwork(107, 82, 164) };
        media.Thumbnail = artwork[0];
        AlbumArtView.PreviewDurationScale = 4; // Inspect edge-on exchange at ordinary UI capture speed.
        int artIndex = 0;
        var mediaView = new MediaIslandView(media, null);
        _battery = new BatterySpotlightView(battery, theme);
        _media = new MediaSpotlightView(media, null, null, theme);
        _service.SetLiveContent("preview.media", new IslandLiveContent
        {
            MorphView = mediaView, CompactSize = new Size(260, 40), ExpandedSize = new Size(420, 152),
            Priority = 100, OwnerLabel = "正在播放", OwnerGlyph = "\uE8D6", OnTap = Media,
        });
        _service.SetLiveContent("preview.battery", new IslandLiveContent
        {
            MorphView = new BatteryIslandView(battery), CompactSize = new Size(230, 40), ExpandedSize = new Size(420, 150),
            Priority = 80, OwnerLabel = "充电监控", OwnerGlyph = "\uE857", OnTap = Battery,
        });
        for (int i = 0; i < 4; i++)
        {
            _service.SetLiveContent("preview.queue." + i, new IslandLiveContent
            {
                CompactContent = new TextBlock { Text = "兼容双视图插件", VerticalAlignment = VerticalAlignment.Center },
                ExpandedContent = new TextBlock { Text = "队列内容 " + (i + 1), Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center },
                CompactSize = new Size(240, 40), ExpandedSize = new Size(400, 110), Priority = 60 - i,
            });
        }
        var panel = new StackPanel { Padding = new Thickness(24), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "动画与布局预览", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "使用独立测试设置，关闭窗口退出预览。", TextWrapping = TextWrapping.Wrap });
        Add(panel, "PreviewEdgeExpansion", "边缘检查 · 展开 / 收起", _island.PreviewEdgeExpansion);
        Add(panel, "PreviewEdgeLight", "边缘检查 · Fluent 浅色", () => _island.PreviewEdges(IslandStyleKind.Fluent, true));
        Add(panel, "PreviewEdgeDark", "边缘检查 · Fluent 深色", () => _island.PreviewEdges(IslandStyleKind.Fluent, false));
        Add(panel, "PreviewEdgeApple", "边缘检查 · Apple", () => _island.PreviewEdges(IslandStyleKind.Apple, false));
        Add(panel, "PreviewEdgePosition", "边缘检查 · 顶部 / 底部", () => _settings.Set("island.position",
            _settings.Get("island.position", "top") == "top" ? "bottom" : "top"));
        Add(panel, "PreviewEdgeScale", "边缘检查 · 100% / 125%", () => _settings.Set("island.scale.island",
            _settings.Get("island.scale.island", 100d) == 100d ? 125d : 100d));
        Add(panel, "PreviewEdgeMaterial", "边缘检查 · Acrylic / Mica", () => _settings.Set("island.material",
            _settings.Get("island.material", "acrylic") == "acrylic" ? "mica" : "acrylic"));
        Add(panel, "PreviewCoverSwitch", "切换歌曲封面", () => media.Thumbnail = artwork[++artIndex % artwork.Length]);
        Add(panel, "PreviewCoverRapid", "快速连续切换封面", async () =>
        {
            media.Thumbnail = artwork[1]; await Task.Delay(140);
            media.Thumbnail = artwork[2]; await Task.Delay(100);
            media.Thumbnail = artwork[0]; artIndex = 0;
        });
        Add(panel, "PreviewWaveWithoutGlow", "小岛律动 · 背景关闭", () =>
        { media.IsPlaying = true; media.WaveEnabled = true; media.GlowEnabled = false; media.BackgroundMode = "off"; mediaView.PreviewAudio(null); });
        Add(panel, "PreviewWaveBass", "小岛律动 · 低频", () => mediaView.PreviewAudio(new Vector3(.95f, .15f, .04f)));
        Add(panel, "PreviewWaveTreble", "小岛律动 · 高频", () => mediaView.PreviewAudio(new Vector3(.03f, .15f, .95f)));
        Add(panel, "PreviewWavePause", "暂停律动", () => media.IsPlaying = false);
        Add(panel, "PreviewWaveDisable", "关闭小岛律动", () => media.WaveEnabled = false);
        Add(panel, "PreviewSettings", "Fluent 设置界面", () =>
        {
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(_service, _settings);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }
            _settingsWindow.Activate();
        });
        Add(panel, "PreviewSettingsClose", "关闭设置预览", () => _settingsWindow?.Close());
        Add(panel, "PreviewReturn", "连续收回验证", async () =>
        {
            for (int i = 0; i < 4; i++)
            {
                Media();
                await Task.Delay(700);
                _service.CloseSpotlight("preview.media");
                await Task.Delay(550);
            }
        });
        Add(panel, "PreviewExit", "退出测试预览", () => _controls.Close());
        Add(panel, "PreviewProgress25", "进度 25%", () => media.Position = TimeSpan.FromSeconds(60));
        Add(panel, "PreviewProgress75", "进度 75%", () => media.Position = TimeSpan.FromSeconds(180));
        Add(panel, "PreviewMedia", "媒体聚光卡", Media);
        var previewLines = Enumerable.Range(0, 16).Select(i => new LyricLine(TimeSpan.FromSeconds(8 + i * 6),
            new[] { "清晨的光，慢慢落在窗边", "Let the quiet carry us home", "把忙碌留给昨天，让音乐继续", "Every little moment finds its rhythm" }[i % 4])).ToArray();
        Add(panel, "PreviewLyrics", "自动歌词滚动", () => { Media(); media.HasProgress = true; media.Position = TimeSpan.FromSeconds(38); _media.PreviewLyrics(previewLines); });
        Add(panel, "PreviewLyricNext", "歌词下一行", () => media.Position += TimeSpan.FromSeconds(6));
        Add(panel, "PreviewIntro", "前奏等待", () => { Media(); media.HasProgress = true; media.Position = TimeSpan.Zero; _media.PreviewLyrics(previewLines); });
        Add(panel, "PreviewManualLyrics", "无进度 · 自由滚动", () => { Media(); media.HasProgress = false; _media.PreviewLyrics(previewLines); });
        Add(panel, "PreviewNoLyrics", "没有歌词", () => { Media(); _media.PreviewLyrics(null); });
        foreach (var effect in new[] { "spectrum", "wave", "bass", "off" })
            Add(panel, "PreviewEffect" + effect, "背景 · " + effect, () => { media.BackgroundMode = effect; });
        foreach (var effect in new[] { "beam", "circuit", "chargepulse" })
            Add(panel, "PreviewCharge" + effect, "充电 · " + effect, () => { battery.BackgroundMode = effect; Battery(); });
        Add(panel, "PreviewAudioQuiet", "律动 · 静音", () => _media.PreviewAudio(0, 0, 0));
        Add(panel, "PreviewAudioBass", "律动 · 低频", () => _media.PreviewAudio(.95f, .12f, .04f));
        Add(panel, "PreviewAudioTreble", "律动 · 高频", () => _media.PreviewAudio(.06f, .2f, .95f));
        Add(panel, "PreviewChargeOff", "停止充电", () => battery.IsCharging = !battery.IsCharging);
        _service.AddSettingsPage(new SettingsPageDescriptor("effects", "背景动效", "\uE790",
            () => new BackgroundEffectSettings(_settings, true, charging: true), 20));
        Add(panel, "PreviewBattery", "电池聚光卡", Battery);
        Add(panel, "PreviewClose", "收起聚光卡", () => { _service.CloseSpotlight("preview.media"); _service.CloseSpotlight("preview.battery"); _service.CloseSpotlight("preview.sample"); });
        Add(panel, "PreviewMessage", "两行临时消息", () => _service.SendMessage(new IslandMessage
            { Title = "内容交接测试", Text = "正在更新插件内容。\n所有状态应该连续衔接。", Duration = TimeSpan.FromSeconds(4) }));
        Add(panel, "PreviewReverse", "快速开关与替换", async () =>
        {
            Media(); await Task.Delay(90); Battery(); await Task.Delay(80);
            _service.CloseSpotlight("preview.battery"); await Task.Delay(80); Media();
        });
        Add(panel, "PreviewStyle", "切换 Apple / Fluent", () => _settings.Set("island.style",
            _settings.Get("island.style", "apple") == "apple" ? "fluent" : "apple"));
        Add(panel, "PreviewBottom", "切换顶部 / 底部", () => _settings.Set("island.position",
            _settings.Get("island.position", "top") == "top" ? "bottom" : "top"));
        if (SamplePreview.Available)
        {
            foreach (var name in new[] { "HardwareMonitor", "WeatherIsland", "DeviceIsland", "ClipboardIsland" })
            {
                Add(panel, "Preview" + name, "示例设计 · " + name, () =>
                {
                    try
                    {
                        var sample = SamplePreview.Create(name, theme);
                        _service.OpenSpotlight("preview.sample", new IslandSpotlight { Content = sample.View, Size = sample.Size });
                        if (sample.View is IMorphView morph) morph.AnimateToExpanded(TimeSpan.FromMilliseconds(320));
                    }
                    catch (Exception ex) { _status.Text = "Sample error: " + ex.GetBaseException().Message; }
                });
            }
        }
        AutomationProperties.SetAutomationId(_status, "PreviewStatus");
        panel.Children.Add(_status);
        _controls.Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        double dpi = Win32.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(_controls)) / 96.0;
        _controls.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(440 * dpi), (int)(540 * dpi)));
        _controls.Closed += (_, _) => { theme.Dispose(); _settingsWindow?.Close(); _island.Close(); Application.Current.Exit(); };
    }

    private static WriteableBitmap CreateArtwork(byte red, byte green, byte blue)
    {
        var bitmap = new WriteableBitmap(96, 96);
        byte[] pixels = new byte[96 * 96 * 4];
        for (int y = 0; y < 96; y++) for (int x = 0; x < 96; x++)
        {
            int offset = (y * 96 + x) * 4;
            bool stripe = ((x + y) / 16) % 2 == 0;
            pixels[offset] = stripe ? blue : (byte)(blue * .6);
            pixels[offset + 1] = stripe ? green : (byte)(green * .6);
            pixels[offset + 2] = stripe ? red : (byte)(red * .6);
            pixels[offset + 3] = 255;
        }
        using var stream = bitmap.PixelBuffer.AsStream(); stream.Write(pixels); bitmap.Invalidate();
        return bitmap;
    }

    private void Add(Panel panel, string id, string label, Action action)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    private void Media() => _service.OpenSpotlight("preview.media", new IslandSpotlight
    {
        Content = _media, Size = new Size(900, 520), OnClosed = () => { _media.OnHostClosed(); _status.Text = "Closed " + ++_closed; },
    });
    private void Battery() => _service.OpenSpotlight("preview.battery", new IslandSpotlight
    {
        Content = _battery, Size = new Size(700, 470), OnClosed = () => _status.Text = "Closed " + ++_closed,
    });
    internal async void Show()
    {
        _island.ShowIsland(); _controls.Activate();
        if (Environment.GetCommandLineArgs().Contains("--edge-checks"))
        {
            await Task.Delay(500);
            await _island.RunEdgeChecksAsync();
        }
    }

    private sealed class PreviewTheme : IIslandTheme, IDisposable
    {
        private readonly SettingsService _settings;
        internal PreviewTheme(SettingsService settings)
        {
            _settings = settings;
            _settings.Changed += SettingsChanged;
            SystemTheme.Changed += ThemeChanged;
        }
        public bool IsLight => IslandStyle.ResolveIsLight(_settings);
        public event Action? Changed;
        private void ThemeChanged() => Changed?.Invoke();
        private void SettingsChanged(string key) { if (key == "island.style") ThemeChanged(); }
        public void Dispose()
        {
            _settings.Changed -= SettingsChanged;
            SystemTheme.Changed -= ThemeChanged;
        }
    }
}
#endif
