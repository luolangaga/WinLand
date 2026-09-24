using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeatherIsland;

/// <summary>逐小时预报里的一格。</summary>
public sealed record HourlyForecast(
    string Label,
    int Code,
    double Temperature,
    int PrecipProbability,
    double WindSpeed,
    bool IsDay);

/// <summary>一天的预报。</summary>
public sealed record DailyForecast(
    string Label,
    string Weekday,
    int Code,
    double Max,
    double Min,
    int PrecipProbability,
    double UvIndex,
    string Sunrise,
    string Sunset,
    double WindMax,
    double PrecipSum);

/// <summary>空气质量（Open-Meteo 空气质量接口）。</summary>
public sealed record AirQuality(double Pm25, double Pm10, double UsAqi, double EuropeanAqi);

/// <summary>一次取回的天气快照。没有数据时 <see cref="Error"/> 带原因。</summary>
public sealed record WeatherSnapshot
{
    public bool HasData { get; init; }
    public string Place { get; init; } = "";
    public bool IsDay { get; init; } = true;

    // ---- 当前实况 ----
    public double Temperature { get; init; }
    public double ApparentTemperature { get; init; }
    public double Humidity { get; init; }
    public double DewPoint { get; init; }
    public double Pressure { get; init; }
    public double CloudCover { get; init; }
    public double Visibility { get; init; }          // km
    public double WindSpeed { get; init; }           // m/s
    public double WindGust { get; init; }            // m/s
    public double WindDirection { get; init; }       // 度
    public double Precipitation { get; init; }       // mm
    public int PrecipProbabilityNow { get; init; }   // %
    public int Code { get; init; }

    // ---- 今日汇总 ----
    public double TodayMax { get; init; }
    public double TodayMin { get; init; }
    public double TodayApparentMax { get; init; }
    public double TodayApparentMin { get; init; }
    public int PrecipProbability { get; init; }      // 今日最高降水概率 %
    public double PrecipSum { get; init; }           // 今日累计降水 mm
    public double UvIndexMax { get; init; }
    public string Sunrise { get; init; } = "";
    public string Sunset { get; init; } = "";
    public string Daylight { get; init; } = "";

    public AirQuality? Air { get; init; }
    public IReadOnlyList<HourlyForecast> Hours { get; init; } = Array.Empty<HourlyForecast>();
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
        62 => "中雨",
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

    private static readonly string[] CompassPoints =
        { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };

    /// <summary>风向角度 → 八方位中文。</summary>
    public static string Compass(double degrees)
    {
        var normalized = ((degrees % 360) + 360) % 360;
        var index = (int)Math.Round(normalized / 45) % 8;
        return CompassPoints[index];
    }

    /// <summary>紫外线指数等级（WHO 口径）。</summary>
    public static string UvLabel(double uv) => uv switch
    {
        < 0.5 => "无",
        < 3 => "弱",
        < 6 => "中等",
        < 8 => "强",
        < 11 => "很强",
        _ => "极强",
    };

    /// <summary>PM2.5 浓度等级（μg/m³，参考国标日均限值）。</summary>
    public static string Pm25Label(double pm25) => pm25 switch
    {
        <= 35 => "优",
        <= 75 => "良",
        <= 115 => "轻度污染",
        <= 150 => "中度污染",
        <= 250 => "重度污染",
        _ => "严重污染",
    };

    /// <summary>美国 AQI 等级。</summary>
    public static string AqiLabel(double aqi) => aqi switch
    {
        <= 50 => "优",
        <= 100 => "良",
        <= 150 => "轻度污染",
        <= 200 => "中度污染",
        <= 300 => "重度污染",
        _ => "严重污染",
    };

    /// <summary>能见度描述。</summary>
    public static string VisibilityLabel(double km) => km switch
    {
        < 1 => "很差",
        < 5 => "较差",
        < 10 => "一般",
        < 20 => "良好",
        _ => "极佳",
    };
}

/// <summary>
/// Open-Meteo 客户端：免费、不用注册、不用 API Key。
/// 先用城市名换经纬度（geocoding），再取实况 + 逐小时 + 7 天预报；空气质量单独一次请求。
/// </summary>
public sealed class WeatherService : IDisposable
{
    private const string GeoEndpoint = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ForecastEndpoint = "https://api.open-meteo.com/v1/forecast";
    private const string AirEndpoint = "https://air-quality-api.open-meteo.com/v1/air-quality";

    private const int ForecastDays = 7;
    private const int HourlyCount = 24;

    private static readonly string[] Weekdays = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

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
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinIsland-WeatherIsland/1.1");
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
            + "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,"
            + "precipitation,weather_code,cloud_cover,surface_pressure,"
            + "wind_speed_10m,wind_direction_10m,wind_gusts_10m"
            + "&hourly=temperature_2m,weather_code,precipitation_probability,dew_point_2m,"
            + "visibility,uv_index,wind_speed_10m,is_day"
            + "&daily=weather_code,temperature_2m_max,temperature_2m_min,"
            + "apparent_temperature_max,apparent_temperature_min,"
            + "precipitation_probability_max,precipitation_sum,uv_index_max,"
            + "sunrise,sunset,daylight_duration,wind_speed_10m_max"
            + "&timezone=auto&forecast_days={3}",
            ForecastEndpoint, latitude, longitude, ForecastDays);

        var payload = await GetJsonAsync<ForecastResponse>(url, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("天气服务返回了空数据");

        var current = payload.Current ?? throw new InvalidOperationException("天气服务没有返回当前天气");

        // 空气质量是另一个接口、另一个域名：失败只让这一块显示「--」，不能拖垮主数据
        var air = await TryFetchAirAsync(latitude, longitude, cancellationToken).ConfigureAwait(false);

        var hourStart = FindCurrentHour(payload.Hourly?.Time);

        var hours = new List<HourlyForecast>();
        if (payload.Hourly?.Time is { Count: > 0 } hourlyTimes)
        {
            for (var i = hourStart; i < hourlyTimes.Count && hours.Count < HourlyCount; i++)
            {
                hours.Add(new HourlyForecast(
                    HourLabel(hourlyTimes[i], hours.Count),
                    Int(payload.Hourly.WeatherCode, i),
                    Num(payload.Hourly.Temperature, i),
                    (int)Math.Round(Num(payload.Hourly.PrecipitationProbability, i)),
                    Num(payload.Hourly.WindSpeed, i),
                    Int(payload.Hourly.IsDay, i) != 0));
            }
        }

        var days = new List<DailyForecast>();
        if (payload.Daily?.Time is { Count: > 0 } dailyTimes)
        {
            for (var i = 0; i < dailyTimes.Count && i < ForecastDays; i++)
            {
                days.Add(new DailyForecast(
                    DayLabel(i, dailyTimes[i]),
                    WeekdayLabel(dailyTimes[i]),
                    Int(payload.Daily.WeatherCode, i),
                    Num(payload.Daily.TemperatureMax, i),
                    Num(payload.Daily.TemperatureMin, i),
                    (int)Math.Round(Num(payload.Daily.PrecipitationProbabilityMax, i)),
                    Num(payload.Daily.UvIndexMax, i),
                    Clock(Str(payload.Daily.Sunrise, i)),
                    Clock(Str(payload.Daily.Sunset, i)),
                    Num(payload.Daily.WindSpeedMax, i),
                    Num(payload.Daily.PrecipitationSum, i)));
            }
        }

        var today = days.Count > 0 ? days[0] : null;

        return new WeatherSnapshot
        {
            HasData = true,
            Place = place,
            IsDay = current.IsDay != 0,

            Temperature = current.Temperature,
            ApparentTemperature = current.Apparent,
            Humidity = current.Humidity,
            DewPoint = Num(payload.Hourly?.DewPoint, hourStart),
            Pressure = current.Pressure,
            CloudCover = current.CloudCover,
            Visibility = Num(payload.Hourly?.Visibility, hourStart),
            WindSpeed = current.WindSpeed,
            WindGust = current.WindGust,
            WindDirection = current.WindDirection,
            Precipitation = current.Precipitation,
            PrecipProbabilityNow = hours.Count > 0 ? hours[0].PrecipProbability : 0,
            Code = current.WeatherCode,

            TodayMax = today?.Max ?? current.Temperature,
            TodayMin = today?.Min ?? current.Temperature,
            TodayApparentMax = Num(payload.Daily?.ApparentMax, 0),
            TodayApparentMin = Num(payload.Daily?.ApparentMin, 0),
            PrecipProbability = today?.PrecipProbability ?? 0,
            PrecipSum = today?.PrecipSum ?? 0,
            UvIndexMax = today?.UvIndex ?? 0,
            Sunrise = today?.Sunrise ?? "",
            Sunset = today?.Sunset ?? "",
            Daylight = Duration(Num(payload.Daily?.DaylightDuration, 0)),

            Air = air,
            Hours = hours,
            Days = days,
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>空气质量接口（独立域名）。拿不到就返回 null，主数据照常显示。</summary>
    private async Task<AirQuality?> TryFetchAirAsync(
        double latitude, double longitude, CancellationToken cancellationToken)
    {
        try
        {
            var url = string.Format(CultureInfo.InvariantCulture,
                "{0}?latitude={1:0.####}&longitude={2:0.####}&current=pm2_5,pm10,us_aqi,european_aqi&timezone=auto",
                AirEndpoint, latitude, longitude);

            var payload = await GetJsonAsync<AirResponse>(url, cancellationToken).ConfigureAwait(false);
            var current = payload?.Current;
            if (current is null) return null;

            return new AirQuality(
                current.Pm25 ?? 0,
                current.Pm10 ?? 0,
                current.UsAqi ?? 0,
                current.EuropeanAqi ?? 0);
        }
        catch (Exception)
        {
            // 空气质量只是锦上添花：失败了不留痕迹，卡片上显示「--」
            return null;
        }
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

    // ---------------------------------------------------------------- 取值 / 格式化

    /// <summary>找到"当前所在的整点"在逐小时数组里的下标（数组从今天 00:00 起）。</summary>
    private static int FindCurrentHour(List<string?>? times)
    {
        if (times is null || times.Count == 0) return 0;

        var now = DateTime.Now;
        for (var i = 0; i < times.Count; i++)
        {
            if (times[i] is not { Length: > 0 } text) continue;
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)) continue;

            // 这一格覆盖 [stamp, stamp+1h)：结束时刻晚于现在，就是当前这一格
            if (stamp.AddHours(1) > now) return i;
        }

        return 0;
    }

    private static double Num(List<double?>? values, int index)
        => values is not null && index >= 0 && index < values.Count ? values[index] ?? 0 : 0;

    private static int Int(List<int?>? values, int index)
        => values is not null && index >= 0 && index < values.Count ? values[index] ?? 0 : 0;

    private static string Str(List<string?>? values, int index)
        => values is not null && index >= 0 && index < values.Count ? values[index] ?? "" : "";

    /// <summary>"2026-09-24T06:12" → "06:12"。</summary>
    private static string Clock(string iso)
        => iso.Length >= 16 ? iso.Substring(11, 5) : "--:--";

    private static string HourLabel(string? iso, int offset)
    {
        if (offset == 0) return "现在";
        return iso is { Length: >= 16 } ? iso.Substring(11, 5) : "--:--";
    }

    private static string DayLabel(int index, string? iso) => index switch
    {
        0 => "今天",
        1 => "明天",
        2 => "后天",
        _ => WeekdayLabel(iso),
    };

    private static string WeekdayLabel(string? iso)
    {
        if (iso is { Length: >= 10 }
            && DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return Weekdays[(int)date.DayOfWeek];
        }

        return "";
    }

    /// <summary>秒 → "12 小时 34 分"。</summary>
    private static string Duration(double seconds)
    {
        if (seconds <= 0) return "";
        var span = TimeSpan.FromSeconds(seconds);
        return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException => "网络不通，检查一下代理或网络",
        TaskCanceledException => "请求超时",
        _ => ex.Message,
    };

    // ---------------------------------------------------------------- JSON 模型

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
        [JsonPropertyName("hourly")] public HourlyBlock? Hourly { get; set; }
        [JsonPropertyName("daily")] public DailyBlock? Daily { get; set; }
    }

    private sealed class CurrentBlock
    {
        [JsonPropertyName("temperature_2m")] public double Temperature { get; set; }
        [JsonPropertyName("relative_humidity_2m")] public double Humidity { get; set; }
        [JsonPropertyName("apparent_temperature")] public double Apparent { get; set; }
        [JsonPropertyName("is_day")] public int IsDay { get; set; } = 1;
        [JsonPropertyName("precipitation")] public double Precipitation { get; set; }
        [JsonPropertyName("weather_code")] public int WeatherCode { get; set; }
        [JsonPropertyName("cloud_cover")] public double CloudCover { get; set; }
        [JsonPropertyName("surface_pressure")] public double Pressure { get; set; }
        [JsonPropertyName("wind_speed_10m")] public double WindSpeed { get; set; }
        [JsonPropertyName("wind_direction_10m")] public double WindDirection { get; set; }
        [JsonPropertyName("wind_gusts_10m")] public double WindGust { get; set; }
    }

    private sealed class HourlyBlock
    {
        [JsonPropertyName("time")] public List<string?>? Time { get; set; }
        [JsonPropertyName("temperature_2m")] public List<double?>? Temperature { get; set; }
        [JsonPropertyName("weather_code")] public List<int?>? WeatherCode { get; set; }
        [JsonPropertyName("precipitation_probability")] public List<double?>? PrecipitationProbability { get; set; }
        [JsonPropertyName("dew_point_2m")] public List<double?>? DewPoint { get; set; }
        [JsonPropertyName("visibility")] public List<double?>? Visibility { get; set; }
        [JsonPropertyName("uv_index")] public List<double?>? UvIndex { get; set; }
        [JsonPropertyName("wind_speed_10m")] public List<double?>? WindSpeed { get; set; }
        [JsonPropertyName("is_day")] public List<int?>? IsDay { get; set; }
    }

    private sealed class DailyBlock
    {
        [JsonPropertyName("time")] public List<string?>? Time { get; set; }
        [JsonPropertyName("weather_code")] public List<int?>? WeatherCode { get; set; }
        [JsonPropertyName("temperature_2m_max")] public List<double?>? TemperatureMax { get; set; }
        [JsonPropertyName("temperature_2m_min")] public List<double?>? TemperatureMin { get; set; }
        [JsonPropertyName("apparent_temperature_max")] public List<double?>? ApparentMax { get; set; }
        [JsonPropertyName("apparent_temperature_min")] public List<double?>? ApparentMin { get; set; }
        [JsonPropertyName("precipitation_probability_max")] public List<double?>? PrecipitationProbabilityMax { get; set; }
        [JsonPropertyName("precipitation_sum")] public List<double?>? PrecipitationSum { get; set; }
        [JsonPropertyName("uv_index_max")] public List<double?>? UvIndexMax { get; set; }
        [JsonPropertyName("sunrise")] public List<string?>? Sunrise { get; set; }
        [JsonPropertyName("sunset")] public List<string?>? Sunset { get; set; }
        [JsonPropertyName("daylight_duration")] public List<double?>? DaylightDuration { get; set; }
        [JsonPropertyName("wind_speed_10m_max")] public List<double?>? WindSpeedMax { get; set; }
    }

    private sealed class AirResponse
    {
        [JsonPropertyName("current")] public AirCurrent? Current { get; set; }
    }

    private sealed class AirCurrent
    {
        [JsonPropertyName("pm2_5")] public double? Pm25 { get; set; }
        [JsonPropertyName("pm10")] public double? Pm10 { get; set; }
        [JsonPropertyName("us_aqi")] public double? UsAqi { get; set; }
        [JsonPropertyName("european_aqi")] public double? EuropeanAqi { get; set; }
    }
}
