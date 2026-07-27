using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WinIsland.Core;

namespace WinIsland.Modules.Media;

public sealed class MediaModule : IIslandModule
{
    public string Id => "media";
    public string DisplayName => "正在播放";

    private IDynamicIslandApi _api = null!;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly List<GlobalSystemMediaTransportControlsSession> _sessions = new();

    private MediaViewModel _vm = null!;
    private IslandLiveContent _content = null!;
    private AudioLevelMonitor? _levelMonitor;
    private bool _enabled;
    private bool _liveShown;
    private int _refreshVersion;
    private string? _thumbTrackKey;
    private string? _lastSourceAppId;

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
            SwitchToNextSession = SwitchToNextSession,
            SwitchToPrevSession = SwitchToPrevSession,
            RefreshTimeline = RefreshTimeline,
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
            ExpandedSize = new Windows.Foundation.Size(420, 148),
            OnTap = ActivateSourceApp,
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
            _manager.CurrentSessionChanged += (_, _) => RunOnUI(OnCurrentSessionChanged);
            _manager.SessionsChanged += (_, _) => RunOnUI(OnSessionsChanged);
            OnSessionsChanged();
            OnCurrentSessionChanged();
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

    private void OnSessionsChanged()
    {
        _sessions.Clear();
        try
        {
            foreach (var s in _manager?.GetSessions() ?? Array.Empty<GlobalSystemMediaTransportControlsSession>())
                _sessions.Add(s);
        }
        catch { }

        _vm.HasMultipleSessions = _sessions.Count > 1;
        _vm.SessionCount = _sessions.Count;
        UpdateSessionIndex();
    }

    private void OnCurrentSessionChanged()
    {
        var session = _manager?.GetCurrentSession();
        if (ReferenceEquals(session, _session) && session != null) return;

        DetachSession();
        _session = session;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
        UpdateSessionIndex();
        _ = RefreshAsync();
    }

    private void UpdateSessionIndex()
    {
        if (_session != null)
        {
            int idx = _sessions.IndexOf(_session);
            _vm.SessionIndex = idx >= 0 ? idx : 0;
        }
    }

    private void SwitchToNextSession()
    {
        if (_sessions.Count <= 1) return;
        int idx = _vm.SessionIndex;
        int next = (idx + 1) % _sessions.Count;
        RunOnUI(() => SwitchToSession(next));
    }

    private void SwitchToPrevSession()
    {
        if (_sessions.Count <= 1) return;
        int idx = _vm.SessionIndex;
        int prev = (idx - 1 + _sessions.Count) % _sessions.Count;
        RunOnUI(() => SwitchToSession(prev));
    }

    private void SwitchToSession(int index)
    {
        if (index < 0 || index >= _sessions.Count) return;
        var target = _sessions[index];
        if (ReferenceEquals(target, _session)) return;

        DetachSession();
        _session = target;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
        _vm.SessionIndex = index;
        _lastSourceAppId = null;
        _ = RefreshAsync();
    }

    private void DetachSession()
    {
        if (_session == null) return;
        try
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        catch { }
        _session = null;
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        => RunOnUI(() => _ = RefreshAsync());

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        => RunOnUI(RefreshPlaybackOnly);

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        => RunOnUI(RefreshTimeline);

    private void RefreshTimeline()
    {
        var session = _session;
        if (session == null || !_enabled) return;
        try
        {
            var timeline = session.GetTimelineProperties();
            _vm.Position = timeline.Position;
            _vm.Duration = timeline.EndTime - timeline.StartTime;
            _vm.HasProgress = _vm.Duration > TimeSpan.Zero;
        }
        catch
        {
            _vm.HasProgress = false;
        }
    }

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
            RefreshTimeline();
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

            var sourceAppId = session.SourceAppUserModelId;
            if (sourceAppId != _lastSourceAppId)
            {
                _lastSourceAppId = sourceAppId;
                _ = LoadSourceAppInfoAsync(sourceAppId);
            }

            RefreshTimeline();
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

    private void ActivateSourceApp()
    {
        var appId = _session?.SourceAppUserModelId;
        if (string.IsNullOrEmpty(appId)) return;
        try
        {
            Win32.ActivateApp(appId);
        }
        catch
        {
            try
            {
                var exePart = appId.Contains('!') ? appId.Split('!')[0] : appId;
                if (exePart.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exePart,
                        UseShellExecute = true,
                    });
                }
            }
            catch { }
        }
    }

    private async Task LoadSourceAppInfoAsync(string? sourceAppUserModelId)
    {
        if (string.IsNullOrEmpty(sourceAppUserModelId))
        {
            _vm.SourceAppName = "";
            _vm.SourceAppIcon = null;
            return;
        }

        try
        {
            var familyName = sourceAppUserModelId.Split('!')[0];
            var mgr = new PackageManager();
            var pkgs = mgr.FindPackagesForUser("");
            foreach (var pkg in pkgs)
            {
                if (pkg.Id.FamilyName != familyName) continue;
                var displayName = pkg.DisplayName;
                if (string.IsNullOrEmpty(displayName))
                    displayName = LookupKnownApp(PrettifyAumid(sourceAppUserModelId));
                _vm.SourceAppName = displayName;
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(pkg.Logo);
                    using var stream = await file.OpenReadAsync();
                    var bmp = new BitmapImage();
                    await bmp.SetSourceAsync(stream);
                    _vm.SourceAppIcon = bmp;
                }
                catch { }
                return;
            }
        }
        catch { }

        var exeName = sourceAppUserModelId;
        if (exeName.Contains('!')) exeName = exeName.Split('!')[0];
        if (exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            exeName = System.IO.Path.GetFileNameWithoutExtension(exeName);
        }
        else
        {
            exeName = PrettifyAumid(sourceAppUserModelId);
        }

        _vm.SourceAppName = exeName;
        _vm.SourceAppIcon = null;
    }

    private static string PrettifyAumid(string aumid)
    {
        var parts = aumid.Split('!');
        var familyPart = parts[0];
        var appId = parts.Length > 1 ? parts[1] : null;

        if (!string.IsNullOrEmpty(appId) && !appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var dotIdx = appId.LastIndexOf('.');
            var nice = dotIdx >= 0 ? appId[(dotIdx + 1)..] : appId;
            if (!string.IsNullOrWhiteSpace(nice))
                return LookupKnownApp(nice);
        }

        var segments = familyPart.Split('_');
        string name = segments[0];
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0)
            name = name[(lastDot + 1)..];

        return LookupKnownApp(name);
    }

    private static string LookupKnownApp(string name)
    {
        if (name.Equals("Spotify", StringComparison.OrdinalIgnoreCase) || name.Contains("SpotifyMusic", StringComparison.OrdinalIgnoreCase))
            return "Spotify";
        if (name.Equals("AppleMusic", StringComparison.OrdinalIgnoreCase) || name.Equals("Music", StringComparison.OrdinalIgnoreCase))
            return "Apple Music";
        if (name.Contains("QQMusic", StringComparison.OrdinalIgnoreCase))
            return "QQ音乐";
        if (name.Contains("NeteaseMusic", StringComparison.OrdinalIgnoreCase) || name.Contains("CloudMusic", StringComparison.OrdinalIgnoreCase))
            return "网易云音乐";
        if (name.Contains("Kugou", StringComparison.OrdinalIgnoreCase))
            return "酷狗音乐";
        if (name.Contains("Kuwo", StringComparison.OrdinalIgnoreCase))
            return "酷我音乐";
        if (name.Contains("Bilibili", StringComparison.OrdinalIgnoreCase))
            return "哔哩哔哩";
        if (name.Contains("YouTube", StringComparison.OrdinalIgnoreCase) || name.Equals("YouTubeMusic", StringComparison.OrdinalIgnoreCase))
            return "YouTube";
        if (name.Contains("VLC", StringComparison.OrdinalIgnoreCase))
            return "VLC";
        if (name.Contains("Foobar", StringComparison.OrdinalIgnoreCase))
            return "foobar2000";
        return name;
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
