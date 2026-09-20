using System.Reflection;
using System.Runtime.Loader;
using WinIsland.Core;

namespace WinIsland.Core.Plugins;

public sealed class PluginAssemblyContext : AssemblyLoadContext
{
    private static readonly string[] FrameworkPrefixes = { "System", "Windows", "WinRT" };

    private static readonly HashSet<string> FrameworkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        IslandSdk.ContractAssemblyName,
        "mscorlib",
        "netstandard",
    };

    private readonly AssemblyDependencyResolver _resolver;
    private readonly Dictionary<string, bool> _hostProvided = new(StringComparer.OrdinalIgnoreCase);

    public PluginAssemblyContext(string name, string entryDllPath) : base(name, isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(entryDllPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name == null)
        {
            return null;
        }

        // 宿主已提供的程序集（框架程序集，或宿主目录里存在同名文件）一律绑定宿主那份，
        // 避免同一个程序集在宿主与插件里各有一份、类型身份分裂。
        // 其余（插件自带的私有依赖，哪怕叫 Microsoft.*）都从插件目录解析。
        if (IsHostProvided(name))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }

    private bool IsHostProvided(string name)
    {
        if (FrameworkNames.Contains(name))
        {
            return true;
        }

        foreach (var prefix in FrameworkPrefixes)
        {
            if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        lock (_hostProvided)
        {
            if (_hostProvided.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var provided = File.Exists(Path.Combine(AppContext.BaseDirectory, name + ".dll"));
            _hostProvided[name] = provided;
            return provided;
        }
    }
}
