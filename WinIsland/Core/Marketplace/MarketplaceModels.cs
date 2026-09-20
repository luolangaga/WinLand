using System.Text.Json.Serialization;

namespace WinIsland.Core.Marketplace;

/// <summary>
/// 社区市场清单（仓库根目录 index.json，schema 1）。
/// 客户端只请求这一个文件：所有插件元数据与缩略图标都在里面，
/// 不做逐插件的 API/文件请求。
/// </summary>
public sealed class MarketplaceIndex
{
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; init; } = CurrentSchema;

    [JsonPropertyName("repo")]
    public string? Repo { get; init; }

    [JsonPropertyName("updated")]
    public DateTimeOffset Updated { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("plugins")]
    public List<MarketplacePlugin> Plugins { get; init; } = new();
}

/// <summary>index.json 里的单个插件条目。字段与 plugin.json 对齐，另加包与展示信息。</summary>
public sealed class MarketplacePlugin
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("icon_glyph")]
    public string? IconGlyph { get; init; }

    [JsonPropertyName("homepage")]
    public string? Homepage { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; init; } = new();

    [JsonPropertyName("api_version")]
    public int ApiVersion { get; init; } = 1;

    [JsonPropertyName("min_host_version")]
    public string? MinHostVersion { get; init; }

    /// <summary>包在仓库内的相对路径，如 <c>plugins/hw-monitor/hw-monitor.lwp</c>。</summary>
    [JsonPropertyName("package")]
    public string Package { get; init; } = "";

    [JsonPropertyName("package_size")]
    public long PackageSize { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    /// <summary>仓库内 README 的相对路径（详情页按需拉取）。</summary>
    [JsonPropertyName("readme")]
    public string? Readme { get; init; }

    /// <summary>仓库内原图 logo 的相对路径（供浏览器查看/备用）。</summary>
    [JsonPropertyName("logo")]
    public string? Logo { get; init; }

    /// <summary>缩略 logo（data:image/png;base64,...），随清单一次下发，避免逐插件请求。</summary>
    [JsonPropertyName("logo_data")]
    public string? LogoData { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    public bool HasLogo => !string.IsNullOrWhiteSpace(LogoData);

    /// <summary>宿主能否加载该版本（API 版本 / 最低宿主版本）。</summary>
    public bool IsCompatible(out string? reason)
    {
        if (ApiVersion > IslandSdk.ApiVersion)
        {
            reason = $"需要 API v{ApiVersion}，当前宿主只支持 v{IslandSdk.ApiVersion}，请更新 WinIsland";
            return false;
        }

        if (System.Version.TryParse(MinHostVersion, out var minHost) && minHost > IslandSdk.HostVersion)
        {
            reason = $"需要 WinIsland >= {minHost}，当前为 {IslandSdk.HostVersion}";
            return false;
        }

        reason = null;
        return true;
    }

    public string DescribeSize() => PackageSize <= 0 ? "未知大小" : FormatSize(PackageSize);

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024.0:0.##} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes} B";
    }
}
