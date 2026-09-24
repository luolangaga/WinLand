using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace ClipboardIsland;

/// <summary>
/// 把缓存下来的 PNG 读成界面用的 BitmapImage。
///
/// 刻意绕开 RandomAccessStreamReference / AsStreamForRead 这类 WinRT 流扩展：
/// 用最朴素的「读字节 → 灌进 InMemoryRandomAccessStream → SetSourceAsync」，
/// 在插件这种动态加载的程序集里最不容易踩到"找不到方法"的坑。
///
/// 必须在 UI 线程调用（BitmapImage 有线程亲和性）。
/// </summary>
internal static class ThumbnailLoader
{
    public static async Task<BitmapImage?> LoadAsync(string? path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        BitmapImage? result = null;
        var stream = new InMemoryRandomAccessStream();

        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            if (bytes.Length == 0) return null;

            var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();

            stream.Seek(0);

            var image = new BitmapImage
            {
                // 缩略图本来就小，解码时就降采样，省内存
                DecodePixelWidth = decodeWidth,
                DecodePixelType = DecodePixelType.Logical,
            };

            await image.SetSourceAsync(stream);
            result = image;
        }
        catch
        {
            result = null;
        }

        // 注意：这里**不**主动关掉 stream —— BitmapImage 在 DPI 变化等场景下还会回头再读一次源，
        // 提前释放会让缩略图变空白。交给 GC 收尾即可。
        return result;
    }
}
