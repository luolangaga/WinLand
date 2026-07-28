using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinIsland.Modules.AiMonitor;

public sealed class AiMonitorViewModel : INotifyPropertyChanged
{
    private string _taskName = "";
    private string _status = "";
    private string _detail = "";
    private string _subDetail = "";
    private double _progress;
    private bool _isRunning;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string TaskName
    {
        get => _taskName;
        set => SetField(ref _taskName, value);
    }

    public string Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                Raise(nameof(StatusGlyph));
                Raise(nameof(StatusColor));
            }
        }
    }

    public string Detail
    {
        get => _detail;
        set => SetField(ref _detail, value);
    }

    public string SubDetail
    {
        get => _subDetail;
        set => SetField(ref _subDetail, value);
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (SetField(ref _progress, value))
            {
                Raise(nameof(ProgressText));
                Raise(nameof(HasProgress));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetField(ref _isRunning, value))
            {
                Raise(nameof(StatusGlyph));
                Raise(nameof(StatusColor));
                Raise(nameof(RunningVisibility));
                Raise(nameof(IdleVisibility));
            }
        }
    }

    public string StatusGlyph => _isRunning ? "\uE965" : "\uE73E";
    public SolidColorBrush StatusColor => _isRunning
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F))
        : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x90, 0x90, 0x95));

    public string ProgressText => _progress > 0 ? $"{_progress:P0}" : "";
    public Visibility HasProgress => _progress > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunningVisibility => _isRunning ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IdleVisibility => _isRunning ? Visibility.Collapsed : Visibility.Visible;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
