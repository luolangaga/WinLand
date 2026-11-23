using System.Text.Json;

namespace WinIsland.Core;

/// <summary>
/// 临时消息历史：岛上的消息 4 秒就消失，事后想复习（"刚才那条是谁发的？"）就无从查起。
/// 这里在内存里留最近 <see cref="Capacity"/> 条并落盘到 <c>%LocalAppData%\WinIsland\messages.json</c>，
/// 设置页可以翻看、清空。
///
/// 只记宿主与插件发过的**文本**（标题/正文/来源/时间），不保存任何 UIElement —— 那是插件的可视树，
/// 既不能跨窗口复用，也不该被宿主长期持有。
/// </summary>
public sealed class MessageHistory
{
    public const int Capacity = 200;
    public const string FileName = "messages.json";

    private readonly List<MessageHistoryEntry> _entries = new();
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Action<string, Exception>? _onError;
    private DateTime _lastWrite = DateTime.MinValue;

    public MessageHistory(string? directory = null, Action<string, Exception>? onError = null)
    {
        _onError = onError;
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland");
        _path = Path.Combine(dir, FileName);
        Load();
    }

    /// <summary>最近一条在最前。</summary>
    public IReadOnlyList<MessageHistoryEntry> Recent
    {
        get
        {
            lock (_gate) return _entries.ToArray().Reverse().ToArray();
        }
    }

    public void Add(string title, string? text, string? owner, string? glyph)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(text)) return;

        var entry = new MessageHistoryEntry
        {
            Title = title ?? string.Empty,
            Text = text,
            Owner = string.IsNullOrWhiteSpace(owner) ? null : owner,
            Glyph = glyph,
            At = DateTimeOffset.Now,
        };

        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
        }

        // 消息可能一秒来好几条（插件批量推送）：落盘节流到 2 秒一次，退出时由 App 再 flush 一次
        if (DateTime.UtcNow - _lastWrite < TimeSpan.FromSeconds(2)) return;
        Save();
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Save();
    }

    /// <summary>立刻落盘（退出前调用）。</summary>
    public void Flush() => Save();

    private void Save()
    {
        if (_path == null) return;
        _lastWrite = DateTime.UtcNow;

        MessageHistoryEntry[] snapshot;
        lock (_gate) snapshot = _entries.ToArray();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _onError?.Invoke("保存消息历史失败", ex);
        }
    }

    private void Load()
    {
        if (_path == null || !File.Exists(_path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<MessageHistoryEntry>>(File.ReadAllText(_path), JsonOptions);
            if (loaded == null) return;
            lock (_gate)
            {
                _entries.Clear();
                _entries.AddRange(loaded.TakeLast(Capacity));
            }
        }
        catch (Exception ex)
        {
            _onError?.Invoke("读取消息历史失败", ex);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}

/// <summary>一条历史消息（纯文本，可安全序列化）。</summary>
public sealed class MessageHistoryEntry
{
    public string Title { get; set; } = string.Empty;
    public string? Text { get; set; }

    /// <summary>来源插件 id；宿主自己的消息为 null。</summary>
    public string? Owner { get; set; }

    public string? Glyph { get; set; }
    public DateTimeOffset At { get; set; }
}
