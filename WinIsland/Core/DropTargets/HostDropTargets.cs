using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinIsland.Core;

namespace WinIsland.Core.DropTargets;

/// <summary>
/// 宿主内置的三个文件投放动作：打开 / 在资源管理器中显示 / 复制路径。
/// 它们是 shell 行为、不属于任何插件，所以在组装根（App）里直接注册进 IslandService；
/// Order 取 900+，让插件注册的投放目标排在它们前面。
/// </summary>
public static class HostDropTargets
{
    /// <summary>宿主目标的 ownerId（插件用插件 Id，永远不会撞上）。</summary>
    private const string Owner = "(host)";

    /// <param name="ownerHwnd">岛窗口句柄：系统对话框（「另存为」）要它当 owner，未打包应用必须显式传。</param>
    public static void Register(IslandService service, IPluginLogger log, nint ownerHwnd)
    {
        service.AddDropTarget(Owner, Open(log));
        service.AddDropTarget(Owner, Reveal(log));
        service.AddDropTarget(Owner, CopyPaths());
        service.AddDropTarget(Owner, CopyText());
        service.AddDropTarget(Owner, SaveImage(log, ownerHwnd));
    }

    private static IslandDropTarget Open(IPluginLogger log) => new()
    {
        Id = "host.open",
        Title = "打开",
        Glyph = "\uE8E5",
        Order = 900,
        Kinds = IslandDropKind.Files,
        Handler = ctx =>
        {
            int ok = 0, failed = 0;
            foreach (var path in ctx.Paths)
            {
                try
                {
                    // UseShellExecute：走系统关联，目录直接用资源管理器打开
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    log.Warn($"打开「{path}」失败：{ex.Message}");
                }
            }

            return Task.FromResult<string?>(Describe(ok, failed, $"已打开 {ok} 项", "打开"));
        },
    };

    private static IslandDropTarget Reveal(IPluginLogger log) => new()
    {
        Id = "host.reveal",
        Title = "所在位置",
        Glyph = "\uE838",
        Order = 910,
        Kinds = IslandDropKind.Files,
        Handler = ctx =>
        {
            var first = ctx.Paths[0];
            try
            {
                // 目录直接打开它；文件则打开父目录并选中 —— /select 一次只能选一个，
                // 多选时只定位第一项，文案里说清楚
                var args = Directory.Exists(first) ? $"\"{first}\"" : $"/select,\"{first}\"";
                Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
                return Task.FromResult<string?>(ctx.IsSingle ? "已在资源管理器中显示" : $"已定位第一项「{ctx.Names[0]}」");
            }
            catch (Exception ex)
            {
                log.Warn($"打开资源管理器失败：{ex.Message}");
                return Task.FromResult<string?>($"打开资源管理器失败：{ex.Message}");
            }
        },
    };

    private static IslandDropTarget CopyPaths() => new()
    {
        Id = "host.copyPath",
        Title = "复制路径",
        Glyph = "\uE8C8",
        Order = 920,
        Kinds = IslandDropKind.Files,
        Handler = async ctx =>
        {
            try
            {
                await WriteClipboardAsync(string.Join("\r\n", ctx.Paths));
                return ctx.IsSingle ? "已复制路径" : $"已复制 {ctx.Paths.Count} 个路径";
            }
            catch (Exception ex)
            {
                return $"复制路径失败：{ex.Message}";
            }
        },
    };

    private static IslandDropTarget CopyText() => new()
    {
        Id = "host.copyText",
        Title = "复制文本",
        Glyph = "\uE8C8",
        Order = 930,
        Kinds = IslandDropKind.Text,
        Handler = async ctx =>
        {
            var text = ctx.Text ?? string.Empty;
            try
            {
                await WriteClipboardAsync(text);
                return $"已复制文本（{text.Length} 字符）";
            }
            catch (Exception ex)
            {
                return $"复制文本失败：{ex.Message}";
            }
        },
    };

    private static IslandDropTarget SaveImage(IPluginLogger log, nint ownerHwnd) => new()
    {
        Id = "host.saveImage",
        Title = "保存图片",
        Glyph = "\uE91B",
        Order = 940,
        Kinds = IslandDropKind.Image,
        Handler = async ctx =>
        {
            if (ctx.ImageBytes is not { Length: > 0 } bytes)
            {
                return "这张图片读不出来";
            }

            try
            {
                // 存哪儿由用户决定：弹系统「另存为」
                // （未打包应用必须把窗口句柄交给 picker，否则会直接抛异常）
                var extension = SniffImageExtension(bytes);
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                    SuggestedFileName = $"图片-{DateTime.Now:yyyyMMdd-HHmmss}",
                };
                picker.FileTypeChoices.Add(extension == ".bin" ? "文件" : "图片", new List<string> { extension });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);

                var file = await picker.PickSaveFileAsync();
                if (file is null)
                {
                    log.Info("投放的图片已取消保存");
                    return "已取消保存";
                }

                await FileIO.WriteBytesAsync(file, bytes);
                log.Info($"投放的图片已保存：{file.Path}（{bytes.Length / 1024} KB）");
                return $"已保存到「{file.Name}」";
            }
            catch (Exception ex)
            {
                log.Warn($"保存投放的图片失败：{ex.Message}");
                return $"保存图片失败：{ex.Message}";
            }
        },
    };

    /// <summary>按魔数猜图片格式（浏览器拖过来的图片多半是 PNG / JPEG / SVG，识别不出就存 .bin）。</summary>
    private static string SniffImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return ".gif";
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D) return ".bmp";
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return ".webp";
        if (LooksLikeSvg(bytes)) return ".svg";
        return ".bin";
    }

    /// <summary>
    /// SVG 是文本格式，没有魔数：跳过 UTF-8 BOM 与空白后看开头是不是 <c>&lt;svg</c> / <c>&lt;?xml</c>（带 svg）。
    /// 网页里拖的图标大多是 SVG，按 .svg 存下来才能保留矢量。
    /// </summary>
    private static bool LooksLikeSvg(byte[] bytes)
    {
        var limit = Math.Min(bytes.Length, 1024);
        var text = System.Text.Encoding.UTF8.GetString(bytes, 0, limit).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (!text.StartsWith("<", StringComparison.Ordinal)) return false;

        return text.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
               || (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                   && text.Contains("<svg", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>剪贴板会被别的程序短暂独占（CLIPBRD_E_CANT_OPEN），重试几次再放弃。</summary>
    private static async Task WriteClipboardAsync(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetContent(package);
                try
                {
                    // 让内容留在剪贴板上，宿主退出后依然可粘贴（失败无所谓）
                    Clipboard.Flush();
                }
                catch (Exception)
                {
                }

                return;
            }
            catch (Exception) when (attempt < 3)
            {
                await Task.Delay(60);
            }
        }
    }

    private static string Describe(int ok, int failed, string success, string verb)
        => failed == 0 ? success : ok == 0 ? $"{verb}失败（{failed} 项）" : $"{success}，{failed} 项失败";
}
