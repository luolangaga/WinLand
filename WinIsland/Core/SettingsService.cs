using System.Text.Json;

namespace WinIsland.Core;

public sealed class SettingsService : ISettingsStore
{
    private readonly string _path;
    private readonly Dictionary<string, JsonElement> _data = new();
    private readonly object _lock = new();
    private readonly object _eventGate = new();
    private Action<string>? _changed;

    public SettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        Load();
    }

    public event Action<string>? Changed
    {
        add
        {
            lock (_eventGate)
            {
                _changed += value;
            }
        }
        remove
        {
            lock (_eventGate)
            {
                _changed -= value;
            }
        }
    }

    public T Get<T>(string key, T defaultValue)
    {
        lock (_lock)
        {
            if (_data.TryGetValue(key, out var element))
            {
                try
                {
                    return element.Deserialize<T>() ?? defaultValue;
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
        RaiseChanged(key);
    }

    public void Remove(string key)
    {
        bool removed;
        lock (_lock)
        {
            removed = _data.Remove(key);
        }

        if (!removed)
        {
            return;
        }

        Save();
        RaiseChanged(key);
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
        }
    }

    private void RaiseChanged(string key)
    {
        Action<string>? handler;
        lock (_eventGate)
        {
            handler = _changed;
        }

        handler?.Invoke(key);
    }
}
