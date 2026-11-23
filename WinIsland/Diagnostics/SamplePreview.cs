#if DEBUG
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using WinIsland.Core;

namespace WinIsland.Diagnostics;

/// <summary>Loads already-built sample views only. No plugin initialization, scanning, network or clipboard writes.</summary>
internal static class SamplePreview
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static bool Available => Directory.Exists(Root());
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "WinIsland.Core")))
                return Path.Combine(directory.FullName, "artifacts", "motion-preview", "plugins");
            directory = directory.Parent;
        }
        return "";
    }

    internal static (UIElement View, Size Size) Create(string name, IIslandTheme theme)
    {
        string id = name switch { "HardwareMonitor" => "hw-monitor", "DeviceIsland" => "device-island",
            "ClipboardIsland" => "clipboard-island", _ => "weather-island" };
        var assembly = Assembly.LoadFrom(Path.Combine(Root(), id, name + ".dll"));
        Type Type(string suffix) => assembly.GetType(name + "." + suffix, throwOnError: true)!;
        object New(string suffix, params object?[] arguments) => Activator.CreateInstance(Type(suffix), Members, null, arguments, null)!;
        void Apply(object target, string method, params object?[] arguments) => target.GetType().GetMethod(method, Members)!.Invoke(target, arguments);
        object Model(string suffix, params (string Key, object? Value)[] properties)
        {
            var model = New(suffix);
            foreach (var (key, value) in properties) model.GetType().GetProperty(key)!.SetValue(model, value);
            return model;
        }
        Array Items(string suffix, params object[] items)
        {
            var array = Array.CreateInstance(Type(suffix), items.Length);
            for (int i = 0; i < items.Length; i++) array.SetValue(items[i], i);
            return array;
        }
        object view;
        Size size;
        if (name == "HardwareMonitor")
        {
            view = New("HardwareMonitorView", theme);
            Apply(view, "Apply", New("HardwareSnapshot", "Intel Core Ultra", 24d, "NVIDIA GeForce", 48d,
                "Wi-Fi", 245760d, 12288d, "前台应用", 120d, "正在采样", false), New("CompactOptions", true, true, true, true));
            size = new Size(420, 152);
        }
        else if (name == "WeatherIsland")
        {
            view = New("WeatherIslandView", theme);
            var days = Items("DailyForecast", New("DailyForecast", "今天", "周五", 0, 25d, 18d, 5, 3d, "06:00", "18:00", 3d, 0d),
                New("DailyForecast", "明天", "周六", 2, 24d, 17d, 10, 2d, "06:01", "17:59", 3d, 0d),
                New("DailyForecast", "后天", "周日", 61, 22d, 16d, 60, 1d, "06:02", "17:58", 5d, 4d));
            Apply(view, "Apply", Model("WeatherSnapshot", ("HasData", true), ("Place", "上海 · 预览数据"),
                ("Temperature", 23d), ("Code", 0), ("Days", days), ("UpdatedAt", DateTimeOffset.Now)));
            size = new Size(400, 152);
        }
        else if (name == "DeviceIsland")
        {
            var actionType = typeof(Action<>).MakeGenericType(Type("DeviceInfo"));
            var noop = Expression.Lambda(actionType, Expression.Empty(), Expression.Parameter(Type("DeviceInfo"))).Compile();
            view = New("DeviceSpotlightView", new PluginManifest { Id = "preview.device", Version = "1.0.0", Name = "设备" }, noop, noop, theme);
            Apply(view, "Apply", Items("DeviceInfo",
                Model("DeviceInfo", ("Name", "便携工作盘"), ("Detail", "E: · 256 GB"), ("CanEject", true)),
                Model("DeviceInfo", ("Name", "Studio Wireless Headphones"), ("Detail", "蓝牙音频设备"), ("BatteryPercent", 82)),
                Model("DeviceInfo", ("Name", "Xbox Wireless Controller"), ("Detail", "游戏控制器"), ("BatteryPercent", 64))));
            size = new Size(620, 380);
        }
        else
        {
            view = New("ClipboardSpotlightView", new PluginManifest { Id = "preview.clipboard", Version = "1.0.0", Name = "剪贴板" }, null, null, null, theme);
            Apply(view, "Refresh", Items("ClipItem",
                Model("ClipItem", ("Text", "设计评审：把信息层级和间距整理清楚。"), ("CapturedAt", DateTimeOffset.Now)),
                Model("ClipItem", ("Text", "https://github.com/luolan"), ("CapturedAt", DateTimeOffset.Now.AddMinutes(-3))),
                Model("ClipItem", ("Text", "今天的待办：检查动画、验证快速切换、整理测试结果。"), ("CapturedAt", DateTimeOffset.Now.AddMinutes(-8)))), false);
            size = new Size(650, 380);
        }
        return ((UIElement)view, size);
    }
}
#endif
