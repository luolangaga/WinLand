using System.Text.Encodings.Web;
using System.Text.Json;
using WinIsland.Core;

namespace ClipboardIsland;

/// <summary>
/// 剪贴板历史：内存里一份（最新在最前），同时把文本和图片文件缓存到磁盘，
/// 关掉 WinIsland 再打开历史还在。
///
/// 约定：所有读改都发生在 UI 线程（插件的捕获流程本来就在 UI 线程），
/// 只有真正写文件那一步丢到后台线程，并用序号保证"新快照不会被旧快照覆盖"。
/// </summary>
public sealed class ClipboardHistory
{
    private const string HistoryFileName = "history.json";
    private const string ImagesFolderName = "images";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IPluginLogger _log;
    private readonly List<ClipItem> _items = [];
    private readonly object _writeLock = new();

    private long _persistSeq;
    private long _highestStartedSeq;

    public ClipboardHistory(IPluginLogger log)
    {
        _log = log;

        RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinIsland",
            "clipboard-island");

        ImagesDirectory = Path.Combine(RootDirectory, ImagesFolderName);
        HistoryFile = Path.Combine(RootDirectory, HistoryFileName);
    }

    public string RootDirectory { get; }

    public string ImagesDirectory { get; }

    public string HistoryFile { get; }

    public IReadOnlyList<ClipItem> Items => _items;

    public int Count => _items.Count;

    /// <summary>历史内容有变化（新增 / 顺序变化 / 裁剪 / 清空）时触发，回调在 UI 线程。</summary>
    public event Action? Changed;

    /// <summary>从磁盘载入历史。文件坏了、缓存图片不在了都只退化，不抛异常。</summary>
    public void Load(int max)
    {
        _items.Clear();

        try
        {
            if (File.Exists(HistoryFile))
            {
                var json = File.ReadAllText(HistoryFile);
                var dtos = JsonSerializer.Deserialize<List<ClipDto>>(json, JsonOptions) ?? [];
                foreach (var dto in dtos)
                {
                    var item = dto.ToItem(this, _log);
                    if (item is not null) _items.Add(item);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"读取剪贴板历史失败，从空历史开始：{ex.Message}");
            _items.Clear();
        }

        Trim(max, notify: false);
        DeleteOrphanImages();
        _log.Info($"剪贴板历史已载入：{_items.Count} 条（{RootDirectory}）");
    }

    /// <summary>记一条文本。内容已在历史里时只挪位置，不产生新记录。</summary>
    public ClipOutcome AddText(string text, string signature, int max)
    {
        if (TryPromote(signature, out var wasHead)) return wasHead ? ClipOutcome.Repeated : ClipOutcome.Promoted;

        _items.Insert(0, new ClipItem
        {
            Id = NewId(),
            Kind = ClipKind.Text,
            Text = text,
            Signature = signature,
            CapturedAt = DateTimeOffset.Now,
        });

        AfterMutation(max);
        return ClipOutcome.Added;
    }

    /// <summary>记一条图片（字节会写成 PNG 缓存文件）。内容已在历史里时只挪位置，不产生新记录。</summary>
    public ClipOutcome AddImage(byte[] bytes, string signature, int max)
    {
        if (TryPromote(signature, out var wasHead)) return wasHead ? ClipOutcome.Repeated : ClipOutcome.Promoted;

        var id = NewId();
        string? path = null;

        try
        {
            Directory.CreateDirectory(ImagesDirectory);
            var candidate = Path.Combine(ImagesDirectory, id + ".png");
            File.WriteAllBytes(candidate, bytes);
            path = candidate;
        }
        catch (Exception ex)
        {
            // 图片没落盘也照样记这条历史，只是缩略图会缺
            _log.Error("保存剪贴板图片失败（这条记录将没有缩略图）", ex);
        }

        _items.Insert(0, new ClipItem
        {
            Id = id,
            Kind = ClipKind.Image,
            ImagePath = path,
            ImageBytes = bytes.Length,
            Signature = signature,
            CapturedAt = DateTimeOffset.Now,
        });

        AfterMutation(max);
        return ClipOutcome.Added;
    }

    /// <summary>
    /// 记一批文件/文件夹（只存路径引用，不复制文件本身）。
    /// 内容已在历史里时只挪位置，不产生新记录。
    /// </summary>
    public ClipOutcome AddFiles(IReadOnlyList<string> paths, string signature, int max)
    {
        if (TryPromote(signature, out var wasHead)) return wasHead ? ClipOutcome.Repeated : ClipOutcome.Promoted;

        _items.Insert(0, new ClipItem
        {
            Id = NewId(),
            Kind = ClipKind.Files,
            Paths = paths.ToArray(),
            Signature = signature,
            CapturedAt = DateTimeOffset.Now,
        });

        AfterMutation(max);
        return ClipOutcome.Added;
    }

    public bool TryPromote(string signature) => TryPromote(signature, out _);

    /// <summary>
    /// 内容已在历史里就把它提到最前（不产生新记录）。命中返回 true，
    /// <paramref name="wasHead"/> 表示它本来就在最前（没有挪动，即「又复制了一遍同一条」）。
    /// </summary>
    public bool TryPromote(string signature, out bool wasHead)
    {
        wasHead = false;
        if (string.IsNullOrEmpty(signature)) return false;

        var index = _items.FindIndex(i => string.Equals(i.Signature, signature, StringComparison.Ordinal));
        if (index < 0) return false;
        if (index == 0)
        {
            wasHead = true;
            return true;   // 已经是最新那条，什么都不用做
        }

        var item = _items[index];
        _items.RemoveAt(index);
        _items.Insert(0, item with { CapturedAt = DateTimeOffset.Now });

        Changed?.Invoke();
        QueuePersist();
        return true;
    }

    /// <summary>裁剪到上限条数（超出部分连同图片缓存一起删掉）。</summary>
    public void Trim(int max, bool notify = true)
    {
        if (max < 1) max = 1;
        if (_items.Count <= max) return;

        while (_items.Count > max)
        {
            var last = _items[^1];
            _items.RemoveAt(_items.Count - 1);
            DeleteImageFile(last);
        }

        if (notify) Changed?.Invoke();
        QueuePersist();
    }

    /// <summary>清空历史，并把缓存图片全部删掉。</summary>
    public void Clear()
    {
        foreach (var item in _items) DeleteImageFile(item);
        _items.Clear();

        Changed?.Invoke();
        QueuePersist();
    }

    /// <summary>把这条记录重新复制时用的图片路径（文件不在了返回 null）。</summary>
    public string? GetExistingImagePath(ClipItem item)
        => item.ImagePath is not null && File.Exists(item.ImagePath) ? item.ImagePath : null;

    private void AfterMutation(int max)
    {
        Trim(max, notify: false);
        Changed?.Invoke();
        QueuePersist();
    }

    private void DeleteImageFile(ClipItem item)
    {
        if (item.ImagePath is null) return;

        try
        {
            if (File.Exists(item.ImagePath)) File.Delete(item.ImagePath);
        }
        catch (Exception ex)
        {
            _log.Debug($"删除图片缓存失败（忽略）：{ex.Message}");
        }
    }

    /// <summary>历史里已经不引用的 png 直接清掉（比如上次异常退出留下的）。</summary>
    private void DeleteOrphanImages()
    {
        try
        {
            if (!Directory.Exists(ImagesDirectory)) return;

            var referenced = new HashSet<string>(
                _items.Where(i => i.ImagePath is not null).Select(i => Path.GetFileName(i.ImagePath!)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(ImagesDirectory, "*.png"))
            {
                if (referenced.Contains(Path.GetFileName(file))) continue;

                try { File.Delete(file); }
                catch { /* 删不掉就算了 */ }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"清理孤儿图片失败（忽略）：{ex.Message}");
        }
    }

    /// <summary>把当前快照排进后台写盘队列。多个快照并发时只让最新的那个落盘。</summary>
    private void QueuePersist()
    {
        var snapshot = new List<ClipDto>(_items.Count);
        foreach (var item in _items) snapshot.Add(ClipDto.From(item));

        long seq;
        lock (_writeLock)
        {
            _persistSeq++;
            seq = _persistSeq;
        }

        _ = Task.Run(() =>
        {
            lock (_writeLock)
            {
                if (seq < _highestStartedSeq) return;   // 已经有更新的快照在写了，这个旧的就别覆盖回去
                _highestStartedSeq = seq;

                // 写盘放在同一把锁里串行执行：两个快照同时写同一个 .tmp 会互相占用文件
                // （曾经因此丢掉过一次写入：IOException "history.json.tmp 正被另一进程使用"）
                WriteSnapshot(snapshot);
            }
        });
    }

    private void WriteSnapshot(List<ClipDto> snapshot)
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var temp = HistoryFile + ".tmp";

            // 先写临时文件再改名：中途断电也不会把 history.json 写坏
            File.WriteAllText(temp, json);
            File.Move(temp, HistoryFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Error("写入剪贴板历史失败（已忽略）", ex);
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("n");

    /// <summary>history.json 里的一行。</summary>
    internal sealed class ClipDto
    {
        public string Id { get; set; } = "";

        public string Kind { get; set; } = "text";

        public string? Text { get; set; }

        /// <summary>文件记录（Kind = "files"）的路径列表。</summary>
        public List<string>? Paths { get; set; }

        /// <summary>只存文件名（不存绝对路径），缓存目录整体挪位置也不影响。</summary>
        public string? ImageFile { get; set; }

        public long ImageBytes { get; set; }

        public string Signature { get; set; } = "";

        public DateTimeOffset CapturedAt { get; set; }

        public static ClipDto From(ClipItem item) => new()
        {
            Id = item.Id,
            Kind = item.IsImage ? "image" : item.IsFiles ? "files" : "text",
            Text = item.Text,
            Paths = item.Paths.Count > 0 ? item.Paths.ToList() : null,
            ImageFile = item.ImagePath is null ? null : Path.GetFileName(item.ImagePath),
            ImageBytes = item.ImageBytes,
            Signature = item.Signature,
            CapturedAt = item.CapturedAt,
        };

        public ClipItem? ToItem(ClipboardHistory owner, IPluginLogger log)
        {
            if (string.Equals(Kind, "image", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(ImageFile)) return null;

                var path = Path.Combine(owner.ImagesDirectory, ImageFile);
                if (!File.Exists(path))
                {
                    log.Debug($"图片缓存已不在，跳过这条记录：{ImageFile}");
                    return null;
                }

                return new ClipItem
                {
                    Id = string.IsNullOrEmpty(Id) ? NewId() : Id,
                    Kind = ClipKind.Image,
                    ImagePath = path,
                    ImageBytes = ImageBytes,
                    Signature = Signature,
                    CapturedAt = CapturedAt,
                };
            }

            if (string.Equals(Kind, "files", StringComparison.OrdinalIgnoreCase))
            {
                var paths = Paths ?? new List<string>();
                if (paths.Count == 0) return null;

                return new ClipItem
                {
                    Id = string.IsNullOrEmpty(Id) ? NewId() : Id,
                    Kind = ClipKind.Files,
                    Paths = paths,
                    Signature = Signature,
                    CapturedAt = CapturedAt,
                };
            }

            if (string.IsNullOrWhiteSpace(Text)) return null;

            return new ClipItem
            {
                Id = string.IsNullOrEmpty(Id) ? NewId() : Id,
                Kind = ClipKind.Text,
                Text = Text,
                Signature = Signature,
                CapturedAt = CapturedAt,
            };
        }
    }
}
