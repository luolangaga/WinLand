using System.Text.Json;

namespace WinIsland.Core;

public sealed class SettingsService : ISettingsStore, IDisposable
{
    private readonly string _path;
    private readonly Dictionary<string, JsonElement> _data = new();
    private readonly object _lock = new();
    private readonly object _eventGate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Timer _saveTimer;
    private readonly Action<string, Exception>? _onError;
    private Action<string>? _changed;
    private long _revision;
    private long _savedRevision;
    private bool _disposed;
    private bool _primaryValid;
    public Exception? LastPersistenceError { get; private set; }

    public SettingsService(string? directory = null, Action<string, Exception>? onError = null)
    {
        _onError = onError;
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        Load();
        _saveTimer = new Timer(_ => _ = SaveAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action<string>? Changed
    {
        add { lock (_eventGate) _changed += value; }
        remove { lock (_eventGate) _changed -= value; }
    }

    public T Get<T>(string key, T defaultValue)
    {
        lock (_lock)
        {
            if (_data.TryGetValue(key, out var element))
            {
                try { return element.Deserialize<T>() ?? defaultValue; }
                catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
            }
        }
        return defaultValue;
    }

    public void Set<T>(string key, T value)
    {
        var element = JsonSerializer.SerializeToElement(value);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_data.TryGetValue(key, out var previous) && previous.GetRawText() == element.GetRawText()) return;
            _data[key] = element;
            _revision++;
            _saveTimer.Change(150, Timeout.Infinite);
        }
        RaiseChanged(key);
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_data.Remove(key)) return;
            _revision++;
            _saveTimer.Change(150, Timeout.Infinite);
        }
        RaiseChanged(key);
    }

    private void Load()
    {
        foreach (var path in new[] { _path, _path + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))
                    ?? throw new JsonException("设置文件内容为空。");
                foreach (var pair in data) _data[pair.Key] = pair.Value;
                _primaryValid = path == _path;
                return;
            }
            catch (Exception ex)
            {
                Report($"读取设置失败：{path}", ex);
                // 保留损坏文件供诊断，避免下一次写入把它当作正常备份。
                if (path == _path)
                {
                    try { File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N")); }
                    catch (Exception moveError) { Report("保留损坏设置失败", moveError); }
                }
            }
        }
    }

    public Task FlushAsync() => SaveAsync();

    private async Task SaveAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        string? temporary = null;
        try
        {
            Dictionary<string, JsonElement> snapshot;
            long revision;
            lock (_lock)
            {
                if (_revision == _savedRevision) return;
                revision = _revision;
                snapshot = new(_data);
            }
            temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot,
                    new JsonSerializerOptions { WriteIndented = true }).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            if (File.Exists(_path)) File.Replace(temporary, _path, _primaryValid ? _path + ".bak" : null);
            else File.Move(temporary, _path);
            _primaryValid = true;
            lock (_lock) _savedRevision = revision;
            LastPersistenceError = null;
        }
        catch (Exception ex) { Report("保存设置失败", ex); }
        finally
        {
            if (temporary != null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) { Report("清理设置临时文件失败", ex); }
            }
            _saveGate.Release();
        }
    }

    private void Report(string message, Exception exception, bool persistence = true)
    {
        if (persistence) LastPersistenceError = exception;
        System.Diagnostics.Trace.TraceError($"{message}: {exception}");
        try { _onError?.Invoke(message, exception); }
        catch { /* Diagnostics must not interrupt persistence. */ }
    }

    private void RaiseChanged(string key)
    {
        Action<string>? handler;
        lock (_eventGate) handler = _changed;
        if (handler == null) return;
        foreach (Action<string> subscriber in handler.GetInvocationList())
        {
            try { subscriber(key); }
            catch (Exception ex) { Report($"设置通知失败：{key}", ex, persistence: false); }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _saveTimer.Dispose();
        }
    }
}
