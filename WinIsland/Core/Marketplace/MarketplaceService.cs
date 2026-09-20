using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinIsland.Core.Marketplace;

public sealed class MarketplaceException : Exception
{
    public MarketplaceException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// 插件市场客户端：只请求仓库根目录的 index.json（带 ETag/TTL 缓存与离线回退），
/// 图标随清单内嵌下发；只有真正安装时才下载 .lwp，并强制校验 SHA-256。
///
/// 未显式配置 <c>marketplace.baseUrl</c> 时按镜像列表依次尝试（GitHub 直连 → jsDelivr），
/// 这样国内网络也能拿到清单；成功过的源会被优先使用。
/// 显式配置一个本地目录（如 <c>D:\WinLandPlugin</c>）可用于离线调试。
/// </summary>
public sealed class MarketplaceService
{
    /// <summary>
    /// 市场源候选：官方 GitHub 是唯一源头（Action 在那边生成 index.json），
    /// 其余都是镜像 —— GitCode 为国内镜像（自动同步，raw.githubusercontent.com 常被墙时用它），
    /// jsDelivr 为 CDN 镜像。按顺序尝试，命中一个就停；成功过的源下次优先。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultMirrors = new[]
    {
        "https://raw.githubusercontent.com/luolangaga/WinLandPlugin/main",
        "https://api.gitcode.com/api/v5/repos/luolangaga/WinLandPlugin/raw",
        "https://cdn.jsdelivr.net/gh/luolangaga/WinLandPlugin@main",
        "https://gcore.jsdelivr.net/gh/luolangaga/WinLandPlugin@main",
    };

    public const string RepositoryUrl = "https://github.com/luolangaga/WinLandPlugin";

    public const string MirrorUrl = "https://gitcode.com/luolangaga/WinLandPlugin";

    public const string BaseUrlSettingKey = "marketplace.baseUrl";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    /// <summary>单个源的尝试超时：被墙的地址有时会挂着不返回，不能让它拖住整个刷新。</summary>
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly SettingsService _settings;
    private readonly IPluginLogger _log;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="cacheDirectory">
    /// 缓存根目录，默认 <c>%LocalAppData%\WinIsland\marketplace</c>。
    /// 测试/调试时可指向别处，避免与真实缓存互相污染。
    /// </param>
    public MarketplaceService(SettingsService settings, IPluginLogger log, string? cacheDirectory = null)
    {
        _settings = settings;
        _log = log;
        Directory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinIsland",
                "marketplace")
            : cacheDirectory;
        System.IO.Directory.CreateDirectory(Directory);

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"WinIsland/{IslandSdk.HostVersion}");
    }

    /// <summary>本地缓存根目录。</summary>
    public string Directory { get; }

    /// <summary>用户显式配置的市场源；为空表示自动（按镜像列表尝试）。</summary>
    public string? ConfiguredBaseUrl
    {
        get
        {
            var value = _settings.Get(BaseUrlSettingKey, "");
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        set => _settings.Set(BaseUrlSettingKey, (value ?? "").Trim());
    }

    public bool IsAuto => ConfiguredBaseUrl == null;

    /// <summary>最近一次成功使用的源（可能是镜像）；用于下载包与读取说明。</summary>
    public string ActiveBaseUrl { get; private set; } = DefaultMirrors[0];

    /// <summary>最近一次是否用了非官方源（自动模式下的镜像）。</summary>
    public bool IsMirrorActive => IsAuto && !string.Equals(ActiveBaseUrl, DefaultMirrors[0], StringComparison.OrdinalIgnoreCase);

    /// <summary>最近一次清单的获取时间（缓存命中时是缓存写入时间）。</summary>
    public DateTimeOffset? LastIndexTime { get; private set; }

    /// <summary>最近一次清单是否来自本地缓存。</summary>
    public bool LastIndexFromCache { get; private set; }

    /// <summary>给界面看的来源名称（官方源 / 国内镜像 / CDN）。</summary>
    public string ActiveSourceLabel => DescribeSource(ActiveBaseUrl);

    public static string DescribeSource(string baseUrl)
    {
        if (IsLocalSource(baseUrl))
        {
            return "本机目录";
        }

        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : baseUrl;
        return host switch
        {
            "raw.githubusercontent.com" => "GitHub 官方源",
            "api.gitcode.com" => "GitCode 国内镜像",
            "cdn.jsdelivr.net" or "gcore.jsdelivr.net" => "jsDelivr CDN 镜像",
            _ => host,
        };
    }

    public async Task<MarketplaceIndex> GetIndexAsync(bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var candidates = BuildCandidates();
            var cached = TryReadCache(out var cachedAt, out var cachedBase);

            if (!force && cached != null && DateTimeOffset.UtcNow - cachedAt < CacheTtl)
            {
                ActiveBaseUrl = cachedBase ?? candidates[0];
                LastIndexTime = cachedAt;
                LastIndexFromCache = true;
                return cached;
            }

            Exception? lastError = null;
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // 只有同一个源才谈得上 ETag / 304
                    var etag = string.Equals(candidate, cachedBase, StringComparison.OrdinalIgnoreCase) ? ReadEtag() : null;
                    var fetch = await FetchIndexTextAsync(candidate, cached != null ? etag : null, ct);

                    if (fetch.Text == null && cached != null)
                    {
                        // 304：内容没变，只刷新时间戳
                        ActiveBaseUrl = candidate;
                        WriteCache(candidate, null, fetch.ETag);
                        LastIndexTime = cachedAt;
                        LastIndexFromCache = true;
                        return cached;
                    }

                    var index = ParseIndex(fetch.Text!);
                    ActiveBaseUrl = candidate;
                    WriteCache(candidate, fetch.Text, fetch.ETag);
                    LastIndexTime = DateTimeOffset.UtcNow;
                    LastIndexFromCache = false;
                    _log.Info($"插件市场清单已更新：{index.Plugins.Count} 个插件（来源 {candidate}）");
                    return index;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (candidates.Count > 1)
                    {
                        _log.Warn($"市场源 {candidate} 不可用，尝试下一个：{ex.Message}");
                    }
                }
            }

            if (cached != null)
            {
                _log.Warn($"所有市场源都不可用，已回退到本地缓存：{lastError?.Message}");
                ActiveBaseUrl = cachedBase ?? candidates[0];
                LastIndexTime = cachedAt;
                LastIndexFromCache = true;
                return cached;
            }

            throw new MarketplaceException($"无法连接插件市场：{lastError?.Message ?? "未知错误"}", lastError);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>按需拉取某个插件的 README（列表阶段不做任何逐插件请求）。</summary>
    public async Task<string?> GetReadmeAsync(MarketplacePlugin plugin, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plugin.Readme))
        {
            return null;
        }

        var cache = Path.Combine(Directory, "readme", $"{Sanitize(plugin.Id)}-{Sanitize(plugin.Version)}.md");
        try
        {
            if (File.Exists(cache))
            {
                return await File.ReadAllTextAsync(cache, ct);
            }

            var text = await ReadTextAsync(plugin.Readme, ct);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            await File.WriteAllTextAsync(cache, text, ct);
            return text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"读取插件 {plugin.Id} 的说明失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>下载 .lwp 到本地缓存（已下载且校验通过时直接复用），返回本地路径。</summary>
    public async Task<string> DownloadPackageAsync(MarketplacePlugin plugin, IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plugin.Package))
        {
            throw new MarketplaceException($"插件 {plugin.Id} 的清单条目缺少 package 字段");
        }

        var target = Path.Combine(Directory, "downloads", $"{Sanitize(plugin.Id)}-{Sanitize(plugin.Version)}.lwp");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (File.Exists(target) && VerifyFile(target, plugin, out _))
        {
            progress?.Report(1);
            return target;
        }

        var temp = target + ".part";
        try
        {
            Exception? lastError = null;
            foreach (var source in PackageCandidates())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (IsLocalSource(source))
                    {
                        var localPath = Path.Combine(source, plugin.Package);
                        if (!File.Exists(localPath))
                        {
                            throw new MarketplaceException($"本地市场目录缺少插件包：{localPath}");
                        }

                        File.Copy(localPath, temp, true);
                        progress?.Report(1);
                    }
                    else
                    {
                        await DownloadFileAsync(source, plugin, temp, progress, ct);
                    }

                    if (!VerifyFile(temp, plugin, out var error))
                    {
                        throw new MarketplaceException(error ?? "插件包校验失败");
                    }

                    File.Move(temp, target, true);
                    _log.Info($"已下载插件包 {plugin.Id} {plugin.Version}（{plugin.DescribeSize()}，来源 {source}）");
                    return target;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _log.Warn($"从 {source} 下载 {plugin.Id} 失败：{ex.Message}");
                    TryDelete(temp);
                }
            }

            throw new MarketplaceException($"下载插件包失败：{lastError?.Message ?? "未知错误"}", lastError);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private async Task<IndexFetch> FetchIndexTextAsync(string baseUrl, string? etag, CancellationToken ct)
    {
        if (IsLocalSource(baseUrl))
        {
            var path = Path.Combine(baseUrl, "index.json");
            if (!File.Exists(path))
            {
                throw new MarketplaceException($"本地市场目录缺少 index.json：{path}");
            }

            return new IndexFetch(await File.ReadAllTextAsync(path, ct), etag);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Combine(baseUrl, "index.json"));
        if (!string.IsNullOrEmpty(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(IndexTimeout);
        using var response = await _http.SendAsync(request, timeout.Token);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new IndexFetch(null, etag);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new MarketplaceException($"市场返回 {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return new IndexFetch(await response.Content.ReadAsStringAsync(timeout.Token), response.Headers.ETag?.Tag ?? etag);
    }

    private async Task DownloadFileAsync(string baseUrl, MarketplacePlugin plugin, string target, IProgress<double>? progress, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await _http.GetAsync(Combine(baseUrl, plugin.Package), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new MarketplaceException($"下载失败：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var total = response.Content.Headers.ContentLength ?? plugin.PackageSize;
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var destination = File.Create(target);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            received += read;
            if (total > 0)
            {
                progress?.Report(Math.Clamp((double)received / total, 0, 1));
            }
        }
    }

    private async Task<string> ReadTextAsync(string relativePath, CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (var baseUrl in PackageCandidates())
        {
            try
            {
                if (IsLocalSource(baseUrl))
                {
                    var path = Path.Combine(baseUrl, relativePath);
                    if (!File.Exists(path))
                    {
                        throw new MarketplaceException($"本地市场目录缺少文件：{path}");
                    }

                    return await File.ReadAllTextAsync(path, ct);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(IndexTimeout);
                using var response = await _http.GetAsync(Combine(baseUrl, relativePath), timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new MarketplaceException($"读取 {relativePath} 失败：{(int)response.StatusCode}");
                }

                return await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new MarketplaceException($"无法读取 {relativePath}：{lastError?.Message}", lastError);
    }

    /// <summary>显式配置时只用配置的源；自动模式时按「上次成功 → 官方 → 镜像」顺序尝试。</summary>
    private List<string> BuildCandidates()
    {
        if (ConfiguredBaseUrl is { } configured)
        {
            return new List<string> { configured };
        }

        var list = DefaultMirrors.ToList();
        var preferred = ReadCachedBaseUrl() ?? ActiveBaseUrl;
        if (!string.IsNullOrEmpty(preferred))
        {
            var index = list.FindIndex(m => string.Equals(m, preferred, StringComparison.OrdinalIgnoreCase));
            if (index > 0)
            {
                list.RemoveAt(index);
                list.Insert(0, preferred);
            }
        }

        return list;
    }

    /// <summary>下载/读说明用的候选源：优先最近成功的源，再退回其余镜像。</summary>
    private List<string> PackageCandidates()
    {
        var list = BuildCandidates();
        var index = list.FindIndex(m => string.Equals(m, ActiveBaseUrl, StringComparison.OrdinalIgnoreCase));
        if (index > 0)
        {
            list.RemoveAt(index);
            list.Insert(0, ActiveBaseUrl);
        }

        return list;
    }

    private static bool IsLocalSource(string baseUrl)
        => !baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private static string Combine(string baseUrl, string relativePath)
    {
        var escaped = string.Join('/', relativePath.Split('/', '\\').Where(s => s.Length > 0).Select(Uri.EscapeDataString));
        return $"{baseUrl.TrimEnd('/')}/{escaped}";
    }

    private bool VerifyFile(string path, MarketplacePlugin plugin, out string? error)
    {
        error = null;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            error = "插件包为空";
            return false;
        }

        if (plugin.PackageSize > 0 && info.Length != plugin.PackageSize)
        {
            error = $"插件包大小不符（预期 {plugin.PackageSize} 字节，实际 {info.Length} 字节）";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(plugin.Sha256))
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!hash.Equals(plugin.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "插件包 SHA-256 校验失败，已放弃安装";
                return false;
            }
        }

        return true;
    }

    private MarketplaceIndex ParseIndex(string json)
    {
        MarketplaceIndex? index;
        try
        {
            index = JsonSerializer.Deserialize<MarketplaceIndex>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MarketplaceException($"index.json 不是有效的 JSON：{ex.Message}", ex);
        }

        if (index == null)
        {
            throw new MarketplaceException("index.json 内容为空");
        }

        if (index.Schema > MarketplaceIndex.CurrentSchema)
        {
            throw new MarketplaceException($"清单 schema {index.Schema} 高于客户端支持的 {MarketplaceIndex.CurrentSchema}，请更新 WinIsland");
        }

        index.Plugins.RemoveAll(p => string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Package));
        return index;
    }

    private string CachePath => Path.Combine(Directory, "index.json");

    private string MetaPath => Path.Combine(Directory, "index.meta.json");

    private string EtagPath => Path.Combine(Directory, "index.etag");

    private MarketplaceIndex? TryReadCache(out DateTimeOffset fetchedAt, out string? baseUrl)
    {
        fetchedAt = DateTimeOffset.MinValue;
        baseUrl = null;
        try
        {
            if (!File.Exists(CachePath) || !File.Exists(MetaPath))
            {
                return null;
            }

            var meta = JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(MetaPath), JsonOptions);
            if (meta == null || !IsCacheUsable(meta.BaseUrl))
            {
                return null;
            }

            fetchedAt = meta.FetchedAt;
            baseUrl = meta.BaseUrl;
            return ParseIndex(File.ReadAllText(CachePath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 缓存是否还能代表"当前配置下该看到的内容"：
    /// 显式配置时缓存必须来自该源；自动模式时缓存必须来自当前的镜像之一。
    /// 否则会出现"上次用了本地目录/旧镜像，这次打开还显示旧清单"。
    /// </summary>
    private bool IsCacheUsable(string? cachedBase)
    {
        if (string.IsNullOrWhiteSpace(cachedBase))
        {
            return false;
        }

        if (ConfiguredBaseUrl is { } configured)
        {
            return string.Equals(cachedBase, configured, StringComparison.OrdinalIgnoreCase);
        }

        return DefaultMirrors.Any(m => string.Equals(m, cachedBase, StringComparison.OrdinalIgnoreCase));
    }

    private string? ReadCachedBaseUrl()
    {
        try
        {
            return File.Exists(MetaPath)
                ? JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(MetaPath), JsonOptions)?.BaseUrl
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void WriteCache(string baseUrl, string? json, string? etag)
    {
        try
        {
            if (json != null)
            {
                File.WriteAllText(CachePath, json);
            }

            File.WriteAllText(MetaPath, JsonSerializer.Serialize(new CacheMeta(baseUrl, DateTimeOffset.UtcNow), JsonOptions));
            if (etag != null)
            {
                File.WriteAllText(EtagPath, etag);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"写入插件市场缓存失败：{ex.Message}");
        }
    }

    private string? ReadEtag()
    {
        try
        {
            return File.Exists(EtagPath) ? File.ReadAllText(EtagPath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string Sanitize(string value)
        => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    private sealed record CacheMeta(string BaseUrl, DateTimeOffset FetchedAt);

    /// <summary>清单抓取结果：<c>Text</c> 为 null 表示 304（内容未变）。</summary>
    private sealed record IndexFetch(string? Text, string? ETag);
}
