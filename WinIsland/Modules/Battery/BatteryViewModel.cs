using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinIsland.Modules.Battery;

public sealed class BatteryViewModel : INotifyPropertyChanged
{
    private int _percent;
    private bool _isCharging;
    private double _chargePower;
    private string _statusText = "未在充电";
    private bool _glowEnabled = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Percent
    {
        get => _percent;
        set
        {
            if (SetField(ref _percent, value))
            {
                Raise(nameof(PercentText));
                Raise(nameof(BatteryGlyph));
                Raise(nameof(BatteryColor));
                Raise(nameof(PowerArcAngle));
            }
        }
    }

    public bool IsCharging
    {
        get => _isCharging;
        set
        {
            if (SetField(ref _isCharging, value))
            {
                Raise(nameof(BatteryGlyph));
                Raise(nameof(BatteryColor));
                Raise(nameof(ChargingVisibility));
                Raise(nameof(NotChargingVisibility));
            }
        }
    }

    public double ChargePower
    {
        get => _chargePower;
        set
        {
            if (SetField(ref _chargePower, value))
            {
                Raise(nameof(ChargePowerText));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public bool GlowEnabled
    {
        get => _glowEnabled;
        set => SetField(ref _glowEnabled, value);
    }

    public string PercentText => $"{_percent}%";

    public string ChargePowerText => _chargePower > 0 ? $"{_chargePower:F1} W" : "";

    public string BatteryGlyph => _isCharging ? "\uE859" : (_percent switch
    {
        >= 90 => "\uE859",
        >= 70 => "\uE858",
        >= 50 => "\uE857",
        >= 30 => "\uE856",
        >= 10 => "\uE855",
        _ => "\uE854"
    });

    public SolidColorBrush BatteryColor => _isCharging
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F))
        : (_percent switch
        {
            >= 50 => new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F)),
            >= 20 => new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF5, 0xA6, 0x23)),
            _ => new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x4D, 0x3D))
        });

    public double PowerArcAngle => _percent * 3.6;

    public Visibility ChargingVisibility => _isCharging ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NotChargingVisibility => _isCharging ? Visibility.Collapsed : Visibility.Visible;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
