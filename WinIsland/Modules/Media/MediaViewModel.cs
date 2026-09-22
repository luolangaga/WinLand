using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinIsland.Modules.Media;

public sealed class MediaViewModel : INotifyPropertyChanged
{
    private string _title = "";
    private string _artist = "";
    private string _albumTitle = "";
    private string _sourceAppName = "";
    private bool _isPlaying;
    private bool _glowEnabled = true;
    private bool _lyricsEnabled = true;
    private ImageSource? _thumbnail;
    private ImageSource? _sourceAppIcon;
    private TimeSpan _position;
    private TimeSpan _duration;
    private bool _hasProgress;
    private bool _hasMultipleSessions;
    private int _sessionIndex;
    private int _sessionCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Action? TogglePlayPause { get; set; }
    public Action? SkipNext { get; set; }
    public Action? SkipPrevious { get; set; }
    public Action? SwitchToNextSession { get; set; }
    public Action? SwitchToPrevSession { get; set; }
    public Action? RefreshTimeline { get; set; }
    public Action<TimeSpan>? SeekTo { get; set; }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Artist
    {
        get => _artist;
        set => SetField(ref _artist, value);
    }

    public string AlbumTitle
    {
        get => _albumTitle;
        set => SetField(ref _albumTitle, value);
    }

    public bool LyricsEnabled
    {
        get => _lyricsEnabled;
        set => SetField(ref _lyricsEnabled, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (SetField(ref _isPlaying, value))
            {
                Raise(nameof(PlayPauseGlyph));
            }
        }
    }

    public bool GlowEnabled
    {
        get => _glowEnabled;
        set => SetField(ref _glowEnabled, value);
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (SetField(ref _thumbnail, value))
            {
                Raise(nameof(ThumbnailVisibility));
                Raise(nameof(PlaceholderVisibility));
            }
        }
    }

    public string SourceAppName
    {
        get => _sourceAppName;
        set => SetField(ref _sourceAppName, value);
    }

    public ImageSource? SourceAppIcon
    {
        get => _sourceAppIcon;
        set
        {
            if (SetField(ref _sourceAppIcon, value))
            {
                Raise(nameof(SourceAppIconVisibility));
            }
        }
    }

    public TimeSpan Position
    {
        get => _position;
        set
        {
            if (SetField(ref _position, value))
            {
                Raise(nameof(Progress));
                Raise(nameof(PositionText));
            }
        }
    }

    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            if (SetField(ref _duration, value))
            {
                Raise(nameof(Progress));
                Raise(nameof(DurationText));
                Raise(nameof(HasProgress));
            }
        }
    }

    public bool HasProgress
    {
        get => _hasProgress;
        set
        {
            if (SetField(ref _hasProgress, value))
            {
                Raise(nameof(ProgressVisibility));
            }
        }
    }

    public bool HasMultipleSessions
    {
        get => _hasMultipleSessions;
        set
        {
            if (SetField(ref _hasMultipleSessions, value))
            {
                Raise(nameof(SwitcherVisibility));
            }
        }
    }

    public int SessionIndex
    {
        get => _sessionIndex;
        set => SetField(ref _sessionIndex, value);
    }

    public int SessionCount
    {
        get => _sessionCount;
        set => SetField(ref _sessionCount, value);
    }

    public double Progress => _duration.TotalMilliseconds > 0
        ? Math.Clamp(_position.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1)
        : 0;

    public string PositionText => FormatTime(_position);
    public string DurationText => FormatTime(_duration);

    public Visibility SourceAppIconVisibility => _sourceAppIcon != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => _hasProgress ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SwitcherVisibility => _hasMultipleSessions ? Visibility.Visible : Visibility.Collapsed;

    public string PlayPauseGlyph => _isPlaying ? "\uE769" : "\uE768";
    public Visibility ThumbnailVisibility => _thumbnail != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlaceholderVisibility => _thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

    public static string FormatTime(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{t.Minutes}:{t.Seconds:D2}";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
