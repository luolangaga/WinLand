using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace WeatherIsland;

/// <summary>
/// 自绘天气图标。
///
/// 为什么不用图标字体：Segoe Fluent Icons 与 Segoe MDL2 Assets 里都没有天气字形
/// （E9C4 附近的天气位是空的，塞进去只会显示成方框），所以这里按 24×24 的设计网格
/// 用形状画出来，交给 Viewbox 缩放任意尺寸。
/// </summary>
internal static class WeatherIcon
{
    private const double Design = 24;

    private static readonly Color SunColor = Color.FromArgb(255, 0xFF, 0xC8, 0x3D);
    private static readonly Color CloudLight = Color.FromArgb(255, 0xF4, 0xF5, 0xF7);
    private static readonly Color CloudDark = Color.FromArgb(255, 0xC3, 0xCA, 0xD3);
    private static readonly Color RainColor = Color.FromArgb(255, 0x5A, 0xB0, 0xF5);
    private static readonly Color SnowColor = Color.FromArgb(255, 0xEA, 0xF4, 0xFF);
    private static readonly Color BoltColor = Color.FromArgb(255, 0xFF, 0xD2, 0x4D);
    private static readonly Color FogColor = Color.FromArgb(255, 0xCB, 0xD1, 0xD8);

    /// <summary>按 WMO 天气代码生成一个 24×24 的画布。<paramref name="isDay"/> 决定晴天画太阳还是月亮。</summary>
    public static Canvas Create(int code, bool isDay = true)
    {
        var canvas = new Canvas { Width = Design, Height = Design };

        switch (WeatherCodes.Kind(code))
        {
            case WeatherKind.Sunny:
                if (isDay) AddSun(canvas, 12, 12, 5.5, 9.0, 3.4, 2.1);
                else AddMoon(canvas, 12, 12, 6.2, 1.6);
                break;

            case WeatherKind.PartlyCloudy:
                if (isDay) AddSun(canvas, 8.2, 8.2, 3.6, 6.6, 2.6, 1.7);
                else AddMoon(canvas, 8.8, 7.8, 4.4, 1.3);
                AddCloud(canvas, 1.2, 2.8, CloudLight);
                break;

            case WeatherKind.Cloudy:
                AddCloud(canvas, 0, 0, CloudLight);
                break;

            case WeatherKind.Overcast:
                AddCloud(canvas, 0, 0, CloudDark);
                break;

            case WeatherKind.Fog:
                AddCloud(canvas, 0, -2.2, CloudLight);
                AddRoundedRect(canvas, 6.0, 16.4, 13.0, 2.2, 1.1, FogColor);
                AddRoundedRect(canvas, 7.5, 20.2, 10.0, 2.2, 1.1, FogColor);
                break;

            case WeatherKind.Rain:
                AddCloud(canvas, 0, -1.6, CloudDark);
                AddRotatedRect(canvas, 9.0, 20.4, 1.8, 4.4, 16, RainColor);
                AddRotatedRect(canvas, 13.2, 21.4, 1.8, 4.4, 16, RainColor);
                AddRotatedRect(canvas, 17.4, 20.4, 1.8, 4.4, 16, RainColor);
                break;

            case WeatherKind.Snow:
                AddCloud(canvas, 0, -1.6, CloudLight);
                AddEllipse(canvas, 7.6, 19.0, 2.8, 2.8, SnowColor);
                AddEllipse(canvas, 11.8, 20.2, 2.8, 2.8, SnowColor);
                AddEllipse(canvas, 16.0, 19.0, 2.8, 2.8, SnowColor);
                break;

            case WeatherKind.Thunder:
                AddCloud(canvas, 0, -2.4, CloudDark);
                AddPolygon(canvas,
                    (13.8, 14.6), (10.0, 20.6), (12.3, 20.6), (10.6, 23.8), (14.6, 18.2), (12.2, 18.2));
                break;
        }

        return canvas;
    }

    private static void AddSun(Canvas canvas, double cx, double cy, double radius,
        double rayRadius, double rayLength, double rayWidth)
    {
        for (var i = 0; i < 8; i++)
        {
            var angle = i * 45d;
            var radians = angle * Math.PI / 180d;
            AddRotatedRect(canvas,
                cx + rayRadius * Math.Cos(radians),
                cy + rayRadius * Math.Sin(radians),
                rayWidth, rayLength, angle + 90, SunColor);
        }

        AddEllipse(canvas, cx - radius, cy - radius, radius * 2, radius * 2, SunColor);
    }

    /// <summary>
    /// 夜间晴 / 少云用月亮。画的是满月加两处环形山（Canvas 上没法"挖洞"，月牙得用两段
    /// 圆弧拼路径，不值得为这点装饰引入解析不确定的几何），旁边点两颗小星强调"夜里"。
    /// </summary>
    private static void AddMoon(Canvas canvas, double cx, double cy, double radius, double starRadius)
    {
        var moon = Color.FromArgb(255, 0xDC, 0xE6, 0xF5);
        var crater = Color.FromArgb(255, 0xBD, 0xCC, 0xE2);

        AddEllipse(canvas, cx - radius, cy - radius, radius * 2, radius * 2, moon);
        AddEllipse(canvas, cx - radius * 0.46, cy - radius * 0.34, radius * 0.52, radius * 0.52, crater);
        AddEllipse(canvas, cx + radius * 0.20, cy + radius * 0.12, radius * 0.36, radius * 0.36, crater);

        AddEllipse(canvas, cx + radius * 1.02, cy - radius * 1.20, starRadius, starRadius, moon);
        AddEllipse(canvas, cx + radius * 1.52, cy - radius * 0.62, starRadius * 0.7, starRadius * 0.7, moon);
    }

    private static void AddCloud(Canvas canvas, double dx, double dy, Color color)
    {
        AddEllipse(canvas, 4.5 + dx, 9.5 + dy, 8, 8, color);
        AddEllipse(canvas, 7.5 + dx, 6.5 + dy, 10.5, 10.5, color);
        AddEllipse(canvas, 12.5 + dx, 10 + dy, 8, 8, color);
        AddRoundedRect(canvas, 4.5 + dx, 13.5 + dy, 17, 6.5, 3.25, color);
    }

    private static void AddEllipse(Canvas canvas, double x, double y, double w, double h, Color color)
    {
        var ellipse = new Ellipse { Width = w, Height = h, Fill = new SolidColorBrush(color) };
        Canvas.SetLeft(ellipse, x);
        Canvas.SetTop(ellipse, y);
        canvas.Children.Add(ellipse);
    }

    private static void AddRoundedRect(Canvas canvas, double x, double y, double w, double h, double radius, Color color)
    {
        var rect = new Rectangle
        {
            Width = w,
            Height = h,
            RadiusX = radius,
            RadiusY = radius,
            Fill = new SolidColorBrush(color),
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        canvas.Children.Add(rect);
    }

    private static void AddRotatedRect(Canvas canvas, double cx, double cy,
        double w, double h, double angle, Color color)
    {
        var rect = new Rectangle
        {
            Width = w,
            Height = h,
            Fill = new SolidColorBrush(color),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = angle },
        };
        Canvas.SetLeft(rect, cx - w / 2);
        Canvas.SetTop(rect, cy - h / 2);
        canvas.Children.Add(rect);
    }

    private static void AddPolygon(Canvas canvas, params (double X, double Y)[] points)
    {
        var polygon = new Polygon { Fill = new SolidColorBrush(BoltColor) };
        foreach (var (x, y) in points)
        {
            polygon.Points.Add(new Point(x, y));
        }
        canvas.Children.Add(polygon);
    }
}
