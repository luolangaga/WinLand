using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using WinIsland.Core;

namespace ClipboardIsland;

/// <summary>
/// 剪贴板变化监听 —— 两路并行，缺一不可。
///
/// 只用 WinRT 的 <c>Clipboard.ContentChanged</c> 会漏事件，这是"偶尔不触发"的根源：
///   · 极短时间内连续复制，事件会被合并甚至丢掉；
///   · 以延迟渲染方式写剪贴板的程序、走老 OLE 路径的程序，不一定会触发这个事件；
///   · 剪贴板被某个程序短暂独占（打开了没关）时那一次变化也可能被吞掉。
///
/// 所以这里再加一条轮询，用 Win32 的 <c>GetClipboardSequenceNumber</c> 比对序号。
/// 这个序号是**系统自己**维护的计数器，剪贴板每变一次就自增 —— 不依赖任何程序自觉上报，
/// 因此事件通道漏掉的变化，轮询一定补得回来。
///
/// 两路都先比对序号再上报：谁先发现谁处理，另一路自动被序号吸收，绝不会重复触发。
/// 两路都跑在 UI 线程（事件订阅于 UI 线程，定时器也是宿主在 UI 线程上跑的），所以无需加锁。
/// </summary>
internal sealed class ClipboardWatcher : IDisposable
{
    /// <summary>轮询间隔。一次 user32 调用而已，开销可以忽略；400ms 是"够及时"和"够省"的折中。</summary>
    private const int PollIntervalMs = 400;

    private readonly IPluginLogger _log;
    private readonly Func<TimeSpan, bool, Action, IDisposable> _createTimer;
    private readonly Action<long, string> _onChanged;

    private IDisposable? _timer;
    private long _lastSequence;
    private bool _eventSubscribed;
    private bool _disposed;

    /// <param name="createTimer">宿主的 <c>IPluginContext.CreateTimer</c>：interval、是否重复、tick。</param>
    /// <param name="onChanged">序号变化时回调：参数是新的剪贴板序号，以及是哪一路发现的（写日志用）。</param>
    public ClipboardWatcher(
        IPluginLogger log,
        Func<TimeSpan, bool, Action, IDisposable> createTimer,
        Action<long, string> onChanged)
    {
        _log = log;
        _createTimer = createTimer;
        _onChanged = onChanged;
    }

    /// <summary>
    /// 当前剪贴板序号。取不到（剪贴板正被独占）时返回 0。
    /// 这个调用不需要打开剪贴板，失败率极低。
    /// </summary>
    public static long SequenceNumber()
    {
        try
        {
            return GetClipboardSequenceNumber();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>开始监听。</summary>
    public void Start()
    {
        if (_disposed) return;

        // 先记下基准：启动之前就躺在剪贴板里的内容不算「用户刚复制」
        _lastSequence = SequenceNumber();

        var eventOk = false;
        try
        {
            Clipboard.ContentChanged += OnContentChanged;
            _eventSubscribed = true;
            eventOk = true;
        }
        catch (Exception ex)
        {
            // 事件通道挂了还有轮询兜底，所以只 Warn，不放弃监听
            _log.Warn($"订阅剪贴板事件失败（改由序号轮询兜底）：{ex.Message}");
        }

        _timer = _createTimer(TimeSpan.FromMilliseconds(PollIntervalMs), true, () => Poll("序号轮询"));

        _log.Info($"剪贴板监听已启动（起始序号 {_lastSequence}；事件通道{(eventOk ? "正常" : "不可用")}"
                  + $" + {PollIntervalMs}ms 序号轮询兜底）");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Dispose();
        _timer = null;

        if (!_eventSubscribed) return;

        _eventSubscribed = false;
        try
        {
            Clipboard.ContentChanged -= OnContentChanged;
        }
        catch (Exception ex)
        {
            _log.Debug($"退订剪贴板事件失败（忽略）：{ex.Message}");
        }
    }

    /// <summary>事件通道：订阅在 UI 线程，回调也回到 UI 线程；再兜一次以防万一。</summary>
    private void OnContentChanged(object? sender, object e) => Poll("剪贴板事件");

    private void Poll(string reason)
    {
        if (_disposed) return;

        var sequence = SequenceNumber();

        // 序号 0 = 这一刻剪贴板被别的程序独占，读不到基准。
        // 直接跳过、**不要**更新 _lastSequence，等下一拍再判，免得把这次变化永远吞掉
        if (sequence == 0) return;

        if (sequence == _lastSequence) return;   // 没变，或这一路是另一路已经处理过的回声

        _lastSequence = sequence;
        _onChanged(sequence, reason);
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
