using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using WinIsland.Core;

namespace ClipboardIsland;

/// <summary>
/// 剪贴板岛。
///
/// 小岛常驻显示最近一条内容的摘要 + 总条数；每次复制到新内容，让宿主的岛弹一条
/// 「已复制」通知（把岛体临时接管几秒，比在常驻视图里做高亮显眼得多）；
/// 展开看最近 3 条；点岛体弹出聚光卡看全部历史。
///
/// 点历史里的任意一条（大岛展开态的那几行、聚光卡里的行）都会：
///   写回剪贴板 → 收起卡片 → 直接粘进用户当前正在输入的那个窗口。
///
/// 必须是 public sealed 且有公共无参构造函数，一个包里只允许一个 IIslandPlugin 实现。
/// </summary>
public sealed class ClipboardIslandPlugin : IslandPluginBase
{
    private static readonly Windows.UI.Color Accent = Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF);

    private const int DefaultHistoryLimit = 20;
    private const int DefaultFlashSeconds = 3;
    private const int DefaultPriority = 55;
    private const int MinHistoryLimit = 1;
    private const int MaxHistoryLimit = 200;
    private const int MinFlashSeconds = 1;
    private const int MaxFlashSeconds = 30;

    /// <summary>写剪贴板之后、按 Ctrl+V 之前的等待：剪贴板内容是异步落到系统里的，不等这一拍目标程序可能还拿到旧内容。</summary>
    private const int PasteDelayMs = 80;

    /// <summary>收起聚光卡之后再动键盘：卡片收回动画期间宿主会处理焦点，抢在它前面粘贴会粘错地方。</summary>
    private const int SpotlightSettleMs = 60;

    /// <summary>
    /// 我们自己写回剪贴板之后，多久以内的同内容变化算「自己弄出来的回声」。
    /// 用户不可能在 2 秒内手动复制出一条和刚点进去的那条一模一样的内容，所以这个窗口很安全。
    /// </summary>
    private static readonly TimeSpan SelfWriteEchoWindow = TimeSpan.FromSeconds(2);

    private ClipboardHistory _history = null!;
    private ClipboardIslandView _view = null!;
    private ClipboardSpotlightView? _spotlight;
    private IslandLiveContent _content = null!;
    private ClipboardWatcher? _watcher;

    /// <summary>捕获串行化：同一时刻只跑一次，后来的请求排队等待，绝不丢弃。</summary>
    private readonly SemaphoreSlim _captureGate = new(1, 1);

    /// <summary>粘贴串行化：连点几条历史时按顺序执行，别让两次 Ctrl+V 交叉。</summary>
    private readonly SemaphoreSlim _pasteGate = new(1, 1);

    /// <summary>
    /// 我们刚写回剪贴板时"可能出现的指纹"：文件记录正常写的是 CF_HDROP，
    /// 写失败时退回路径文本 —— 两种都要认，否则那次变化会被当成"用户又复制了一遍"。
    /// </summary>
    private IReadOnlyList<string> _selfWriteSignatures = Array.Empty<string>();
    private DateTimeOffset _selfWriteAt;

    private bool _stopped;

    /// <summary>
    /// 岛体换主题：岛视图就地重刷配色；聚光卡下次打开时自然会按新主题重建
    /// （它是另一棵可视树，正在屏幕上开着的话留着旧配色直到收起，不值得为它做热替换）。
    /// </summary>
    private void OnThemeChanged()
    {
        Log.Info($"岛体主题已切换为{(Theme.IsLight ? "浅色" : "深色")}，重刷岛视图配色。");
        _view?.RefreshTheme();
        _spotlight = null;
    }

    protected override Task OnInitializeAsync()
    {
        _stopped = false;

        _history = new ClipboardHistory(Log);
        _history.Load(HistoryLimit);
        _history.Changed += OnHistoryChanged;
        Context.Register(new ActionDisposable(() => _history.Changed -= OnHistoryChanged));
        Context.Register(new ActionDisposable(() => _stopped = true));

        _view = new ClipboardIslandView(Manifest, Activate, Theme);
        _view.Apply(_history.Items);
        // 岛体换主题（Fluent 跟随系统明暗）：代码搭的视图颜色烘在画刷里，就地重刷一遍
        Context.Theme.Changed += OnThemeChanged;
        Context.Register(new ActionDisposable(() => Context.Theme.Changed -= OnThemeChanged));

        _content = new IslandLiveContent
        {
            Priority = Settings.Get("priority", DefaultPriority),
            OwnerLabel = Manifest.Name,
            OwnerGlyph = Manifest.IconGlyph,
            OwnerAccent = Accent,
            MorphView = _view,
            CompactSize = new Windows.Foundation.Size(260, 40),
            ExpandedSize = new Windows.Foundation.Size(430, 168),
            OnTap = OpenSpotlight,      // 点击岛体 = 打开「超级展开」聚光卡
        };

        Context.Island.AddSettingsPage(new SettingsPageDescriptor(
            Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE77F",
            () => new ClipboardSettingsPage(Context, this), order: 120));

        // 设置页里的开关实时生效
        Context.Register(Context.OnSettingsChanged("enabled", ApplyEnabled));
        Context.Register(Context.OnSettingsChanged("limit", ApplyLimit));

        // 记着「用户刚才在哪个窗口打字」。岛和聚光卡都不抢焦点，正常情况下点历史那一刻
        // 前台窗口就是目标；但用户中途点过别处时，这个采样是唯一的兜底线索
        PasteTarget.Sample();
        Context.Register(Context.CreateTimer(TimeSpan.FromSeconds(1), true, PasteTarget.Sample));

        AttachClipboard();
        ApplyEnabled();
        Log.Info($"剪贴板岛已启动（历史 {_history.Count} 条，上限 {HistoryLimit} 条，"
                 + $"图片{(IncludeImages ? "记录" : "不记录")}，复制提示{(NotifyOnCopy ? "岛通知" : "视图内高亮")}，"
                 + $"点击历史{(PasteOnPick ? "复制并粘贴" : "仅复制")}）");
        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        _stopped = true;
        DetachClipboard();
        SetContent(null);
        Log.Info("剪贴板岛已停用");
        return Task.CompletedTask;
    }

    public string DisplayName => Manifest.Name;

    public int HistoryCount => _history.Count;

    /// <summary>历史缓存目录（设置页里显示给用户看）。</summary>
    public string StoragePath => _history.RootDirectory;

    public bool IsEnabled => Settings.Get("enabled", true);

    public bool IncludeImages => Settings.Get("images", true);

    /// <summary>复制后是否让岛弹通知（关掉则退化成常驻视图内的高亮闪光）。</summary>
    public bool NotifyOnCopy => Settings.Get("notify", true);

    /// <summary>点历史条目后是否连带着粘进当前输入框（关掉则只写回剪贴板）。</summary>
    public bool PasteOnPick => Settings.Get("paste", true);

    public int HistoryLimit =>
        Math.Clamp(Settings.Get("limit", DefaultHistoryLimit), MinHistoryLimit, MaxHistoryLimit);

    public int FlashSeconds =>
        Math.Clamp(Settings.Get("flash", DefaultFlashSeconds), MinFlashSeconds, MaxFlashSeconds);

    /// <summary>历史有变化时触发（UI 线程）。设置页用它刷新计数。</summary>
    public event Action? HistoryChanged;

    /// <summary>清空历史，返回清掉的条数。</summary>
    public int ClearHistory()
    {
        var removed = _history.Count;
        _history.Clear();
        Log.Info($"已清空剪贴板历史（{removed} 条）");
        return removed;
    }

    /// <summary>设置页里的「试一下」：按当前开关预览一次复制反馈（没记录就用示例文本）。</summary>
    public void PreviewFeedback()
    {
        var item = _history.Count > 0
            ? _history.Items[0]
            : new ClipItem
            {
                Id = "preview",
                Kind = ClipKind.Text,
                Text = "这是一条示例文本，用来预览复制时的提示效果",
                Signature = "preview",
                CapturedAt = DateTimeOffset.Now,
            };

        ShowCopyFeedback(item);
    }

    // ---------------------------------------------------------------- 剪贴板监听

    private void AttachClipboard()
    {
        if (_watcher is not null) return;

        try
        {
            var watcher = new ClipboardWatcher(Log, Context.CreateTimer, OnClipboardChanged);
            _watcher = watcher;
            Context.Register(watcher);   // 插件卸载时自动停掉，不会留下野定时器
            watcher.Start();
        }
        catch (Exception ex)
        {
            Log.Error("无法监听剪贴板变化（插件仍可用，但不会自动记录）", ex);
        }
    }

    private void DetachClipboard()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnClipboardChanged(long sequence, string reason)
    {
        if (_stopped) return;

        try
        {
            // 剪贴板读取要回到 UI 线程；不在 UI 线程时这里只排队，读多少内容在 CaptureAsync 里决定
            Context.RunOnUI(() => _ = CaptureAsync(sequence, reason));
        }
        catch (Exception ex)
        {
            Log.Debug($"调度剪贴板读取失败（忽略）：{ex.Message}");
        }
    }

    /// <summary>
    /// 读一次剪贴板并入库。
    ///
    /// 顺序很关键，三步不能换：
    ///   1. <c>Clipboard.GetContent()</c> **立刻**取 —— 拿到的视图指向"此刻"的剪贴板，
    ///      任何等待（等锁、等上一次图片解码）之后再去取，读到的就是更新后的内容，
    ///      这一条就永久丢了。这是之前"偶尔漏记录"的直接原因；
    ///   2. 再进队列串行化解析 + 入库，保证连续快速复制时每条都跑到；
    ///   3. 整个链路跑在 UI 线程（剪贴板 API 的要求），且不做 ConfigureAwait(false)。
    /// </summary>
    private async Task CaptureAsync(long sequence, string reason)
    {
        if (_stopped) return;

        // 第 1 步：抢占刻的快照。Clipboard.GetContent() 是同步调用，这里还没 await，所以是"立刻"
        DataPackageView? view = null;
        try
        {
            view = Clipboard.GetContent();
        }
        catch (Exception ex)
        {
            // 剪贴板正被源程序占着：交给下面的重试路径重新去取。这属于常态，安静记 Debug
            Log.Debug($"取剪贴板快照失败（{reason}，seq {sequence}，改走重试）：{ex.Message}");
        }

        await _captureGate.WaitAsync();
        try
        {
            if (_stopped) return;

            var capture = view is null
                ? await ClipboardReader.TryReadAsync(Log, IncludeImages, reason)
                : await ClipboardReader.ExtractAsync(Log, view, IncludeImages, reason);

            if (capture is null || _stopped) return;

            // 我们自己刚写回去的内容：静默吞掉，否则每次点历史粘贴都会再弹一条通知
            if (IsSelfEcho(capture.Signature))
            {
                Log.Debug($"忽略自己的回声（{reason}，seq {sequence}）：{capture.Preview}");
                return;
            }

            var outcome = capture.Kind switch
            {
                ClipKind.Image => _history.AddImage(capture.ImageBytes!, capture.Signature, HistoryLimit),
                ClipKind.Files => _history.AddFiles(capture.Paths, capture.Signature, HistoryLimit),
                _ => _history.AddText(capture.Text!, capture.Signature, HistoryLimit),
            };

            var what = capture.Kind switch
            {
                ClipKind.Image => "图片",
                ClipKind.Files => "文件",
                _ => "文本",
            };
            Log.Info(outcome switch
            {
                ClipOutcome.Added => $"记录了一条{what}（{reason}，seq {sequence}）：{capture.Preview}",
                ClipOutcome.Promoted => $"这条已在历史里，已提到最前（{reason}，seq {sequence}）：{capture.Preview}",
                _ => $"又复制了一遍同一条（{reason}，seq {sequence}）：{capture.Preview}",
            });

            // 三种情况都给反馈：用户按了 Ctrl+C，岛上就该有动静，
            // 哪怕内容跟刚才一样 —— 之前这里对「已有记录」直接 return，就是"复制了却没反应"
            var head = _history.Count > 0 ? _history.Items[0] : null;
            if (head is not null) ShowCopyFeedback(head);
        }
        catch (Exception ex)
        {
            Log.Error($"处理剪贴板内容失败（{reason}，seq {sequence}）", ex);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    /// <summary>
    /// 这次变化是不是我们自己写回剪贴板引起的。判据是「写回动作后 2 秒内 + 内容指纹一致」，
    /// 比按内容去重可靠：内容去重分不清"我们自己写回"和"用户又复制了一遍同一个东西"。
    /// </summary>
    private bool IsSelfEcho(string signature)
    {
        if (_selfWriteAt == default) return false;

        if (DateTimeOffset.UtcNow - _selfWriteAt > SelfWriteEchoWindow)
        {
            _selfWriteSignatures = Array.Empty<string>();
            _selfWriteAt = default;
            return false;
        }

        foreach (var candidate in _selfWriteSignatures)
        {
            if (string.Equals(candidate, signature, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- 反馈

    /// <summary>
    /// 复制成功后的提示。默认交给宿主的岛弹一条通知 —— 它会把岛体临时接管成一条
    /// 「已复制 · 摘要」的小条，几秒后自动还原成常驻内容。这比在常驻视图内部做高亮显眼得多，
    /// 也正是「复制时岛上一闪几秒」想要的效果；关掉通知开关才退回视图内闪光。
    /// </summary>
    private void ShowCopyFeedback(ClipItem item) => ShowToast("已复制", item);

    /// <summary>岛上的反馈条：标题随动作变（复制 / 粘贴），正文是这条记录的摘要。</summary>
    private void ShowToast(string title, ClipItem item)
    {
        var seconds = FlashSeconds;

        if (NotifyOnCopy)
        {
            try
            {
                Context.Island.ShowMessage(new IslandMessage
                {
                    Title = title,
                    Text = item.Summary,
                    Glyph = Manifest.IconGlyph ?? "\uE77F",
                    AccentColor = Accent,
                    Duration = TimeSpan.FromSeconds(seconds),
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"弹岛通知失败，退回视图内高亮（已忽略）：{ex.Message}");
                _view.ShowFlash(item, TimeSpan.FromSeconds(seconds), title);
            }

            return;
        }

        _view.ShowFlash(item, TimeSpan.FromSeconds(seconds), title);
    }

    // ---------------------------------------------------------------- 岛与聚光卡

    private void ApplyEnabled() => SetContent(Settings.Get("enabled", true) ? _content : null);

    private void ApplyLimit()
    {
        _history.Trim(HistoryLimit);
        Log.Info($"历史上限已改为 {HistoryLimit} 条");
    }

    private void OnHistoryChanged()
    {
        _view.Apply(_history.Items);

        // 设置页的监听者出错不能影响宿主
        try
        {
            HistoryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"设置页状态同步失败（已忽略）：{ex.Message}");
        }
    }

    /// <summary>
    /// 点岛体：打开聚光卡。卡片尺寸、内容都由插件决定，
    /// 飞入飞回、遮罩、点卡片外与 Esc 收起由宿主负责。
    /// 视图必须独立于岛视图（每个窗口一棵树），实例可以复用。
    /// </summary>
    private void OpenSpotlight()
    {
        _spotlight ??= new ClipboardSpotlightView(Manifest, Activate, () => ClearHistory(), Theme);
        _spotlight.Refresh(_history.Items);

        Context.Island.OpenSpotlight(new IslandSpotlight
        {
            Content = _spotlight,
            Size = new Windows.Foundation.Size(720, 460),   // 期望尺寸（DIP），宿主会夹到工作区内
            OnClosed = () => _spotlight?.OnHostClosed(),
        });
    }

    // ---------------------------------------------------------------- 点一条历史

    /// <summary>点历史里的一条（大岛展开态的行 / 聚光卡里的行）：写回剪贴板并粘进当前输入框。</summary>
    private void Activate(ClipItem item) => _ = ActivateAsync(item);

    private async Task ActivateAsync(ClipItem item)
    {
        await _pasteGate.WaitAsync();
        try
        {
            if (_stopped) return;

            if (!await WriteToClipboardAsync(item)) return;

            // 顺序要紧：先让宿主收掉卡片，再把焦点还给目标窗口。
            // 反过来会被卡片收回动画那一下抢走焦点，Ctrl+V 就发到空处了
            Context.Island.CloseSpotlight();
            await Task.Delay(SpotlightSettleMs).ConfigureAwait(true);

            var pasted = false;
            if (PasteOnPick && !_stopped)
            {
                pasted = await PasteTarget.SendPasteAsync(Log);
                Log.Info(pasted
                    ? $"已粘贴到当前窗口：{item.Summary}"
                    : $"只写回了剪贴板（未粘贴）：{item.Summary}");
            }
            else
            {
                Log.Info($"已重新复制到剪贴板：{item.Summary}");
            }

            if (_stopped) return;

            // 反馈要跟动作对上：真的按了 Ctrl+V 就说「已粘贴」，否则只能说「已复制」
            ShowToast(pasted ? "已粘贴" : "已复制", item);
        }
        catch (Exception ex)
        {
            Log.Error("处理历史条目失败（已忽略）", ex);
        }
        finally
        {
            _pasteGate.Release();
        }
    }

    /// <summary>把一条历史写回系统剪贴板。成功返回 true。</summary>
    private async Task<bool> WriteToClipboardAsync(ClipItem item)
    {
        try
        {
            // 先记下"这次是我们自己写的"：剪贴板变化可能在我们返回之前就送到，
            // 晚一步记录就会被当成"用户又复制了一遍"。文件记录要认两种指纹（文件 / 路径文本退路）
            _selfWriteSignatures = item.IsFiles
                ? new[]
                {
                    ClipboardReader.SignatureForFiles(item.Paths),
                    ClipboardReader.SignatureForText(item.PlainText),
                }
                : new[] { item.Signature };
            _selfWriteAt = DateTimeOffset.UtcNow;

            // 文件记录走 Win32 CF_HDROP：WinRT 的 StorageFile 打不开带隐藏/系统属性的文件
            // （桌面上的 .lnk 经常带），那种情况会退化成"粘出来只剩路径"
            if (item.IsFiles && await ClipboardWriter.TrySetFilesAsync(item.Paths, Log))
            {
                FinishWrite(item);
                await Task.Delay(PasteDelayMs).ConfigureAwait(true);
                return true;
            }

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };

            if (item.IsImage)
            {
                var path = _history.GetExistingImagePath(item);
                if (path is null)
                {
                    Log.Warn("图片缓存已不在，无法重新复制");
                    Context.Island.ShowMessage(new IslandMessage
                    {
                        Title = Manifest.Name,
                        Text = "这条图片的缓存已经不在，没法重新复制了",
                        Glyph = Manifest.IconGlyph ?? "\uE77F",
                        Duration = TimeSpan.FromSeconds(3),
                    });
                    return false;
                }

                var file = await StorageFile.GetFileFromPathAsync(path);
                package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            }
            else if (item.IsFiles)
            {
                // 文件没写进去（剪贴板被占等）：退回路径文本，至少还能粘出路径
                Log.Warn($"写文件到剪贴板失败，退回路径文本（{item.Paths.Count} 项）");
                package.SetText(item.PlainText);
            }
            else
            {
                package.SetText(item.PlainText);
            }

            Clipboard.SetContent(package);
            FinishWrite(item);

            // 剪贴板内容是异步落到系统里的，等一拍再按键，否则目标程序可能还拿到旧内容
            await Task.Delay(PasteDelayMs).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("写剪贴板失败（已忽略）", ex);
            return false;
        }
    }

    /// <summary>写完之后：让内容留在剪贴板上，并把这条提到历史最前。</summary>
    private void FinishWrite(ClipItem item)
    {
        // 让内容在插件退出后也留在剪贴板里；失败不影响这次复制（图片场景常失败，属正常）
        try { Clipboard.Flush(); }
        catch (Exception ex) { Log.Debug($"Clipboard.Flush 失败（忽略）：{ex.Message}"); }

        // 提到最前（会触发 Changed → 界面刷新）
        _history.TryPromote(item.Signature);
    }
}
