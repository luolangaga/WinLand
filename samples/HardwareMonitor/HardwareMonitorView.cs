using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace HardwareMonitor;

public sealed record CompactOptions(bool ShowCpu, bool ShowGpu, bool ShowNet, bool ShowFps);

public sealed class HardwareMonitorView : UserControl, IMorphView
{
    private const double ExpandedHeight = 104;

    private readonly TextBlock _compactText = NewValueText(13);
    private readonly StackPanel _expandedPanel;
    private readonly TextBlock _fpsValue = NewValueText(20);
    private readonly TextBlock _fpsProcess = NewHintText();
    private readonly TextBlock _cpuValue = NewValueText(13);
    private readonly TextBlock _cpuName = NewHintText();
    private readonly TextBlock _gpuValue = NewValueText(13);
    private readonly TextBlock _gpuName = NewHintText();
    private readonly TextBlock _netValue = NewValueText(13);
    private readonly TextBlock _netName = NewHintText();
    private readonly DispatcherQueueTimer _morphTimer;

    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;
    private double _progress;

    public HardwareMonitorView()
    {
        var icon = new FontIcon
        {
            Glyph = "\uE950",
            FontSize = 16,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 194, 255)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var compactRow = new Grid { ColumnSpacing = 10 };
        compactRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        compactRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        compactRow.Children.Add(icon);
        Grid.SetColumn(_compactText, 1);
        compactRow.Children.Add(_compactText);

        _expandedPanel = new StackPanel
        {
            Height = 0,
            Opacity = 0,
            Spacing = 7,
            Padding = new Thickness(0, 8, 0, 0),
            Children =
            {
                BuildFpsRow(),
                BuildMetricRow("CPU", _cpuValue, _cpuName),
                BuildMetricRow("GPU", _gpuValue, _gpuName),
                BuildMetricRow("网络", _netValue, _netName),
            },
        };

        var root = new StackPanel
        {
            Padding = new Thickness(16, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { compactRow, _expandedPanel },
        };

        Content = root;

        _morphTimer = DispatcherQueue.CreateTimer();
        _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
        _morphTimer.IsRepeating = true;
        _morphTimer.Tick += (_, _) => OnMorphTick();
    }

    public UIElement View => this;

    public void Apply(HardwareSnapshot snapshot, CompactOptions options)
    {
        _compactText.Text = BuildCompactText(snapshot, options);

        if (snapshot.Fps > 0)
        {
            _fpsValue.Text = snapshot.Fps.ToString("0");
            _fpsProcess.Text = string.IsNullOrEmpty(snapshot.FpsProcess) ? "前台窗口" : snapshot.FpsProcess;
        }
        else
        {
            _fpsValue.Text = "--";
            _fpsProcess.Text = snapshot.FpsRequiresAdmin
                ? "FPS 需要以管理员模式启动 WinIsland"
                : snapshot.FpsStatus;
        }

        _cpuValue.Text = FormatPercent(snapshot.CpuLoad);
        _cpuName.Text = snapshot.CpuName;
        _gpuValue.Text = FormatPercent(snapshot.GpuLoad);
        _gpuName.Text = snapshot.GpuName;
        _netValue.Text = $"↓ {FormatSpeed(snapshot.NetDown)}  ↑ {FormatSpeed(snapshot.NetUp)}";
        _netName.Text = snapshot.NetName;
    }

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(1, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(0, duration);

    private void StartMorph(double target, TimeSpan duration)
    {
        _morphFrom = _progress;
        _morphTarget = target;
        _morphDuration = duration;
        _morphStart = DateTimeOffset.UtcNow;
        _morphTimer.Start();
    }

    private void OnMorphTick()
    {
        var elapsed = (DateTimeOffset.UtcNow - _morphStart).TotalMilliseconds;
        var duration = Math.Max(1, _morphDuration.TotalMilliseconds);
        var t = Math.Clamp(elapsed / duration, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        ApplyProgress(_morphFrom + (_morphTarget - _morphFrom) * eased);
        if (t >= 1)
        {
            ApplyProgress(_morphTarget);
            _morphTimer.Stop();
        }
    }

    private void ApplyProgress(double value)
    {
        _progress = value;
        _expandedPanel.Height = ExpandedHeight * value;
        _expandedPanel.Opacity = value;
    }

    private UIElement BuildFpsRow()
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = "帧率",
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _fpsValue.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(_fpsProcess, 1);
        _fpsProcess.VerticalAlignment = VerticalAlignment.Center;
        _fpsProcess.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_fpsValue, 2);

        row.Children.Add(label);
        row.Children.Add(_fpsProcess);
        row.Children.Add(_fpsValue);
        return row;
    }

    private static UIElement BuildMetricRow(string label, TextBlock value, TextBlock name)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        value.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(value, 1);
        row.Children.Add(value);

        name.MaxWidth = 168;
        Grid.SetColumn(name, 2);
        row.Children.Add(name);
        return row;
    }

    private static TextBlock NewValueText(double size) => new()
    {
        FontSize = size,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        VerticalAlignment = VerticalAlignment.Center,
        Text = "--",
    };

    private static TextBlock NewHintText() => new()
    {
        FontSize = 11,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)),
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = 1,
    };

    private static string BuildCompactText(HardwareSnapshot snapshot, CompactOptions options)
    {
        var parts = new List<string>();
        if (options.ShowCpu) parts.Add($"CPU {FormatPercent(snapshot.CpuLoad)}");
        if (options.ShowGpu) parts.Add($"GPU {FormatPercent(snapshot.GpuLoad)}");
        if (options.ShowNet) parts.Add($"↓{FormatSpeed(snapshot.NetDown)} ↑{FormatSpeed(snapshot.NetUp)}");
        if (options.ShowFps) parts.Add($"FPS {(snapshot.Fps > 0 ? snapshot.Fps.ToString("0") : "--")}");
        return parts.Count == 0 ? "硬件监控" : string.Join("  ", parts);
    }

    private static string FormatPercent(double value) => value < 0 ? "--" : $"{value:0}%";

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1) return "0";
        if (bytesPerSecond < 1024) return $"{bytesPerSecond:0}B/s";
        if (bytesPerSecond < 1024 * 1024) return $"{bytesPerSecond / 1024:0}K/s";
        if (bytesPerSecond < 1024 * 1024 * 1024) return $"{bytesPerSecond / 1024 / 1024:0.0}M/s";
        return $"{bytesPerSecond / 1024 / 1024 / 1024:0.0}G/s";
    }
}
