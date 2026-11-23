#if DEBUG
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;
using WinIsland.Core.Plugins;
using WinIsland.Core.Marketplace;
using WinIsland.Island;
using WinIsland.Settings;

namespace WinIsland.Diagnostics;

internal static class ReliabilityTests
{
    internal static bool Requested => Environment.GetCommandLineArgs().Contains("--reliability-tests");

    internal static async Task RunAsync(PluginLogService logs)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "reliability-tests");
        Directory.CreateDirectory(root);
        var report = Path.Combine(root, "results.json");
        var results = new List<object>();
        using var settings = new SettingsService(Path.Combine(root, Guid.NewGuid().ToString("N")));
        var island = new IslandWindow(settings, logs);
        var service = new IslandService(island, settings, logs.Host);
        var host = new PluginHost(service, settings, island.DispatcherQueue, logs, Path.Combine(root, "plugins"));
        SettingsWindow? window = null;
        int exitCode = 0;
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        async Task Check(string name, Func<Task> action)
        {
            if (Environment.GetCommandLineArgs().Contains("--plugin-list-only") && !name.StartsWith("Plugin list")) return;
            try { await action(); results.Add(new { name, status = "PASS" }); }
            catch (Exception ex) { results.Add(new { name, status = "FAIL", detail = ex.ToString() }); exitCode = 1; }
        }
        PluginInstance Instance(string id, Probe plugin) => new(host, new PluginInfo {
            Id = id, Name = id, IsBuiltIn = true, Source = root,
            Manifest = new PluginManifest { Id = id, Name = id, Version = "1.0.0" }
        }, () => plugin);
        try
        {
            await Check("Plugin callbacks and multicast settings are guarded", async () => {
                var plugin = new Probe(); var instance = Instance("guard-test", plugin);
                await instance.ActivateAsync();
                plugin.Context!.RunOnUI(() => throw new Exception("UI callback failure"));
                Require(instance.Info.LastError != null, "RunOnUI did not record plugin error");
                int delivered = 0;
                plugin.Context.Settings.Changed += _ => throw new Exception("settings callback failure");
                plugin.Context.Settings.Changed += _ => delivered++;
                plugin.Context.Settings.Set("value", 1);
                Require(delivered == 1, "Throwing subscriber blocked notification");
                await plugin.Context.RunOnUIAsync(() => Task.FromException(new Exception("async callback failure")));
                await instance.DeactivateAsync(); await instance.UnloadAssemblyAsync();
            });
            await Check("Old context is rejected after reactivation including queued callbacks", async () => {
                var plugin = new Probe(); var instance = Instance("scope-test", plugin);
                await instance.ActivateAsync(); var old = plugin.Context!;
                await instance.DeactivateAsync(); await instance.ActivateAsync();
                int called = 0;
                old.RunOnUI(() => called++);
                await old.RunOnUIAsync(() => { called++; return Task.CompletedTask; });
                old.Island.AddSettingsPage(new("stale", "Stale", "", () => new TextBlock()));
                old.Settings.Set("stale", true);
                Require(called == 0 && !settings.Get("scope-test.stale", false), "Old context was accepted");
                Require(!service.SettingsPages.Any(p => p.Id == "scope-test/stale"), "Old page registered");
                // Queue from a worker while UI is occupied, then revoke before dispatcher executes it.
                var current = plugin.Context!;
                var post = Task.Run(() => current.RunOnUI(() => called++));
                await instance.DeactivateAsync();
                await post;
                await Task.Delay(100);
                Require(called == 0, "Revoked queued callback ran");
                await instance.UnloadAssemblyAsync();
            });
            await Check("Failed initialization calls shutdown", async () => {
                var plugin = new Probe { Initialize = _ => Task.FromException(new Exception("init failure")) };
                var instance = Instance("failed-test", plugin);
                await instance.ActivateAsync();
                Require(instance.Info.State == PluginState.Faulted && plugin.ShutdownCount == 1, "Failed initialization not cleaned");
                await instance.UnloadAssemblyAsync();
            });
            await Check("Late async callback failure does not fault the new activation", async () => {
                var plugin = new Probe(); var instance = Instance("late-error", plugin);
                await instance.ActivateAsync();
                var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var callback = plugin.Context!.RunOnUIAsync(() => pending.Task);
                await instance.DeactivateAsync(); await instance.ActivateAsync();
                pending.SetException(new Exception("Old callback error")); await callback;
                Require(instance.Info.LastError == null, "Old error contaminated current activation");
                await instance.DeactivateAsync(); await instance.UnloadAssemblyAsync();
            });
            await Check("Timed out shutdown blocks restart until the old generation finishes", async () => {
                var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var plugin = new Probe { Shutdown = () => pending.Task };
                var instance = Instance("shutdown-test", plugin);
                await instance.ActivateAsync(); await instance.DeactivateAsync(); await instance.ActivateAsync();
                Require(plugin.InitializeCount == 1, "Restart overlapped old shutdown");
                pending.SetResult(); plugin.Shutdown = () => Task.CompletedTask;
                await instance.ActivateAsync();
                Require(instance.Info.State == PluginState.Active, "Completed shutdown still blocks restart");
                await instance.DeactivateAsync(); await instance.UnloadAssemblyAsync();
            });
            await Check("Timed out initialization prevents overlapping retry and cleans when complete", async () => {
                var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var plugin = new Probe { Initialize = _ => pending.Task };
                var instance = Instance("timeout-test", plugin);
                await instance.ActivateAsync();
                Require(instance.Info.State == PluginState.Faulted, "Init did not time out");
                await instance.ActivateAsync();
                Require(plugin.InitializeCount == 1, "Retry overlapped old init");
                pending.SetResult();
                for (int i = 0; i < 30 && plugin.ShutdownCount == 0; i++) await Task.Delay(20);
                Require(plugin.ShutdownCount == 1, "Timed out init not cleaned");
                plugin.Initialize = _ => Task.CompletedTask;
                await instance.ActivateAsync();
                Require(instance.Info.State == PluginState.Active, "Cleaned init could not retry");
                await instance.DeactivateAsync(); await instance.UnloadAssemblyAsync();
            });
            await Check("Clicking a display card really moves the island to that display", async () => {
                // 真实双屏机器无法在开发机 / CI 上复现，所以注入两份**物理像素**的显示器几何：
                // 主屏 1920x1080 @0,0，副屏 1280x1024 摆在左边 @-1280,0（负坐标，最容易暴露
                // "被夹回主屏"的老毛病）。注入只影响枚举结果，落点 / DPI / 形状全走真实代码。
                var primary = new MonitorInfo(@"\\.\DISPLAY1", true, 0, 0, 1920, 1080, 0, 0, 1920, 1040);
                var secondary = new MonitorInfo(@"\\.\DISPLAY2", false, -1280, 0, 1280, 1024, -1280, 0, 1280, 984);
                DisplayResolver.TestMonitors = new[] { primary, secondary };
                try
                {
                    settings.Set(DisplayResolver.SettingKey, DisplayResolver.DisplayAuto);
                    island.ShowIsland();
                    await Task.Delay(400);

                    // 只建页面、不开设置窗口：卡片的构建与点击链路（ChoiceCard.Click -> Settings.Set ->
                    // IslandWindow.OnSettingChanged -> RefreshFromSettings -> ApplyCanvasBounds）
                    // 全部是真实代码，与用户点卡片走同一条路径。
                    var page = new GeneralSettingsPage(settings);

                    // 设置页必须把两块屏都画出来（旧实现只能画出一张「自动」）
                    var displayGrid = page.DisplayCardsForTest;
                    Require(displayGrid.Children.Count > 0, "Display cards were not built");
                    var cardIds = displayGrid.Children.OfType<Button>()
                        .Select(Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId)
                        .Where(id => !string.IsNullOrEmpty(id) && id.StartsWith("display.", StringComparison.Ordinal))
                        .ToArray();
                    Require(cardIds.Length == 3, $"Expected auto + 2 displays, got: {string.Join(",", cardIds)}");

                    // 真的点「显示器 2」这张卡（UIA Invoke，等同用户点击）
                    var secondaryCard = displayGrid.Children.OfType<Button>()
                        .First(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == "display.1");
                    Invoke(secondaryCard);
                    await Task.Delay(700);

                    Require(settings.Get<string?>(DisplayResolver.SettingKey, null) == secondary.Key,
                        "Clicking the card did not persist island.display");
                    var movedTo = island.ResolvedDisplayForTest;
                    Require(movedTo.Key == secondary.Key,
                        $"Island still resolves to {movedTo.Key}, expected {secondary.Key}");

                    // 窗口必须真的搬到副屏坐标（负 X），而不是只改了设置
                    var position = island.AppWindow.Position;
                    Require(position.X < 0,
                        $"Island window is still at x={position.X}; it must be on the left-hand display");

                    // 点回主屏：必须真的搬回去
                    var primaryCard = displayGrid.Children.OfType<Button>()
                        .First(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == "display.0");
                    Invoke(primaryCard);
                    await Task.Delay(700);

                    Require(settings.Get<string?>(DisplayResolver.SettingKey, null) == primary.Key,
                        "Switching back did not persist island.display");
                    Require(island.ResolvedDisplayForTest.Key == primary.Key,
                        $"Island did not return to the primary display (still {island.ResolvedDisplayForTest.Key})");
                    Require(island.AppWindow.Position.X >= 0,
                        $"Island window did not move back (x={island.AppWindow.Position.X})");

                    // 切到「自动」：鼠标不在副屏上时应当落在主屏，且不抛异常
                    var autoCard = displayGrid.Children.OfType<Button>()
                        .First(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b) == "display.auto");
                    Invoke(autoCard);
                    await Task.Delay(500);
                    Require(settings.Get<string?>(DisplayResolver.SettingKey, null) == DisplayResolver.DisplayAuto,
                        "Auto card did not persist");
                }
                finally
                {
                    // 关键收尾顺序：先把窗口挪回真实显示器，再撤掉注入的假屏。
                    // 反过来的话，窗口会短暂停留在一块"真实不存在"的负坐标屏上，
                    // 之后的 XAML/DWM 收尾会撞 stowed exception（0xC000027B）。
                    var real = Win32.EnumerateMonitors();
                    if (real.Length > 0)
                    {
                        var home = real[0];
                        island.AppWindow.Move(new Windows.Graphics.PointInt32(home.X + 24, home.Y + 24));
                        await Task.Delay(150);
                    }
                    DisplayResolver.TestMonitors = null;
                    settings.Set(DisplayResolver.SettingKey, DisplayResolver.DisplayAuto);
                    island.RefreshFromSettings();
                    await Task.Delay(300);
                }
            });

            await Check("Plugin list paints cards, refreshes, recycles containers and restores navigation", async () => {
                for (int i = 0; i < 80; i++) {
                    host.RegisterBuiltIn(new PluginManifest { Id = "list-" + i, Name = "Plugin " + i, Version = "1.0.0" }, () => new Probe());
                }
                await host.InitializeAsync();
                var source = Path.Combine(root, "catalog");
                Directory.CreateDirectory(source);
                await File.WriteAllTextAsync(Path.Combine(source, "index.json"), "{\"schema\":1,\"plugins\":[]}");
                settings.Set(MarketplaceService.BaseUrlSettingKey, source);
                var market = new MarketplaceService(settings, logs.Host, Path.Combine(root, "cache"));
                var page = new PluginCenterPage(market, host, settings);
                service.AddSettingsPage(new("plugins", "插件中心", "", () => { page = new PluginCenterPage(market, host, settings); page.ShowInstalled(); return page; }));
                window = new SettingsWindow(service, settings); window.SelectPage("plugins"); window.Activate();
                await Task.Delay(800);
                var list = Descendants(page).OfType<GridView>().Single();
                Require(list.Items.Count == 80, "Plugin list incomplete");
                var realized = Enumerable.Range(0, 80).Count(i => list.ContainerFromIndex(i) != null);
                Require(realized > 0 && realized < 80, $"List not virtualized: {realized} containers");
                var toggles = Descendants(page).OfType<ToggleSwitch>().ToArray();
                Require(toggles.Length > 0, "Containers exist but no plugin cards are painted");
                Require(Descendants(page).OfType<TextBlock>().Any(text => text.Text == "Plugin 0" && text.ActualHeight > 0),
                    "First plugin name is not rendered");
                Require(toggles.All(control =>
                    !string.IsNullOrWhiteSpace(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(control))), "Unnamed toggles");
                await host.SetEnabledAsync("list-0", false);
                await Task.Delay(200);
                Require(Descendants(page).OfType<ToggleSwitch>().Any(), "Refresh removed painted cards");
                list.ScrollIntoView(list.Items[79]);
                await Task.Delay(300);
                Require(Descendants(page).OfType<TextBlock>().Any(text => text.Text == "Plugin 9" && text.ActualHeight > 0),
                    "Recycled container did not paint the last plugin");
                var currentPage = page;
                service.AddSettingsPage(new("other", "Other", "", () => new TextBlock { Text = "Other" }));
                Require(ReferenceEquals(currentPage, page), "Updating settings navigation recreated the active plugin center");
                window.SelectPage("other"); await Task.Delay(200);
                window.SelectPage("plugins"); await Task.Delay(300);
                Require(Descendants(page).OfType<ToggleSwitch>().Any(), "Returning to page removed painted cards");
            });
        }
        catch (Exception ex) { results.Add(new { name = "Test harness", status = "FAIL", detail = ex.ToString() }); exitCode = 1; }
        finally
        {
            await host.ShutdownAllAsync(); host.Dispose();
            window?.Close(); island.Close(); await settings.FlushAsync();
            // 同步写报表：这里已经 close 了岛窗口，任何 await 都会把控制权交回消息泵，
            // 而 disposed 的 XAML/WinRT 对象在事件循环里会抛 stowed exception（0xC000027B）。
            File.WriteAllText(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Environment.Exit(exitCode);
        }
    }

    /// <summary>真的点一下按钮：走 UIA Invoke（与鼠标点击同一条 Click 路径）。</summary>
    private static void Invoke(Button button)
    {
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
        var pattern = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)
            peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
        pattern.Invoke();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed class Probe : IIslandPlugin
    {
        internal IPluginContext? Context;
        internal Func<IPluginContext, Task> Initialize = _ => Task.CompletedTask;
        internal Func<Task> Shutdown = () => Task.CompletedTask;
        internal int InitializeCount, ShutdownCount;
        public Task InitializeAsync(IPluginContext context) { Context = context; InitializeCount++; return Initialize(context); }
        public Task ShutdownAsync() { ShutdownCount++; return Shutdown(); }
    }
}
#endif
