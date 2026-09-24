using Microsoft.UI.Dispatching;
using WinIsland.Core;
using WinIsland.Island;

namespace WinIsland.Core.Plugins;

public sealed class PluginHost
{
    private readonly List<PluginInstance> _instances = new();
    private readonly List<(PluginManifest Manifest, Func<IIslandPlugin> Factory)> _builtIns = new();
    private readonly List<string> _discoveryWarnings = new();

    public PluginHost(IslandService island, SettingsService settings, DispatcherQueue dispatcher, PluginLogService? logs = null)
    {
        Island = island;
        Settings = settings;
        Dispatcher = dispatcher;
        Logs = logs ?? new PluginLogService();
        PluginsDirectory = IslandSdk.GetPluginsDirectory(AppContext.BaseDirectory);

        // 岛体生效主题由「外观风格 + 系统明暗」两处决定，两个来源都要盯：
        // 风格切到 Fluent 时即使系统主题没动，岛的明暗也会变，插件得跟着走
        SystemTheme.Changed += OnIslandThemeSourceChanged;
        Settings.Changed += OnSettingChanged;
    }

    public PluginLogService Logs { get; }

    public string PluginsDirectory { get; }

    public IReadOnlyList<PluginInfo> Plugins => _instances.Select(i => i.Info).ToList();

    public IReadOnlyList<string> DiscoveryWarnings => _discoveryWarnings;

    public event Action? PluginsChanged;

    internal IslandService Island { get; }

    internal SettingsService Settings { get; }

    internal DispatcherQueue Dispatcher { get; }

    public void RegisterBuiltIn(PluginManifest manifest, Func<IIslandPlugin> factory)
        => _builtIns.Add((manifest, factory));

    public async Task InitializeAsync()
    {
        await UiDispatch.RunAsync(Dispatcher, async () =>
        {
            RegisterBuiltIns();
            await ScanPackagesAsync();
            await ActivateAllAsync();
            PluginsChanged?.Invoke();
        });
    }

    public Task<bool> SetEnabledAsync(string pluginId, bool enabled) => UiDispatch.InvokeAsync(Dispatcher, async () =>
    {
        var instance = Find(pluginId);
        if (instance == null)
        {
            return false;
        }

        if (enabled)
        {
            Settings.Set(DisabledKey(pluginId), false);
            await instance.ActivateAsync();
            PluginsChanged?.Invoke();
            return instance.Info.State == PluginState.Active;
        }

        Settings.Set(DisabledKey(pluginId), true);
        await instance.DeactivateAsync();
        PluginsChanged?.Invoke();
        return true;
    });

    public Task<bool> ReloadAsync(string pluginId) => UiDispatch.InvokeAsync(Dispatcher, async () =>
    {
        var instance = Find(pluginId);
        if (instance == null)
        {
            return false;
        }

        await instance.DeactivateAsync();
        await instance.UnloadAssemblyAsync();

        if (instance.Info.IsBuiltIn)
        {
            if (instance.Info.Manifest == null)
            {
                return false;
            }

            instance.Reset(instance.Info.Manifest);
        }
        else
        {
            try
            {
                var manifest = PluginManifestReader.Read(instance.Info.Source);
                if (!string.Equals(manifest.Id, instance.Info.Id, StringComparison.OrdinalIgnoreCase))
                {
                    Logs.Host.Info($"插件 Id 已变更：{instance.Info.Id} → {manifest.Id}，按新插件处理。");
                    var source = instance.Info.Source;
                    _instances.Remove(instance);
                    ApplyDiscovered(new DiscoveredPlugin(source, manifest, null));
                    RaiseChanged();
                    instance = Find(manifest.Id);
                    if (instance == null)
                    {
                        return false;
                    }
                }
                else
                {
                    instance.Reset(manifest);
                }
            }
            catch (Exception ex)
            {
                instance?.Fault("重新加载", $"清单校验失败：{ex.Message}", ex);
                PluginsChanged?.Invoke();
                return false;
            }
        }

        Settings.Set(DisabledKey(instance.Info.Id), false);
        await instance.ActivateAsync();
        PluginsChanged?.Invoke();
        return instance.Info.State == PluginState.Active;
    });

    public async Task<InstallResult> InstallAsync(string packagePath)
    {
        PreparedInstall prepared;
        try
        {
            prepared = await Task.Run(() => LwpInstaller.Prepare(packagePath, PluginsDirectory));
        }
        catch (Exception ex)
        {
            Logs.Host.Error($"插件包校验失败：{ex.Message}");
            return InstallResult.Fail(ex.Message);
        }

        var manifest = prepared.Manifest;
        try
        {
            await UiDispatch.RunAsync(Dispatcher, async () =>
            {
                var existing = Find(manifest.Id);
                if (existing != null)
                {
                    await existing.DeactivateAsync();
                    await existing.UnloadAssemblyAsync();
                    _instances.Remove(existing);
                }

                await Task.Run(() => LwpInstaller.Commit(prepared));
                ApplyDiscovered(new DiscoveredPlugin(prepared.TargetDirectory, manifest, null));
                Settings.Set(DisabledKey(manifest.Id), false);
            });

            var created = Find(manifest.Id);
            if (created != null)
            {
                await created.ActivateAsync();
            }

            PluginsChanged?.Invoke();
            Logs.Host.Info(prepared.IsUpdate
                ? $"已更新插件 {manifest.Id}：{prepared.OldVersion} → {manifest.Version}"
                : $"已安装插件 {manifest.Id} {manifest.Version}");
            return InstallResult.Ok(manifest, prepared.IsUpdate, prepared.OldVersion, prepared.Warnings);
        }
        catch (Exception ex)
        {
            LwpInstaller.Cleanup(prepared);
            Logs.Host.Error($"安装插件包失败：{ex.Message}", ex);
            return InstallResult.Fail(ex.Message);
        }
    }

    public Task<bool> UninstallAsync(string pluginId) => UiDispatch.InvokeAsync(Dispatcher, async () =>
    {
        var instance = Find(pluginId);
        if (instance == null || instance.Info.IsBuiltIn)
        {
            return false;
        }

        var directory = instance.Info.Source;
        await instance.DeactivateAsync();
        await instance.UnloadAssemblyAsync();
        _instances.Remove(instance);

        try
        {
            await Task.Run(() =>
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                // 被占用的 DLL 会让"删除整个目录"失败，但改名几乎总能成功；
                // 改成 .uninstalling-* 后插件不会再被发现，残留文件在下次启动时清理。
                var retired = Path.Combine(PluginsDirectory, $".uninstalling-{pluginId}-{Guid.NewGuid():N}"[..Math.Min(80, $".uninstalling-{pluginId}-{Guid.NewGuid():N}".Length)]);
                Directory.Move(directory, retired);
                try
                {
                    Directory.Delete(retired, true);
                }
                catch
                {
                    Logs.Host.Warn($"插件 {pluginId} 的旧目录暂时无法删除（文件被占用），已标记为待清理，将在下次启动时移除。");
                }
            });
        }
        catch (Exception ex)
        {
            Logs.Host.Error($"移除插件目录失败：{ex.Message}");
        }

        Settings.Remove(DisabledKey(pluginId));
        PluginsChanged?.Invoke();
        return true;
    });

    public Task ShutdownAllAsync() => UiDispatch.RunAsync(Dispatcher, async () =>
    {
        foreach (var instance in _instances.ToList())
        {
            await instance.DeactivateAsync();
        }

        foreach (var instance in _instances.ToList())
        {
            await instance.UnloadAssemblyAsync();
        }
    });

    public void Dispose()
    {
        SystemTheme.Changed -= OnIslandThemeSourceChanged;
        Settings.Changed -= OnSettingChanged;

        foreach (var instance in _instances.ToList())
        {
            try
            {
                instance.DisposeSync();
            }
            catch
            {
            }
        }

        _instances.Clear();
    }

    internal void RequestAutoDisable(PluginInstance instance)
    {
        Dispatcher.TryEnqueue(async () =>
        {
            if (instance.Info.State != PluginState.Active)
            {
                return;
            }

            Settings.Set(DisabledKey(instance.Info.Id), true);
            await instance.DeactivateAsync();
            instance.Fault("运行", $"累计 {PluginInstance.MaxErrorsBeforeDisable} 次未处理异常，已自动停用。");
            PluginsChanged?.Invoke();
        });
    }

    private void OnIslandThemeSourceChanged() => DispatchThemeChange();

    private void OnSettingChanged(string key)
    {
        if (string.Equals(key, IslandStyle.StyleKey, StringComparison.Ordinal))
        {
            DispatchThemeChange();
        }
    }

    /// <summary>
    /// 岛体生效主题（外观风格 + 系统主题）可能变了：广播给所有插件，让代码里搭视图的插件换掉配色。
    /// 必须在 UI 线程发 —— 插件会在回调里改 UI（换画刷、重建视图）；<see cref="PluginTheme"/> 自己判断有没有真变。
    /// </summary>
    private void DispatchThemeChange()
    {
        if (!Dispatcher.HasThreadAccess)
        {
            Dispatcher.TryEnqueue(DispatchThemeChange);
            return;
        }

        foreach (var instance in _instances.ToList())
        {
            instance.ApplyTheme();
        }
    }

    private void RegisterBuiltIns()
    {
        foreach (var (manifest, factory) in _builtIns)
        {
            if (_instances.Any(i => string.Equals(i.Info.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)))
            {
                Logs.Host.Warn($"内置插件 Id 重复，已忽略：{manifest.Id}");
                continue;
            }

            var info = new PluginInfo
            {
                Id = manifest.Id,
                Name = manifest.Name,
                IsBuiltIn = true,
                Source = AppContext.BaseDirectory,
            };
            info.ApplyManifest(manifest);
            _instances.Add(new PluginInstance(this, info, factory));
        }
    }

    private async Task ScanPackagesAsync()
    {
        var (legacyDlls, packages, staleDirectories) = await Task.Run(() =>
        {
            Directory.CreateDirectory(PluginsDirectory);
            var stale = new List<string>();
            stale.AddRange(Directory.GetDirectories(PluginsDirectory, ".installing-*", SearchOption.TopDirectoryOnly));
            stale.AddRange(Directory.GetDirectories(PluginsDirectory, ".uninstalling-*", SearchOption.TopDirectoryOnly));
            stale.AddRange(Directory.GetDirectories(PluginsDirectory, "*.uninstalling-*", SearchOption.TopDirectoryOnly));
            return (
                Directory.GetFiles(PluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly),
                Directory.GetFiles(PluginsDirectory, "*" + IslandSdk.PackageExtension, SearchOption.TopDirectoryOnly),
                stale);
        });

        foreach (var stale in staleDirectories)
        {
            try
            {
                Directory.Delete(stale, true);
            }
            catch
            {
            }
        }

        var legacyNames = legacyDlls.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToArray();
        if (legacyNames.Length > 0)
        {
            var message = $"检测到 {legacyNames.Length} 个旧格式 DLL（{string.Join("、", legacyNames)}）。v2 只加载 plugins/<id>/ + plugin.json 或 .lwp 包，请按新规范重新打包。";
            _discoveryWarnings.Add(message);
            Logs.Host.Warn(message);
        }

        foreach (var package in packages)
        {
            var name = Path.GetFileName(package);
            try
            {
                var prepared = await Task.Run(() => LwpInstaller.Prepare(package, PluginsDirectory));
                await Task.Run(() => LwpInstaller.Commit(prepared));
                Logs.Host.Info($"已自动安装插件包 {name} → {prepared.Manifest.Id} {prepared.Manifest.Version}");
                try
                {
                    File.Delete(package);
                }
                catch (Exception ex)
                {
                    _discoveryWarnings.Add($"插件包 {name} 已安装，但删除包文件失败：{ex.Message}");
                }
            }
            catch (Exception ex)
            {
                var message = $"自动安装插件包 {name} 失败：{ex.Message}";
                _discoveryWarnings.Add(message);
                Logs.Host.Error(message);
            }
        }

        var discovered = await Task.Run(DiscoverAll);
        foreach (var entry in discovered)
        {
            ApplyDiscovered(entry);
        }
    }

    private List<DiscoveredPlugin> DiscoverAll()
    {
        var discovered = new List<DiscoveredPlugin>();
        foreach (var directory in Directory.GetDirectories(PluginsDirectory))
        {
            if (Path.GetFileName(directory).StartsWith('.'))
            {
                continue;
            }

            if (!File.Exists(Path.Combine(directory, IslandSdk.ManifestFileName)))
            {
                continue;
            }

            try
            {
                discovered.Add(new DiscoveredPlugin(directory, PluginManifestReader.Read(directory), null));
            }
            catch (Exception ex)
            {
                discovered.Add(new DiscoveredPlugin(directory, null, ex.Message));
            }
        }

        return discovered;
    }

    private void ApplyDiscovered(DiscoveredPlugin discovered)
    {
        if (discovered.Manifest is { } manifest)
        {
            if (_instances.Any(i => string.Equals(i.Info.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)))
            {
                var message = $"插件 Id 重复，已忽略目录 {discovered.Directory}：{manifest.Id}";
                _discoveryWarnings.Add(message);
                Logs.Host.Error(message);
                return;
            }

            var info = new PluginInfo
            {
                Id = manifest.Id,
                Name = manifest.Name,
                IsBuiltIn = false,
                Source = discovered.Directory,
            };
            info.ApplyManifest(manifest);
            _instances.Add(new PluginInstance(this, info, null));
            Logs.GetLogger(manifest.Id).Info($"发现插件 {manifest.Name} {manifest.Version}（{discovered.Directory}）");
            return;
        }

        var name = Path.GetFileName(discovered.Directory);
        var broken = new PluginInfo
        {
            Id = name,
            Name = name,
            IsBuiltIn = false,
            Source = discovered.Directory,
            State = PluginState.Faulted,
            LastError = new PluginError
            {
                Phase = "发现",
                Message = $"清单校验失败：{discovered.Error}",
            },
        };
        _instances.Add(new PluginInstance(this, broken, null));
        Logs.GetLogger(name).Error($"清单校验失败（{discovered.Directory}）：{discovered.Error}");
    }

    private async Task ActivateAllAsync()
    {
        foreach (var instance in _instances.ToList())
        {
            if (instance.Info.Manifest == null || instance.Info.State == PluginState.Faulted)
            {
                continue;
            }

            if (IsDisabled(instance.Info.Id))
            {
                instance.SetDisabledState();
                continue;
            }

            await instance.ActivateAsync();
        }
    }

    private void RaiseChanged()
    {
        PluginsChanged?.Invoke();
    }

    private PluginInstance? Find(string pluginId)
        => _instances.FirstOrDefault(i => string.Equals(i.Info.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    private static string DisabledKey(string pluginId) => $"plugin.{pluginId}.disabled";

    private bool IsDisabled(string pluginId) => Settings.Get(DisabledKey(pluginId), false);

    private sealed record DiscoveredPlugin(string Directory, PluginManifest? Manifest, string? Error);
}
