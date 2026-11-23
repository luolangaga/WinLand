using System.Text;
using System.Text.Json;

namespace WinIsland.Core;

/// <summary>
/// 命令行 / URI 自动化入口。重复启动的进程不建岛，而是把请求打包成 <see cref="CommandRequest"/>
/// 交给已在运行的实例（复用单实例闸门已有的命名事件），由它来执行 —— 这样脚本、CI、
/// 其他程序都能推一条消息到岛上，不必自己啃 UI 自动化。
///
/// 支持的用法：
/// <code>
/// WinIsland.exe --notify "构建完成" "耗时 1m20s"
/// WinIsland.exe --notify "标题" --text "正文" --glyph E7E8 --accent "#3B82F6" --duration 6
/// WinIsland.exe --show --settings
/// WinIsland.exe --quiet 30           # 静默 30 分钟
/// WinIsland.exe --unquiet
/// winisland://notify?title=构建完成&amp;text=耗时1m20s
/// winisland://settings
/// winisland://quiet?minutes=30
/// </code>
///
/// 参数与 URI 都统一解析成 <see cref="CommandRequest"/>，再由主进程执行 —— 只有一份语义。
/// </summary>
internal static class CommandLine
{
    /// <summary>URI 协议名（需要在注册表里登记才能被浏览器/Shell 唤起，见 <see cref="ProtocolRegistration"/>）。</summary>
    public const string Scheme = "winisland";

    /// <summary>
    /// 解析一次启动的全部参数。返回 null 表示「这是一次普通启动」（没有任何自动化参数）。
    /// 解析失败的参数不抛异常，只记进 <see cref="CommandRequest.Warnings"/> —— 自动化入口不该把应用挡在门外。
    /// </summary>
    public static CommandRequest? Parse(string[] args)
    {
        if (args.Length == 0) return null;

        // 第一个参数可能是 winisland://... 也可能是普通开关
        if (args[0].StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            return ParseUri(args[0]);
        }

        var request = new CommandRequest();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--notify" or "-n":
                    request.Kind = CommandKind.Notify;
                    if (i + 1 < args.Length && !IsSwitch(args[i + 1])) request.Title = args[++i];
                    if (i + 1 < args.Length && !IsSwitch(args[i + 1]))
                    {
                        request.Text = args[++i];
                        // 三参数形式：--notify 标题 正文 之后再跟 --duration 之类，位置已经对了
                    }
                    break;

                case "--text" or "-t":
                    if (i + 1 < args.Length && !IsSwitch(args[i + 1])) request.Text = args[++i];
                    break;

                case "--title":
                    if (i + 1 < args.Length && !IsSwitch(args[i + 1])) request.Title = args[++i];
                    break;

                case "--glyph" or "-g":
                    if (i + 1 < args.Length) request.Glyph = Unescape(args[++i]);
                    break;

                case "--accent" or "-a":
                    if (i + 1 < args.Length) request.Accent = args[++i];
                    break;

                case "--duration" or "-d":
                    if (i + 1 < args.Length && double.TryParse(args[i + 1], out double seconds))
                    {
                        request.DurationSeconds = Math.Clamp(seconds, 1, 60);
                        i++;
                    }
                    else
                    {
                        request.Warnings.Add("--duration 需要一个 1..60 之间的秒数。");
                    }
                    break;

                case "--settings":
                    request.Kind = CommandKind.Settings;
                    if (i + 1 < args.Length && !IsSwitch(args[i + 1])) request.Page = args[++i];
                    break;

                case "--show":
                    if (request.Kind == CommandKind.None) request.Kind = CommandKind.Show;
                    break;

                case "--quiet":
                    request.Kind = CommandKind.Quiet;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int minutes))
                    {
                        request.QuietMinutes = Math.Clamp(minutes, 1, 24 * 60);
                        i++;
                    }
                    else
                    {
                        request.QuietMinutes = 30;
                    }
                    break;

                case "--unquiet":
                    request.Kind = CommandKind.Unquiet;
                    break;

                case "--help" or "-h" or "/?":
                    request.Kind = CommandKind.Help;
                    break;
            }
        }

        if (request.Kind == CommandKind.None)
        {
            // 只有 --text 之类、没给 --notify：按通知处理（最常用的那条路）
            if (!string.IsNullOrWhiteSpace(request.Title) || !string.IsNullOrWhiteSpace(request.Text))
            {
                request.Kind = CommandKind.Notify;
            }
            else
            {
                return null;
            }
        }

        if (request.Kind == CommandKind.Notify && string.IsNullOrWhiteSpace(request.Title)
            && string.IsNullOrWhiteSpace(request.Text))
        {
            request.Warnings.Add("--notify 至少需要标题或正文。");
            return null;
        }

        return request;
    }

    private static CommandRequest? ParseUri(string uri)
    {
        var request = new CommandRequest();
        try
        {
            var parsed = new Uri(uri, UriKind.Absolute);
            string host = parsed.Host.ToLowerInvariant();
            var query = ParseQuery(parsed.Query);

            switch (host)
            {
                case "notify":
                    request.Kind = CommandKind.Notify;
                    request.Title = Get(query, "title") ?? Get(query, "t");
                    request.Text = Get(query, "text") ?? Get(query, "body") ?? Get(query, "b");
                    request.Glyph = Get(query, "glyph") ?? Get(query, "g");
                    request.Accent = Get(query, "accent") ?? Get(query, "a");
                    if (double.TryParse(Get(query, "duration") ?? Get(query, "d"), out double seconds))
                        request.DurationSeconds = Math.Clamp(seconds, 1, 60);
                    break;

                case "settings":
                    request.Kind = CommandKind.Settings;
                    request.Page = Get(query, "page");
                    break;

                case "show":
                    request.Kind = CommandKind.Show;
                    break;

                case "quiet":
                    request.Kind = CommandKind.Quiet;
                    request.QuietMinutes = int.TryParse(Get(query, "minutes"), out int minutes)
                        ? Math.Clamp(minutes, 1, 24 * 60) : 30;
                    break;

                case "unquiet":
                    request.Kind = CommandKind.Unquiet;
                    break;

                default:
                    request.Warnings.Add($"未知的 URI 主机：{host}");
                    return null;
            }
        }
        catch (UriFormatException ex)
        {
            return new CommandRequest { Kind = CommandKind.Help, Warnings = { $"URI 无法解析：{ex.Message}" } };
        }

        if (request.Kind == CommandKind.Notify && string.IsNullOrWhiteSpace(request.Title)
            && string.IsNullOrWhiteSpace(request.Text))
        {
            return null;
        }

        return request;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = query.TrimStart('?');
        if (text.Length == 0) return result;

        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = pair.IndexOf('=');
            if (split < 0)
            {
                result[Unescape(pair)] = string.Empty;
                continue;
            }
            result[Unescape(pair[..split])] = Unescape(pair[(split + 1)..]);
        }
        return result;
    }

    private static string? Get(Dictionary<string, string> query, string key)
        => query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>URI / 命令行转义还原（<c>%20</c>、<c>+</c>）。</summary>
    private static string Unescape(string value)
    {
        value = value.Replace('+', ' ');
        try { return Uri.UnescapeDataString(value); }
        catch { return value; }
    }

    private static bool IsSwitch(string arg)
        => arg.StartsWith("--", StringComparison.Ordinal);

    /// <summary>序列化成一行 JSON，交给已在运行的实例。</summary>
    public static string Serialize(CommandRequest request) => JsonSerializer.Serialize(request);

    /// <summary>解析来自命名事件的请求（失败返回 null）。</summary>
    public static CommandRequest? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<CommandRequest>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>命令行帮助（<c>--help</c> 与原生提示框用）。</summary>
    public static string HelpText => string.Join(Environment.NewLine,
        "WinIsland 命令行：",
        "  --notify \"标题\" \"正文\"        推一条临时消息到灵动岛",
        "  --text <正文>                  正文（可与 --title 分开给）",
        "  --glyph <字形>                 图标（Segoe Fluent Icons，如 E7E8）",
        "  --accent <#RRGGBB>             强调色",
        "  --duration <秒>                显示时长，1..60，默认 4",
        "  --settings [页面]              打开设置（页面 id：general / plugins / about）",
        "  --quiet [分钟]                 静默一段时间，默认 30",
        "  --unquiet                      结束静默",
        "  --show                         显示灵动岛",
        "",
        "也可以用 URI：",
        "  winisland://notify?title=构建完成&text=耗时1m20s",
        "  winisland://settings?page=plugins",
        "  winisland://quiet?minutes=30");
}

internal enum CommandKind
{
    None,
    Notify,
    Settings,
    Show,
    Quiet,
    Unquiet,
    Help,
}

/// <summary>一次自动化请求（命令行的解析结果，也会被序列化后跨进程传递）。</summary>
internal sealed class CommandRequest
{
    public CommandKind Kind { get; set; }
    public string? Title { get; set; }
    public string? Text { get; set; }
    public string? Glyph { get; set; }
    public string? Accent { get; set; }
    public double? DurationSeconds { get; set; }
    public string? Page { get; set; }
    public int QuietMinutes { get; set; } = 30;
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// <c>winisland://</c> 协议登记（HKCU\Software\Classes，每用户、不需要管理员）。
/// 只在用户从设置页主动打开时写入，卸载/关闭时删除 —— 不静默抢占协议。
/// </summary>
internal static class ProtocolRegistration
{
    private const string Root = @"Software\Classes\winisland";

    public static bool IsRegistered()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Root);
            return key?.GetValue("URL Protocol") != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>登记协议。返回 (成功, 错误文案)。</summary>
    public static (bool Ok, string? Error) Register()
    {
        try
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WinIsland.exe");
            using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Root);
            root.SetValue("", "URL:WinIsland Protocol");
            root.SetValue("URL Protocol", string.Empty);

            using var command = root.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Ok, string? Error) Unregister()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
