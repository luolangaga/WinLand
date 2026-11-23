using System.Text.Json;
using WinIsland.Core.Plugins;

namespace WinIsland.Core;

/// <summary>
/// 单实例闸门：同一个登录会话里只允许跑一个 WinIsland。
/// 检查在 XAML 起来之前做（<see cref="Program.Main"/>），重复启动的进程根本走不到建岛、装托盘、起插件那一步。
///
/// 名字带 <c>Local\</c> 前缀 = 按登录会话隔离：多用户 / 远程桌面各自可以跑一个，同一会话里不管是不是管理员身份都只跑一个；
/// 顺带避开 <c>Global\</c> 需要的「创建全局对象」特权（普通用户没有）。
///
/// 判定用 <see cref="Mutex.WaitOne(int)"/> 而不是构造函数给出来的 createdNew：后者只看对象在不在，
/// 而上一个实例刚退出、正弹提醒的重复实例还攥着它的句柄时对象仍然在，会把后来的实例误判成多开。
/// WaitOne 问的是「有没有人真的持有」，那才是实例还活着的证据。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\WinIsland.SingleInstance";
    private const string ActivationEventName = @"Local\WinIsland.SingleInstance.Activate";

    /// <summary>
    /// 请求交接区。**必须是文件而不是命名共享内存**：重复启动的进程写完就退，
    /// 进程一退，它持有的内存映射句柄随之关闭 —— 映射对象当场销毁，
    /// 在跑的实例再去 OpenExisting 只会拿到 FileNotFoundException。
    /// 文件没有这个问题（文件系统持有它），并且天然支持「同时来了两条请求」。
    /// </summary>
    private static string RequestsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland", "requests");

    /// <summary>闸门本身。持有它 = 这个进程就是那个唯一实例，必须活到进程结束。</summary>
    private readonly Mutex _mutex;

    /// <summary>重复启动的通知：后来者 Set 一下，在跑的实例据此把自己亮出来。</summary>
    private readonly EventWaitHandle _activation;

    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle activation)
    {
        _mutex = mutex;
        _activation = activation;
    }

    /// <summary>
    /// 抢闸门：抢到返回实例；已经有实例在跑则返回 null —— 此时已经把
    /// <paramref name="request"/> 交给在跑的那个实例，调用方提醒用户之后退出即可。
    /// </summary>
    /// <param name="request">要交给在跑实例的自动化请求（null = 只是重复点了图标）。</param>
    public static SingleInstance? TryAcquire(CommandRequest? request = null)
    {
        EventWaitHandle? activation = null;
        Mutex? mutex = null;
        try
        {
            // 事件先于互斥体创建：互斥体被别人占着的时候，事件必然已经存在，后来者才通知得到那个实例
            activation = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            mutex = new Mutex(false, MutexName);
        }
        catch (UnauthorizedAccessException)
        {
            // 同名对象存在却打不开（在跑的实例是管理员身份，对象带了高完整性标签）：
            // 「打不开」本身就是「已经有人占着」的证据，按多开处理
            activation?.Dispose();
            return null;
        }

        if (TryTakeOwnership(mutex))
        {
            return new SingleInstance(mutex, activation);
        }

        // 已经有实例在跑：先让它拿到抢前台的资格（它把设置窗口调到前面时才不会被系统的前台锁挡住），
        // 再把参数放进共享内存、喊它一声
        Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
        WriteRequest(request);
        activation.Set();

        activation.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// 在跑的实例调用：每来一次重复启动就回调一次（线程池线程，调用方自己编组到 UI 线程）。
    /// 回调拿到的是**这一批**待处理请求（可能为空 = 用户只是又点了一次图标）。
    /// 早到的通知不会丢 —— 自动重置事件会把「没人等着的时候收到的信号」留到下一次等待。
    /// </summary>
    public void ListenForActivation(Action<IReadOnlyList<CommandRequest>> onActivated)
        => _wait = ThreadPool.RegisterWaitForSingleObject(
            _activation,
            (_, _) =>
            {
                try
                {
                    onActivated(TakeRequests());
                }
                catch (Exception)
                {
                    // 线程池回调里绝不能把异常抛出去
                }
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);

    /// <summary>
    /// 重复启动的收尾：记一条日志 + 原生提示。提示框必须是原生的 ——
    /// 这个位置在 XAML 起来之前，没有任何 XAML 对话框可用。
    /// 带自动化参数时不再弹框（脚本调用不该被人点一下）：只写日志。
    /// </summary>
    public static void ReportDuplicateLaunch(bool silent = false)
    {
        try
        {
            new PluginLogService().Host.Warn(silent
                ? "已有实例在运行：参数已转交，本次进程退出。"
                : "检测到重复启动：已经有一个 WinIsland 在运行，本次启动被拒绝。");
        }
        catch (Exception)
        {
            // 日志写不出来不影响提醒
        }

        if (silent) return;

        Win32.ShowStartupMessage(
            "WinIsland 已经在运行，不能重复打开。\n\n程序只允许开一个实例，请使用托盘里的那个（双击托盘图标打开设置）。",
            "WinIsland");
    }

    /// <summary>
    /// 抢闸门的所有权。上一个实例崩溃（没来得及释放）时 WaitOne 会抛 AbandonedMutexException —— 那算我们抢到。
    /// </summary>
    private static bool TryTakeOwnership(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static void WriteRequest(CommandRequest? request)
    {
        if (request == null) return;
        try
        {
            Directory.CreateDirectory(RequestsDirectory);
            // 文件名带时间戳与随机后缀：并发写入互不覆盖，读取方按名字（= 时间）排序即可保序
            var name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json";
            File.WriteAllText(Path.Combine(RequestsDirectory, name), CommandLine.Serialize(request));
        }
        catch (Exception)
        {
            // 交接失败不影响「已经在跑」这个事实：老实例照样只是把设置窗口亮出来
        }
    }

    /// <summary>
    /// 取出并消费**所有**待处理请求（按写入顺序）。没有请求时返回空数组。
    /// 每次都在通知到达后整批取走：一批里可能积了不止一条（脚本连发、事件合并）。
    /// </summary>
    internal static IReadOnlyList<CommandRequest> TakeRequests()
    {
        var result = new List<CommandRequest>();
        try
        {
            if (!Directory.Exists(RequestsDirectory)) return result;

            foreach (var path in Directory.GetFiles(RequestsDirectory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    var request = CommandLine.Deserialize(File.ReadAllText(path));
                    if (request != null) result.Add(request);
                }
                catch (Exception)
                {
                    // 单条坏文件不能卡住后面的请求
                }
                finally
                {
                    try { File.Delete(path); }
                    catch (Exception) { /* 下次启动会再扫到它 */ }
                }
            }
        }
        catch (Exception)
        {
            // 目录读不了：当作没有请求
        }

        return result;
    }

    public void Dispose()
    {
        _wait?.Unregister(null);   // 不等回调跑完：这会儿应用正在退出
        _activation.Dispose();
        // 关掉最后一个句柄，闸门自动交出去；不显式 ReleaseMutex：所有权是线程相关的，退出时未必在同一个线程上
        _mutex.Dispose();
    }
}
