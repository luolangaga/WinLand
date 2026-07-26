using System.Text.Json;

namespace WinIsland.Core;

/// <summary>
/// JSON 键值设置存储，保存于 %LocalAppData%\WinIsland\settings.json。
/// </summary>
public sealed class SettingsService : ISettingsStore
{
    private readonly string _path;
    private readonly Dictionary<string, JsonElement> _data = new();
    private readonly object _lock = new();

    public event Action<string>? Changed;

    public SettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (doc == null) return;
            foreach (var kv in doc) _data[kv.Key] = kv.Value;
        }
        catch
        {
            // 配置损坏时忽略，使用默认值
        }
    }

    private void Save()
    {
        try
        {
            lock (_lock)
            {
                File.WriteAllText(_path,
                    JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch
        {
            // 写盘失败不影响运行
        }
    }

    public T Get<T>(string key, T defaultValue)
    {
        lock (_lock)
        {
            if (_data.TryGetValue(key, out var el))
            {
                try
                {
                    return el.Deserialize<T>() ?? defaultValue;
                }
                catch
                {
                    return defaultValue;
                }
            }
        }
        return defaultValue;
    }

    public void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            _data[key] = JsonSerializer.SerializeToElement(value);
        }
        Save();
        Changed?.Invoke(key);
    }
}
