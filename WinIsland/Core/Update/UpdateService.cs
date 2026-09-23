using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinIsland.Core.Update;

/// <summary>
/// 更新检查（只查不装）：先问 GitCode 国内镜像，失败再回退 GitHub 官方源，
/// 两个源都有结果时取版本较新的那个。发现新版本只做提醒，下载与安装由用户在浏览器里完成
/// （「前往下载」打开对应源的发布页）。
///
/// 检查的仓库默认是 <see cref="DefaultRepository"/>，可用设置键 <see cref="RepoKey"/>
/// （<c>owner/repo</c>）覆盖，方便开发与离线验证。全程软失败：任何网络/解析问题都变成
/// 中文说明 + 日志，不抛给界面。
/// </summary>
public sealed class UpdateService
{
    public const string DefaultRepository = "luolangaga/WinLand";

    /// <summary>项目主页。</summary>
    public const string GitHubUrl = "https://github.com/" + DefaultRepository;

    /// <summary>国内镜像主页。</summary>
    public const string GitCodeUrl = "https://gitcode.com/" + DefaultRepository;

    /// <summary>启动后自动检查（最多每 <see cref="AutoCheckInterval"/> 一次）。</summary>
    public const string AutoCheckKey = "update.autoCheck";

    /// <summary>上次检查时间（ISO 8601 文本）。</summary>
    public const string LastCheckKey = "update.lastCheck";

    /// <summary>「跳过此版本」记下的 tag。</summary>
    public const string SkipKey = "update.skippedVersion";

    /// <summary>检查哪个仓库（<c>owner/repo</c>）；留空用官方仓库。仅供开发/验证。</summary>
    public const string RepoKey = "update.repo";

    /// <summary>自动检查的最小间隔。</summary>
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);

    /// <summary>打开「关于」页时，距上次检查超过这么久就顺手刷新一次。</summary>
    private static readonly TimeSpan OpenRefreshWindow = TimeSpan.FromMinutes(5);

    /// <summary>单个源的超时：被墙的地址会挂着不返回，不能让它拖住整个回退。</summary>
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(8);

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

    public UpdateService(SettingsService settings, IPluginLogger log)
    {
        _settings = settings;
        _log = log;

        // 上限比单源超时略宽：正常情况下总是每源自己的 CancellationTokenSource 先到点
        _http = new HttpClient { Timeout = SourceTimeout + TimeSpan.FromSeconds(4) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"WinIsland/{IslandSdk.HostVersion}");

        CurrentVersion = ReadCurrentVersion();
        LastCheck = ReadLastCheck();
    }

    /// <summary>检查开始/结束、跳过状态变化时触发；界面据此刷新。</summary>
    public event Action? Changed;

    /// <summary>当前程序版本（exe 的文件版本，CI 由 WINISLAND_VERSION 注入）。</summary>
    public Version CurrentVersion { get; }

    /// <summary>界面显示的版本号，例如 <c>v1.2.3</c>；本地构建显示「开发版」。</summary>
    public string CurrentVersionText => IsDevBuild
        ? "开发版"
        : $"v{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(CurrentVersion.Build, 0)}";

    /// <summary>本地构建：没有注入版本号，文件版本停在 MSBuild 默认的 1.0.0.0。</summary>
    public bool IsDevBuild => CurrentVersion == new Version(1, 0, 0, 0) || CurrentVersion <= new Version(0, 0, 0, 0);

    /// <summary>当前进程架构，用于挑对应安装包。</summary>
    public string ArchitectureText => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        var other => other.ToString().ToLowerInvariant(),
    };

    /// <summary>最近一次检查发现的可用新版本；没有就是 null。</summary>
    public UpdateRelease? Available { get; private set; }

    /// <summary>是否正在检查。</summary>
    public bool IsChecking { get; private set; }

    /// <summary>上次检查时间（持久化，重启后仍然显示）。</summary>
    public DateTimeOffset? LastCheck { get; private set; }

    /// <summary>最近一次检查的结论说明（只存在内存里，供「关于」页显示）。</summary>
    public string? LastMessage { get; private set; }

    /// <summary>上次检查的失败原因；检查成功时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>被「跳过此版本」记下的 tag；没跳过就是空串。</summary>
    public string SkippedTag => _settings.Get(SkipKey, "").Trim();

    /// <summary>要检查的仓库（<c>owner/repo</c>）。</summary>
    public string Repository
    {
        get
        {
            var value = _settings.Get(RepoKey, "").Trim().Trim('/');
            return value.Length == 0 ? DefaultRepository : value;
        }
    }

    /// <summary>启动后是否自动检查（开关打开且距上次检查超过间隔）。</summary>
    public bool AutoCheck
    {
        get => _settings.Get(AutoCheckKey, true);
        set
        {
            _settings.Set(AutoCheckKey, value);
            Raise();
        }
    }

    public bool ShouldAutoCheck()
        => AutoCheck && (LastCheck is not { } last || DateTimeOffset.Now - last > AutoCheckInterval);

    /// <summary>打开「关于」页时是否该刷新一次。</summary>
    public bool ShouldRefreshOnOpen()
        => LastCheck is not { } last || DateTimeOffset.Now - last > OpenRefreshWindow;

    public bool IsSkipped(UpdateRelease release)
        => string.Equals(SkippedTag, release.Tag, StringComparison.OrdinalIgnoreCase);

    public void Skip(UpdateRelease release)
    {
        _settings.Set(SkipKey, release.Tag);
        Raise();
    }

    public void ClearSkip()
    {
        _settings.Set(SkipKey, "");
        Raise();
    }

    /// <summary>
    /// 检查更新。GitCode 优先：它有可用结果就直接用；失败、没发布、或拿到的版本比当前还旧
    /// （镜像滞后等异常态）时再问 GitHub，取两者中较新的一个。
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            IsChecking = true;
            Raise();

            var gitcode = await FetchAsync(UpdateSource.GitCode, ct);
            var best = gitcode.Release;
            SourceFetch? github = null;

            if (best is null || best.Version <= CurrentVersion)
            {
                github = await FetchAsync(UpdateSource.GitHub, ct);
                if (github.Release is { } candidate && (best is null || candidate.Version > best.Version))
                {
                    best = candidate;
                }
            }

            if (best is not null && best.Version > CurrentVersion)
            {
                Available = best;
                LastError = null;
                return Finish(UpdateStatus.UpdateAvailable, $"发现新版本 {best.Tag}", gitcode, github);
            }

            Available = null;

            if (gitcode.Error is not null && github is { Error: not null })
            {
                LastError = $"GitCode 失败：{gitcode.Error}；GitHub 失败：{github.Error}";
                return Finish(UpdateStatus.Failed, LastError, gitcode, github);
            }

            LastError = null;

            if (!gitcode.HasAny && github?.HasAny != true)
            {
                return Finish(UpdateStatus.NoReleases, $"{Repository} 还没有发布过任何版本", gitcode, github);
            }

            return Finish(UpdateStatus.UpToDate, $"已是最新版本（{CurrentVersionText}）", gitcode, github);
        }
        finally
        {
            IsChecking = false;
            _gate.Release();
            Raise();
        }
    }

    private UpdateCheckResult Finish(UpdateStatus status, string message, SourceFetch gitcode, SourceFetch? github)
    {
        LastCheck = DateTimeOffset.Now;
        LastMessage = message;
        _settings.Set(LastCheckKey, LastCheck.Value.ToString("o", CultureInfo.InvariantCulture));

        // 每次检查留一行日志：结论 + 两个源各自的结果，排障时看这一行就够
        var detail = $"GitCode {DescribeFetch(gitcode)} · GitHub {DescribeFetch(github)}";
        if (status == UpdateStatus.Failed)
        {
            _log.Warn($"检查更新失败（{Repository}）：{message}｜{detail}");
        }
        else
        {
            _log.Info($"检查更新（{Repository}）：{message}｜{detail}");
        }

        return new UpdateCheckResult(status, status == UpdateStatus.UpdateAvailable ? Available : null, message);
    }

    private static string DescribeFetch(SourceFetch? fetch)
        => fetch is null ? "未查询"
            : fetch.Error is not null ? "失败"
            : fetch.HasAny ? "有发布"
            : "无发布";

    private async Task<SourceFetch> FetchAsync(UpdateSource source, CancellationToken ct)
    {
        var repo = Repository;
        var label = Label(source);
        var url = source == UpdateSource.GitCode
            ? $"https://api.gitcode.com/api/v5/repos/{repo}/releases?per_page=20"
            : $"https://api.github.com/repos/{repo}/releases?per_page=20";

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SourceTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (source == UpdateSource.GitHub)
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }

            using var response = await _http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                var failure = DescribeFailure(response, body);
                _log.Warn($"{label} 检查更新失败：{failure}");
                return new SourceFetch(null, failure, false);
            }

            var releases = JsonSerializer.Deserialize<List<ReleaseDto>>(body, JsonOptions) ?? new List<ReleaseDto>();
            return new SourceFetch(PickNewest(releases, repo, source, ArchitectureText), null, releases.Count > 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            var failure = $"超过 {SourceTimeout.TotalSeconds:0} 秒无响应";
            _log.Warn($"{label} 检查更新失败：{failure}");
            return new SourceFetch(null, failure, false);
        }
        catch (JsonException)
        {
            const string failure = "返回内容不是合法的发布列表";
            _log.Warn($"{label} 检查更新失败：{failure}");
            return new SourceFetch(null, failure, false);
        }
        catch (Exception ex)
        {
            _log.Warn($"{label} 检查更新失败：{ex.Message}");
            return new SourceFetch(null, ex.Message, false);
        }
    }

    private static string DescribeFailure(HttpResponseMessage response, string body)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "仓库不存在或还没有任何发布";
        }

        // GitHub 未登录时限流 60 次/小时
        if ((response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
            && RateLimitRemaining(response) == "0")
        {
            return "GitHub 接口限流（未登录每小时 60 次），稍后再试";
        }

        // GitCode 的错误体是 {"error_code":…,"error_message":"…"}
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error_message", out var message)
                && message.GetString() is { Length: > 0 } detail)
            {
                return detail;
            }
        }
        catch
        {
        }

        return $"HTTP {(int)response.StatusCode}";
    }

    private static string? RateLimitRemaining(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var values))
        {
            return values.FirstOrDefault();
        }

        return response.Content.Headers.TryGetValues("x-ratelimit-remaining", out var contentValues)
            ? contentValues.FirstOrDefault()
            : null;
    }

    private static UpdateRelease? PickNewest(List<ReleaseDto> releases, string repo, UpdateSource source, string arch)
    {
        UpdateRelease? best = null;
        foreach (var dto in releases)
        {
            var release = TryConvert(dto, repo, source, arch);
            // 不信任接口返回顺序，直接比版本号
            if (release is not null && (best is null || release.Version > best.Version))
            {
                best = release;
            }
        }

        return best;
    }

    private static UpdateRelease? TryConvert(ReleaseDto dto, string repo, UpdateSource source, string arch)
    {
        // 预览版与草稿一律不提示：GitHub 用 prerelease/draft，GitCode 用 release_status=pre
        if (dto.Draft
            || dto.Prerelease
            || string.Equals(dto.ReleaseStatus, "pre", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tag = dto.TagName?.Trim() ?? "";
        // 版本号必须是能解析的数字 tag（如 v1.2.3）；带后缀的预览版（如 v1.2.3-beta、0.0.0-dev）
        // 解析不了，会被这一行挡掉，不需要额外的字符串规则
        if (tag.Length == 0 || !Version.TryParse(tag.TrimStart('v', 'V'), out var version))
        {
            return null;
        }

        var (assetName, assetUrl, assetSize) = MatchAsset(dto.Assets, version, arch);
        if (assetName is not null && string.IsNullOrWhiteSpace(assetUrl))
        {
            // GitCode 偶尔不给 browser_download_url，按官方路径拼一条
            assetUrl = source == UpdateSource.GitCode
                ? $"https://gitcode.com/{repo}/-/releases/{tag}/downloads/{assetName}"
                : $"https://github.com/{repo}/releases/download/{tag}/{assetName}";
        }

        return new UpdateRelease
        {
            Version = version,
            Tag = tag,
            Title = string.IsNullOrWhiteSpace(dto.Name) ? tag : dto.Name.Trim(),
            Notes = dto.Body?.Trim() ?? "",
            Source = source,
            SourceLabel = Label(source),
            ReleasePageUrl = ReleasePage(source, repo),
            AssetName = assetName,
            AssetUrl = assetUrl,
            AssetSize = assetSize,
        };
    }

    /// <summary>挑当前架构的安装包：先精确匹配 <c>WinIsland-{版本}-{架构}-setup.exe</c>，再退化成后缀匹配。</summary>
    private static (string? Name, string? Url, long Size) MatchAsset(List<AssetDto>? assets, Version version, string arch)
    {
        if (assets is null || assets.Count == 0)
        {
            return (null, null, 0);
        }

        var suffix = $"-{arch}-setup.exe";
        var exact = $"WinIsland-{version}-{arch}-setup.exe";

        var match = assets.FirstOrDefault(a => string.Equals(a.Name, exact, StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(a => a.Name?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true);

        return match is null ? (null, null, 0) : (match.Name, match.BrowserDownloadUrl, match.Size);
    }

    private static string Label(UpdateSource source)
        => source == UpdateSource.GitCode ? "GitCode 国内镜像" : "GitHub 官方源";

    private static string ReleasePage(UpdateSource source, string repo)
        => source == UpdateSource.GitCode
            ? $"https://gitcode.com/{repo}/releases"
            : $"https://github.com/{repo}/releases";

    private static Version ReadCurrentVersion()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (Version.TryParse(info.FileVersion, out var version))
                {
                    return version;
                }
            }
        }
        catch
        {
        }

        return new Version(0, 0, 0);
    }

    private DateTimeOffset? ReadLastCheck()
        => DateTimeOffset.TryParse(
            _settings.Get(LastCheckKey, ""),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : null;

    private void Raise() => Changed?.Invoke();

    /// <summary>单个源的抓取结果：<c>Error</c> 非 null 表示这个源不可用。</summary>
    private sealed record SourceFetch(UpdateRelease? Release, string? Error, bool HasAny);
}
