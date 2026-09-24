using System.Runtime.InteropServices;
using System.Text;
using WinIsland.Core;

namespace ClipboardIsland;

/// <summary>
/// 把一批文件/文件夹写进剪贴板（CF_HDROP + 「复制」效果）。
///
/// 刻意走 Win32 而不是 WinRT 的 <c>SetStorageItems</c>：
/// <c>StorageFile.GetFileFromPathAsync</c> 对**带隐藏/系统属性**的文件会直接失败
/// （<c>UNABLE_TO_MASK_PATH</c>，桌面上的 .lnk 快捷方式经常带这些属性），
/// 失败后粘出来就只剩路径文本了。CF_HDROP 是资源管理器自己用的那条路，任何路径都能写。
/// </summary>
internal static class ClipboardWriter
{
    private const uint CF_HDROP = 15;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;
    private const int DropeffectCopy = 1;
    private const int MaxAttempts = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct DropFiles
    {
        public uint FileOffset;
        public int PointX;
        public int PointY;
        public int NotClientArea;
        public int Wide;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint hMem);

    /// <summary>把路径列表写成文件投放载荷。剪贴板被别的程序占着时会重试几次。</summary>
    public static async Task<bool> TrySetFilesAsync(IReadOnlyList<string> paths, IPluginLogger log)
    {
        if (paths.Count == 0) return false;

        var payload = BuildDropFiles(paths);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (!OpenClipboard(nint.Zero))
            {
                // 剪贴板被占是常态（另一个程序刚写完还没关），等一拍再试
                await Task.Delay(40 * attempt).ConfigureAwait(true);
                continue;
            }

            try
            {
                EmptyClipboard();

                if (!SetDropFiles(payload))
                {
                    log.Warn($"写文件到剪贴板失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
                    return false;
                }

                SetCopyEffect();
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        log.Warn($"打开剪贴板失败（连续 {MaxAttempts} 次被占用），这次没能写进去");
        return false;
    }

    /// <summary>DROPFILES 头 + UTF-16 的「路径\0路径\0\0」列表。</summary>
    private static byte[] BuildDropFiles(IReadOnlyList<string> paths)
    {
        var headerSize = Marshal.SizeOf<DropFiles>();
        var charCount = paths.Sum(path => path.Length + 1) + 1;   // 每个路径一个 '\0'，末尾再来一个
        var payload = new byte[headerSize + charCount * 2];

        BitConverter.GetBytes((uint)headerSize).CopyTo(payload, 0);
        BitConverter.GetBytes(1).CopyTo(payload, 16);             // fWide = 1：列表是 UTF-16

        var offset = headerSize;
        foreach (var path in paths)
        {
            var encoded = Encoding.Unicode.GetBytes(path);
            encoded.CopyTo(payload, offset);
            offset += encoded.Length;
            payload[offset++] = 0;                                // 每个路径自己的结束符
            payload[offset++] = 0;
        }

        // 整个列表的结束符由数组的零初始化提供
        return payload;
    }

    private static bool SetDropFiles(byte[] payload)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (nuint)payload.Length);
        if (handle == nint.Zero) return false;

        var target = GlobalLock(handle);
        if (target == nint.Zero)
        {
            GlobalFree(handle);
            return false;
        }

        try
        {
            Marshal.Copy(payload, 0, target, payload.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        if (SetClipboardData(CF_HDROP, handle) == nint.Zero)
        {
            // 只有失败时内存还归我们；成功后归系统，绝不能再释放
            GlobalFree(handle);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 标成「复制」：不写这个，部分程序粘出来会当成移动（把源文件搬走）。
    /// 资源管理器自己复制文件时写的也是这个格式。
    /// </summary>
    private static void SetCopyEffect()
    {
        var format = RegisterClipboardFormat("Preferred DropEffect");
        if (format == 0) return;

        var handle = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, 4);
        if (handle == nint.Zero) return;

        var target = GlobalLock(handle);
        if (target == nint.Zero)
        {
            GlobalFree(handle);
            return;
        }

        Marshal.WriteInt32(target, DropeffectCopy);
        GlobalUnlock(handle);

        if (SetClipboardData(format, handle) == nint.Zero)
        {
            GlobalFree(handle);
        }
    }
}
