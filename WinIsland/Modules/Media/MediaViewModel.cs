using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinIsland.Modules.Media;

/// <summary>正在播放视图模型（紧凑/展开视图共用）。</summary>
public sealed class MediaViewModel : INotifyPropertyChanged
{
    private string _title = "";
    private string _artist = "";
    private bool _isPlaying;
    private bool _glowEnabled = true;
    private ImageSource? _thumbnail;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>由模块注入的播控动作。</summary>
    public Action? TogglePlayPause { get; set; }
    public Action? SkipNext { get; set; }
    public Action? SkipPrevious { get; set; }

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

    public string PlayPauseGlyph => _isPlaying ? "\uE769" : "\uE768";

    public Visibility ThumbnailVisibility => _thumbnail != null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PlaceholderVisibility => _thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
