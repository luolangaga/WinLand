using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinIsland.Core;

namespace WinIsland.Modules.Media;

/// <summary>一行歌词：时间轴 + 文本。</summary>
public readonly record struct LyricLine(TimeSpan Time, string Text);

/// <summary>
/// 歌词获取：先查 LRCLIB（官方开放接口、免鉴权），查不到再用网易云公开接口按「歌名 + 歌手」搜。
/// 带内存 + 磁盘（<c>%LocalAppData%\WinIsland\cache\lyrics</c>）缓存、6 秒超时；
/// 任何失败都只返回 null 并记 Debug/Warn —— 歌词是锦上添花，绝不能影响播放卡本身。
/// </summary>
public sealed class LyricsService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    private readonly IPluginLogger _log;
    private readonly string _cacheDirectory;
    private readonly Dictionary<string, IReadOnlyList<LyricLine>?> _memory = new();

    public LyricsService(IPluginLogger log, string pluginDirectory)
    {
        _log = log;
        _cacheDirectory = Path.Combine(pluginDirectory, "lyrics-cache");
    }

    /// <summary>取歌词（同步的 lrc）；没有可用歌词时返回 null。</summary>
    public async Task<IReadOnlyList<LyricLine>?> FetchAsync(
        string title, string artist, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var key = $"{title}\u0001{artist}";
        if (_memory.TryGetValue(key, out var cached))
        {
            return cached;
        }

        IReadOnlyList<LyricLine>? lines = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            lines = await LoadFromDiskAsync(key) ?? await FetchFromNetworkAsync(title, artist, duration, timeout.Token);
            if (lines is { Count: > 0 })
            {
                await SaveToDiskAsync(key, lines);
            }
            else
            {
                lines = null;
            }
        }
        catch (OperationCanceledException)
        {
            return null;    // 切歌/关闭卡片导致的取消：不留缓存，下次还会再试
        }
        catch (Exception ex)
        {
            _log.Debug($"歌词获取失败（{title} - {artist}）：{ex.Message}");
            lines = null;
        }

        _memory[key] = lines;
        return lines;
    }

    private async Task<IReadOnlyList<LyricLine>?> FetchFromNetworkAsync(
        string title, string artist, TimeSpan duration, CancellationToken cancellationToken)
    {
        var lines = await TryLrclibAsync(title, artist, duration, cancellationToken);
        if (lines is { Count: > 0 })
        {
            return lines;
        }

        return await TryNetEaseAsync(title, artist, cancellationToken);
    }

    // ---- LRCLIB（首选）----

    private async Task<IReadOnlyList<LyricLine>?> TryLrclibAsync(
        string title, string artist, TimeSpan duration, CancellationToken cancellationToken)
    {
        var query = $"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
        if (duration > TimeSpan.Zero)
        {
            query += $"&duration={(int)Math.Round(duration.TotalSeconds)}";
        }

        var exact = await GetJsonAsync($"https://lrclib.net/api/get?{query}", cancellationToken);
        var synced = exact is { } document ? ReadLrclibSynced(document) : null;
        if (synced != null) return Parse(synced);

        var search = await GetJsonAsync(
            $"https://lrclib.net/api/search?q={Uri.EscapeDataString($"{title} {artist}")}", cancellationToken);
        if (search is not { } results || results.ValueKind != JsonValueKind.Array) return null;

        foreach (var item in results.EnumerateArray())
        {
            if (ReadLrclibSynced(item) is not { } candidate) continue;

            var candidateTitle = item.TryGetProperty("trackName", out var t) ? t.GetString() : null;
            var candidateArtist = item.TryGetProperty("artistName", out var a) ? a.GetString() : null;
            if (!Matches(title, candidateTitle) || !Matches(artist, candidateArtist)) continue;

            var parsed = Parse(candidate);
            if (parsed.Count > 0) return parsed;
        }

        return null;
    }

    private static string? ReadLrclibSynced(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty("syncedLyrics", out var synced)
           && synced.ValueKind == JsonValueKind.String
            ? synced.GetString()
            : null;

    // ---- 网易云（兜底）----

    private async Task<IReadOnlyList<LyricLine>?> TryNetEaseAsync(
        string title, string artist, CancellationToken cancellationToken)
    {
        var search = await GetJsonAsync(
            $"https://music.163.com/api/search/get/web?csrf_token=&s={Uri.EscapeDataString($"{title} {artist}")}" +
            "&type=1&offset=0&total=true&limit=5",
            cancellationToken);

        if (search is not { } document
            || !document.TryGetProperty("result", out var result)
            || !result.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Array
            || songs.GetArrayLength() == 0)
        {
            return null;
        }

        if (!songs[0].TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var songId))
        {
            return null;
        }

        var lyric = await GetJsonAsync($"https://music.163.com/api/song/lyric?id={songId}&lv=-1&kv=-1&tv=-1", cancellationToken);
        if (lyric is not { } lyricDocument
            || !lyricDocument.TryGetProperty("lrc", out var lrc)
            || !lrc.TryGetProperty("lyric", out var text))
        {
            return null;
        }

        return Parse(text.GetString());
    }

    // ---- LRC 解析 ----

    /// <summary>解析 LRC：支持一行多个时间标签、<c>[mm:ss]</c> / <c>[mm:ss.xx]</c> / <c>[mm:ss.xxx]</c>，忽略元数据行。</summary>
    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return Array.Empty<LyricLine>();

        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var tags = TimeTag.Matches(raw);
            if (tags.Count == 0) continue;

            var text = TimeTag.Replace(raw, "").Trim();
            if (text.Length == 0) continue;

            foreach (Match tag in tags)
            {
                int minutes = int.Parse(tag.Groups[1].Value);
                int seconds = int.Parse(tag.Groups[2].Value);
                var fraction = tag.Groups[3].Value;
                int milliseconds = fraction.Length switch
                {
                    1 => int.Parse(fraction) * 100,
                    2 => int.Parse(fraction) * 10,
                    3 => int.Parse(fraction),
                    _ => 0,
                };

                if (seconds >= 60) continue;
                lines.Add(new LyricLine(new TimeSpan(0, 0, minutes, seconds, milliseconds), text));
            }
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    // ---- 缓存 ----

    private async Task<IReadOnlyList<LyricLine>?> LoadFromDiskAsync(string key)
    {
        try
        {
            var path = CachePath(key);
            if (!File.Exists(path)) return null;

            var text = await File.ReadAllTextAsync(path);
            var lines = Parse(text);
            return lines.Count > 0 ? lines : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveToDiskAsync(string key, IReadOnlyList<LyricLine> lines)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var payload = new StringBuilder();
            foreach (var line in lines)
            {
                payload.Append('[')
                       .Append((int)line.Time.TotalMinutes).Append(':')
                       .Append(line.Time.Seconds.ToString("D2")).Append('.')
                       .Append(line.Time.Milliseconds.ToString("D3")).Append(']')
                       .Append(line.Text)
                       .Append('\n');
            }

            await File.WriteAllTextAsync(CachePath(key), payload.ToString());
        }
        catch
        {
            // 缓存写不进去无所谓
        }
    }

    private string CachePath(string key)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_cacheDirectory, hash + ".lrc");
    }

    // ---- 基础设施 ----

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool Matches(string expected, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var left = Normalize(expected);
        var right = Normalize(candidate);
        return right.Length > 0 && (right.Contains(left) || left.Contains(right));
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinIsland/2.1 (+https://github.com/luolan/winland)");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return client;
    }
}
