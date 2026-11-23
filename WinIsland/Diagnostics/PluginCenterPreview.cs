#if DEBUG
using System.Text.Json;
using Microsoft.UI.Xaml;
using WinIsland.Core;
using WinIsland.Core.Marketplace;
using WinIsland.Core.Plugins;
using WinIsland.Island;
using WinIsland.Settings;

namespace WinIsland.Diagnostics;

/// <summary>Opt-in, isolated catalog for repeatable plugin-center visual and interaction checks.</summary>
internal static class PluginCenterPreview
{
    internal static bool Requested => Environment.GetCommandLineArgs().Contains("--plugin-center-preview");

    internal static async Task ShowAsync(PluginLogService logs)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "plugin-center-preview");
        var source = Path.Combine(root, "catalog");
        Directory.CreateDirectory(source);
        var settings = new SettingsService(root);
        settings.Set(MarketplaceService.BaseUrlSettingKey, source);
        var island = new IslandWindow(settings, logs);
        var service = new IslandService(island, settings, logs.Host);
        var host = new PluginHost(service, settings, island.DispatcherQueue, logs, Path.Combine(root, "plugins"));
        var external = Path.Combine(root, "plugins", "preview-4");
        Directory.CreateDirectory(external);
        // Disabled fixture: discovery validates the entry file without loading its contents.
        await File.WriteAllBytesAsync(Path.Combine(external, "Preview.dll"), [0]);
        await File.WriteAllTextAsync(Path.Combine(external, "plugin.json"), """
            {"id":"preview-4","name":"设备之间","version":"1.0.0","description":"电量与连接状态，在小岛上自然相遇。","entry_dll":"Preview.dll","api_version":2}
            """);
        settings.Set("plugin.preview-4.disabled", true);
        var names = new[] { "剪贴板时光机", "天气与此刻", "硬件仪表盘", "专注倒计时", "设备之间", "今天听什么", "一键快传", "桌面便笺" };
        var descriptions = new[] { "找回刚才复制的文字，让灵感在指尖接续。", "温度、天空与未来三天，一眼就能看见。", "实时了解处理器、显卡和内存的工作状态。", "为重要的事留一段不被打扰的时间。", "电量与连接状态，在小岛上自然相遇。", "把正在播放的旋律，留在桌面最轻盈的角落。", "把文件拖进小岛，轻松分享给身边的设备。", "把稍后要做的事，安放在触手可及的地方。" };
        var glyphs = new[] { "\uE77F", "\uE753", "\uE9D9", "\uE916", "\uE772", "\uE8D6", "\uE898", "\uE70B" };
        var entries = Enumerable.Range(0, names.Length).Select(i => new MarketplacePlugin {
            Id = "preview-" + i, Name = names[i], Description = descriptions[i], Author = "WinIsland Community", Version = "2.0.0",
            IconGlyph = glyphs[i], ApiVersion = 2, Tags = [i % 2 == 0 ? "效率" : "生活"], Package = "not-a-real-package.lwp", PackageSize = 1024 * (i + 1)
        }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(source, "index.json"), JsonSerializer.Serialize(new MarketplaceIndex { Plugins = entries.ToList() }));
        for (int i = 0; i < 4; i++)
        {
            host.RegisterBuiltIn(new PluginManifest { Id = entries[i].Id, Name = names[i], Description = descriptions[i], IconGlyph = glyphs[i], Version = "2.0.0", Author = "WinIsland" }, () => new PreviewPlugin());
        }
        await host.InitializeAsync();
        await host.SetEnabledAsync(entries[2].Id, false);
        var market = new MarketplaceService(settings, logs.Host, Path.Combine(root, "cache"));
        service.AddSettingsPage(new("general", "通用", "\uE713", () => new GeneralSettingsPage(settings), 0));
        service.AddSettingsPage(new("plugins", "插件中心", "\uE7B8", () => new PluginCenterPage(market, host, settings), 900));
        var window = new SettingsWindow(service, settings);
        if (window.Content is FrameworkElement content && Environment.GetCommandLineArgs().Contains("--preview-dark"))
            content.RequestedTheme = ElementTheme.Dark;
        if (Environment.GetCommandLineArgs().Contains("--preview-narrow"))
        {
            double scale = Win32.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96.0;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(640 * scale), (int)(720 * scale)));
        }
        ((App)Application.Current)._settingsWindow = window;
        window.SelectPage("plugins");
        window.Closed += async (_, _) => { await host.ShutdownAllAsync(); host.Dispose(); island.Close(); settings.Dispose(); Application.Current.Exit(); };
        window.Activate();
    }

    private sealed class PreviewPlugin : IslandPluginBase { }
}
#endif
