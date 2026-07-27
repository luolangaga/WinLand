using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Control;
using WinIsland.Core;

namespace WinIsland.Modules.Media;

/// <summary>
/// 正在播放模块：通过 GlobalSystemMediaTransportControls (GSMTC)
/// 监听系统媒体会话，把正在播放的音乐常驻显示在灵动岛上。
/// 只提供内容（IslandLiveContent），尺寸/悬浮/展开全部由主程序处理。
/// </summary>
public sealed class MediaModule : IIslandModule
{
    public string Id => "media";
    public string DisplayName => "正在播放";

    private IDynamicIslandApi _api = null!;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private MediaViewModel _vm = null!;
    private IslandLiveContent _content = null!;
    private AudioLevelMonitor? _levelMonitor;
    private bool _enabled;
    private bool _liveShown;
    private int _refreshVersion;
    private string? _thumbTrackKey;

    /// <summary>当前播放状态描述（供设置页展示）。</summary>
    public string StatusText { get; private set; } = "当前没有活动的媒体会话";

    public async Task InitializeAsync(IDynamicIslandApi api)
    {
        _api = api;
        _enabled = api.Settings.Get("media.enabled", true);

        _vm = new MediaViewModel
        {
            TogglePlayPause = () => _ = _session?.TryTogglePlayPauseAsync(),
            SkipNext = () => _ = _session?.TrySkipNextAsync(),
            SkipPrevious = () => _ = _session?.TrySkipPreviousAsync(),
            GlowEnabled = api.Settings.Get("media.glow", true),
        };

        _levelMonitor = new AudioLevelMonitor();

        _content = new IslandLiveContent
        {
            Priority = 100,
            OwnerLabel = "正在播放",
            OwnerGlyph = "\uE8D6",
            OwnerAccent = Windows.UI.Color.FromArgb(255, 255, 45, 85),
            MorphView = new MediaIslandView(_vm, _levelMonitor),
            CompactSize = new Windows.Foundation.Size(250, 40),
            ExpandedSize = new Windows.Foundation.Size(420, 158),
        };

        api.AddSettingsPage(new SettingsPageDescriptor(
            "media", DisplayName, "\uE8D6", () => new MediaSettingsPage(_api, this)));

        api.Settings.Changed += key =>
        {
            if (key == "media.enabled")
            {
                _enabled = _api.Settings.Get("media.enabled", true);
                RunOnUI(() => _ = RefreshAsync());
            }
            else if (key == "media.glow")
            {
                _vm.GlowEnabled = _api.Settings.Get("media.glow", true);
                UpdateLevelMonitor();
            }
        };

        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => RunOnUI(AttachCurrentSession);
            AttachCurrentSession();
        }
        catch
        {
            StatusText = "无法访问系统媒体会话";
        }
    }

    public Task ShutdownAsync()
    {
        DetachSession();
        _levelMonitor?.Stop();
        _levelMonitor?.Dispose();
        _levelMonitor = null;
        _manager = null;
        return Task.CompletedTask;
    }

    private void RunOnUI(Action action)
    {
        if (_api.Dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _api.Dispatcher.TryEnqueue(() => action());
        }
    }

    private void AttachCurrentSession()
    {
        var session = _manager?.GetCurrentSession();
        if (ReferenceEquals(session, _session) && session != null) return;

        DetachSession();
        _session = session;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }
        _ = RefreshAsync();
    }

    private void DetachSession()
    {
        if (_session == null) return;
        try
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        catch { }
        _session = null;
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        => RunOnUI(() => _ = RefreshAsync());

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        => RunOnUI(RefreshPlaybackOnly);

    private void RefreshPlaybackOnly()
    {
        var session = _session;
        if (!_enabled || session == null)
        {
            _ = RefreshAsync();
            return;
        }

        try
        {
            var status = session.GetPlaybackInfo().PlaybackStatus;
            bool active = status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            if (active != _liveShown)
            {
                _ = RefreshAsync();
                return;
            }
            if (!active) return;

            _vm.IsPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            UpdateLevelMonitor();
            StatusText = $"{(_vm.IsPlaying ? "正在播放" : "已暂停")}：{_vm.Title} - {_vm.Artist}（来自 {session.SourceAppUserModelId}）";
        }
        catch
        {
            _ = RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        var session = _session;

        if (!_enabled || session == null)
        {
            StatusText = _enabled ? "当前没有活动的媒体会话" : "模块已停用";
            _levelMonitor?.Stop();
            SetLive(false);
            return;
        }

        try
        {
            var status = session.GetPlaybackInfo().PlaybackStatus;
            bool active = status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            if (!active)
            {
                StatusText = "当前没有活动的媒体会话";
                _levelMonitor?.Stop();
                SetLive(false);
                return;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            if (version != _refreshVersion) return;

            _vm.Title = string.IsNullOrWhiteSpace(props.Title) ? "未知曲目" : props.Title;
            _vm.Artist = string.IsNullOrWhiteSpace(props.Artist) ? session.SourceAppUserModelId : props.Artist;
            _vm.IsPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            UpdateLevelMonitor();
            StatusText = $"{(_vm.IsPlaying ? "正在播放" : "已暂停")}：{_vm.Title} - {_vm.Artist}（来自 {session.SourceAppUserModelId}）";

            SetLive(true);

            var trackKey = $"{_vm.Title}\n{_vm.Artist}";
            if (trackKey != _thumbTrackKey || _vm.Thumbnail == null)
            {
                _thumbTrackKey = trackKey;
                await LoadThumbnailAsync(props, version);
            }
        }
        catch
        {
            SetLive(false);
        }
    }

    private async Task LoadThumbnailAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties props, int version)
    {
        if (props.Thumbnail == null)
        {
            if (version == _refreshVersion) _vm.Thumbnail = null;
            return;
        }

        try
        {
            using var stream = await props.Thumbnail.OpenReadAsync();
            if (version != _refreshVersion) return;
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            if (version != _refreshVersion) return;
            _vm.Thumbnail = bmp;
        }
        catch
        {
            if (version == _refreshVersion) _vm.Thumbnail = null;
        }
    }

    private void SetLive(bool show)
    {
        if (show == _liveShown) return;
        _liveShown = show;
        _api.SetLiveContent(Id, show ? _content : null);
    }

    private void UpdateLevelMonitor()
    {
        AudioLog.Write($"UpdateLevelMonitor: IsPlaying={_vm.IsPlaying}, enabled={_enabled}, Glow={_vm.GlowEnabled}, monitor={_levelMonitor != null}, running={_levelMonitor?.IsRunning}, status={_levelMonitor?.Status}");
        if (_vm.IsPlaying && _enabled && _vm.GlowEnabled)
            _levelMonitor?.Start();
        else
            _levelMonitor?.Stop();
    }
}
