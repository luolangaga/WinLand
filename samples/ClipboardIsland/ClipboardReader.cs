using System.Security.Cryptography;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinIsland.Core;

namespace ClipboardIsland;

/// <summary>
/// 读剪贴板。三个必须注意的点：
///
///   1. <c>Clipboard.GetContent()</c> 必须在 UI 线程调用（我们的捕获流程整个跑在 UI 线程上）；
///   2. 剪贴板经常被源程序短暂占用（<c>0x800401D0 CLIPBRD_E_CANT_OPEN</c>），
///      所以「取 DataPackageView + 真正读出数据」要整体重试几次，而不是只重试第一步；
///   3. 图片用纯 WinRT 的 DataReader 分块读，不依赖 System.Runtime.WindowsRuntime 的流扩展方法
///      （插件侧不一定拿得到那个程序集）。
/// </summary>
internal static class ClipboardReader
{
    private const int MaxAttempts = 5;
    private const uint ChunkSize = 64 * 1024;

    /// <summary>剪贴板图片的体积上限，超过就不记了（避免有人复制超大图把内存撑爆）。</summary>
    private const long MaxImageBytes = 32L * 1024 * 1024;

    /// <summary>
    /// 拿当前剪贴板内容再解析。只在「没能抢到事件那一刻的快照」时用（见
    /// <see cref="ExtractAsync"/> 的说明）：这时已经慢了半拍，所以重试几轮。
    /// </summary>
    public static async Task<ClipboardCapture?> TryReadAsync(IPluginLogger log, bool includeImages, string reason)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var view = Clipboard.GetContent();
                return await ExtractAsync(log, view, includeImages, reason).ConfigureAwait(true);
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                // 剪贴板被源程序占着：等一下再试。这属于常态，安静记 Debug 就行
                log.Debug($"读剪贴板失败（{reason}，第 {attempt} 次，稍后重试）：{ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(40 * attempt)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                log.Warn($"读剪贴板失败（{reason}，已放弃）：{ex.Message}");
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 解析一份 <see cref="DataPackageView"/>。
    ///
    /// 为什么要单独暴露出来：<c>DataPackageView</c> 指向的是**取它那一刻**的剪贴板内容，
    /// 所以必须趁剪贴板刚变完、立刻取一份拿在手里，再去排队解析 —— 排队期间剪贴板可能又变了，
    /// 那时再 <c>GetContent()</c> 就会读到更新的内容，上一条就永久丢了。
    /// </summary>
    public static async Task<ClipboardCapture?> ExtractAsync(
        IPluginLogger log, DataPackageView view, bool includeImages, string reason)
    {
        // 复制的文件/文件夹（资源管理器里 Ctrl+C）：记成一条**文件记录**（只存路径引用），
        // 重新复制时写回剪贴板的也是文件本身（CF_HDROP）—— 在资源管理器里粘回去就是复制文件，
        // 不是贴一串路径文本。
        // 放在文本之前判断：复制文件时剪贴板里通常只有 CF_HDROP，两者极少同时出现
        if (view.Contains(StandardDataFormats.StorageItems))
        {
            var paths = new List<string>();
            foreach (var item in await view.GetStorageItemsAsync())
            {
                if (!string.IsNullOrEmpty(item.Path)) paths.Add(item.Path);
            }

            if (paths.Count > 0)
            {
                return new ClipboardCapture(ClipKind.Files, null, null, SignatureForFiles(paths))
                {
                    Paths = paths,
                };
            }

            log.Info($"剪贴板里是文件，但没有可用的本地路径（{reason}，可用格式：{DescribeFormats(view)}）");
            return null;
        }

        // 文本优先：很多程序（Word / Excel / 浏览器）复制时会同时塞 Text 和 Bitmap，
        // 这种时候用户想要的显然是文本。
        if (view.Contains(StandardDataFormats.Text))
        {
            var text = await view.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) return null;

            return new ClipboardCapture(ClipKind.Text, text, null, SignatureForText(text));
        }

        if (includeImages && view.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await view.GetBitmapAsync();
            var stream = await reference.OpenReadAsync();

            byte[] bytes;
            try
            {
                bytes = await ReadAllBytesAsync(stream);
            }
            finally
            {
                SafeClose(stream);
            }

            if (bytes.Length == 0) return null;

            return new ClipboardCapture(ClipKind.Image, null, bytes, "image:" + HashBytes(bytes));
        }

        // 其余格式（HTML 片段、自定义格式…）不记录。
        // 这里必须留下日志：否则用户会觉得"我明明复制了却没反应"，而日志里一片空白无法解释
        log.Info($"剪贴板里是暂不支持记录的格式（{reason}，可用格式：{DescribeFormats(view)}）");
        return null;
    }

    private static string DescribeFormats(DataPackageView view)
    {
        var names = view.AvailableFormats;
        return names.Count == 0 ? "（无）" : string.Join("、", names);
    }

    private static async Task<byte[]> ReadAllBytesAsync(IRandomAccessStream stream)
    {
        var size = (long)stream.Size;
        if (size <= 0) return [];

        if (size > MaxImageBytes)
        {
            throw new InvalidOperationException($"剪贴板图片过大（{ClipText.FormatBytes(size)}），已跳过");
        }

        var buffer = new byte[size];
        var offset = 0;

        var reader = new DataReader(stream.GetInputStreamAt(0));
        try
        {
            while (offset < size)
            {
                var want = (uint)Math.Min(ChunkSize, size - offset);
                var loaded = await reader.LoadAsync(want);
                if (loaded == 0) break;

                var chunk = new byte[loaded];
                reader.ReadBytes(chunk);
                System.Buffer.BlockCopy(chunk, 0, buffer, offset, (int)loaded);
                offset += (int)loaded;
            }
        }
        finally
        {
            SafeClose(reader);
        }

        return offset == buffer.Length ? buffer : buffer[..offset];
    }

    /// <summary>WinRT 对象一律通过 IClosable→IDisposable 关掉；拿不到就放过，不影响主流程。</summary>
    private static void SafeClose(object? target)
    {
        if (target is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch { /* 关闭失败无所谓 */ }
        }
    }

    /// <summary>文本记录的指纹。写回剪贴板时插件也要算同一个值，用来认自己弄出来的回声。</summary>
    internal static string SignatureForText(string text) => "text:" + HashText(text);

    /// <summary>文件记录的指纹（按路径列表算，同一批文件再复制一次不会产生新记录）。</summary>
    internal static string SignatureForFiles(IReadOnlyList<string> paths)
        => "files:" + HashText(string.Join('\u001f', paths));

    private static string HashText(string text) => Short(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private static string HashBytes(byte[] data) => Short(SHA256.HashData(data));

    private static string Short(byte[] hash) => Convert.ToHexString(hash)[..24];
}
