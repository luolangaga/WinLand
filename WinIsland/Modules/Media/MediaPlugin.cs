using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.Media.Control;
using WinIsland.Core;

namespace WinIsland.Modules.Media;

public sealed class MediaPlugin : IslandPluginBase
{
    private const int DefaultPriority = 100;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly List<GlobalSystemMediaTransportControlsSession> _sessions = new();

    // GSMTC 的会话管理器会僵死：请求它时系统里还没有任何会话的话，此后它的事件不再触发，
    // GetSessions/GetCurrentSession 也永远只返回那一刻的快照（cppwinrt#1310）。
    // 唯一可靠的恢复手段是重新 RequestAsync，所以这里按固定节奏重绑并收敛状态，
    // 保证任何媒体一开始播放都能自动同步，而不是靠用户手动刷新。
    private static readonly TimeSpan IdleSyncInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LiveSyncInterval = TimeSpan.FromSeconds(15);

    private bool _rebinding;
    private bool _shuttingDown;
    private DateTime _lastSyncUtc = DateTime.MinValue;
    private string? _pinnedAppId;

    private MediaViewModel _vm = null!;
    private MediaIslandView _islandView = null!;
    private MediaSpotlightView? _spotlightView;
    private LyricsService? _lyrics;
    private AudioLevelMonitor? _levelMonitor;
    private IslandLiveContent _content = null!;
    private bool _enabled;
    private bool _spotlightOpen;
    private int _priority = DefaultPriority;
    private int _refreshVersion;
    private string? _thumbTrackKey;
    private string? _lastSourceAppId;

    public string StatusText { get; private set; } = "当前没有活动的媒体会话";

    protected override async Task OnInitializeAsync()
    {
        _shuttingDown = false;
        _enabled = Settings.Get("enabled", true);
        _priority = Settings.Get("priority", DefaultPriority);

        _vm = new MediaViewModel
        {
            TogglePlayPause = () => _ = _session?.TryTogglePlayPauseAsync(),
            SkipNext = () => _ = _session?.TrySkipNextAsync(),
            SkipPrevious = () => _ = _session?.TrySkipPreviousAsync(),
            SwitchToNextSession = SwitchToNextSession,
            SwitchToPrevSession = SwitchToPrevSession,
            RefreshTimeline = RefreshTimeline,
            SeekTo = SeekTo,
            GlowEnabled = Settings.Get("glow", true),
            LyricsEnabled = Settings.Get("lyrics", true),
        };

        _lyrics = new LyricsService(Log, PluginDirectory);
        _levelMonitor = new AudioLevelMonitor();
        _islandView = new MediaIslandView(_vm, _levelMonitor);
        _content = BuildContent();

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            "media", Manifest.Name, "\uE8D6", () => new MediaSettingsPage(Context, this), 10));

        Context.OnSettingsChanged("enabled", () =>
        {
            _enabled = Settings.Get("enabled", true);
            _ = RefreshAsync();
        });
        Context.OnSettingsChanged("glow", () =>
        {
            _vm.GlowEnabled = Settings.Get("glow", true);
            UpdateLevelMonitor();
        });
        Context.OnSettingsChanged("lyrics", () => _vm.LyricsEnabled = Settings.Get("lyrics", true));
        Context.OnSettingsChanged("priority", () =>
        {
            _priority = Settings.Get("priority", DefaultPriority);
            _content = BuildContent();
            UpdateContent(_content);
        });

        Context.CreateTimer(TimeSpan.FromSeconds(1), true, WatchdogTick);

        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            SubscribeManager();
            await ReconcileSessionsAsync();
        }
        catch (Exception ex)
        {
            StatusText = "无法访问系统媒体会话";
            Log.Warn($"无法访问系统媒体会话：{ex.Message}");
        }
    }

    protected override Task OnShutdownAsync()
    {
        _shuttingDown = true;
        UnsubscribeManager();
        _manager = null;

        DetachSession();
        _sessions.Clear();
        _levelMonitor?.Stop();
        _levelMonitor?.Dispose();
        _levelMonitor = null;
        _spotlightOpen = false;
        SetContent(null);
        return Task.CompletedTask;
    }

    private IslandLiveContent BuildContent() => new()
    {
        Priority = _priority,
        OwnerLabel = "正在播放",
        OwnerGlyph = "\uE8D6",
        OwnerAccent = Windows.UI.Color.FromArgb(255, 255, 45, 85),
        MorphView = _islandView,
        CompactSize = new Windows.Foundation.Size(250, 40),
        ExpandedSize = new Windows.Foundation.Size(420, 148),
        OnTap = OpenSpotlight,
    };

    /// <summary>点击岛体 = 展开超级大卡片（歌词流 / 大封面 / 可拖动进度）。</summary>
    private void OpenSpotlight()
    {
        if (_session == null || string.IsNullOrWhiteSpace(_vm.Title)) return;

        _spotlightView ??= new MediaSpotlightView(_vm, _lyrics, _levelMonitor);
        _spotlightOpen = true;
        Context.Island.OpenSpotlight(new IslandSpotlight
        {
            Content = _spotlightView,
            Size = new Windows.Foundation.Size(760, 470),
            OnClosed = () =>
            {
                _spotlightOpen = false;
                _spotlightView?.OnHostClosed();
            },
        });
    }

    private void CloseSpotlight()
    {
        if (!_spotlightOpen) return;
        _spotlightOpen = false;
        Context.Island.CloseSpotlight();
    }

    private void SeekTo(TimeSpan position)
    {
        var session = _session;
        if (session == null) return;

        try
        {
            _ = session.TryChangePlaybackPositionAsync(position.Ticks);
        }
        catch
        {
        }
    }

    private void SetLive(bool show) => SetContent(show ? _content : null);

    private void OnManagerCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => RunOnUI(() => _ = ReconcileSessionsAsync());

    private void OnManagerSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => RunOnUI(() => _ = ReconcileSessionsAsync());

    private void SubscribeManager()
    {
        if (_manager == null) return;
        _manager.CurrentSessionChanged += OnManagerCurrentSessionChanged;
        _manager.SessionsChanged += OnManagerSessionsChanged;
    }

    private void UnsubscribeManager()
    {
        if (_manager == null) return;
        try
        {
            _manager.CurrentSessionChanged -= OnManagerCurrentSessionChanged;
            _manager.SessionsChanged -= OnManagerSessionsChanged;
        }
        catch
        {
        }
    }

    private void WatchdogTick()
    {
        if (!_enabled || _shuttingDown) return;

        var interval = _session == null ? IdleSyncInterval : LiveSyncInterval;
        if (DateTime.UtcNow - _lastSyncUtc < interval) return;
        _ = RebindAsync();
    }

    public Task RefreshNowAsync() => RebindAsync();

    private async Task RebindAsync()
    {
        if (_rebinding || _shuttingDown) return;

        _rebinding = true;
        _lastSyncUtc = DateTime.UtcNow;
        try
        {
            var fresh = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_shuttingDown) return;

            if (!ReferenceEquals(fresh, _manager))
            {
                UnsubscribeManager();
                _manager = fresh;
                SubscribeManager();
            }

            await ReconcileSessionsAsync();
        }
        catch (Exception ex)
        {
            Log.Debug($"重新获取媒体会话管理器失败：{ex.Message}");
        }
        finally
        {
            _rebinding = false;
        }
    }

    private async Task ReconcileSessionsAsync()
    {
        try
        {
            _sessions.Clear();
            try
            {
                foreach (var session in _manager?.GetSessions() ?? Array.Empty<GlobalSystemMediaTransportControlsSession>())
                {
                    _sessions.Add(session);
                }
            }
            catch
            {
            }

            _vm.HasMultipleSessions = _sessions.Count > 1;
            _vm.SessionCount = _sessions.Count;

            var target = ResolveSession();
            if (!ReferenceEquals(target, _session))
            {
                DetachSession();
                _session = target;
                AttachSession();
            }

            UpdateSessionIndex();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Debug($"同步媒体会话失败：{ex.Message}");
        }
    }

    private GlobalSystemMediaTransportControlsSession? ResolveSession()
    {
        if (_pinnedAppId != null)
        {
            var pinned = FindSession(_pinnedAppId);
            if (pinned != null && IsActive(pinned)) return pinned;
            _pinnedAppId = null;
        }

        var current = _manager?.GetCurrentSession();
        if (current != null && IsActive(current))
        {
            return FindSession(current.SourceAppUserModelId) ?? current;
        }

        foreach (var session in _sessions)
        {
            if (IsPlaying(session)) return session;
        }

        foreach (var session in _sessions)
        {
            if (IsActive(session)) return session;
        }

        return current;
    }

    private GlobalSystemMediaTransportControlsSession? FindSession(string appId)
    {
        foreach (var session in _sessions)
        {
            if (string.Equals(session.SourceAppUserModelId, appId, StringComparison.Ordinal)) return session;
        }

        return null;
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsActive(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var status = session.GetPlaybackInfo().PlaybackStatus;
            return status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
        }
        catch
        {
            return false;
        }
    }

    private void AttachSession()
    {
        if (_session == null) return;
        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
    }

    private void UpdateSessionIndex()
    {
        if (_session != null)
        {
            var index = _sessions.IndexOf(_session);
            _vm.SessionIndex = index >= 0 ? index : 0;
        }
    }

    private void SwitchToNextSession()
    {
        if (_sessions.Count <= 1) return;
        var next = (_vm.SessionIndex + 1) % _sessions.Count;
        RunOnUI(() => SwitchToSession(next));
    }

    private void SwitchToPrevSession()
    {
        if (_sessions.Count <= 1) return;
        var previous = (_vm.SessionIndex - 1 + _sessions.Count) % _sessions.Count;
        RunOnUI(() => SwitchToSession(previous));
    }

    private void SwitchToSession(int index)
    {
        if (index < 0 || index >= _sessions.Count) return;
        var target = _sessions[index];
        if (ReferenceEquals(target, _session)) return;

        DetachSession();
        _session = target;
        _pinnedAppId = target.SourceAppUserModelId;
        AttachSession();
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
        catch
        {
        }

        _session = null;
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => RunOnUI(() => _ = RefreshAsync());

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => RunOnUI(RefreshPlaybackOnly);

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
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
            var active = status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            if (!active)
            {
                _ = RefreshAsync();
                return;
            }

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
            CloseSpotlight();
            SetLive(false);
            return;
        }

        try
        {
            var status = session.GetPlaybackInfo().PlaybackStatus;
            var active = status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            if (!active)
            {
                StatusText = "当前没有活动的媒体会话";
                _levelMonitor?.Stop();
                CloseSpotlight();
                SetLive(false);
                return;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            if (version != _refreshVersion) return;

            _vm.Title = string.IsNullOrWhiteSpace(props.Title) ? "未知曲目" : props.Title;
            _vm.Artist = string.IsNullOrWhiteSpace(props.Artist) ? session.SourceAppUserModelId : props.Artist;
            _vm.AlbumTitle = props.AlbumTitle ?? "";
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
            CloseSpotlight();
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
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (version != _refreshVersion) return;
            _vm.Thumbnail = bitmap;
        }
        catch
        {
            if (version == _refreshVersion) _vm.Thumbnail = null;
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
            var packageManager = new PackageManager();
            foreach (var package in packageManager.FindPackagesForUser(""))
            {
                if (package.Id.FamilyName != familyName) continue;
                var displayName = package.DisplayName;
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = LookupKnownApp(PrettifyAumid(sourceAppUserModelId));
                }

                _vm.SourceAppName = displayName;
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(package.Logo);
                    using var stream = await file.OpenReadAsync();
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    _vm.SourceAppIcon = bitmap;
                }
                catch
                {
                }

                return;
            }
        }
        catch
        {
        }

        var exeName = sourceAppUserModelId;
        if (exeName.Contains('!')) exeName = exeName.Split('!')[0];
        if (exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            exeName = Path.GetFileNameWithoutExtension(exeName);
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
            var dotIndex = appId.LastIndexOf('.');
            var nice = dotIndex >= 0 ? appId[(dotIndex + 1)..] : appId;
            if (!string.IsNullOrWhiteSpace(nice))
            {
                return LookupKnownApp(nice);
            }
        }

        var segments = familyPart.Split('_');
        var name = segments[0];
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0)
        {
            name = name[(lastDot + 1)..];
        }

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
        if (_vm.IsPlaying && _enabled && _vm.GlowEnabled)
        {
            _levelMonitor?.Start();
        }
        else
        {
            _levelMonitor?.Stop();
        }
    }
}
