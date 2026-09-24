using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;

namespace WinIsland.Core.Plugins;

public sealed class PluginInstance
{
    public const int InitTimeoutSeconds = 10;
    public const int ShutdownTimeoutSeconds = 5;
    public const int MaxErrorsBeforeDisable = 5;

    private readonly PluginHost _host;
    private readonly Func<IIslandPlugin>? _builtInFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IIslandPlugin? _plugin;
    private PluginAssemblyContext? _assemblyContext;
    private Assembly? _assembly;
    private PluginScope? _scope;
    private PluginTheme? _theme;
    private int _errorCount;
    private int _ignoredCallLogs;

    internal PluginInstance(PluginHost host, PluginInfo info, Func<IIslandPlugin>? builtInFactory)
    {
        _host = host;
        Info = info;
        _builtInFactory = builtInFactory;
        Log = host.Logs.GetLogger(info.Id);
    }

    public PluginInfo Info { get; }

    public IPluginLogger Log { get; }

    public PluginScope? Scope => _scope;

    internal IslandService Island => _host.Island;

    internal SettingsService Settings => _host.Settings;

    internal DispatcherQueue Dispatcher => _host.Dispatcher;

    internal void RunOnUI(Action action) => UiDispatch.Run(Dispatcher, action);

    internal Task RunOnUIAsync(Func<Task> action) => UiDispatch.RunAsync(Dispatcher, action);

    public async Task ActivateAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await UiDispatch.RunAsync(Dispatcher, ActivateCoreAsync);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeactivateAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await UiDispatch.RunAsync(Dispatcher, DeactivateCoreAsync);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> UnloadAssemblyAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return await UiDispatch.Invoke(Dispatcher, UnloadAssemblyCore);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void InvokeGuarded(string phase, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            RecordError(phase, ex);
        }
    }

    /// <summary>
    /// 异步版守卫：插件回调可能 await（例如投放动作要读文件）。语义与 <see cref="InvokeGuarded"/> 一致 ——
    /// 异常只记日志并计入自动停用，宿主不受影响。
    /// </summary>
    public async Task<T?> InvokeGuardedAsync<T>(string phase, Func<Task<T?>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            RecordError(phase, ex);
            return default;
        }
    }

    public UIElement GuardPageFactory(SettingsPageDescriptor page)
    {
        try
        {
            return page.Factory();
        }
        catch (Exception ex)
        {
            RecordError($"设置页（{page.Id}）", ex);
            return BuildErrorView($"设置页加载失败：{ex.Message}", ex);
        }
    }

    internal void SetDisabledState()
    {
        if (Info.State is PluginState.Discovered or PluginState.Loaded)
        {
            Info.State = PluginState.Disabled;
        }
    }

    /// <summary>岛体生效主题可能变了（UI 线程，由 <see cref="PluginHost"/> 广播）：让这个插件的主题对象自己核算。</summary>
    internal void ApplyTheme() => _theme?.Apply();

    internal void Reset(PluginManifest manifest)
    {
        Info.ApplyManifest(manifest);
        Info.State = PluginState.Discovered;
        Info.LastError = null;
        _errorCount = 0;
    }

    internal void Fault(string phase, string message, Exception? exception = null)
    {
        Info.State = PluginState.Faulted;
        Info.LastError = new PluginError
        {
            Message = message,
            Phase = phase,
            ExceptionType = exception?.GetType().Name,
        };

        if (exception != null)
        {
            Log.Error($"[{phase}] {message}", exception);
        }
        else
        {
            Log.Error($"[{phase}] {message}");
        }
    }

    internal bool TryEnter(string operation)
    {
        // Loaded = 正在执行 InitializeAsync，此时注册合法；Active = 正常运行期。
        // 其余状态（已发现/已禁用/错误/卸载中）或作用域已撤销时，一律拒绝，避免停用后的幽灵调用。
        if (Info.State is PluginState.Loaded or PluginState.Active && _scope is { IsRevoked: false })
        {
            return true;
        }

        if (_ignoredCallLogs < 3)
        {
            _ignoredCallLogs++;
            var suffix = _ignoredCallLogs == 3 ? "（后续同类调用日志已省略）" : string.Empty;
            Log.Warn($"已忽略调用 {operation}：插件当前状态为 {PluginStateText.Describe(Info.State)}{suffix}。");
        }

        return false;
    }

    internal void DisposeSync()
    {
        RevokeScope();
        UnloadAssemblyCore();
    }

    private async Task ActivateCoreAsync()
    {
        if (Info.State == PluginState.Active)
        {
            return;
        }

        if (Info.Manifest == null)
        {
            Log.Error("插件清单缺失，无法启用。");
            return;
        }

        if (!Info.IsBuiltIn && _builtInFactory == null && !File.Exists(Path.Combine(Info.Source, Info.Manifest.EntryDll)))
        {
            Fault("加载", $"入口程序集不存在：{Info.Manifest.EntryDll}");
            return;
        }

        _errorCount = 0;
        _ignoredCallLogs = 0;
        Info.LastError = null;

        if (!await EnsureLoadedAsync())
        {
            return;
        }

        var scope = new PluginScope(Info.Id, ex => Log.Error("插件注册项释放失败", ex));
        _scope = scope;
        var directory = Info.IsBuiltIn ? AppContext.BaseDirectory : Info.Source;
        // 主题是每个插件一份的（要按插件守卫与编组），随作用域生灭
        var theme = new PluginTheme(this, scope);
        _theme = theme;
        var context = new PluginContext(this, scope, Info.Manifest, directory, theme);
        Info.State = PluginState.Loaded;
        Log.Info("正在初始化…");

        try
        {
            var initialize = _plugin!.InitializeAsync(context);
            if (await Task.WhenAny(initialize, Task.Delay(TimeSpan.FromSeconds(InitTimeoutSeconds))) != initialize)
            {
                ObserveLater(initialize, "InitializeAsync");
                _scope = null;
                scope.Revoke();
                Fault("初始化", $"初始化超时（超过 {InitTimeoutSeconds} 秒），已中止启用该插件。");
                return;
            }

            await initialize;
            Info.State = PluginState.Active;
            Info.LastError = null;
            Log.Info("插件已启用。");
        }
        catch (Exception ex)
        {
            _scope = null;
            scope.Revoke();
            Fault("初始化", $"初始化失败：{DescribeLoadError(ex)}", ex);
        }
    }

    private async Task DeactivateCoreAsync()
    {
        if (_plugin != null && Info.State == PluginState.Active)
        {
            try
            {
                var shutdown = _plugin.ShutdownAsync();
                if (await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(ShutdownTimeoutSeconds))) != shutdown)
                {
                    ObserveLater(shutdown, "ShutdownAsync");
                    Log.Warn($"ShutdownAsync 超时（超过 {ShutdownTimeoutSeconds} 秒），已继续停用流程。");
                }
                else
                {
                    await shutdown;
                }
            }
            catch (Exception ex)
            {
                Log.Error("ShutdownAsync 抛出未处理异常", ex);
            }
        }

        RevokeScope();
        _plugin = null;

        if (Info.State is PluginState.Active or PluginState.Loaded or PluginState.Unloading)
        {
            Info.State = PluginState.Disabled;
            Log.Info("插件已停用。");
        }
    }

    private async Task<bool> EnsureLoadedAsync()
    {
        if (_plugin != null)
        {
            return true;
        }

        if (Info.IsBuiltIn)
        {
            if (_builtInFactory == null)
            {
                Fault("加载", "内置插件实例工厂缺失。");
                return false;
            }

            try
            {
                _plugin = _builtInFactory();
                Info.State = PluginState.Loaded;
                return true;
            }
            catch (Exception ex)
            {
                Fault("加载", $"创建内置插件实例失败：{ex.Message}", ex);
                return false;
            }
        }

        if (_assembly != null)
        {
            return CreateInstance(_assembly);
        }

        var entryPath = Path.Combine(Info.Source, Info.Manifest!.EntryDll);
        try
        {
            var (context, assembly) = await Task.Run(() =>
            {
                var loadContext = new PluginAssemblyContext($"island-plugin:{Info.Id}", entryPath);
                try
                {
                    return (loadContext, loadContext.LoadFromAssemblyPath(entryPath));
                }
                catch
                {
                    loadContext.Unload();
                    throw;
                }
            });

            _assemblyContext = context;
            _assembly = assembly;

            var reference = assembly.GetReferencedAssemblies()
                .FirstOrDefault(a => string.Equals(a.Name, IslandSdk.ContractAssemblyName, StringComparison.OrdinalIgnoreCase));

            if (reference?.Version == null)
            {
                Fault("加载", $"该程序集没有引用 {IslandSdk.ContractAssemblyName}，无法作为插件运行。");
                return false;
            }

            if (reference.Version.Major != IslandSdk.HostVersion.Major)
            {
                Fault("加载",
                    $"插件针对 {IslandSdk.ContractAssemblyName} {reference.Version} 构建，与宿主 {IslandSdk.HostVersion} 不兼容。请使用 SDK {IslandSdk.HostVersion.Major}.x 重新编译。");
                return false;
            }

            Log.Info($"已加载程序集 {Path.GetFileName(entryPath)}（引用 Core {reference.Version}）。");
            return CreateInstance(assembly);
        }
        catch (Exception ex)
        {
            Fault("加载", DescribeLoadError(ex), ex);
            return false;
        }
    }

    private bool CreateInstance(Assembly assembly)
    {
        Type[] types;
        ReflectionTypeLoadException? typeLoadError = null;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            typeLoadError = ex;
            types = ex.Types.Where(t => t != null).ToArray()!;
        }

        var candidates = types
            .Where(t => t != null && typeof(IIslandPlugin).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract)
            .ToList();

        if (candidates.Count == 0)
        {
            if (typeLoadError != null)
            {
                Fault("加载", DescribeLoadError(typeLoadError), typeLoadError);
                return false;
            }

            Fault("加载", $"程序集里没有找到 {nameof(IIslandPlugin)} 实现（要求 public、非抽象、带公共无参构造函数）。");
            return false;
        }

        if (candidates.Count > 1)
        {
            Fault("加载",
                $"一个插件包只允许一个 {nameof(IIslandPlugin)} 实现，发现 {candidates.Count} 个：{string.Join("、", candidates.Select(t => t!.FullName))}");
            return false;
        }

        var type = candidates[0]!;
        if (type.GetConstructor(Type.EmptyTypes) == null)
        {
            Fault("加载", $"{type.FullName} 缺少公共无参构造函数。");
            return false;
        }

        try
        {
            _plugin = (IIslandPlugin)Activator.CreateInstance(type)!;
            Info.State = PluginState.Loaded;
            return true;
        }
        catch (Exception ex)
        {
            Fault("加载", $"实例化 {type.FullName} 失败：{DescribeLoadError(ex.InnerException ?? ex)}", ex);
            return false;
        }
    }

    private void RevokeScope()
    {
        var scope = _scope;
        _scope = null;
        _theme = null;
        if (scope == null)
        {
            return;
        }

        try
        {
            scope.Revoke();
        }
        catch (Exception ex)
        {
            Log.Error("撤销插件注册时发生异常", ex);
        }
    }

    private bool UnloadAssemblyCore()
    {
        RevokeScope();
        _plugin = null;
        _assembly = null;
        Info.State = PluginState.Unloading;

        var context = _assemblyContext;
        _assemblyContext = null;
        if (context == null)
        {
            return true;
        }

        var weak = new WeakReference(context, trackResurrection: true);
        UnloadContext(context);
        context = null;

        for (var i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        if (!weak.IsAlive)
        {
            Log.Info("插件程序集已卸载并回收。");
            return true;
        }

        Log.Warn("插件程序集已请求卸载，但未能回收（可能存在残留引用）。如反复出现请重启应用。");
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UnloadContext(AssemblyLoadContext context) => context.Unload();

    private void RecordError(string phase, Exception ex)
    {
        Log.Error($"[{phase}] 未处理异常", ex);
        Info.LastError = new PluginError
        {
            Message = ex.Message,
            Phase = phase,
            ExceptionType = ex.GetType().Name,
        };

        _errorCount++;
        if (Info.State == PluginState.Active && _errorCount >= MaxErrorsBeforeDisable)
        {
            Log.Error($"累计 {_errorCount} 次未处理异常，自动停用该插件。");
            _host.RequestAutoDisable(this);
        }
    }

    private void ObserveLater(Task task, string phase)
    {
        _ = task.ContinueWith(
            t => Log.Error($"{phase} 在超时后仍抛出异常", t.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static string DescribeLoadError(Exception ex) => ex switch
    {
        ReflectionTypeLoadException typeLoad => "加载程序集类型失败：" + string.Join("；",
            typeLoad.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct().Take(3)),
        FileNotFoundException fileNotFound => DescribeMissingAssembly(fileNotFound),
        // 插件静态构造里触发的加载失败会被包一层，拆开才认得出是「缺依赖」还是「版本错配」
        TypeInitializationException { InnerException: { } inner } => DescribeLoadError(inner),
        FileLoadException fileLoad => $"程序集加载失败：{fileLoad.Message}",
        BadImageFormatException => "程序集格式无效（可能不是 .NET 程序集，或目标框架/位数不匹配）。",
        TypeLoadException typeLoad => $"类型加载失败：{typeLoad.Message}",
        _ => ex.Message,
    };

    /// <summary>
    /// 「缺少依赖程序集」有两种成因，必须分开报：插件自己的依赖没复制到插件目录（按 PLUGIN.md §6 补文件即可），
    /// 或者插件构建用的 .NET / Windows SDK 比宿主新 —— 引用了宿主没有的更高版本框架程序集
    /// （Microsoft.Windows.SDK.NET、WinRT.Runtime 这类投影）。.NET 不允许向下绑定强命名程序集，
    /// 只说一句「缺少 xxx」会把人引去查依赖复制，所以这里直接比对宿主目录里那份的版本并把话说清。
    /// </summary>
    private static string DescribeMissingAssembly(FileNotFoundException ex)
    {
        var requested = TryParseAssemblyName(ex.FileName);
        if (requested?.Name is not { } name || requested.Version is not { } wanted)
        {
            return $"缺少依赖程序集：{ex.FileName ?? ex.Message}";
        }

        // 宿主目录里没有同名文件 → 真的是插件自己漏带了依赖；宿主那份版本不低于要求 → 不是版本错配
        var hostPath = Path.Combine(AppContext.BaseDirectory, name + ".dll");
        if (!File.Exists(hostPath)) return $"缺少依赖程序集：{ex.FileName}";

        if (TryGetAssemblyVersion(hostPath) is not { } provided || provided >= wanted)
        {
            return $"缺少依赖程序集：{ex.FileName}";
        }

        return $"插件需要 {name} {wanted}，宿主提供的是 {provided}：" +
               "插件构建用的 .NET / Windows SDK 比宿主新，请用与宿主相同或更旧的 SDK 重新构建插件（见 PLUGIN.md §6）。";
    }

    private static AssemblyName? TryParseAssemblyName(string? displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return null;

        try
        {
            var name = new AssemblyName(displayName);
            return string.IsNullOrEmpty(name.Name) ? null : name;
        }
        catch (Exception)
        {
            // 不是程序集显示名（例如普通文件路径），交给通用文案
            return null;
        }
    }

    /// <summary>只读程序集元数据取版本，不会把它加载进任何 ALC。</summary>
    private static Version? TryGetAssemblyVersion(string path)
    {
        try
        {
            return AssemblyName.GetAssemblyName(path).Version;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static UIElement BuildErrorView(string message, Exception exception)
    {
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
        panel.Children.Add(new FontIcon { Glyph = "\uEA39", FontSize = 28 });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = exception.ToString(),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            FontSize = 12,
        });
        return panel;
    }
}
