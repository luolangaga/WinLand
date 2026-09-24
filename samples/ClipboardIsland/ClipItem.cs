using System.Text;

namespace ClipboardIsland;

/// <summary>一条剪贴板记录的类型。</summary>
public enum ClipKind
{
    Text,
    Image,

    /// <summary>文件/文件夹（资源管理器里 Ctrl+C）。只存路径引用，重新复制时写回的是文件本身。</summary>
    Files,
}

/// <summary>
/// 一次入库的结果。三种情况必须分开 —— 早先只用一个 bool 表示"有没有新增"，
/// 结果「内容已经在历史里」这件事被当成了"什么都没发生"，界面上一点反馈都没有，
/// 用户看到的就是"复制了但没触发"。
/// </summary>
public enum ClipOutcome
{
    /// <summary>新内容，插到最前。</summary>
    Added,

    /// <summary>历史里已有，这次把它提到了最前。</summary>
    Promoted,

    /// <summary>历史里已有，而且本来就在最前 —— 用户又复制了一遍同一条。</summary>
    Repeated,
}

/// <summary>
/// 从剪贴板里读出来的一条原始内容（还没落盘）。Signature 用于"和上一条是不是同一个东西"的去重判断。
/// </summary>
public sealed record ClipboardCapture(ClipKind Kind, string? Text, byte[]? ImageBytes, string Signature)
{
    /// <summary>文件/文件夹路径（<see cref="ClipKind.Files"/> 时有值）。</summary>
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();

    /// <summary>可选的展示摘要（例如「3 个文件 · a.txt」）；为空时按类型自动生成。</summary>
    public string? Summary { get; init; }

    public string Preview => Summary ?? Kind switch
    {
        ClipKind.Image => ClipText.DescribeImage(ImageBytes?.LongLength ?? 0),
        ClipKind.Files => ClipText.DescribeFiles(Paths),
        _ => ClipText.Collapse(Text),
    };
}

/// <summary>
/// 历史里的一条记录（不可变）。文本直接存；图片只存缓存文件路径 + 原始字节数，
/// 避免把几十张图片全塞进内存和 history.json。
/// </summary>
public sealed record ClipItem
{
    public string Id { get; init; } = "";

    public ClipKind Kind { get; init; }

    public string? Text { get; init; }

    /// <summary>文件/文件夹路径（<see cref="ClipKind.Files"/> 时有值）。只存引用，不复制文件本身。</summary>
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();

    /// <summary>图片缓存文件的绝对路径（PNG）。</summary>
    public string? ImagePath { get; init; }

    /// <summary>图片原始字节数，用于在界面上显示大小。</summary>
    public long ImageBytes { get; init; }

    /// <summary>内容指纹，用来判断"这条是不是已经记过了"。</summary>
    public string Signature { get; init; } = "";

    public DateTimeOffset CapturedAt { get; init; }

    public bool IsImage => Kind == ClipKind.Image;

    /// <summary>这一条是"一批文件/文件夹的引用"：写回剪贴板时写的是文件本身（CF_HDROP），不是路径文本。</summary>
    public bool IsFiles => Kind == ClipKind.Files;

    /// <summary>折叠成一行显示用的摘要。</summary>
    public string Summary => Kind switch
    {
        ClipKind.Image => ClipText.DescribeImage(ImageBytes),
        ClipKind.Files => ClipText.DescribeFiles(Paths),
        _ => ClipText.Collapse(Text),
    };

    /// <summary>写回剪贴板时用的文本（文件记录在文件都不在了时退化成路径文本）。</summary>
    public string PlainText => Text ?? (Paths.Count > 0 ? string.Join("\r\n", Paths) : string.Empty);
}

/// <summary>文本摘要与时间文案的小工具。</summary>
internal static class ClipText
{
    private const int MaxSummaryChars = 90;

    /// <summary>把多行 / 连续空白压成一行，方便在小岛上显示。</summary>
    public static string Collapse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "（空白文本）";

        var sb = new StringBuilder(Math.Min(text.Length, MaxSummaryChars + 1));
        var pendingSpace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(ch);
            if (sb.Length > MaxSummaryChars) break;   // 长文本不必扫完
        }

        var result = sb.ToString().Trim();
        return result.Length > MaxSummaryChars ? result[..MaxSummaryChars] + "…" : result;
    }

    public static string DescribeImage(long bytes) => $"图片 · {FormatBytes(bytes)}";

    /// <summary>文件列表的摘要：一个显示文件名，多个显示「N 个文件 · 第一个」。</summary>
    public static string DescribeFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return "（空文件列表）";

        var first = Path.GetFileName(paths[0]);
        if (string.IsNullOrEmpty(first)) first = paths[0];

        return paths.Count == 1 ? $"文件 · {first}" : $"{paths.Count} 个文件 · {first}";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / 1024.0 / 1024.0:0.#} MB";
    }

    /// <summary>相对时间：刚刚 / n 分钟前 / 时:分 / n月n日。</summary>
    public static string Ago(DateTimeOffset time)
    {
        var delta = DateTimeOffset.Now - time;
        if (delta < TimeSpan.Zero) return "刚刚";
        if (delta < TimeSpan.FromSeconds(45)) return "刚刚";
        if (delta < TimeSpan.FromMinutes(60)) return $"{(int)delta.TotalMinutes} 分钟前";
        if (delta < TimeSpan.FromHours(24) && time.Date == DateTimeOffset.Now.Date) return time.ToString("HH:mm");
        if (delta < TimeSpan.FromHours(24)) return $"昨天 {time:HH:mm}";
        return time.ToString("M月d日");
    }
}
