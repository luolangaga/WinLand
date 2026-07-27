using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WinIsland.Modules.Media;

internal static class AudioLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinIsland", "audio_monitor.log");

    public static void Write(string msg)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }
}

#region WASAPI COM 接口 — 全部 PreserveSig + IntPtr，避免 vtable 偏移错误

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr ppDevices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr ppEndpoint);
    [PreserveSig] int GetDevice(IntPtr pwstrId, out IntPtr ppDevice);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
    [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
    [PreserveSig] int GetId(out IntPtr ppstrId);
    [PreserveSig] int GetState(out int pdwState);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long hnsLatency);
    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr pDeviceFormat);
    [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid iid, out IntPtr ppService);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr pData, out uint numFrames, out int flags, out long devicePosition, out long qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint numFrames);
}

[StructLayout(LayoutKind.Sequential)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

internal static class WinAudioNative
{
    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    public const int CLSCTX_ALL = 0x17;
    public const int eRender = 0;
    public const int eConsole = 0;
    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(ref Guid clsid, IntPtr pUnkOuter, int clsctx, ref Guid iid, out IntPtr ppv);

    public const int COINIT_MULTITHREADED = 0;
}

#endregion

internal sealed class AudioLevelMonitor : IDisposable
{
    private Thread? _captureThread;
    private volatile bool _running;

    private float _bassLp, _midLp;
    private float _bassEnv, _midEnv, _trebleEnv;
    private float _bassPeak, _midPeak, _treblePeak;

    public float Bass { get; private set; }
    public float Mid { get; private set; }
    public float Treble { get; private set; }
    public string Status { get; private set; } = "未启动";

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        Status = "正在启动...";
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "AudioLevelMonitor" };
        _captureThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _captureThread?.Join(1000);
        _captureThread = null;
        Bass = Mid = Treble = 0;
        _bassEnv = _midEnv = _trebleEnv = 0;
        _bassLp = _midLp = 0;
        _bassPeak = _midPeak = _treblePeak = 0;
        Status = "已停止";
    }

    public void Dispose() => Stop();

    private void CaptureLoop()
    {
        WinAudioNative.CoInitializeEx(IntPtr.Zero, WinAudioNative.COINIT_MULTITHREADED);

        try
        {
            // 1. CoCreateInstance(MMDeviceEnumerator)
            var clsid = WinAudioNative.CLSID_MMDeviceEnumerator;
            var iidEnum = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
            int hr = WinAudioNative.CoCreateInstance(ref clsid, IntPtr.Zero,
                WinAudioNative.CLSCTX_ALL, ref iidEnum, out IntPtr pEnum);
            if (hr != 0)
            {
                Status = $"CoCreateInstance 失败 hr=0x{hr:X8}";
                AudioLog.Write(Status);
                _running = false;
                return;
            }
            var enumerator = Marshal.GetObjectForIUnknown(pEnum) as IMMDeviceEnumerator;
            Marshal.Release(pEnum);
            if (enumerator == null)
            {
                Status = "MMDeviceEnumerator QI 失败";
                AudioLog.Write(Status);
                _running = false;
                return;
            }
            AudioLog.Write("MMDeviceEnumerator 创建成功");

            // 2. GetDefaultAudioEndpoint
            hr = enumerator.GetDefaultAudioEndpoint(WinAudioNative.eRender, WinAudioNative.eConsole, out IntPtr pDevice);
            if (hr != 0)
            {
                Status = $"GetDefaultAudioEndpoint 失败 hr=0x{hr:X8}";
                AudioLog.Write(Status);
                _running = false;
                return;
            }
            var device = Marshal.GetObjectForIUnknown(pDevice) as IMMDevice;
            Marshal.Release(pDevice);
            if (device == null)
            {
                Status = "IMMDevice QI 失败";
                AudioLog.Write(Status);
                _running = false;
                return;
            }
            AudioLog.Write("默认渲染设备获取成功");

            // 3. Activate IAudioClient
            var iidAudioClient = WinAudioNative.IID_IAudioClient;
            hr = device.Activate(ref iidAudioClient, WinAudioNative.CLSCTX_ALL, IntPtr.Zero, out IntPtr pAudioClient);
            if (hr != 0)
            {
                Status = $"Activate(IAudioClient) 失败 hr=0x{hr:X8}";
                AudioLog.Write(Status);
                _running = false;
                return;
            }
            var audioClient = Marshal.GetObjectForIUnknown(pAudioClient) as IAudioClient;
            Marshal.Release(pAudioClient);
            if (audioClient == null)
            {
                Status = "IAudioClient QI 失败";
                AudioLog.Write(Status);
                _running = false;
                return;
            }
            AudioLog.Write("IAudioClient 激活成功");

            // 4. GetMixFormat
            hr = audioClient.GetMixFormat(out IntPtr pMixFormat);
            if (hr != 0)
            {
                Status = $"GetMixFormat 失败 hr=0x{hr:X8}";
                AudioLog.Write(Status);
                _running = false;
                return;
            }
            var wfx = Marshal.PtrToStructure<WAVEFORMATEX>(pMixFormat);
            int channels = wfx.nChannels;
            int sampleRate = (int)wfx.nSamplesPerSec;
            int bitsPerSample = wfx.wBitsPerSample;
            int bytesPerSample = bitsPerSample / 8;
            AudioLog.Write($"格式: {channels}ch {sampleRate}Hz {bitsPerSample}bit (tag={wfx.wFormatTag})");

            // 5. Initialize (loopback)
            hr = audioClient.Initialize(
                WinAudioNative.AUDCLNT_SHAREMODE_SHARED,
                WinAudioNative.AUDCLNT_STREAMFLAGS_LOOPBACK,
                0, 0, pMixFormat, IntPtr.Zero);
            if (hr != 0)
            {
                Status = $"Initialize 失败 hr=0x{hr:X8}";
                AudioLog.Write(Status);
                _running = false;
                return;
            }
            AudioLog.Write("Loopback 初始化成功");

            // 6. GetService IAudioCaptureClient
            var iidCapture = WinAudioNative.IID_IAudioCaptureClient;
            hr = audioClient.GetService(ref iidCapture, out IntPtr pCapture);
            if (hr != 0)
            {
                Status = $"GetService(IAudioCaptureClient) 失败 hr=0x{hr:X8}";
                AudioLog.Write(Status);
                _running = false;
                return;
            }
            var captureClient = Marshal.GetObjectForIUnknown(pCapture) as IAudioCaptureClient;
            Marshal.Release(pCapture);
            if (captureClient == null)
            {
                Status = "IAudioCaptureClient QI 失败";
                AudioLog.Write(Status);
                _running = false;
                return;
            }
            AudioLog.Write("IAudioCaptureClient 获取成功");

            // 7. Start
            hr = audioClient.Start();
            if (hr != 0)
            {
                Status = $"Start 失败 hr=0x{hr:X8}";
                AudioLog.Write(Status);
                _running = false;
                return;
            }
            Status = "捕获中";
            AudioLog.Write("捕获开始！");

            float bassAlpha = 1f - (float)Math.Exp(-2 * Math.PI * 200 / sampleRate);
            float midAlpha = 1f - (float)Math.Exp(-2 * Math.PI * 2000 / sampleRate);

            while (_running)
            {
                uint packetFrames;
                captureClient.GetNextPacketSize(out packetFrames);

                while (packetFrames > 0 && _running)
                {
                    captureClient.GetBuffer(out IntPtr pData, out uint numFrames, out int flags,
                        out long devPos, out long qpcPos);

                    if ((flags & WinAudioNative.AUDCLNT_BUFFERFLAGS_SILENT) == 0 && numFrames > 0)
                    {
                        unsafe
                        {
                            ProcessBuffer(pData, (int)numFrames, channels, bytesPerSample, bassAlpha, midAlpha);
                        }
                    }

                    captureClient.ReleaseBuffer(numFrames);
                    captureClient.GetNextPacketSize(out packetFrames);
                }

                Thread.Sleep(8);
            }

            audioClient.Stop();
            Status = "已停止";
        }
        catch (Exception ex)
        {
            Status = $"异常: {ex.Message}";
            AudioLog.Write($"异常: {ex}");
            _running = false;
        }
        finally
        {
            WinAudioNative.CoUninitialize();
        }
    }

    private unsafe void ProcessBuffer(IntPtr pData, int numFrames, int channels,
        int bytesPerSample, float bassAlpha, float midAlpha)
    {
        double bassSum = 0, midSum = 0, trebleSum = 0;
        int n = 0;

        if (bytesPerSample == 4)
        {
            float* p = (float*)pData;
            for (int i = 0; i < numFrames; i++)
            {
                float s = 0;
                for (int c = 0; c < channels; c++)
                    s += p[i * channels + c];
                s /= channels;

                _bassLp += bassAlpha * (s - _bassLp);
                _midLp += midAlpha * (s - _midLp);

                float bass = _bassLp;
                float mid = _midLp - _bassLp;
                float treble = s - _midLp;

                bassSum += bass * bass;
                midSum += mid * mid;
                trebleSum += treble * treble;
                n++;
            }
        }
        else if (bytesPerSample == 2)
        {
            short* p = (short*)pData;
            for (int i = 0; i < numFrames; i++)
            {
                float s = 0;
                for (int c = 0; c < channels; c++)
                    s += p[i * channels + c];
                s = s / channels / 32768f;

                _bassLp += bassAlpha * (s - _bassLp);
                _midLp += midAlpha * (s - _midLp);

                float bass = _bassLp;
                float mid = _midLp - _bassLp;
                float treble = s - _midLp;

                bassSum += bass * bass;
                midSum += mid * mid;
                trebleSum += treble * treble;
                n++;
            }
        }

        if (n <= 0) return;

        float bassRms = (float)Math.Sqrt(bassSum / n) * 8f;
        float midRms = (float)Math.Sqrt(midSum / n) * 10f;
        float trebleRms = (float)Math.Sqrt(trebleSum / n) * 16f;

        Bass = UpdateEnv(ref _bassEnv, ref _bassPeak, bassRms);
        Mid = UpdateEnv(ref _midEnv, ref _midPeak, midRms);
        Treble = UpdateEnv(ref _trebleEnv, ref _treblePeak, trebleRms);
    }

    private static float UpdateEnv(ref float env, ref float peak, float target)
    {
        if (target > peak)
            peak = target;
        else
            peak *= 0.96f;

        float normalized = peak > 0.01f ? Math.Clamp(target / (peak + 0.001f), 0f, 1f) : target;

        float coeff = normalized > env ? 0.6f : 0.18f;
        env += (normalized - env) * coeff;
        return Math.Clamp(env, 0f, 1f);
    }
}
