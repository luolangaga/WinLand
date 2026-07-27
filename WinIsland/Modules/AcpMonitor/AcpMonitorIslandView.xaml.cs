using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinIsland.Core;

namespace WinIsland.Modules.AcpMonitor;

public sealed partial class AcpMonitorIslandView : UserControl, IMorphView
{
    private Storyboard? _morphStoryboard;
    private readonly Storyboard _spinnerStoryboard = new();
    private bool _spinnerRunning;
    private bool _isExpanded;

    private readonly DispatcherQueueTimer _glowTimer;
    private float _glowPhase;

    internal AcpMonitorViewModel ViewModel { get; }

    UIElement IMorphView.View => this;

    internal AcpMonitorIslandView(AcpMonitorViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();

        BuildSpinnerAnimation();
        Loaded += (_, _) => SyncSpinner();
        Unloaded += (_, _) => { StopSpinner(); _glowTimer?.Stop(); };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e == null) return;
            switch (e.PropertyName)
            {
                case nameof(AcpMonitorViewModel.IsRunning):
                case nameof(AcpMonitorViewModel.IsConnected):
                    SyncSpinner();
                    SyncGlow();
                    break;
                case nameof(AcpMonitorViewModel.AgentName):
                    AgentName.Text = ViewModel.TaskDisplay;
                    break;
                case nameof(AcpMonitorViewModel.CurrentTaskName):
                case nameof(AcpMonitorViewModel.TaskDisplay):
                    AgentName.Text = ViewModel.TaskDisplay;
                    break;
                case nameof(AcpMonitorViewModel.ProgressPercent):
                    ProgressBar.Value = ViewModel.ProgressPercent;
                    break;
                case nameof(AcpMonitorViewModel.StepText):
                    StepCount.Text = ViewModel.StepText;
                    break;
                case nameof(AcpMonitorViewModel.CompletedSteps):
                case nameof(AcpMonitorViewModel.TotalSteps):
                    UpdatePlanListUI();
                    break;
            }
        };

        _glowTimer = DispatcherQueue.CreateTimer();
        _glowTimer.Interval = TimeSpan.FromMilliseconds(16);
        _glowTimer.Tick += (_, _) => OnGlowTick();
    }

    public void AnimateToExpanded(TimeSpan duration)
    {
        _morphStoryboard?.Stop();
        _isExpanded = true;
        SyncGlow();

        var sb = new Storyboard();
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var midTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5);

        sb.Children.Add(DiscreteKeyFrameAnim(RootGrid, "Padding",
            new Thickness(20, 16, 20, 12), midTime));
        sb.Children.Add(Anim(MainGrid, "ColumnSpacing", MainGrid.ColumnSpacing, 14, duration, easing));
        sb.Children.Add(DiscreteKeyFrameAnim(IconBorder, "CornerRadius",
            new CornerRadius(14), midTime));

        sb.Children.Add(Anim(IconBorder, "Width", 26, 48, duration, easing));
        sb.Children.Add(Anim(IconBorder, "Height", 26, 48, duration, easing));
        sb.Children.Add(Anim(AgentIcon, "FontSize", 12, 20, duration, easing));
        sb.Children.Add(Anim(AgentName, "FontSize", 12, 15, duration, easing));

        var halfDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.4);
        sb.Children.Add(Anim(ProgressRow, "Height", 0, 22, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.15)));
        sb.Children.Add(Anim(ProgressRow, "Opacity", 0, 1, halfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.15)));

        sb.Children.Add(Anim(SpinnerIcon, "Opacity", 1, 0,
            TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3),
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        var expHalfDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5);
        sb.Children.Add(Anim(ExpandedPanel, "Height", 0, 80, expHalfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));
        sb.Children.Add(Anim(ExpandedPanel, "Opacity", 0, 1, expHalfDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));

        if (ViewModel.IsRunning)
        {
            sb.Children.Add(Anim(GlowLayer, "Opacity", 0, 1,
                TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5),
                new CubicEase { EasingMode = EasingMode.EaseOut },
                beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));
        }

        sb.Completed += (_, _) =>
        {
            AgentName.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            AgentName.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
            if (ViewModel.IsRunning) GlowLayer.Opacity = 1;
            SyncGlow();
            UpdatePlanListUI();
        };

        _morphStoryboard = sb;
        sb.Begin();
    }

    public void AnimateToCompact(TimeSpan duration)
    {
        _morphStoryboard?.Stop();
        _isExpanded = false;
        _glowTimer.Stop();

        var sb = new Storyboard();
        var easing = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var midTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5);

        sb.Children.Add(DiscreteKeyFrameAnim(RootGrid, "Padding",
            new Thickness(8, 0, 14, 0), midTime));
        sb.Children.Add(Anim(MainGrid, "ColumnSpacing", MainGrid.ColumnSpacing, 8, duration, easing));
        sb.Children.Add(DiscreteKeyFrameAnim(IconBorder, "CornerRadius",
            new CornerRadius(7), midTime));

        sb.Children.Add(Anim(IconBorder, "Width", IconBorder.ActualWidth > 0 ? IconBorder.ActualWidth : 48, 26, duration, easing));
        sb.Children.Add(Anim(IconBorder, "Height", IconBorder.ActualHeight > 0 ? IconBorder.ActualHeight : 48, 26, duration, easing));
        sb.Children.Add(Anim(AgentIcon, "FontSize", AgentIcon.FontSize > 0 ? AgentIcon.FontSize : 20, 12, duration, easing));
        sb.Children.Add(Anim(AgentName, "FontSize", AgentName.FontSize > 0 ? AgentName.FontSize : 15, 12, duration, easing));

        var thirdDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3);
        sb.Children.Add(Anim(ProgressRow, "Height", ProgressRow.ActualHeight > 0 ? ProgressRow.ActualHeight : 22, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        sb.Children.Add(Anim(ProgressRow, "Opacity", ProgressRow.Opacity > 0 ? ProgressRow.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Children.Add(Anim(ExpandedPanel, "Height", ExpandedPanel.ActualHeight > 0 ? ExpandedPanel.ActualHeight : 80, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        sb.Children.Add(Anim(ExpandedPanel, "Opacity", ExpandedPanel.Opacity > 0 ? ExpandedPanel.Opacity : 1, 0, thirdDur,
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        var twoThirdDur = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.5);
        sb.Children.Add(Anim(SpinnerIcon, "Opacity", SpinnerIcon.Opacity >= 0 ? SpinnerIcon.Opacity : 0, 1, twoThirdDur,
            new CubicEase { EasingMode = EasingMode.EaseOut },
            beginTime: TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3)));

        sb.Children.Add(Anim(GlowLayer, "Opacity",
            GlowLayer.Opacity > 0 ? GlowLayer.Opacity : 1, 0,
            TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.3),
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        sb.Completed += (_, _) =>
        {
            AgentName.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            AgentName.Foreground = new SolidColorBrush(
                Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
            SyncSpinner();
        };

        _morphStoryboard = sb;
        sb.Begin();
    }

    private void UpdatePlanListUI()
    {
        if (!_isExpanded) return;
        PlanList.Children.Clear();

        var entries = ViewModel.PlanEntries;
        if (entries == null || entries.Count == 0) return;

        for (int i = 0; i < Math.Min(entries.Count, 8); i++)
        {
            var entry = entries[i];
            var icon = entry.Status switch
            {
                AgentPlanEntryStatus.Completed => "\uE73E",
                AgentPlanEntryStatus.InProgress => "\uE72C",
                _ => "\uE711"
            };
            var color = entry.Status switch
            {
                AgentPlanEntryStatus.Completed => "#FF6CCB5F",
                AgentPlanEntryStatus.InProgress => "#FF9B59B6",
                _ => "#FF95A5A6"
            };

            var row = new Grid
            {
                ColumnSpacing = 8,
                Padding = new Thickness(4, 2, 4, 2),
                Opacity = entry.Status == AgentPlanEntryStatus.Pending ? 0.5 : 1.0,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var statusIcon = new FontIcon
            {
                Glyph = icon,
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(
                    0xFF,
                    byte.Parse(color.Substring(3, 2), System.Globalization.NumberStyles.HexNumber),
                    byte.Parse(color.Substring(5, 2), System.Globalization.NumberStyles.HexNumber),
                    byte.Parse(color.Substring(7, 2), System.Globalization.NumberStyles.HexNumber))),
            };
            row.Children.Add(statusIcon);

            var text = new TextBlock
            {
                Text = entry.Content,
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            PlanList.Children.Add(row);
        }
    }

    internal void UpdateFromPlan(IReadOnlyList<AgentPlanEntry> entries)
    {
        int completed = 0;
        int inProgress = 0;
        foreach (var e in entries)
        {
            if (e.Status == AgentPlanEntryStatus.Completed) completed++;
            else if (e.Status == AgentPlanEntryStatus.InProgress) inProgress++;
        }
        ViewModel.CompletedSteps = completed;
        ViewModel.TotalSteps = entries.Count;
        ViewModel.ProgressPercent = entries.Count > 0 ? (double)completed / entries.Count : 0;

        if (inProgress > 0)
            ViewModel.State = AgentSessionState.Running;
        else if (completed == entries.Count && entries.Count > 0)
            ViewModel.State = AgentSessionState.Connected;

        UpdatePlanListUI();
    }

    internal void UpdateFromToolCall(AgentToolCall toolCall)
    {
        ViewModel.CurrentToolName = toolCall.Title;
        ViewModel.CurrentToolStatus = toolCall.Status;
        if (toolCall.Status == AgentToolCallStatus.InProgress)
            ViewModel.State = AgentSessionState.Running;
    }

    #region Glow Animation

    private void OnGlowTick()
    {
        _glowPhase += 0.04f;
        float pulse1 = 0.15f * ((float)Math.Sin(_glowPhase) * 0.5f + 0.5f);
        float pulse2 = 0.12f * ((float)Math.Sin(_glowPhase * 0.8f + 1.0f) * 0.5f + 0.5f);

        Glow1Scale.ScaleX = Glow1Scale.ScaleY = 0.50 + pulse1 * 1.5;
        Glow1.Opacity = 0.35 + pulse1 * 0.55;
        Glow2Scale.ScaleX = Glow2Scale.ScaleY = 0.45 + pulse2 * 1.2;
        Glow2.Opacity = 0.30 + pulse2 * 0.45;
    }

    private void SyncGlow()
    {
        bool shouldGlow = _isExpanded && ViewModel.IsRunning;
        if (shouldGlow && !_glowTimer.IsRunning)
            _glowTimer.Start();
        else if (!shouldGlow && _glowTimer.IsRunning)
            _glowTimer.Stop();
    }

    #endregion

    #region Spinner Animation

    private void BuildSpinnerAnimation()
    {
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromMilliseconds(1500)),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(anim, SpinnerRotate);
        Storyboard.SetTargetProperty(anim, "Angle");
        _spinnerStoryboard.Children.Add(anim);
    }

    private void SyncSpinner()
    {
        if (ViewModel.IsRunning && IsLoaded && !_isExpanded)
        {
            if (!_spinnerRunning)
            {
                _spinnerStoryboard.Begin();
                _spinnerRunning = true;
            }
        }
        else
        {
            StopSpinner();
        }
    }

    private void StopSpinner()
    {
        if (_spinnerRunning)
        {
            _spinnerStoryboard.Stop();
            _spinnerRunning = false;
        }
    }

    #endregion

    #region Animation Helpers

    private static DoubleAnimation Anim(
        DependencyObject target, string property,
        double from, double to, TimeSpan duration,
        EasingFunctionBase easing, TimeSpan? beginTime = null)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            EasingFunction = easing,
            EnableDependentAnimation = true,
        };
        if (beginTime.HasValue) anim.BeginTime = beginTime.Value;
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        return anim;
    }

    private static ObjectAnimationUsingKeyFrames DiscreteKeyFrameAnim(
        DependencyObject target, string property, object value, TimeSpan keyTime)
    {
        var anim = new ObjectAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new DiscreteObjectKeyFrame
        {
            KeyTime = keyTime,
            Value = value,
        });
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, property);
        return anim;
    }

    #endregion
}
