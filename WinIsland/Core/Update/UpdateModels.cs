using System.Text.Json.Serialization;

namespace WinIsland.Core.Update;

/// <summary>更新来源：GitCode 优先，失败回退 GitHub。</summary>
public enum UpdateSource
{
    GitCode,
    GitHub,
}

/// <summary>一次检查的结论。</summary>
public enum UpdateStatus
{
    /// <summary>已经是最新版本。</summary>
    UpToDate,

    /// <summary>发现新版本。</summary>
    UpdateAvailable,

    /// <summary>仓库里还没有可提示的正式版本。</summary>
    NoReleases,

    /// <summary>所有源都没能给出结果。</summary>
    Failed,
}

/// <summary>一个可用的新版本（已经解析好版本号、下载页与安装包直链）。</summary>
public sealed class UpdateRelease
{
    public required Version Version { get; init; }

    /// <summary>发布 tag（如 <c>v1.2.4</c>）；「跳过此版本」按它记录。</summary>
    public required string Tag { get; init; }

    public required string Title { get; init; }

    /// <summary>发布说明的 Markdown 原文，可能为空。</summary>
    public required string Notes { get; init; }

    public required UpdateSource Source { get; init; }

    /// <summary>来源的中文名（GitCode 国内镜像 / GitHub 官方源）。</summary>
    public required string SourceLabel { get; init; }

    /// <summary>发布列表页；界面上的「前往下载」打开它。</summary>
    public required string ReleasePageUrl { get; init; }

    /// <summary>匹配当前架构的安装包名；没匹配到为 null。</summary>
    public string? AssetName { get; init; }

    /// <summary>安装包直链；没匹配到为 null。</summary>
    public string? AssetUrl { get; init; }

    /// <summary>安装包大小（只有 GitHub 会返回，GitCode 为 0）。</summary>
    public long AssetSize { get; init; }
}

/// <summary>检查结果；<c>Message</c> 是可以直接显示给用户的中文说明。</summary>
public readonly record struct UpdateCheckResult(UpdateStatus Status, UpdateRelease? Release, string Message);

/// <summary>
/// 发布信息。GitCode 与 GitHub 的 release JSON 形状一致
/// （<c>tag_name</c> / <c>name</c> / <c>body</c> / <c>prerelease</c> / <c>assets[].name</c> /
/// <c>assets[].browser_download_url</c>），两个源共用这一套 DTO。
/// </summary>
internal sealed class ReleaseDto
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("body")] public string? Body { get; set; }

    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }

    /// <summary>草稿（只有 GitHub 有这个概念）。</summary>
    [JsonPropertyName("draft")] public bool Draft { get; set; }

    /// <summary>GitCode 专有：<c>latest</c> / <c>pre</c> / <c>none</c>。</summary>
    [JsonPropertyName("release_status")] public string? ReleaseStatus { get; set; }

    [JsonPropertyName("assets")] public List<AssetDto>? Assets { get; set; }
}

/// <summary>发布资产（GitCode 是附件，GitHub 是 release asset）。</summary>
internal sealed class AssetDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }

    /// <summary>只有 GitHub 会返回。</summary>
    [JsonPropertyName("size")] public long Size { get; set; }
}
