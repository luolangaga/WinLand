using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeatherIsland;

/// <summary>一天的预报。</summary>
public sealed record DailyForecast(string Label, int Code, double Max, double Min);

/// <summary>一次取回的天气快照。没有数据时 <see cref="Error"/> 带原因。</summary>
public sealed record WeatherSnapshot
{
    public bool HasData { get; init; }
    public string Place { get; init; } = "";
    public double Temperature { get; init; }
    public int Code { get; init; }
    public IReadOnlyList<DailyForecast> Days { get; init; } = Array.Empty<DailyForecast>();
    public DateTimeOffset UpdatedAt { get; init; }
    public string? Error { get; init; }

    public static WeatherSnapshot Empty { get; } = new() { Error = "正在获取天气…" };
}

/// <summary>图标形态：自绘图标只区分这几类。</summary>
public enum WeatherKind
{
    Sunny,
    PartlyCloudy,
    Cloudy,
    Overcast,
    Fog,
    Rain,
    Snow,
    Thunder,
}

public static class WeatherCodes
{
    public static WeatherKind Kind(int code) => code switch
    {
        0 => WeatherKind.Sunny,
        1 or 2 => WeatherKind.PartlyCloudy,
        3 => WeatherKind.Overcast,
        45 or 48 => WeatherKind.Fog,
        51 or 53 or 55 or 56 or 57 => WeatherKind.Rain,
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => WeatherKind.Rain,
        71 or 73 or 75 or 77 or 85 or 86 => WeatherKind.Snow,
        95 or 96 or 99 => WeatherKind.Thunder,
        _ => WeatherKind.Cloudy,
    };

    public static string Describe(int code) => code switch
    {
        0 => "晴",
        1 => "晴间多云",
        2 => "多云",
        3 => "阴",
        45 => "有雾",
        48 => "雾凇",
        51 => "小毛毛雨",
        53 => "毛毛雨",
        55 => "大毛毛雨",
        56 => "冻毛毛雨",
        57 => "冻雨",
        61 => "小雨",
        63 => "中雨",
        65 => "大雨",
        66 => "冻雨",
        67 => "强冻雨",
        71 => "小雪",
        73 => "中雪",
        75 => "大雪",
        77 => "米雪",
        80 => "阵雨",
        81 => "强阵雨",
        82 => "暴雨",
        85 => "阵雪",
        86 => "强阵雪",
        95 => "雷阵雨",
        96 => "雷阵雨伴冰雹",
        99 => "强雷阵雨伴冰雹",
        _ => "未知",
    };
}

/// <summary>
/// Open-Meteo 客户端：免费、不用注册、不用 API Key。
/// 先用城市名换经纬度（geocoding），再取当前天气 + 三天预报。
/// </summary>
public sealed class WeatherService : IDisposable
{
    private const string GeoEndpoint = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ForecastEndpoint = "https://api.open-meteo.com/v1/forecast";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public WeatherService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinIsland-WeatherIsland/1.0");
    }

    public void Dispose()
    {
        _disposed = true;
        _http.Dispose();
    }

    /// <summary>取一次天气。失败时返回带 <see cref="WeatherSnapshot.Error"/> 的空快照，不抛异常。</summary>
    public async Task<WeatherSnapshot> FetchAsync(string city, CancellationToken cancellationToken = default)
    {
        if (_disposed) return WeatherSnapshot.Empty with { Place = city, Error = "服务已释放" };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FetchCoreAsync(city, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return WeatherSnapshot.Empty with { Place = city, Error = "请求超时" };
        }
        catch (Exception ex)
        {
            return WeatherSnapshot.Empty with { Place = city, Error = Describe(ex) };
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WeatherSnapshot> FetchCoreAsync(string city, CancellationToken cancellationToken)
    {
        var query = (city ?? string.Empty).Trim();
        if (query.Length == 0) query = "北京";

        var (latitude, longitude, place) = await ResolveAsync(query, cancellationToken).ConfigureAwait(false);

        // 坐标必须用不变区域格式（小数点），否则某些系统区域设置会拼出 "30,25" 这种非法值
        var url = string.Format(CultureInfo.InvariantCulture,
            "{0}?latitude={1:0.####}&longitude={2:0.####}"
            + "&current=temperature_2m,weather_code"
            + "&daily=weather_code,temperature_2m_max,temperature_2m_min"
            + "&timezone=auto&forecast_days=3",
            ForecastEndpoint, latitude, longitude);

        var payload = await GetJsonAsync<ForecastResponse>(url, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("天气服务返回了空数据");

        var current = payload.Current ?? throw new InvalidOperationException("天气服务没有返回当前天气");

        var days = new List<DailyForecast>();
        var labels = new[] { "今天", "明天", "后天" };
        if (payload.Daily?.Time is { Count: > 0 } times)
        {
            for (var i = 0; i < times.Count && i < labels.Length; i++)
            {
                days.Add(new DailyForecast(
                    labels[i],
                    At(payload.Daily.WeatherCode, i),
                    At(payload.Daily.TemperatureMax, i),
                    At(payload.Daily.TemperatureMin, i)));
            }
        }

        return new WeatherSnapshot
        {
            HasData = true,
            Place = place,
            Temperature = current.Temperature,
            Code = current.WeatherCode,
            Days = days,
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    private async Task<(double Latitude, double Longitude, string Place)> ResolveAsync(
        string query, CancellationToken cancellationToken)
    {
        if (TryParseCoordinates(query, out var lat, out var lon))
        {
            return (lat, lon, query);
        }

        var url = $"{GeoEndpoint}?name={Uri.EscapeDataString(query)}&count=1&language=zh&format=json";
        var geo = await GetJsonAsync<GeoResponse>(url, cancellationToken).ConfigureAwait(false);

        if (geo?.Results is not { Count: > 0 })
        {
            throw new InvalidOperationException($"没找到城市「{query}」");
        }

        var hit = geo.Results[0];
        var name = string.IsNullOrWhiteSpace(hit.Name) ? query : hit.Name!;
        return (hit.Latitude, hit.Longitude, name);
    }

    /// <summary>支持直接填「纬度,经度」，省掉一次地名解析。</summary>
    private static bool TryParseCoordinates(string text, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;

        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out latitude)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out longitude)) return false;

        return latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static double At(List<double>? values, int index)
        => values is not null && index < values.Count ? values[index] : 0;

    private static int At(List<int>? values, int index)
        => values is not null && index < values.Count ? values[index] : 0;

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException => "网络不通，检查一下代理或网络",
        TaskCanceledException => "请求超时",
        _ => ex.Message,
    };

    private sealed class GeoResponse
    {
        [JsonPropertyName("results")] public List<GeoResult>? Results { get; set; }
    }

    private sealed class GeoResult
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("latitude")] public double Latitude { get; set; }
        [JsonPropertyName("longitude")] public double Longitude { get; set; }
    }

    private sealed class ForecastResponse
    {
        [JsonPropertyName("current")] public CurrentBlock? Current { get; set; }
        [JsonPropertyName("daily")] public DailyBlock? Daily { get; set; }
    }

    private sealed class CurrentBlock
    {
        [JsonPropertyName("temperature_2m")] public double Temperature { get; set; }
        [JsonPropertyName("weather_code")] public int WeatherCode { get; set; }
    }

    private sealed class DailyBlock
    {
        [JsonPropertyName("time")] public List<string>? Time { get; set; }
        [JsonPropertyName("weather_code")] public List<int>? WeatherCode { get; set; }
        [JsonPropertyName("temperature_2m_max")] public List<double>? TemperatureMax { get; set; }
        [JsonPropertyName("temperature_2m_min")] public List<double>? TemperatureMin { get; set; }
    }
}
