using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LocalSendIsland;

internal static class LocalSendBridge
{
    internal static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland", "localsend-share", "outbox");

    internal static string? FindExecutable(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("指定的 LocalSend 程序不存在，请检查设置中的路径");
            return Path.GetFullPath(path);
        }
        foreach (var name in new[] { "localsend_app", "localsend" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try { if (process.MainModule?.FileName is string path && File.Exists(path)) return path; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LocalSend"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LocalSend"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LocalSend")
        };
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) continue;
                foreach (var key in uninstall.GetSubKeyNames())
                {
                    using var app = uninstall.OpenSubKey(key);
                    if (!(app?.GetValue("DisplayName") as string ?? "").Contains("LocalSend", StringComparison.OrdinalIgnoreCase)) continue;
                    if (app?.GetValue("InstallLocation") is string dir && dir.Length > 0) roots.Add(dir);
                    if (app?.GetValue("DisplayIcon") is string icon)
                    {
                        var path = icon.Split(',')[0].Trim('"');
                        if (File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return path;
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        roots.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)))
        foreach (var name in new[] { "localsend_app.exe", "localsend.exe" })
        {
            var path = Path.Combine(root.Trim('"'), name);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        return null;
    }

    internal static async Task<string[]> PrepareAsync(string kind, IReadOnlyList<string> paths, string? text, byte[]? image, bool nativeText, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (kind == "Files")
        {
            if (paths.Count == 0) throw new IOException("没有可分享的文件");
            var fullPaths = paths.Select(Path.GetFullPath).ToArray();
            foreach (var path in fullPaths)
                if (!File.Exists(path) && !Directory.Exists(path)) throw new IOException("文件或文件夹已不存在：" + Path.GetFileName(path));
            CheckLength(fullPaths);
            return fullPaths;
        }
        if (kind == "Text" && text != null && nativeText && !string.IsNullOrWhiteSpace(text) && !text.Contains('\0') && text.Length < 12000)
            return ["--text", text];
        Directory.CreateDirectory(CacheRoot);
        foreach (var stale in Directory.EnumerateFiles(CacheRoot))
        {
            try { if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddDays(-7)) File.Delete(stale); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var stem = Path.Combine(CacheRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        if (kind == "Text" && text != null)
        {
            var file = stem + ".txt";
            await File.WriteAllTextAsync(file, text, new UTF8Encoding(false), token);
            return [file];
        }
        if (kind == "Image" && image is { Length: > 0 })
        {
            var file = stem + ImageExtension(image);
            await File.WriteAllBytesAsync(file, image, token);
            return [file];
        }
        throw new IOException("投放内容为空或类型不受支持");
    }

    internal static string ImageExtension(byte[] bytes)
    {
        var data = bytes.AsSpan();
        if (data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (data.StartsWith(new byte[] { 255, 216, 255 })) return ".jpg";
        if (data.StartsWith("GIF8"u8)) return ".gif";
        if (data.StartsWith("BM"u8)) return ".bmp";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return ".webp";
        if (data.StartsWith(new byte[] { 73, 73, 42, 0 }) || data.StartsWith(new byte[] { 77, 77, 0, 42 })) return ".tif";
        return ".img"; // 未识别格式仍保留所有原始字节。
    }

    internal static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
    {
        var list = args.ToArray();
        CheckLength(list);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var arg in list) info.ArgumentList.Add(arg);
        return info;
    }

    private static void CheckLength(IEnumerable<string> args)
    {
        if (args.Sum(x => (long)x.Length * 2 + 3) > 28000)
            throw new IOException("内容超过 Windows 命令行长度限制，请分批拖入或改为分享所在文件夹");
    }

    private static readonly SemaphoreSlim LaunchGate = new(1, 1);

    internal static async Task LaunchAsync(string exe, IEnumerable<string> args, CancellationToken token = default)
    {
        var list = args.ToArray();
        await LaunchGate.WaitAsync(token);
        try
        {
            // 不再依赖 LocalSend 二次启动时的自动交接：部分版本的 TLS 交接失败会创建第二个窗口。
            if (IsRunning(exe))
            {
                await HandoverAsync(exe, list, token);
                return;
            }
            // 冷启动只启动一个空实例，等待服务就绪，再走同一条交接路径。
            using var process = Process.Start(StartInfo(exe, []));
            if (process == null) throw new IOException("LocalSend 启动失败");
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(500, token);
                try { await HandoverAsync(exe, list, token); return; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException && !token.IsCancellationRequested) { }
            }
            throw new IOException("LocalSend 已启动，但交接接口未就绪。请检查 LocalSend 状态后再试，不会再打开新实例。");
        }
        finally { LaunchGate.Release(); }
    }

    private static bool IsRunning(string exe)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (process)
            {
                if (process.SessionId == Process.GetCurrentProcess().SessionId) return true;
            }
        }
        return false;
    }

    internal static async Task HandoverAsync(string exe, string[] args, CancellationToken token)
    {
        var portable = Path.Combine(Path.GetDirectoryName(exe)!, "settings.json");
        var settings = File.Exists(portable) ? portable : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalSend", "settings.json");
        if (!File.Exists(settings)) throw new IOException("找不到 LocalSend 的交接配置，已有实例不会被重复启动");
        using var prefs = JsonDocument.Parse(await File.ReadAllTextAsync(settings, token));
        JsonElement Get(string key)
        {
            if (prefs.RootElement.TryGetProperty("flutter." + key, out var value) || prefs.RootElement.TryGetProperty(key, out value)) return value;
            return default;
        }
        var portValue = Get("ls_port");
        var port = portValue.ValueKind == JsonValueKind.Number ? portValue.GetInt32() : 53317;
        var httpsValue = Get("ls_https");
        var https = httpsValue.ValueKind != JsonValueKind.False;
        var showToken = Get("ls_show_token").GetString();
        if (string.IsNullOrEmpty(showToken)) throw new IOException("LocalSend 缺少交接令牌，请先正常打开 LocalSend");
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        X509Certificate2? certificate = null;
        try
        {
            if (https)
            {
                var securityValue = Get("ls_security_context");
                using var security = JsonDocument.Parse(securityValue.ValueKind == JsonValueKind.String ? securityValue.GetString()! : securityValue.GetRawText());
                var certPem = security.RootElement.GetProperty("certificate").GetString()!;
                var keyPem = security.RootElement.GetProperty("privateKey").GetString()!;
                using var pem = X509Certificate2.CreateFromPem(certPem, keyPem);
                // Schannel 在 Windows 上需要可用的私钥句柄。
                certificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
                handler.ClientCertificates.Add(certificate);
                handler.ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer != null && peer.RawData.AsSpan().SequenceEqual(certificate.RawData);
            }
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            var uri = new Uri($"{(https ? "https" : "http")}://127.0.0.1:{port}/api/localsend/v2/show?token={Uri.EscapeDataString(showToken)}");
            using var response = await client.PostAsJsonAsync(uri, new { args }, token);
            if (!response.IsSuccessStatusCode) throw new IOException($"LocalSend 拒绝交接（HTTP {(int)response.StatusCode}），不会启动第二个实例");
        }
        finally { certificate?.Dispose(); }
    }
}
