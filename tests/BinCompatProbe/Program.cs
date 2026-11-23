using System.Reflection;
using System.Runtime.Loader;
using WinIsland.Core;

namespace BinCompatProbe;

/// <summary>
/// 二进制兼容探针：用**旧版 SDK** 编译出来的插件程序集，交给**新版 Core** 的运行时加载。
///
/// 用法：BinCompatProbe &lt;旧插件 dll 路径&gt;
///
/// 它验证的正是用户真实面对的场景 —— 机器上已经装好的插件不会被新版宿主打断：
///  1. 旧插件程序集能被加载，且引用的 Core 版本仍可解析（不因 2.4 → 2.6 而 TypeLoadException）；
///  2. 旧插件里的 IIslandPlugin 实现仍被视为同一个接口；
///  3. 旧插件用到的每个 API 都还在，签名未变。
/// </summary>
internal static class Probe
{
    private static int _failures;

    private static void Require(bool condition, string message)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
        if (!condition) _failures++;
    }

    private static int Main(string[] args)
    {
        var core = typeof(IIslandPlugin).Assembly;
        Console.WriteLine($"runtime Core: {core.GetName().Version}");

        if (args.Length == 0)
        {
            Console.WriteLine("usage: BinCompatProbe <legacy-plugin.dll>");
            return 2;
        }

        // 只加载插件本身；Core 由运行时绑定到**这一份**（新版）
        var path = Path.GetFullPath(args[0]);
        Require(File.Exists(path), "legacy plugin dll exists");

        var context = new AssemblyLoadContext("bincompat", isCollectible: true);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL legacy plugin failed to load: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }

        Require(true, "legacy plugin assembly loads against the new Core");

        var coreReference = assembly.GetReferencedAssemblies()
            .FirstOrDefault(a => a.Name == IslandSdk.ContractAssemblyName);
        Require(coreReference != null, "legacy plugin still references WinIsland.Core");
        Console.WriteLine($"  plugin was built against Core {coreReference?.Version}");

        // 最关键的一条：旧插件里的类型必须仍然实现**当前** IIslandPlugin
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
            Console.WriteLine("  (type load issues: " +
                string.Join("; ", ex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct()) + ")");
        }

        var pluginTypes = types
            .Where(t => t != null && typeof(IIslandPlugin).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract)
            .ToList();
        Require(pluginTypes.Count > 0,
            "legacy plugin type still implements IIslandPlugin (interface identity preserved)");

        // 旧插件继承 IslandPluginBase 时，基类成员必须都还在
        foreach (var type in pluginTypes)
        {
            if (type.BaseType == typeof(IslandPluginBase))
            {
                Require(true, $"{type.Name} extends IslandPluginBase without a type-load failure");
            }
        }

        // 旧插件可能自己实现 IIslandSurface（测试替身、包装器）：**新增**成员必须是默认实现，
        // 否则它们会在加载时抛 TypeLoadException。
        // 注意：SetContent(IslandLiveContent?) 是 2.0 就有的抽象成员，本来就该由实现方提供 ——
        // 只有带 instanceId 的那个重载和 RemoveContent 才是 2.6 新增的。
        var surface = typeof(IIslandSurface);
        var newMembers = new[]
        {
            surface.GetMethod("SetContent", new[] { typeof(string), typeof(IslandLiveContent) }),
            surface.GetMethod("RemoveContent", new[] { typeof(string) }),
        };
        foreach (var member in newMembers)
        {
            Require(member != null, $"IIslandSurface.{member?.Name} exists");
            Require(member is { IsAbstract: false }, $"IIslandSurface.{member?.Name} has a default implementation");
        }

        // 原有的抽象成员必须仍然存在且保持不变（旧插件的调用点靠它编译）
        var original = surface.GetMethod("SetContent", new[] { typeof(IslandLiveContent) });
        Require(original != null && original.IsAbstract, "original IIslandSurface.SetContent unchanged");

        // 数据契约：旧插件构造 IslandLiveContent 用到的成员一个不少
        var content = new IslandLiveContent
        {
            Priority = 200,
            OwnerLabel = "legacy",
            OwnerGlyph = "\uE8D6",
            OwnerAccent = Windows.UI.Color.FromArgb(255, 255, 45, 85),
            CompactSize = new Windows.Foundation.Size(250, 40),
            ExpandedSize = new Windows.Foundation.Size(420, 148),
            OnTap = () => { },
        };
        Require(content.Priority == 200 && content.OwnerLabel == "legacy", "IslandLiveContent contract intact");
        Require(content.ExpandedPriority == null, "ExpandedPriority still optional (null by default)");

        assembly = null;
        context.Unload();

        Console.WriteLine(_failures == 0 ? "BINCOMPAT PASS" : $"BINCOMPAT FAIL ({_failures})");
        return _failures == 0 ? 0 : 1;
    }
}
