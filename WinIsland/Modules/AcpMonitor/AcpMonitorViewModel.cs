using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinIsland.Modules.AcpMonitor;

public sealed class AcpMonitorViewModel : INotifyPropertyChanged
{
    private AgentSessionState _state = AgentSessionState.Idle;
    private string _agentName = "";
    private string _agentIcon = "\uE932";
    private string _currentTaskName = "";
    private int _completedSteps;
    private int _totalSteps;
    private string _currentToolName = "";
    private AgentToolCallStatus _currentToolStatus;
    private double _progressPercent;
    private long _usedTokens;
    private double _costAmount;
    private string _costCurrency = "USD";
    private bool _isRunning;
    private string _statusText = "未连接";
    private Windows.UI.Color _accentColor = Windows.UI.Color.FromArgb(255, 138, 43, 226);
    private IReadOnlyList<AgentPlanEntry> _planEntries = Array.Empty<AgentPlanEntry>();
    private IReadOnlyList<AgentToolCall> _toolCalls = Array.Empty<AgentToolCall>();

    public event PropertyChangedEventHandler? PropertyChanged;

    public AgentSessionState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                Raise(nameof(IsConnected));
                Raise(nameof(IsRunning));
                Raise(nameof(StatusIcon));
                Raise(nameof(StatusColor));
            }
        }
    }

    public string AgentName
    {
        get => _agentName;
        set => SetField(ref _agentName, value);
    }

    public string AgentIcon
    {
        get => _agentIcon;
        set => SetField(ref _agentIcon, value);
    }

    public string CurrentTaskName
    {
        get => _currentTaskName;
        set
        {
            if (SetField(ref _currentTaskName, value))
                Raise(nameof(TaskDisplay));
        }
    }

    public string TaskDisplay => string.IsNullOrEmpty(_currentTaskName) ? _agentName : _currentTaskName;

    public int CompletedSteps
    {
        get => _completedSteps;
        set
        {
            if (SetField(ref _completedSteps, value))
            {
                Raise(nameof(ProgressPercent));
                Raise(nameof(StepText));
            }
        }
    }

    public int TotalSteps
    {
        get => _totalSteps;
        set
        {
            if (SetField(ref _totalSteps, value))
            {
                Raise(nameof(ProgressPercent));
                Raise(nameof(StepText));
            }
        }
    }

    public string CurrentToolName
    {
        get => _currentToolName;
        set => SetField(ref _currentToolName, value);
    }

    public AgentToolCallStatus CurrentToolStatus
    {
        get => _currentToolStatus;
        set => SetField(ref _currentToolStatus, value);
    }

    public double ProgressPercent
    {
        get => _totalSteps > 0 ? (double)_completedSteps / _totalSteps : 0;
        set => SetField(ref _progressPercent, value);
    }

    public string StepText => _totalSteps > 0 ? $"{_completedSteps}/{_totalSteps}" : "";

    public long UsedTokens
    {
        get => _usedTokens;
        set => SetField(ref _usedTokens, value);
    }

    public double CostAmount
    {
        get => _costAmount;
        set => SetField(ref _costAmount, value);
    }

    public string CostCurrency
    {
        get => _costCurrency;
        set => SetField(ref _costCurrency, value);
    }

    public bool IsRunning
    {
        get => _state == AgentSessionState.Running;
        set => SetField(ref _isRunning, value);
    }

    public bool IsConnected => _state is AgentSessionState.Connected or AgentSessionState.Running;

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public Windows.UI.Color AccentColor
    {
        get => _accentColor;
        set
        {
            if (SetField(ref _accentColor, value))
                Raise(nameof(AccentColorBrush));
        }
    }

    public SolidColorBrush AccentColorBrush => new(_accentColor);

    public IReadOnlyList<AgentPlanEntry> PlanEntries
    {
        get => _planEntries;
        set => SetField(ref _planEntries, value);
    }

    public IReadOnlyList<AgentToolCall> ToolCalls
    {
        get => _toolCalls;
        set => SetField(ref _toolCalls, value);
    }

    public string StatusIcon => _state switch
    {
        AgentSessionState.Running => "\uE9F5",
        AgentSessionState.Connected => "\uE73E",
        AgentSessionState.Idle => "\uE711",
        _ => "\uE711"
    };

    public string StatusColor => _state switch
    {
        AgentSessionState.Running => "#FF9B59B6",
        AgentSessionState.Connected => "#FF6CCB5F",
        AgentSessionState.Idle => "#FF95A5A6",
        _ => "#FF95A5A6"
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
