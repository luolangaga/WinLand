using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace HardwareMonitor;

internal sealed class ProcessFrames
{
    public const long SameFrameTicks = TimeSpan.TicksPerMillisecond;
    public const long Win32kSameFrameTicks = TimeSpan.TicksPerMillisecond * 4;
    public const long ModeWindowTicks = TimeSpan.TicksPerMillisecond * 500;

    public readonly long[] Stamps = new long[180];
    public int Index;
    public long LastPresent;
    public long LastHistory;
    public long LastWin32k;
}

public sealed class FpsMeter : IDisposable
{
    private const string SessionName = "WinIslandHwMonitor_FPS";

    private static readonly Guid DxgKrnlProviderId = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private static readonly Guid Win32kProviderId = new("8C416C79-D49B-4F01-A467-E56D3AA8234C");

    private const int Present = 0x00B8;
    private const int PresentHistoryStart = 0x00AB;
    private const int PresentHistoryDetailedStart = 0x00D7;
    private const int Blt = 0x00A6;
    private const int MmioFlip = 0x0074;
    private const int MmioFlipMpo = 0x0103;
    private const int MmioFlipMpo3 = 0x0182;
    private const int Flip = 0x00A8;
    private const int FlipMpo = 0x00FC;
    private const int IndependentFlip = 0x010A;
    private const int Win32kPresent = 0x00C9;

    private readonly ConcurrentDictionary<int, ProcessFrames> _frames = new();
    private readonly Action<string> _log;
    private TraceEventSession? _session;
    private Task? _pump;

    private long _lastComposedFrames;
    private DateTime _lastCompositionSample;
    private double _compositionFps = -1;
    private double _refreshHz = -1;

    public FpsMeter(Action<string> log)
    {
        _log = log;
        Status = "桌面合成帧率（未启用窗口帧率）";
    }

    public string Status { get; private set; }

    public bool HasWindowFps => _session != null;

    public bool RequiresAdmin => _session == null;

    public void Start()
    {
        if (_session != null) return;

        if (!IsElevated())
        {
            Status = "桌面合成帧率（以管理员运行可显示前台窗口帧率）";
            return;
        }

        try
        {
            var session = new TraceEventSession(SessionName);
            try { session.EnableProvider(DxgKrnlProviderId); } catch { }
            try { session.EnableProvider(Win32kProviderId); } catch { }

            session.Source.Dynamic.All += OnTraceEvent;
            _session = session;
            _pump = Task.Factory.StartNew(() =>
            {
                try { session.Source.Process(); }
                catch (Exception ex) { _log($"帧率 ETW 会话结束：{ex.Message}"); }
            }, TaskCreationOptions.LongRunning);

            Status = "前台窗口帧率（ETW present 事件）";
        }
        catch (Exception ex)
        {
            Status = "桌面合成帧率（ETW 启动失败）";
            _log($"启动帧率 ETW 会话失败：{ex.Message}");
            try { _session?.Dispose(); } catch { }
            _session = null;
        }
    }

    public void Dispose()
    {
        try { _session?.Source.StopProcessing(); } catch { }
        try { _session?.Dispose(); } catch { }
        _session = null;
        _pump = null;
    }

    public double ReadFps(out string source)
    {
        if (_session != null)
        {
            var pid = ForegroundProcessId(out var processName);
            if (pid > 0 && _frames.TryGetValue(pid, out var frames))
            {
                var fps = CountRecent(frames);
                if (fps > 0)
                {
                    source = processName;
                    return fps;
                }
            }

            source = "前台窗口无帧数据";
            return -1;
        }

        SampleComposition();
        source = _refreshHz > 0 ? $"桌面合成 · {_refreshHz:0}Hz" : "桌面合成";
        return _compositionFps;
    }

    private static double CountRecent(ProcessFrames frames)
    {
        var now = DateTime.UtcNow.Ticks;
        var window = TimeSpan.FromSeconds(1).Ticks;
        var count = 0;
        lock (frames)
        {
            foreach (var stamp in frames.Stamps)
            {
                if (stamp > 0 && now - stamp <= window) count++;
            }
        }

        return count;
    }

    private void SampleComposition()
    {
        try
        {
            var info = new DwmTimingInfo { Size = Marshal.SizeOf<DwmTimingInfo>() };
            if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) != 0) return;

            if (_refreshHz <= 0 && info.RateRefresh.Denominator != 0)
            {
                _refreshHz = (double)info.RateRefresh.Numerator / info.RateRefresh.Denominator;
            }

            var now = DateTime.UtcNow;
            if (_lastCompositionSample != default && _lastComposedFrames > 0)
            {
                var elapsed = (now - _lastCompositionSample).TotalSeconds;
                var frames = (long)info.ComposedFrames - _lastComposedFrames;
                if (elapsed > 0.2 && frames >= 0)
                {
                    _compositionFps = Math.Round(frames / elapsed, 0);
                }
            }

            _lastCompositionSample = now;
            _lastComposedFrames = (long)info.ComposedFrames;
        }
        catch
        {
            _compositionFps = -1;
        }
    }

    private void OnTraceEvent(TraceEvent data)
    {
        var id = (int)data.ID;
        if (!IsPresentEventId(id)) return;
        if (data.ProcessID <= 4) return;

        var frames = _frames.GetOrAdd(data.ProcessID, _ => new ProcessFrames());
        Record(frames, id, DateTime.UtcNow.Ticks);
    }

    private static void Record(ProcessFrames frames, int id, long ticks)
    {
        lock (frames)
        {
            if (id is PresentHistoryStart or PresentHistoryDetailedStart) frames.LastHistory = ticks;
            else if (id == Win32kPresent) frames.LastWin32k = ticks;

            var dedupe = id == Win32kPresent ? ProcessFrames.Win32kSameFrameTicks : ProcessFrames.SameFrameTicks;
            if (ticks - frames.LastPresent < dedupe) return;

            var isHistory = id is PresentHistoryStart or PresentHistoryDetailedStart;
            if (!isHistory && ticks - frames.LastHistory < ProcessFrames.ModeWindowTicks) return;
            if (id is Present or Blt or MmioFlip or MmioFlipMpo or MmioFlipMpo3 or Flip or FlipMpo or IndependentFlip
                && ticks - frames.LastWin32k < ProcessFrames.ModeWindowTicks) return;

            frames.LastPresent = ticks;
            frames.Stamps[frames.Index] = ticks;
            frames.Index = (frames.Index + 1) % frames.Stamps.Length;
        }
    }

    private static bool IsPresentEventId(int id) =>
        id is Present or PresentHistoryStart or PresentHistoryDetailedStart
            or Blt or MmioFlip or MmioFlipMpo or MmioFlipMpo3
            or Flip or FlipMpo or IndependentFlip or Win32kPresent;

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static int ForegroundProcessId(out string processName)
    {
        processName = string.Empty;
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return 0;

            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName;
            return process.ProcessName.Equals("WinIsland", StringComparison.OrdinalIgnoreCase) ? 0 : (int)pid;
        }
        catch
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnsignedRatio
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmTimingInfo
    {
        public int Size;
        public UnsignedRatio RateRefresh;
        public ulong RefreshPeriod;
        public UnsignedRatio RateCompose;
        public ulong VBlank;
        public ulong RefreshCount;
        public uint DxRefreshCount;
        public ulong ComposeQpc;
        public ulong ComposedFrames;
        public uint DxPresentCount;
        public ulong RefreshFrame;
        public ulong FrameSubmitted;
        public uint DxPresentSubmitted;
        public ulong FrameConfirmed;
        public uint DxPresentConfirmed;
        public ulong RefreshConfirmed;
        public uint DxRefreshConfirmed;
        public ulong FramesLate;
        public uint FramesOutstanding;
        public ulong FrameDisplayed;
        public ulong RefreshFrameDisplayed;
        public ulong FrameComplete;
        public ulong FramePending;
        public ulong FramesDropped;
        public ulong FramesMissed;
        public ulong RefreshNextDisplayed;
        public ulong RefreshNextPresented;
        public ulong RefreshesDisplayed;
        public ulong RefreshesPresented;
        public ulong RefreshStarted;
        public ulong PixelsReceived;
        public ulong PixelsDrawn;
        public ulong BuffersEmpty;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DwmTimingInfo timingInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
