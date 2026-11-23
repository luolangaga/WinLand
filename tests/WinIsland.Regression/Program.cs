using System.IO.Compression;
using System.Text.Json;
using WinIsland.Core;
using WinIsland.Core.Plugins;
using WinIsland.Island;

var root = Path.Combine(AppContext.BaseDirectory, "WinIsland-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
async Task Check(string name, Func<Task> test)
{
    await test();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
string Dir(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
PluginManifest Manifest(string version) => new() { Id = "test-plugin", Name = "Test", Version = version, EntryDll = "Test.dll", ApiVersion = 2 };
void Installed(string directory, string version)
{
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(new {
        id = "test-plugin", name = "Test", version, entry_dll = "Test.dll", api_version = 2 }));
    File.WriteAllText(Path.Combine(directory, "Test.dll"), version);
}
try
{
    await Check("Concurrent settings persist a complete snapshot", async () => {
        var dir = Dir("concurrent");
        using var store = new SettingsService(dir, (message, error) => Console.WriteLine(message + ": " + error));
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() => store.Set("key" + i, i))));
        await store.FlushAsync();
        using var reloaded = new SettingsService(dir);
        for (int i = 0; i < 100; i++) Require(reloaded.Get("key" + i, -1) == i, "Missing setting " + i);
    });
    await Check("Corrupt settings recover from backup and preserve evidence", async () => {
        var dir = Dir("backup");
        using (var store = new SettingsService(dir)) {
            store.Set("value", 1); await store.FlushAsync();
            store.Set("value", 2); await store.FlushAsync();
        }
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{broken");
        using var recovered = new SettingsService(dir);
        Require(recovered.Get("value", 0) == 1, "Backup was not used");
        Require(Directory.GetFiles(dir, "*.corrupt-*").Length == 1, "Corrupt evidence lost");
        recovered.Set("value", 3); await recovered.FlushAsync();
        Require(recovered.LastPersistenceError == null, "Recovery write failed");
    });
    await Check("Failed settings write preserves primary and can retry", async () => {
        var dir = Dir("locked");
        using var store = new SettingsService(dir, (message, error) => Console.WriteLine(message + ": " + error));
        store.Set("value", 1); await store.FlushAsync();
        using (var file = new FileStream(Path.Combine(dir, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
            store.Set("value", 2); await store.FlushAsync();
            Require(store.LastPersistenceError != null, "Locked replacement unexpectedly succeeded");
            Require(File.ReadAllText(Path.Combine(dir, "settings.json")).Contains("1"), "Primary damaged");
        }
        await store.FlushAsync();
        Require(store.LastPersistenceError == null, "Retry failed");
        using var loaded = new SettingsService(dir);
        Require(loaded.Get("value", 0) == 2, "Retry lost dirty revision");
    });
    await Check("Throwing settings subscriber does not block later subscribers", async () => {
        using var store = new SettingsService(Dir("events"));
        var scope = new PluginScope("test");
        int errors = 0, delivered = 0;
        var scoped = new ScopedSettings(store, "test", scope, a => a(), a => { try { a(); } catch { errors++; } });
        scoped.Changed += _ => throw new Exception("plugin failure");
        scoped.Changed += _ => delivered++;
        scoped.Set("value", 1);
        Require(errors == 1 && delivered == 1, "Subscriber isolation failed");
        scope.Revoke(); scoped.Set("value", 2);
        Require(store.Get("test.value", 0) == 1, "Revoked settings changed host state");
        await store.FlushAsync();
    });
    await Check("Queued settings callback becomes silent after revoke", async () => {
        using var store = new SettingsService(Dir("queued"));
        var scope = new PluginScope("test");
        var pending = new Queue<Action>(); int delivered = 0;
        var scoped = new ScopedSettings(store, "test", scope, a => pending.Enqueue(a), a => a());
        scoped.Changed += _ => delivered++;
        scoped.Set("value", 1); scope.Revoke();
        while (pending.TryDequeue(out var callback)) callback();
        Require(delivered == 0, "Revoked queued callback ran");
        await store.FlushAsync();
    });
    await Check("Scope revocation cleans every resource despite disposal failure", () => {
        var scope = new PluginScope("test"); int disposed = 0, errors = 0;
        scope = new PluginScope("test", _ => errors++);
        scope.Register(new ActionDisposable(() => disposed++));
        scope.Register(new ActionDisposable(() => throw new Exception("dispose failure")));
        scope.Revoke(); scope.Revoke(); scope.Register(new ActionDisposable(() => disposed++));
        Require(disposed == 2 && errors == 1, "Scope cleanup failed");
        return Task.CompletedTask;
    });
    await Check("Install commit failure rolls old directory back", () => {
        var plugins = Dir("rollback"); var target = Path.Combine(plugins, "test-plugin");
        Installed(target, "1.0.0");
        var prepared = new PreparedInstall { TempDirectory = Path.Combine(plugins, "missing"), TargetDirectory = target, Manifest = Manifest("2.0.0") };
        try { LwpInstaller.Commit(prepared); throw new Exception("Expected commit failure"); }
        catch (DirectoryNotFoundException) { }
        Require(File.ReadAllText(Path.Combine(target, "Test.dll")) == "1.0.0", "Old version lost");
        return Task.CompletedTask;
    });
    await Check("Interrupted install restores old version; committed install keeps new", () => {
        var plugins = Dir("interrupted"); var retired = Path.Combine(plugins, ".rollback-test");
        Installed(retired, "1.0.0"); LwpInstaller.RecoverInterruptedInstalls(plugins);
        var target = Path.Combine(plugins, "test-plugin");
        Require(File.Exists(Path.Combine(target, "Test.dll")), "Interrupted old version not restored");
        Directory.Move(target, retired); Installed(target, "2.0.0");
        LwpInstaller.RecoverInterruptedInstalls(plugins);
        Require(File.ReadAllText(Path.Combine(target, "Test.dll")) == "2.0.0", "Committed new version overwritten");
        return Task.CompletedTask;
    });
    await Check("Package extraction rejects traversal", () => {
        var plugins = Dir("packages"); var package = Path.Combine(plugins, "bad.lwp");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create)) {
            using (var writer = new StreamWriter(archive.CreateEntry("plugin.json").Open()))
                writer.Write("{\"id\":\"test-plugin\",\"name\":\"Test\",\"version\":\"1.0.0\",\"entry_dll\":\"Test.dll\",\"api_version\":2}");
            using var bad = new StreamWriter(archive.CreateEntry("../escaped.txt").Open()); bad.Write("bad");
        }
        try { LwpInstaller.Prepare(package, plugins); throw new Exception("Expected traversal rejection"); }
        catch (LwpInstallException) { }
        Require(!File.Exists(Path.Combine(root, "escaped.txt")), "Traversal escaped package directory");
        Require(Directory.GetDirectories(plugins, ".installing-*").Length == 0, "Failed package not cleaned");
        return Task.CompletedTask;
    });
    await Check("Queue tail follows owner and geometry remains fixed across pages", () => {
        Require(QueueLayoutPolicy.ResolveTailIndex(new[] { "pin1", "pin2", "other", "chosen" }, "chosen", 2, 3) == 3, "Owner lost");
        Require(QueueLayoutPolicy.ResolveTailIndex(new[] { "pin1", "pin2", "chosen", "other" }, "chosen", 3, 3) == 2, "Owner not followed");
        Require(QueueLayoutPolicy.ResolveTailIndex(new[] { "chosen", "pin2", "other" }, "chosen", 10, 3) == 2, "Pinned fallback wrong");
        Require(QueueLayoutPolicy.TotalHeight(152, 8, 3, 152, 8, 8, 28) == 668, "Queue footprint incorrect");
        Require(QueueLayoutPolicy.TotalHeight(152, 0, 3, 152, 8, 8, 28) == 152, "Empty queue spacing added");
        return Task.CompletedTask;
    });
    await Check("Tall hidden plugins cannot inflate queue or push its pager off screen", () => {
        var height = QueueLayoutPolicy.CardHeight(355, 760, 104, 8, 3, 8, 6, 28);
        Require(height == 180, "Tall notification inflated all queue slots");
        Require(QueueLayoutPolicy.TotalHeight(104, 8, 3, height, 8, 6, 28) <= 760, "Pager outside viewport");
        var small = QueueLayoutPolicy.CardHeight(355, 440, 104, 8, 3, 8, 6, 28);
        Require(QueueLayoutPolicy.TotalHeight(104, 8, 3, small, 8, 6, 28) <= 440, "Small display overflow");
        Require(QueueLayoutPolicy.CardHeight(140, 760, 104, 8, 3, 8, 6, 28) == 140, "Normal plugin grew");
        return Task.CompletedTask;
    });

    // ---- 2.6 增量能力的兼容性 ----

    await Check("An SDK-2.0-style surface implementation still compiles and keeps old semantics", () => {
        // 这个类只实现 2.0 时代的那 10 个成员：接口新增成员用的是默认实现，
        // 因此既有的第三方实现（以及它们的测试替身）不需要改一行代码就能继续工作。
        // 注意必须通过**接口**调用：默认实现只在接口引用上可见（C# 默认接口成员的规则）。
        IIslandSurface surface = new LegacySurface();
        surface.SetContent(new IslandLiveContent { Priority = 5 });
        Require(((LegacySurface)surface).ContentCallCount == 1, "Legacy SetContent broken");
        Require(((LegacySurface)surface).LastContent?.Priority == 5, "Legacy content lost");

        // 新成员走默认实现 → 转发到单实例那条；实例 id 本身被忽略（老宿主/替身认不出它）
        surface.SetContent("city-beijing", new IslandLiveContent { Priority = 9 });
        Require(((LegacySurface)surface).ContentCallCount == 2, "Default SetContent overload did not forward");
        Require(((LegacySurface)surface).LastContent?.Priority == 9, "Instance content lost");

        surface.RemoveContent("city-beijing");
        Require(((LegacySurface)surface).LastContent == null, "Default RemoveContent did not clear");
        return Task.CompletedTask;
    });

    await Check("Plugin bus delivers per topic and drops revoked subscriptions", () => {
        var bus = new PluginBus();
        var scope = new PluginScope("plugin-a");
        var subscriber = bus.For("plugin-a", scope);
        var publisher = bus.For("plugin-b", null);

        var received = new List<string>();
        subscriber.Subscribe("weather.changed", message =>
            received.Add(message.PublisherId + ":" + (string?)message.Payload));
        subscriber.Subscribe("other.topic", _ => received.Add("wrong topic"));

        publisher.Publish("weather.changed", "beijing");
        Require(received.Count == 1 && received[0] == "plugin-b:beijing", "Publish routing broken");

        // 停用插件：作用域撤销后，它的订阅必须再也收不到任何东西
        scope.Revoke();
        publisher.Publish("weather.changed", "shanghai");
        Require(received.Count == 1, "Revoked subscriber still received a message");
        return Task.CompletedTask;
    });

    await Check("Throwing subscriber cannot break the publisher or later subscribers", () => {
        var bus = new PluginBus();
        var first = bus.For("bad", new PluginScope("bad"));
        var second = bus.For("good", new PluginScope("good"));
        int delivered = 0;

        first.Subscribe("topic", _ => throw new Exception("subscriber failure"));
        second.Subscribe("topic", _ => delivered++);

        bus.For("publisher", null).Publish("topic", null);
        Require(delivered == 1, "Exception in one subscriber blocked another");
        return Task.CompletedTask;
    });

    await Check("Quiet mode expires and reports remaining time", async () => {
        using var store = new SettingsService(Dir("quiet"));
        Require(!QuietMode.IsActive(store), "Quiet active by default");

        QuietMode.Snooze(store, TimeSpan.FromMinutes(30));
        Require(QuietMode.IsActive(store), "Snooze did not take effect");
        Require(QuietMode.Remaining(store) > TimeSpan.FromMinutes(29), "Remaining time wrong");

        QuietMode.End(store);
        Require(!QuietMode.IsActive(store), "End did not clear snooze");
        Require(QuietMode.DescribeRemaining(store) == "未启用", "Ended snooze still described as active");

        // 已经过去的时间戳同样算"未启用"（不用删键）
        store.Set(QuietMode.UntilKey, DateTimeOffset.UnixEpoch.ToString("O"));
        Require(!QuietMode.IsActive(store), "Expired timestamp treated as active");
        await store.FlushAsync();
    });

    await Check("Message history keeps the newest entries and round-trips", async () => {
        var dir = Dir("history");
        var history = new MessageHistory(dir);
        for (int i = 0; i < MessageHistory.Capacity + 25; i++)
        {
            history.Add("title " + i, "text " + i, i % 2 == 0 ? "media" : null, null);
        }

        var recent = history.Recent;
        Require(recent.Count == MessageHistory.Capacity, "Capacity not enforced");
        Require(recent[0].Title == "title " + (MessageHistory.Capacity + 24), "Newest entry not first");

        history.Flush();
        var reloaded = new MessageHistory(dir);
        Require(reloaded.Recent.Count == MessageHistory.Capacity, "History did not persist");

        reloaded.Clear();
        Require(reloaded.Recent.Count == 0, "Clear failed");
        await Task.CompletedTask;
    });

    await Check("Startup guard escalates to safe mode after repeated unhealthy starts", () => {
        var dir = Dir("startup");
        // 模拟「启动后立刻崩溃」：每一次都不 Dispose、也不 MarkHealthy（Dispose 本身会标记健康）
        for (int i = 1; i <= StartupGuard.FailureThreshold; i++)
        {
            var guard = StartupGuard.Record(new MutedLogger(), dir);
            Require(guard.Attempt == i, "Attempt counter drifted at " + i);
            Require(guard.SafeModeRequested == (i >= StartupGuard.FailureThreshold),
                "Safe mode threshold wrong at attempt " + i);
        }

        // 一次健康启动（标记 + 正常退出）之后计数归零
        var healthy = StartupGuard.Record(new MutedLogger(), dir);
        Require(healthy.Attempt == StartupGuard.FailureThreshold + 1, "Attempt not incremented");
        healthy.Dispose();

        var next = StartupGuard.Record(new MutedLogger(), dir);
        Require(next.Attempt == 1, "Healthy start did not reset the counter");
        Require(!next.SafeModeRequested, "Safe mode stuck after healthy start");
        next.Dispose();
        return Task.CompletedTask;
    });

    await Check("Command line and URI parse to the same request", () => {
        var notify = CommandLine.Parse(new[] { "--notify", "构建完成", "耗时 1m20s" });
        Require(notify?.Kind == CommandKind.Notify, "Notify not parsed");
        Require(notify!.Title == "构建完成" && notify.Text == "耗时 1m20s", "Notify text wrong");

        var full = CommandLine.Parse(new[]
        {
            "--notify", "下载完成", "--glyph", "E896", "--accent", "#22C55E", "--duration", "6",
        });
        Require(full is not null, "Optional flags rejected the request");
        Require(full!.Glyph == "E896" && full.Accent == "#22C55E", "Optional flags lost");
        Require(full.DurationSeconds == 6, "Duration lost");

        var uri = CommandLine.Parse(new[] { "winisland://notify?title=%E6%9E%84%E5%BB%BA%E5%AE%8C%E6%88%90&text=1m20s" });
        Require(uri?.Kind == CommandKind.Notify, "URI not parsed");
        Require(uri!.Title == "构建完成" && uri.Text == "1m20s", "URI escaping broken");

        Require(CommandLine.Parse(new[] { "winisland://settings?page=plugins" })?.Page == "plugins", "Settings page lost");
        Require(CommandLine.Parse(new[] { "--quiet", "45" })?.QuietMinutes == 45, "Quiet minutes lost");
        Require(CommandLine.Parse(new[] { "--quiet" })?.QuietMinutes == 30, "Default quiet minutes wrong");
        Require(CommandLine.Parse(Array.Empty<string>()) == null, "Plain start treated as a command");
        Require(CommandLine.Parse(new[] { "--duration", "abc" }) == null, "Bogus flags became a command");

        // 同一份请求经过跨进程序列化之后必须等价
        var round = CommandLine.Deserialize(CommandLine.Serialize(full));
        Require(round?.Title == full.Title && round.DurationSeconds == full.DurationSeconds, "Serialize round-trip broke");
        return Task.CompletedTask;
    });

    await Check("Island content keys keep single-instance plugins byte-identical", () => {
        // 单实例：key 就是插件 id 本身（与 2.5 及以前完全相同）
        Require(IslandWindowKeys.LiveKey("media", null) == "media", "Single-instance key changed");
        Require(IslandWindowKeys.LiveKey("media", "") == "media", "Empty instance changed the key");

        // 多实例：带实例后缀，且绝不可能与单实例那条撞车
        Require(IslandWindowKeys.LiveKey("weather", "beijing") != "weather", "Instance key collided with plugin id");
        Require(IslandWindowKeys.LiveKey("weather", "beijing") != IslandWindowKeys.LiveKey("weather", "shanghai"),
            "Two instances share one key");

        // 插件 id 必须能从 key 里还原出来（宿主优先级设置键用插件 id，不能带实例后缀）
        Require(IslandWindowKeys.OwnerOfKey(IslandWindowKeys.LiveKey("weather", "beijing")) == "weather",
            "Owner not recoverable from instance key");
        Require(IslandWindowKeys.OwnerOfKey("media") == "media", "Plain owner broken");
        return Task.CompletedTask;
    });

    await Check("Monitor selection follows cursor, honours a pin, and never collapses to primary", () => {
        // 固定布局：主屏在右（1440x900 @0,0），副屏竖屏在左（1080x1920 @-1080,0）。
        // 这块逻辑曾经因为 DisplayArea.FindAll() 抛异常而全部退化到主屏 —— 多屏用户的岛
        // 永远赖在主屏，设置页也永远写「当前只检测到一块显示器」。
        var main = new MonitorInfo(@"\\.\DISPLAY1", true, 0, 0, 1440, 900, 0, 0, 1440, 860);
        var side = new MonitorInfo(@"\\.\DISPLAY2", false, -1080, 0, 1080, 1920, -1080, 0, 1080, 1880);
        var monitors = MonitorLayout.Sort(new[] { side, main });
        Require(monitors.Length == 2, "Both monitors must survive sorting");
        Require(monitors[0].Name == main.Name, "Primary must sort first");
        Require(monitors[1].Name == side.Name, "Secondary must follow, ordered by position");

        // 跟随鼠标：负坐标（副屏在左边）必须命中副屏，不能被夹成主屏
        Require(MonitorLayout.Pick(monitors, null, -500, 120, null)?.Name == side.Name,
            "Cursor on the left monitor must select it");
        Require(MonitorLayout.Pick(monitors, null, 700, 400, null)?.Name == main.Name,
            "Cursor on the primary must select it");

        // 指定显示器：即使鼠标在另一块屏上也必须听设置的
        Require(MonitorLayout.Pick(monitors, side.Name, 700, 400, null)?.Name == side.Name,
            "A pin must override the cursor");

        // 指定的屏被拔了：静默退回跟随鼠标，而不是死守主屏
        Require(MonitorLayout.Pick(monitors, @"\\.\DISPLAY9", -500, 120, null)?.Name == side.Name,
            "A stale pin must fall back to the cursor");

        // 用户手写的纯数字也认（\\.\DISPLAYn 的序号）
        Require(MonitorLayout.ByName(monitors, "2")?.Name == side.Name, "Numeric pin not matched");

        // 光标落在两屏之间的死区：取最近的一块，不能返回 null
        Require(MonitorLayout.Nearest(monitors, 2000, 100)?.Name == main.Name, "Dead zone must resolve to nearest");

        // 岛矩形跨在两屏交界处：按重叠面积取，面积大的那块赢
        Require(MonitorLayout.BestFit(monitors, -20, 50, 400, 200)?.Name == main.Name,
            "BestFit must pick the larger overlap");
        Require(MonitorLayout.BestFit(monitors, -1000, 50, 400, 200)?.Name == side.Name,
            "BestFit must pick the left monitor for a rect entirely on it");
        return Task.CompletedTask;
    });

    Console.WriteLine($"{passed} regression scenarios passed.");
}
finally { Directory.Delete(root, true); }

/// <summary>
/// 一个"2.0 时代写法"的 <see cref="IIslandSurface"/> 实现：只实现当年那 10 个成员。
/// 它能编译并通过上面的测试，就是"接口新增成员不会打断既有插件"的直接证据。
/// </summary>
internal sealed class LegacySurface : IIslandSurface
{
    public int ContentCallCount { get; private set; }
    public IslandLiveContent? LastContent { get; private set; }

    public void SetContent(IslandLiveContent? content)
    {
        ContentCallCount++;
        LastContent = content;
    }

    public void ShowMessage(IslandMessage message) { }
    public void Show(Microsoft.UI.Xaml.UIElement content, Windows.Foundation.Size size, TimeSpan? duration = null) { }
    public void DismissTemporary() { }
    public void AddSettingsPage(SettingsPageDescriptor page) { }
    public void RemoveSettingsPage(string pageId) { }
    public void AddDropTarget(IslandDropTarget target) { }
    public void RemoveDropTarget(string targetId) { }
    public void OpenSpotlight(IslandSpotlight spotlight) { }
    public void CloseSpotlight() { }
}

/// <summary>测试里不需要日志噪音。</summary>
internal sealed class MutedLogger : IPluginLogger
{
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
    public void Error(string message, Exception exception) { }
}
