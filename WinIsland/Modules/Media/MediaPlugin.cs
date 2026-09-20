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

    private MediaViewModel _vm = null!;
    private MediaIslandView _islandView = null!;
    private AudioLevelMonitor? _levelMonitor;
    private IslandLiveContent _content = null!;
    private bool _enabled;
    private int _priority = DefaultPriority;
    private int _refreshVersion;
    private string? _thumbTrackKey;
    private string? _lastSourceAppId;

    public string StatusText { get; private set; } = "当前没有活动的媒体会话";

    protected override async Task OnInitializeAsync()
    {
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
            GlowEnabled = Settings.Get("glow", true),
        };

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
        Context.OnSettingsChanged("priority", () =>
        {
            _priority = Settings.Get("priority", DefaultPriority);
            _content = BuildContent();
            UpdateContent(_content);
        });

        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnManagerCurrentSessionChanged;
            _manager.SessionsChanged += OnManagerSessionsChanged;
            OnSessionsChanged();
            OnCurrentSessionChanged();
        }
        catch (Exception ex)
        {
            StatusText = "无法访问系统媒体会话";
            Log.Warn($"无法访问系统媒体会话：{ex.Message}");
        }
    }

    protected override Task OnShutdownAsync()
    {
        if (_manager != null)
        {
            _manager.CurrentSessionChanged -= OnManagerCurrentSessionChanged;
            _manager.SessionsChanged -= OnManagerSessionsChanged;
            _manager = null;
        }

        DetachSession();
        _sessions.Clear();
        _levelMonitor?.Stop();
        _levelMonitor?.Dispose();
        _levelMonitor = null;
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
        OnTap = ActivateSourceApp,
    };

    private void SetLive(bool show) => SetContent(show ? _content : null);

    private void OnManagerCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => RunOnUI(OnCurrentSessionChanged);

    private void OnManagerSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => RunOnUI(OnSessionsChanged);

    private void OnSessionsChanged()
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
        UpdateSessionIndex();
    }

    private void OnCurrentSessionChanged()
    {
        var session = _manager?.GetCurrentSession();
        if (ReferenceEquals(session, _session) && session != null) return;

        DetachSession();
        _session = session;
        AttachSession();
        UpdateSessionIndex();
        _ = RefreshAsync();
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
            catch
            {
            }
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
