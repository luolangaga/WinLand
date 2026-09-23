using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinIsland.Core;
using WinIsland.Core.Update;

namespace WinIsland.Settings;

public sealed partial class AboutSettingsPage : UserControl
{
    private readonly UpdateService _updates;
    private bool _loading = true;
    private bool _checking;

    /// <summary>本次会话里最后一次检查的结果说明（打开页面时还没有）。</summary>
    private string? _lastMessage;

    /// <summary>已经渲染过发布说明的版本 tag，避免每次刷新都重建 Markdown 视图。</summary>
    private string? _notesTag;

    public AboutSettingsPage(UpdateService updates)
    {
        _updates = updates;
        InitializeComponent();

        AutoCheckToggle.IsOn = _updates.AutoCheck;
        _loading = false;

        Render();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 幂等：页面可能被重新挂载，先退订再订阅
        _updates.Changed -= OnUpdatesChanged;
        _updates.Changed += OnUpdatesChanged;

        // 从托盘「检查更新…」进来时，检查可能比页面挂载还早开始，这里补一次当前状态
        Render();

        if (_updates.ShouldRefreshOnOpen())
        {
            _ = CheckAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 设置页每次导航都会新建一个实例，服务却是长生命周期的：不退订就会一直持有旧页面
        _updates.Changed -= OnUpdatesChanged;
    }

    private void OnUpdatesChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            Render();
        }
        else
        {
            DispatcherQueue.TryEnqueue(Render);
        }
    }

    private void Render()
    {
        VersionText.Text = $"WinIsland {_updates.CurrentVersionText}";
        BuildText.Text =
            $"宿主 SDK {IslandSdk.HostVersion} · {_updates.ArchitectureText} · 安装位置 {AppContext.BaseDirectory.TrimEnd('\\')}";

        Busy.IsActive = _updates.IsChecking;
        CheckButton.IsEnabled = !_updates.IsChecking;

        var release = _updates.Available;
        bool skipped = release is not null ? _updates.IsSkipped(release) : _updates.SkippedTag.Length > 0;

        UpdateCard.Visibility = release is not null && !skipped ? Visibility.Visible : Visibility.Collapsed;
        SkippedCard.Visibility = skipped ? Visibility.Visible : Visibility.Collapsed;

        StatusText.Text = BuildStatusText(_lastMessage, release, skipped);

        if (release is null || skipped)
        {
            _notesTag = null;
            SkippedText.Text = _updates.SkippedTag.Length > 0
                ? $"已跳过 {_updates.SkippedTag}"
                : "已跳过一个版本";
            return;
        }

        UpdateTitle.Text = $"发现新版本 {release.Tag}";
        UpdateMeta.Text = BuildMetaText(release);
        OpenReleaseButton.Content = release.Source == UpdateSource.GitCode ? "前往 GitCode 下载" : "前往 GitHub 下载";
        DirectButton.Visibility = release.AssetUrl is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        if (_notesTag != release.Tag)
        {
            _notesTag = release.Tag;
            bool hasNotes = release.Notes.Length > 0;
            NotesHost.Content = hasNotes ? MarkdownRenderer.Render(release.Notes) : null;
            NotesHost.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;
            NoNotesText.Visibility = hasNotes ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private string BuildStatusText(string? message, UpdateRelease? release, bool skipped)
    {
        var parts = new List<string>();
        if (_updates.IsChecking)
        {
            parts.Add("正在检查更新…");
        }
        else if (!string.IsNullOrEmpty(message))
        {
            parts.Add(message);
        }
        else if (!string.IsNullOrEmpty(_updates.LastMessage))
        {
            // 启动后的后台检查通常比本页先跑完，用它留下的结论，别让这里显示成"没检查过"
            parts.Add(_updates.LastMessage);
        }
        else if (!string.IsNullOrEmpty(_updates.LastError))
        {
            parts.Add(_updates.LastError);
        }
        else if (release is not null && !skipped)
        {
            parts.Add($"发现新版本（来源 {release.SourceLabel}）");
        }

        if (_updates.LastCheck is { } last)
        {
            parts.Add($"上次检查 {last.LocalDateTime:MM-dd HH:mm}");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "还没有检查过更新";
    }

    private static string BuildMetaText(UpdateRelease release)
    {
        var parts = new List<string> { $"来源：{release.SourceLabel}" };
        if (release.AssetName is { Length: > 0 } name)
        {
            parts.Add(release.AssetSize > 0 ? $"{name}（{FormatSize(release.AssetSize)}）" : name);
        }

        return string.Join(" · ", parts);
    }

    private async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            var result = await _updates.CheckAsync(CancellationToken.None);
            _lastMessage = result.Status == UpdateStatus.UpdateAvailable
                           && result.Release is { } found
                           && _updates.IsSkipped(found)
                ? $"{result.Message}（已在跳过列表里）"
                : result.Message;
        }
        catch (Exception ex)
        {
            _lastMessage = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            _checking = false;
            Render();
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_updates.Available is not { } release) return;
        _updates.Skip(release);
        _lastMessage = $"已跳过 {release.Tag}，之后不再提示这一版";
    }

    private void CancelSkip_Click(object sender, RoutedEventArgs e)
    {
        _updates.ClearSkip();
        _lastMessage = "已取消跳过，下次检查会重新提示这一版";
    }

    private void OpenRelease_Click(object sender, RoutedEventArgs e) => OpenUrl(_updates.Available?.ReleasePageUrl);

    private void DirectDownload_Click(object sender, RoutedEventArgs e) => OpenUrl(_updates.Available?.AssetUrl);

    private void OpenGitCode_Click(object sender, RoutedEventArgs e) => OpenUrl(UpdateService.GitCodeUrl);

    private void OpenGitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(UpdateService.GitHubUrl);

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland", "logs");
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void AutoCheckToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _updates.AutoCheck = AutoCheckToggle.IsOn;
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };
}
