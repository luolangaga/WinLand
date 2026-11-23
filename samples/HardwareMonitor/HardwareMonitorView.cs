using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;
using WinIsland.PluginViews;

namespace HardwareMonitor;

public sealed record CompactOptions(bool ShowCpu, bool ShowGpu, bool ShowNet, bool ShowFps);

public sealed class HardwareMonitorView : UserControl, IMorphView
{
    private const double ExpandedHeight = 104;

    // 中性色共享两支画刷（岛体换主题时只改它们的 Color，所有文字当场跟着变）
    private readonly SolidColorBrush _valueBrush = new();
    private readonly SolidColorBrush _hintBrush = new();

    private readonly TextBlock _compactText;
    private readonly StackPanel _expandedPanel;
    private readonly TextBlock _fpsValue;
    private readonly TextBlock _fpsProcess;
    private readonly TextBlock _cpuValue;
    private readonly TextBlock _cpuName;
    private readonly TextBlock _gpuValue;
    private readonly TextBlock _gpuName;
    private readonly TextBlock _netValue;
    private readonly TextBlock _netName;
    private readonly MorphProgress _morph;
    private readonly IIslandTheme _theme;

    private double _progress;

    public HardwareMonitorView(IIslandTheme theme)
    {
        _theme = theme;
        ApplyThemeColors();
        // 岛体换主题：共享画刷换个颜色，所有文字当场跟着变
        _theme.Changed += ApplyThemeColors;

        // 颜色要等主题上完再建（字段初始化器里拿不到 _theme）
        _compactText = NewValueText(13, _valueBrush);
        _fpsValue = NewValueText(20, _valueBrush);
        _fpsProcess = NewHintText(_hintBrush);
        _cpuValue = NewValueText(18, _valueBrush);
        _cpuName = NewHintText(_hintBrush);
        _gpuValue = NewValueText(18, _valueBrush);
        _gpuName = NewHintText(_hintBrush);
        _netValue = NewValueText(12, _valueBrush);
        _netName = NewHintText(_hintBrush);
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
                BuildDashboard(),
            },
        };

        var root = new StackPanel
        {
            Padding = new Thickness(16, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { compactRow, _expandedPanel },
        };

        Content = root;

        _morph = new MorphProgress(ApplyProgress);
        Unloaded += (_, _) => _morph.Complete();
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

    private void StartMorph(double target, TimeSpan duration) => _morph.To(target, duration);

    private void ApplyProgress(double value)
    {
        _progress = value;
        _expandedPanel.Height = ExpandedHeight * value;
        _expandedPanel.Opacity = value;
    }

    private UIElement BuildDashboard()
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var metrics = new[] { ("CPU", _cpuValue, _cpuName), ("GPU", _gpuValue, _gpuName),
            ("帧率 · FPS", _fpsValue, _fpsProcess), ("网络", _netValue, _netName) };
        for (int i = 0; i < metrics.Length; i++)
        {
            var (label, value, name) = metrics[i];
            var cell = new Grid { RowSpacing = 2, ColumnSpacing = 8 };
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cell.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = _hintBrush, VerticalAlignment = VerticalAlignment.Center });
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(value, 1);
            cell.Children.Add(value);
            Grid.SetRow(name, 1);
            Grid.SetColumnSpan(name, 2);
            cell.Children.Add(name);
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            grid.Children.Add(cell);
        }
        return grid;
    }

    /// <summary>中性色：岛体深色时是白色系，浅色（Fluent + 浅色系统）时是黑色系。</summary>
    private void ApplyThemeColors()
    {
        _valueBrush.Color = Neutral(255);
        _hintBrush.Color = Neutral(150);
    }

    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    private static TextBlock NewValueText(double size, Brush foreground) => new()
    {
        FontSize = size,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = foreground,
        VerticalAlignment = VerticalAlignment.Center,
        Text = "--",
    };

    private static TextBlock NewHintText(Brush foreground) => new()
    {
        FontSize = 11,
        Foreground = foreground,
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
