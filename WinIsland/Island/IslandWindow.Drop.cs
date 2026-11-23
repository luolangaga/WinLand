using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using WinIsland.Core;
using WinIsland.Core.Plugins;

namespace WinIsland.Island;

public sealed partial class IslandWindow
{
    #region 文件投放（拖文件进岛）

    /// <summary>
    /// 投放面板尺寸（DIP）。它是常量，并且由 <see cref="ComputeCanvasFootprint"/> 在启动时就预留进画布 ——
    /// 拖拽全程绝不 resize 窗口：客户区尺寸一变，DWM 会用上一帧合成新窗口矩形（残影），
    /// OLE 拖放的命中也会被那次 resize 打断。
    /// </summary>
    private static readonly Size DropPanelSize = new(460, 104);
    /// <summary>边缘自动滚动触发带宽度（DIP，相对滚动视口）。</summary>
    private const double DropEdgeZone = 44;
    /// <summary>边缘滚动最高速度（DIP/秒），按指针压入深度线性缩放。</summary>
    private const double DropScrollMaxSpeed = 1100;
    private const int DropScrollTickMs = 16;
    /// <summary>指针离开岛体后的宽限收起时间：XAML 在子元素之间移动时偶发 DragLeave，立刻收会抖。</summary>
    private const int DropExitGraceMs = 160;
    /// <summary>速度一阶滤波系数：进出边缘带时加速/减速都是渐变的（那口"顺滑"就靠它）。</summary>
    private const double DropScrollSmoothing = 0.25;
    /// <summary>图片载荷的最大字节数：拖一张几十 MB 的图进来不值得把内存拉满。</summary>
    private const int MaxDropImageBytes = 32 * 1024 * 1024;

    /// <summary>可投放目标快照（IslandService 在台账变化时推来，已按 Order 排好）。</summary>
    private IReadOnlyList<(string Owner, IslandDropTarget Target)> _dropTargets
        = Array.Empty<(string, IslandDropTarget)>();
    /// <summary>投放会话进行中：岛体是投放面板，悬停/内容切换都要为它让路。</summary>
    private bool _dropping;
    private DropStripView? _dropView;
    private readonly DispatcherQueueTimer _dropScroll;
    private readonly DispatcherQueueTimer _dropExitGrace;
    /// <summary>本次会话读出来的载荷（卡片可投状态与摘要都按它算；未知表示还没读完）。</summary>
    private DropPayload _dropPayload = DropPayload.Unknown;
    /// <summary>这次拖拽的内容能不能接（格式判断只做一次，DragOver 复用）。</summary>
    private bool _dropSupported;
    /// <summary>这次拖拽的内容读不出来（未知格式/浏览器虚拟文件）：本次拖拽期间不再展开面板，免得反复开合。</summary>
    private bool _dropUnreadable;
    /// <summary>最近一次 DragOver 的位置（滚动视口坐标，DIP）。</summary>
    private double _dropPointerX = double.NaN;
    private double _dropOffset;
    private double _dropVelocity;
    private int _dropArmedIndex = -1;
    /// <summary>会话序号：异步读载荷期间用户可能已经拖走或又拖回来，回填时用它对齐。</summary>
    private int _dropSessionId;

    /// <summary>投放目标台账变化（插件注册/注销、宿主内置注册）。</summary>
    public void SetDropTargets(IReadOnlyList<(string Owner, IslandDropTarget Target)> targets)
    {
        _dropTargets = targets;
        _log.Debug($"投放目标已更新：{targets.Count} 个（{string.Join("、", targets.Select(t => t.Target.Title))}）");
        if (_dropping) RefreshDropTiles();
        EnsureCanvas();
    }

    /// <summary>按最新台账重建卡片：正在投放时立刻生效，已经被高亮的卡片不再存在就解除高亮。</summary>
    private void RefreshDropTiles()
    {
        _dropView?.SetTargets(_dropTargets);
        _dropArmedIndex = -1;
        _dropOffset = Math.Min(_dropOffset, _dropView?.ScrollableWidth ?? 0);
    }

    /// <summary>
    /// 系统换主题时把投放面板整块换新：面板底色、文字与边缘渐隐都是建视图时烘进画刷的，改不动只能重建。
    /// 会话状态（载荷、滚动位置）由岛窗口持有，重建后原样回填；尺寸不变，所以不碰窗口几何，
    /// 也不打断正在进行的拖放。
    /// </summary>
    private void RebuildDropPanel()
    {
        var view = new DropStripView(_style, _materialApplied, _light);
        view.SetTargets(_dropTargets);
        view.SetPayload(_dropPayload);

        _dropView = view;
        _dropArmedIndex = -1;      // 换了一批卡片，高亮由下一次 DragOver 重新裁决
        SetContentImmediate(view.Root);
        view.ScrollTo(_dropOffset);
    }

    private void IslandRoot_DragEnter(object sender, DragEventArgs e)
    {
        // 格式判断会同步调用源进程（Contains）：每次拖拽只问一次，DragOver 用缓存结果 ——
        // 拖拽期间 DragOver 触发频率很高，不能每次都去问源进程
        _dropSupported = DescribeDrag(e.DataView) != IslandDropKind.None;
        if (!_dropSupported)
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;

        if (_spotlightOccluded) return;      // 聚光卡期间岛体整块隐藏，拖放不该发生
        _dropExitGrace.Stop();
        BeginDropSession(e);
    }

    private void IslandRoot_DragOver(object sender, DragEventArgs e)
    {
        if (!_dropSupported)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        if (!_dropping) BeginDropSession(e);

        e.AcceptedOperation = _dropping ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.Handled = true;

        if (!_dropping) return;

        _dropExitGrace.Stop();
        UpdateDropHover(e);
    }

    private void IslandRoot_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dropUnreadable = false;   // 这次拖拽离开岛体：下一次（新拖拽）重新允许展开
        if (!_dropping) return;

        _dropExitGrace.Stop();
        _dropExitGrace.Start();
    }

    private async void IslandRoot_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        int armed = _dropArmedIndex;
        bool hasTarget = _dropping && armed >= 0 && armed < _dropTargets.Count;
        e.AcceptedOperation = hasTarget ? DataPackageOperation.Copy : DataPackageOperation.None;

        var deferral = e.GetDeferral();
        var target = hasTarget ? _dropTargets[armed] : default;
        // 载荷可能在松手时还没读完（拖得很快）：先用缓存的那份，没有就现读一次
        var payload = _dropPayload;

        // 先收回岛体，再去读载荷/执行动作：插件动作可能等上几秒，不能让它把投放面板吊在屏幕上
        EndDropSession();

        try
        {
            if (!hasTarget) return;      // 没对准卡片：什么都不做（绝不误触发打开文件）

            if (!payload.IsKnown) payload = await ReadPayloadAsync(e.DataView);
            if (!payload.IsKnown)
            {
                _log.Warn($"投放「{target.Target.Title}」已忽略：这次拖拽的载荷读不出来。");
                return;
            }

            // 卡片是在"载荷未知"时点亮的，这里按真实载荷复核一次（类型 + 扩展名）
            if (!DropStripView.Accepts(target.Target, payload))
            {
                _log.Warn($"投放「{target.Target.Title}」已忽略：它不接受{Describe(payload)}。");
                return;
            }

            _log.Info($"投放「{target.Target.Title}」（{target.Owner}）：{Describe(payload)}。");
            var message = await target.Target.Handler(BuildContext(payload));

            // 每个动作都要有反馈：插件没返回文案时，宿主补一条默认的「已完成」
            ShowMessage(new IslandMessage
            {
                Title = string.IsNullOrWhiteSpace(message) ? $"已完成「{target.Target.Title}」" : message,
                Glyph = target.Target.Glyph,
                AccentColor = target.Target.AccentColor,
            });
        }
        catch (Exception ex)
        {
            _log.Error("处理文件投放失败", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>展开成投放面板：岛体变宽、内容换成投放卡片。</summary>
    private void BeginDropSession(DragEventArgs e)
    {
        if (_dropping) return;
        if (_dropUnreadable) return;        // 这次拖拽的内容读不出来，别再反复开合
        if (!_settings.Get(DropEnabledKey, true)) return;

        _dropping = true;
        _dropSessionId++;
        _dropArmedIndex = -1;
        _dropPayload = DropPayload.Unknown;
        _dropPointerX = double.NaN;
        _dropOffset = 0;
        _dropVelocity = 0;

        // 投放面板与展开队列不能共存：队列排到岛体外面，会把面板顶出点击形状
        TeardownQueue();
        _expanded = false;
        ClearTouchExpand();

        _dropView = new DropStripView(_style, _materialApplied, _light);
        _dropView.SetTargets(_dropTargets);
        SetContentImmediate(_dropView.Root);     // 面板自带进场动画，不再套一层内容交接

        EnsureCanvas();
        AnimateIslandSize(DropPanelSize, ExpandDuration);
        ApplyCornerRadius(DropPanelSize.Height, expanded: false);
        _dropView.PlayEnter();
        UpdateVisibility();

        _ = RefreshDropPayloadAsync(e.DataView, _dropSessionId);
    }

    /// <summary>结束投放会话：收起面板、恢复原本的内容（与悬停收起同一套时序）。</summary>
    private void EndDropSession()
    {
        if (!ClearDropSession()) return;

        TransitionToLiveState();
        UpdateVisibility();
    }

    /// <summary>只清会话状态与定时器，不动岛体外观（聚光卡遮挡等路径自己接管后续）。</summary>
    private bool ClearDropSession()
    {
        if (!_dropping) return false;

        _dropping = false;
        _dropSessionId++;
        _dropArmedIndex = -1;
        _dropPayload = DropPayload.Unknown;
        StopDropScroll();
        _dropExitGrace.Stop();
        _dropView = null;
        return true;
    }

    /// <summary>会话仍在进行时把内容切回投放面板（临时消息结束、聚光卡关闭等路径会走到）。</summary>
    private void ShowDropPanel()
    {
        if (_dropView == null) return;
        SetContentImmediate(_dropView.Root);
        AnimateIslandSize(DropPanelSize, ExpandDuration);
        ApplyCornerRadius(DropPanelSize.Height, expanded: false);
    }

    /// <summary>
    /// 拖拽期间的"悬停"：OLE 拖放期间指针被源进程捕获，我们收不到任何指针事件，
    /// 位置只能从 DragOver 的坐标推。这里只记录位置 + 更新高亮与系统提示，
    /// 滚动交给 16ms 定时器 —— 指针停着不动时 OLE 不再发 DragOver，光靠事件永远滚不起来。
    /// </summary>
    private void UpdateDropHover(DragEventArgs e)
    {
        if (_dropView == null) return;

        var point = e.GetPosition(_dropView.Root);
        _dropPointerX = _dropView.ToViewportPoint(point).X;
        // 卡片增减后视口会自己夹一次滚动位置，这里跟它对齐，免得下一拍往回跳
        _dropOffset = _dropView.HorizontalOffset;

        int index = _dropView.HitTest(point);
        if (_dropView.SetArmed(index)) _dropArmedIndex = index;

        e.DragUIOverride.Caption = index >= 0 ? DropCaption(index) : "拖到卡片上松开";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;

        // 在边缘带里才启动定时器；离开由定时器自己减速停下，不硬切
        if (_dropView.ScrollableWidth > 1 && DropScrollTargetVelocity() != 0) _dropScroll.Start();
    }

    /// <summary>
    /// 系统拖拽气泡的文案：卡片带副标题就一起写上（例如「记入历史」· 存进历史列表）——
    /// 卡片只有 72px 宽，副标题在卡片上放不下，气泡是唯一能把"这张卡到底做什么"说清楚的地方。
    /// </summary>
    private string DropCaption(int index)
    {
        var title = _dropView?.TitleOf(index) ?? string.Empty;
        var hint = index >= 0 && index < _dropTargets.Count ? _dropTargets[index].Target.Hint : null;
        return string.IsNullOrWhiteSpace(hint) ? $"投放到「{title}」" : $"投放到「{title}」· {hint}";
    }

    /// <summary>目标滚速：越往边缘带里压越快（线性），带外为 0。</summary>
    private double DropScrollTargetVelocity()
    {
        if (_dropView == null || double.IsNaN(_dropPointerX)) return 0;

        double viewport = _dropView.ViewportWidth;
        if (viewport <= 0) return 0;

        double zone = Math.Min(DropEdgeZone, viewport / 3);
        if (zone <= 0) return 0;

        if (_dropPointerX < zone)
        {
            return -DropScrollMaxSpeed * Math.Clamp((zone - _dropPointerX) / zone, 0, 1);
        }

        if (_dropPointerX > viewport - zone)
        {
            return DropScrollMaxSpeed * Math.Clamp((_dropPointerX - (viewport - zone)) / zone, 0, 1);
        }

        return 0;
    }

    private void DropScrollTick()
    {
        if (!_dropping || _dropView == null)
        {
            StopDropScroll();
            return;
        }

        double target = DropScrollTargetVelocity();
        _dropVelocity += (target - _dropVelocity) * DropScrollSmoothing;

        if (target == 0 && Math.Abs(_dropVelocity) < 4)
        {
            StopDropScroll();
            return;
        }

        double max = _dropView.ScrollableWidth;
        _dropOffset = Math.Clamp(_dropOffset + _dropVelocity * (DropScrollTickMs / 1000.0), 0, max);

        // 撞到两端就把速度清零，免得在边界上顶着不动却还在加速
        if ((_dropOffset <= 0 && _dropVelocity < 0) || (_dropOffset >= max && _dropVelocity > 0))
        {
            _dropVelocity = 0;
        }

        _dropView.ScrollTo(_dropOffset);
    }

    private void StopDropScroll()
    {
        _dropVelocity = 0;
        _dropScroll.Stop();
    }

    /// <summary>载荷读取：面板先出来，摘要与卡片可投状态随后回填（读源进程的数据可能要等一拍）。</summary>
    private async Task RefreshDropPayloadAsync(DataPackageView view, int sessionId)
    {
        try
        {
            var payload = await ReadPayloadAsync(view);
            if (sessionId != _dropSessionId || !_dropping) return;

            if (!payload.IsKnown)
            {
                // 读不出来（未知格式、浏览器虚拟文件等）：收回面板，并且这次拖拽期间不再展开 ——
                // 否则会停在"正在读取…"上一直亮着，看着像卡死
                _log.Info("这次拖入的内容读不出来，投放面板已收回。");
                _dropUnreadable = true;
                EndDropSession();
                return;
            }

            _dropPayload = payload;
            _dropView?.SetPayload(payload);
            // 卡片刚按真实载荷筛过一遍，高亮索引跟着视图走（被收起来的那张不能再算数）
            _dropArmedIndex = _dropView?.ArmedIndex ?? -1;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取拖入的内容失败（投放面板仍可用）：{ex.Message}");
        }
    }

    /// <summary>
    /// 读某个格式的超时时间。拖拽源（浏览器等）偶尔会对某个格式**一直不回应**，
    /// 每次读都必须带超时，读不出来就按该格式不存在处理、走下一步退化。
    /// </summary>
    private static readonly TimeSpan DropProbeTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DropFormatTimeout = TimeSpan.FromMilliseconds(2000);
    /// <summary>整次载荷读取的总上限（各步超时之和的安全网）。</summary>
    private static readonly TimeSpan DropReadTimeout = TimeSpan.FromMilliseconds(6000);

    /// <summary>给一次读取套上超时；超时返回 default，并保证迟到的异常不会变成未观察异常。</summary>
    private async Task<T?> ReadWithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
        {
            _log.Warn($"读取拖入的{what}超时（{timeout.TotalMilliseconds:0} ms），按读不出来处理");
            _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return default;
        }

        return await task;
    }

    /// <summary>
    /// 读一次拖入内容。**整段跑在线程池上**：<see cref="DataPackageView"/> 的每个成员（
    /// <c>Contains</c> / <c>AvailableFormats</c> / <c>GetXxxAsync</c>）都是**同步 COM 调用**，
    /// 源进程不回应时会直接阻塞调用线程 —— 放在 UI 线程上就会把整个界面冻住，超时也救不回来。
    /// </summary>
    private async Task<DropPayload> ReadPayloadAsync(DataPackageView view)
    {
        var payload = await ReadWithTimeoutAsync(
            Task.Run(() => ReadPayloadCoreAsync(view)), DropReadTimeout, "拖入内容");

        return payload ?? DropPayload.Unknown;
    }

    /// <summary>
    /// 载荷读取的**退化链**（每一步都带超时，读不出来就往下走）：
    /// 真实文件 → 图片 → 文本。
    ///
    /// 「真实文件」只认 <c>FileDrop</c>（CF_HDROP）：浏览器拖图片时会用
    /// <c>FileGroupDescriptorW</c> + <c>FileContents</c> 提供**虚拟文件**（没有 FileDrop），
    /// 那种 <c>GetStorageItemsAsync</c> 读不出真实路径（会一直挂着）—— 让它落回图片/文本。
    /// </summary>
    private async Task<DropPayload> ReadPayloadCoreAsync(DataPackageView view)
    {
        string formats;
        try
        {
            formats = string.Join("、", view.AvailableFormats);
        }
        catch (Exception ex)
        {
            formats = $"(读不到格式列表：{ex.Message})";
        }

        var payload = DropPayload.Unknown;

        // 1) 存储项：真文件给路径；浏览器拖图片给的是"虚拟文件"（没有真实路径，但内容可读）→ 按图片读出字节
        if (view.Contains(StandardDataFormats.StorageItems))
        {
            payload = await ReadStorageItemsAsync(view);
        }

        // 2) 位图（能拿到位图字节才算；SVG 这类浏览器给不出位图的会落到第 3 步）
        if (!payload.IsKnown && await LooksLikeImageAsync(view))
        {
            var bytes = await ReadBitmapBytesAsync(view);
            if (bytes is { Length: > 0 })
            {
                payload = new DropPayload(IslandDropKind.Image, Array.Empty<string>(), Array.Empty<string>(), ImageBytes: bytes);
            }
        }

        // 3) 文本（纯文本选区、链接、虚拟文件/矢量图的地址都落在这里）
        if (!payload.IsKnown && view.Contains(StandardDataFormats.Text))
        {
            var text = await SafeGetTextAsync(view);
            if (!string.IsNullOrEmpty(text))
            {
                payload = new DropPayload(IslandDropKind.Text, Array.Empty<string>(), Array.Empty<string>(), Text: text);
            }
        }

        _log.Debug($"拖入载荷：可用格式 [{formats}] → {(payload.IsKnown ? payload.Kind.ToString() : "读不出来")}");
        return payload;
    }

    /// <summary>
    /// 读存储项。两类要分开：
    /// ① 真实文件（有绝对路径）→ 文件载荷；
    /// ② **虚拟文件** —— 浏览器拖网页图片时给的就是这种（只有 <c>FileGroupDescriptorW</c> + <c>FileContents</c>，
    ///   没有真实路径），内容仍可读：按扩展名认出是图片就把它读成字节，走图片载荷（SVG 也能原样保存）。
    /// </summary>
    private async Task<DropPayload> ReadStorageItemsAsync(DataPackageView view)
    {
        var items = await ReadWithTimeoutAsync(view.GetStorageItemsAsync().AsTask(), DropFormatTimeout, "文件列表");
        if (items is null || items.Count == 0) return DropPayload.Unknown;

        var paths = new List<string>();
        var names = new List<string>();
        StorageFile? virtualImage = null;

        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.Path))
            {
                paths.Add(item.Path);
                names.Add(item.Name);
                continue;
            }

            // 虚拟机位（浏览器拖的图片、压缩包内、网络位置）：没有绝对路径
            if (virtualImage is null && item is StorageFile file && IsImageExtension(file.FileType))
            {
                virtualImage = file;
            }
        }

        if (paths.Count > 0) return new DropPayload(IslandDropKind.Files, paths, names);

        if (virtualImage is not null)
        {
            var bytes = await ReadStorageFileBytesAsync(virtualImage);
            if (bytes is { Length: > 0 })
            {
                _log.Info($"拖入的是虚拟文件「{virtualImage.Name}」（浏览器提供，没有真实路径），已按图片读出内容：{bytes.Length / 1024} KB");
                return new DropPayload(IslandDropKind.Image, Array.Empty<string>(), Array.Empty<string>(), ImageBytes: bytes);
            }
        }

        return DropPayload.Unknown;
    }

    private async Task<byte[]?> ReadStorageFileBytesAsync(StorageFile file)
    {
        try
        {
            return await ReadWithTimeoutAsync(ReadStorageFileBytesCoreAsync(file), DropFormatTimeout, "文件内容");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadStorageFileBytesCoreAsync(StorageFile file)
    {
        using var stream = await file.OpenReadAsync();
        if (stream.Size == 0 || stream.Size > (ulong)MaxDropImageBytes) return null;

        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>像不像图片拖拽：有位图格式，或 HTML 片段里带 &lt;img&gt;，或文本本身就是图片地址。</summary>
    private async Task<bool> LooksLikeImageAsync(DataPackageView view)
    {
        if (view.Contains(StandardDataFormats.Bitmap)) return true;
        if (await HasImageHtmlAsync(view)) return true;
        return IsImageUrl(await SafeGetTextAsync(view));
    }

    /// <summary>HTML 片段里有没有 &lt;img&gt;：网页图片拖拽的典型特征（文字选区不会有）。</summary>
    private async Task<bool> HasImageHtmlAsync(DataPackageView view)
    {
        try
        {
            if (!view.Contains(StandardDataFormats.Html)) return false;

            // 探测用的超时更短：读不到就当没有，别为了判断类型把面板卡住
            var html = await ReadWithTimeoutAsync(view.GetHtmlFormatAsync().AsTask(), DropProbeTimeout, "HTML 片段");
            return html is not null && html.Contains("<img", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;   // 读不到就当没有，按文本处理
        }
    }

    private async Task<string?> SafeGetTextAsync(DataPackageView view)
    {
        try
        {
            return await ReadWithTimeoutAsync(view.GetTextAsync().AsTask(), DropFormatTimeout, "文本");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>文本是不是一个"图片地址"（http(s)/file/data/绝对路径，且扩展名是图片）。</summary>
    private static bool IsImageUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.Length > 2048 || trimmed.Contains('\n')) return false;   // 多行文本显然不是单个地址

        bool looksLikeAddress = trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                || trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
                                || trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                                || (trimmed.Length > 2 && trimmed[1] == ':');     // C:\...
        if (!looksLikeAddress) return false;

        // 注意：本文件同时 using 了 Microsoft.UI.Xaml.Shapes（里面也有 Path），必须写全名
        return IsImageExtension(System.IO.Path.GetExtension(trimmed));
    }

    /// <summary>扩展名是不是图片（含矢量图 svg）。传 ".png" 或 "png" 都行。</summary>
    private static bool IsImageExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return false;

        var normalized = extension.StartsWith('.') ? extension : "." + extension;
        switch (normalized.ToLowerInvariant())
        {
            case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp":
            case ".webp": case ".tif": case ".tiff": case ".ico": case ".avif":
            case ".svg": case ".heic":
                return true;
            default:
                return false;
        }
    }

    /// <summary>读图片字节（保留源格式）。超时、超限或读不出来返回 null，调用方按"不支持"处理。</summary>
    private async Task<byte[]?> ReadBitmapBytesAsync(DataPackageView view)
    {
        try
        {
            return await ReadWithTimeoutAsync(ReadBitmapCoreAsync(view), DropFormatTimeout, "图片数据");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadBitmapCoreAsync(DataPackageView view)
    {
        var reference = await view.GetBitmapAsync();
        using var stream = await reference.OpenReadAsync();
        if (stream.Size == 0 || stream.Size > (ulong)MaxDropImageBytes) return null;

        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// 「这次拖拽能不能接」的同步判定（DragEnter/DragOver 用）：有文件、图片或文本之一就接。
    /// 到底算哪种载荷由 <see cref="ResolveKindAsync"/> 读到内容后再定 ——
    /// 同步阶段分辨不出「文字选区附带的那张快照位图」。
    /// </summary>
    private static IslandDropKind DescribeDrag(DataPackageView view)
    {
        if (view.Contains(StandardDataFormats.StorageItems)) return IslandDropKind.Files;
        if (view.Contains(StandardDataFormats.Bitmap)) return IslandDropKind.Image;
        if (view.Contains(StandardDataFormats.Text)) return IslandDropKind.Text;
        return IslandDropKind.None;
    }

    private static IslandDropContext BuildContext(DropPayload payload) => new()
    {
        Kind = payload.Kind,
        Paths = payload.Paths,
        Names = payload.Names,
        Text = payload.Text,
        ImageBytes = payload.ImageBytes,
    };

    /// <summary>日志用的载荷描述。</summary>
    private static string Describe(DropPayload payload) => payload.Kind switch
    {
        IslandDropKind.Files => $"{payload.Paths.Count} 个文件",
        IslandDropKind.Text => $"文本（{payload.Text?.Length ?? 0} 字符）",
        IslandDropKind.Image => $"图片（{(payload.ImageBytes?.LongLength ?? 0) / 1024} KB）",
        _ => "未知载荷",
    };

    /// <summary>island.dropEnabled：关掉后岛对文件拖放完全无感（连 DragEnter 都不会触发）。</summary>
    private void ApplyDropSetting()
    {
        bool enabled = _settings.Get(DropEnabledKey, true);
        IslandRoot.AllowDrop = enabled;
        if (!enabled && _dropping) EndDropSession();
    }

    #endregion
}

