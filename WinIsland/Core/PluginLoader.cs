using System.Reflection;
using System.Runtime.Loader;
using WinIsland.Core;

namespace WinIsland.Core;

public sealed class PluginLoader : IDisposable
{
    private readonly IDynamicIslandApi _api;
    private readonly ISettingsStore _settings;
    private readonly List<PluginInfo> _plugins = new();
    private readonly List<AssemblyLoadContext> _contexts = new();
    private readonly string _pluginsDir;

    public IReadOnlyList<PluginInfo> Plugins => _plugins;
    public event Action? PluginsChanged;

    public PluginLoader(IDynamicIslandApi api, ISettingsStore settings)
    {
        _api = api;
        _settings = settings;
        _pluginsDir = PluginConstants.GetPluginDirectory(AppContext.BaseDirectory);
    }

    public async Task LoadBuiltInModulesAsync()
    {
        foreach (var plugin in _plugins.Where(p => p.IsBuiltIn && !IsDisabled(p.Id)))
        {
            await InitializePluginAsync(plugin);
        }
        PluginsChanged?.Invoke();
    }

    public async Task DiscoverAndLoadAsync()
    {
        if (!Directory.Exists(_pluginsDir))
        {
            Directory.CreateDirectory(_pluginsDir);
            return;
        }

        foreach (var dll in Directory.GetFiles(_pluginsDir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (_plugins.Any(p => p.DllPath == dll)) continue;
            await TryLoadDllAsync(dll, isBuiltIn: false);
        }

        PluginsChanged?.Invoke();
    }

    public async Task<bool> LoadDllAsync(string dllPath)
    {
        if (!File.Exists(dllPath)) return false;

        var destDir = _pluginsDir;
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, Path.GetFileName(dllPath));

        if (File.Exists(destPath) && Path.GetFullPath(destPath) != Path.GetFullPath(dllPath))
        {
            File.Delete(destPath);
        }

        if (Path.GetFullPath(destPath) != Path.GetFullPath(dllPath))
        {
            File.Copy(dllPath, destPath, overwrite: true);
        }

        var result = await TryLoadDllAsync(destPath, isBuiltIn: false);
        PluginsChanged?.Invoke();
        return result;
    }

    public void RegisterBuiltIn(IIslandModule module)
    {
        var info = new PluginInfo
        {
            Id = module.Id,
            DisplayName = module.DisplayName,
            Description = (module as IPluginManifest)?.Description,
            Author = (module as IPluginManifest)?.Author,
            Version = (module as IPluginManifest)?.Version,
            IconGlyph = (module as IPluginManifest)?.IconGlyph ?? "\uE712",
            DllPath = "",
            IsBuiltIn = true,
            State = PluginState.Loaded,
            Module = module,
        };
        _plugins.Add(info);
    }

    public async Task EnablePluginAsync(string pluginId)
    {
        var plugin = _plugins.FirstOrDefault(p => p.Id == pluginId);
        if (plugin == null || plugin.State == PluginState.Initialized) return;

        SetDisabled(pluginId, false);

        if (plugin.State == PluginState.Disabled && plugin.Module != null)
        {
            await InitializePluginAsync(plugin);
        }
        else if (plugin.State == PluginState.Discovered || plugin.State == PluginState.Error)
        {
            if (!string.IsNullOrEmpty(plugin.DllPath))
            {
                await TryLoadDllAsync(plugin.DllPath, plugin.IsBuiltIn);
            }
            else if (plugin.Module != null)
            {
                await InitializePluginAsync(plugin);
            }
        }

        PluginsChanged?.Invoke();
    }

    public async Task DisablePluginAsync(string pluginId)
    {
        var plugin = _plugins.FirstOrDefault(p => p.Id == pluginId);
        if (plugin == null || plugin.State == PluginState.Disabled) return;

        SetDisabled(pluginId, true);

        if (plugin.State == PluginState.Initialized && plugin.Module != null)
        {
            try
            {
                await plugin.Module.ShutdownAsync();
            }
            catch
            {
            }
            _api.SetLiveContent(pluginId, null);
            plugin.State = PluginState.Disabled;
        }
        else
        {
            plugin.State = PluginState.Disabled;
        }

        PluginsChanged?.Invoke();
    }

    public async Task UnloadPluginAsync(string pluginId)
    {
        var plugin = _plugins.FirstOrDefault(p => p.Id == pluginId);
        if (plugin == null || plugin.IsBuiltIn) return;

        if (plugin.Module != null)
        {
            try
            {
                await plugin.Module.ShutdownAsync();
            }
            catch
            {
            }
            _api.SetLiveContent(pluginId, null);
        }

        _plugins.Remove(plugin);
        PluginsChanged?.Invoke();
    }

    public async Task ShutdownAllAsync()
    {
        foreach (var plugin in _plugins.Where(p => p.State == PluginState.Initialized))
        {
            try
            {
                if (plugin.Module != null)
                    await plugin.Module.ShutdownAsync();
            }
            catch
            {
            }
        }
    }

    public bool IsDisabled(string pluginId)
        => _settings.Get($"plugin.{pluginId}.disabled", false);

    private void SetDisabled(string pluginId, bool disabled)
        => _settings.Set($"plugin.{pluginId}.disabled", disabled);

    private async Task<bool> TryLoadDllAsync(string dllPath, bool isBuiltIn)
    {
        try
        {
            var ctx = new AssemblyLoadContext(dllPath, isCollectible: true);
            _contexts.Add(ctx);

            var assembly = ctx.LoadFromAssemblyPath(dllPath);
            var moduleTypes = assembly.GetTypes()
                .Where(t => typeof(IIslandModule).IsAssignableFrom(t)
                            && t.IsClass
                            && !t.IsAbstract
                            && t.GetConstructor(Type.EmptyTypes) != null)
                .ToList();

            if (moduleTypes.Count == 0)
            {
                ctx.Unload();
                _contexts.Remove(ctx);
                return false;
            }

            foreach (var type in moduleTypes)
            {
                var existing = _plugins.FirstOrDefault(p => p.Id != null);
                var module = (IIslandModule)Activator.CreateInstance(type)!;

                var attr = type.GetCustomAttribute<IslandPluginAttribute>();
                var manifest = module as IPluginManifest;

                var info = new PluginInfo
                {
                    Id = module.Id,
                    DisplayName = module.DisplayName,
                    Description = attr?.Description ?? manifest?.Description,
                    Author = attr?.Author ?? manifest?.Author,
                    Version = attr?.Version ?? manifest?.Version,
                    IconGlyph = attr?.IconGlyph ?? manifest?.IconGlyph ?? "\uE712",
                    DllPath = dllPath,
                    IsBuiltIn = isBuiltIn,
                    State = PluginState.Loaded,
                    Module = module,
                };

                _plugins.Add(info);

                if (!IsDisabled(info.Id))
                {
                    await InitializePluginAsync(info);
                }
                else
                {
                    info.State = PluginState.Disabled;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            var info = _plugins.FirstOrDefault(p => p.DllPath == dllPath && p.State == PluginState.Error);
            if (info != null)
            {
                info.ErrorMessage = ex.Message;
            }
            return false;
        }
    }

    private async Task InitializePluginAsync(PluginInfo plugin)
    {
        if (plugin.Module == null) return;

        try
        {
            await plugin.Module.InitializeAsync(_api);
            plugin.State = PluginState.Initialized;
            plugin.ErrorMessage = null;
        }
        catch (Exception ex)
        {
            plugin.State = PluginState.Error;
            plugin.ErrorMessage = ex.Message;
        }
    }

    public void Dispose()
    {
        foreach (var ctx in _contexts)
        {
            try { ctx.Unload(); } catch { }
        }
        _contexts.Clear();
    }
}
