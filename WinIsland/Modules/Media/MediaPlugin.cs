using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.Media.Control;
using WinIsland.Core;
using System.Security.Cryptography;

namespace WinIsland.Modules.Media;

public sealed class MediaPlugin : IslandPluginBase
{
    private const int DefaultPriority = 200;

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
    private string? _thumbnailFingerprint;
    /// <summary>已贴到界面上的那张封面属于哪首（标题+歌手）：换曲时才允许把旧图清掉。</summary>
    private string? _thumbnailTrackKey;
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
            WaveEnabled = Settings.Get("wave", true),
            BackgroundMode = Settings.Get("background", "spectrum"),
            BackgroundIntensity = Settings.Get("backgroundIntensity", 35) / 100.0,
            LyricsEnabled = Settings.Get("lyrics", true),
        };

        _lyrics = new LyricsService(Log, PluginDirectory);
        _levelMonitor = new AudioLevelMonitor();
        _islandView = new MediaIslandView(_vm, _levelMonitor);
        _content = BuildContent();

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            "media", Manifest.Name, "\uE8D6", () => new MediaSettingsPage(Context, this), 10));

        Context.OnSettingsChanged("background", () => { _vm.BackgroundMode = Settings.Get("background", "spectrum"); UpdateLevelMonitor(); });
        Context.OnSettingsChanged("backgroundIntensity", () => _vm.BackgroundIntensity = Settings.Get("backgroundIntensity", 35) / 100.0);
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
        Context.OnSettingsChanged("wave", () => { _vm.WaveEnabled = Settings.Get("wave", true); UpdateLevelMonitor(); });
        Context.OnSettingsChanged("lyrics", () => _vm.LyricsEnabled = Settings.Get("lyrics", true));
        Context.OnSettingsChanged("priority", () =>
        {
            _priority = Settings.Get("priority", DefaultPriority);
            _content = BuildContent();
            UpdateContent(_content);
        });

        Context.CreateTimer(TimeSpan.FromSeconds(1), true, WatchdogTick);

        // 全局快捷键「播放 / 暂停」：宿主不直接调媒体插件，只在总线上广播，这里订阅它。
        // Bus 是按能力取的（老宿主上没有）—— 取不到就只是没有快捷键，其它一切照旧。
        Bus?.Subscribe("host.media.playpause", _message =>
        {
            var session = _session;
            if (session == null) return;
            _ = session.TryTogglePlayPauseAsync();
        });

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

        _spotlightView ??= new MediaSpotlightView(_vm, _lyrics, _levelMonitor, Theme);
        _spotlightOpen = true;
        Context.Island.OpenSpotlight(new IslandSpotlight
        {
            Content = _spotlightView,
            Size = new Windows.Foundation.Size(900, 520),
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
                // `RebindAsync` 每 15 秒重取一次管理器，`GetSessions()` 会为同一个播放器返回**新的包装对象** ——
                // ReferenceEquals 必然变 false。若一律按"换会话"处理，封面就会被周期性地清空再重读，
                // 界面上表现为封面时有时无。只有真的换了应用才清图，同一个应用只续订事件。
                bool sameApp = _session is { } previous && target != null
                    && string.Equals(previous.SourceAppUserModelId, target.SourceAppUserModelId, StringComparison.Ordinal);

                DetachSession(clearArtwork: !sameApp);
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

    private void DetachSession(bool clearArtwork = true)
    {
        if (clearArtwork) ClearArtwork();

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
            var position = timeline.Position;
            // SMTC reports the position at LastUpdatedTime, rather than a ticking clock.
            // Advance it while playing, including when only the spotlight is visible.
            if (_vm.IsPlaying && timeline.LastUpdatedTime > DateTimeOffset.UnixEpoch)
            {
                double elapsed = Math.Max(0, (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds);
                double rate = session.GetPlaybackInfo().PlaybackRate ?? 1;
                position += TimeSpan.FromSeconds(elapsed * Math.Max(0, rate));
            }
            _vm.Position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, timeline.StartTime.Ticks, timeline.EndTime.Ticks));
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
            // 只按「会话是否还是同一个」判定过期：_refreshVersion 在每次刷新开头都会自增，
            // 用它会把读取期间的正常刷新误判成"过期"，导致标题/封面偶发不更新。
            if (!ReferenceEquals(session, _session)) return;

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

            // Players may publish artwork after the title, or replace it without changing metadata.
            // Inspect every media-properties refresh; identical bytes retain the existing image.
            //
            // 封面读取不能挂在刷新计数上：读图要几十到几百毫秒，期间的任何一次刷新都会把
            // 刚到手的封面判为过期 —— 这正是"切歌时封面有时不跟上来"的成因。
            // 这里改为按「会话 + 曲目」判定，见 LoadThumbnailAsync。
            await LoadThumbnailAsync(props, session, $"{_vm.Title}\u0001{_vm.Artist}");
        }
        catch (Exception ex)
        {
            // 一次元数据读取失败（播放器忙、会话正在切换）不代表没有正在播放的内容：
            // 旧实现会顺手 CloseSpotlight + SetLive(false)，把岛整个收走，
            // 用户看到的就是「歌还在放，封面/内容却没了」。这里只记录，保持现状，
            // 下一拍刷新（播放器恢复响应）会自然补上。
            Log.Debug($"读取媒体属性失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 读取并贴上新封面。三条必须守住的规矩（“封面有时不同步”就是它们的反面）：
    ///
    /// ① **瞬时失败绝不刷掉已有封面。** <c>OpenReadAsync</c>/解码失败、或读取期间来了别的刷新，
    ///    都只说明“这一次没读到”，不代表这首歌没有封面 —— 旧实现把它们一律写成 null，
    ///    界面就退回占位图标，用户看到的就是“封面丢了”。
    /// ② **换曲才清图。** 同一首歌报来 <c>Thumbnail == null</c>（播放器还没准备好图）时保留上一张，
    ///    等它补上就自动换上；真的是另一首歌且没有封面时才回到占位图标。
    /// ③ **相同字节不重复解码。** 每 15 秒一次的管理器重绑都会重读属性，指纹一致时直接复用现有位图，
    ///    否则每次刷新都要重新解码一遍，界面上还会多一次无谓的翻转。
    ///
    /// 过期判定只用「会话 + 曲目」，不用任何刷新计数：计数型版本号会让一次无关刷新
    /// 把正在读的封面判死（切歌时封面时有时无的直接成因）。
    /// </summary>
    private async Task LoadThumbnailAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties props,
        GlobalSystemMediaTransportControlsSession session, string trackKey)
    {
        // 过期判定只看「还是不是同一个会话、还是不是同一首歌」。
        // 不能用递增的刷新计数：读图期间的无关刷新会把刚到手的封面判死。
        bool Fresh() => ReferenceEquals(session, _session);

        if (props.Thumbnail == null)
        {
            // 同一首歌：播放器只是还没给图（或暂时读不到），保留当前封面等它补上
            if (!Fresh() || trackKey == _thumbnailTrackKey) return;
            ClearArtwork();
            return;
        }

        try
        {
            using var stream = await props.Thumbnail.OpenReadAsync();
            if (!Fresh()) return;
            using var bytes = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(bytes);
            if (!Fresh()) return;

            string fingerprint = Convert.ToHexString(
                SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
            if (fingerprint == _thumbnailFingerprint && _vm.Thumbnail != null && trackKey == _thumbnailTrackKey) return;

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (!Fresh()) return;

            _thumbnailFingerprint = fingerprint;
            _thumbnailTrackKey = trackKey;
            _vm.Thumbnail = bitmap;
        }
        catch
        {
            // 读不到这一张就保持现状：下一次刷新（播放器补图 / 下一拍重绑）会再试。
            // 只有确实换了曲目才允许把旧封面清掉，否则界面会凭空退回占位图标。
            if (Fresh() && trackKey != _thumbnailTrackKey) ClearArtwork();
        }
    }

    private void ClearArtwork()
    {
        _thumbnailFingerprint = null;
        _thumbnailTrackKey = null;
        _vm.Thumbnail = null;
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
        if (_vm.IsPlaying && _enabled && WinIsland.Island.Motion.Enabled &&
            _vm.GlowEnabled && _vm.BackgroundMode != "off")
        {
            _levelMonitor?.Start();
        }
        else
        {
            _levelMonitor?.Stop();
        }
    }
}
