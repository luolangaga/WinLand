using System.Reflection;
using System.Net;
using WeatherIsland;
foreach (var (metres, label) in new[] { (500d, "很差"), (3000d, "较差"), (8000d, "一般"), (15000d, "良好"), (30000d, "极佳") })
{
    using var service = new WeatherService();
    var client = new HttpClient(new FakeWeather(metres));
    typeof(WeatherService).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, client);
    var snapshot = await service.FetchAsync("30,120");
    if (snapshot.Error != null || snapshot.Visibility != metres / 1000 || WeatherCodes.VisibilityLabel(snapshot.Visibility) != label)
        throw new Exception($"{metres}: {snapshot.Error}, {snapshot.Visibility}, {WeatherCodes.VisibilityLabel(snapshot.Visibility)}");
    Console.WriteLine($"PASS {metres} m -> {snapshot.Visibility} km: {label}");
}
foreach (var adapter in HardwareMonitor.GpuAdapters.Read()) Console.WriteLine($"DXGI {adapter.Name}: {adapter.Luid:X16}");
class FakeWeather(double metres) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.RequestUri!.Host.Contains("geocoding") ? "{\"results\":[{\"name\":\"Test\",\"latitude\":30,\"longitude\":120}]}" :
            System.Text.Json.JsonSerializer.Serialize(new { current = new { temperature_2m = 20 }, hourly = new { time = new[] { DateTime.Now.ToString("yyyy-MM-ddTHH:00") }, visibility = new[] { metres } } });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
