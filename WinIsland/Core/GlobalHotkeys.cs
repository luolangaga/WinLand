using System.Runtime.InteropServices;

namespace WinIsland.Core;

/// <summary>
/// 全局快捷键：岛体本身不抢焦点、只响应鼠标，所以「唤起岛」「播放暂停」这类操作目前没有键盘入口。
/// 这里用 <c>RegisterHotKey</c> 挂一组可配置的全局热键。
///
/// 与聚光卡的 Esc 热键（<see cref="Win32.InstallEscapeHotKey"/>）是两套东西：那个是
/// 「输入会话期间临时撤掉」的一次性热键，必须能独立装卸；这里是常驻的用户配置，
/// 有自己的子类化 id 与热键 id 段，互不干扰。
///
/// 注册失败（被别的程序占用）只记日志并跳过，不影响其它热键 —— 快捷键永远不是必需品。
/// </summary>
internal sealed class GlobalHotkeys : IDisposable
{
    /// <summary>动作 id（也是设置键的后缀：<c>island.hotkey.&lt;action&gt;</c>）。</summary>
    public const string ToggleIslandAction = "toggle";
    public const string PlayPauseAction = "playpause";
    public const string QuietAction = "quiet";

    private const int BaseHotKeyId = 0x5760;
    private const nuint SubclassId = 0x15AF;

    // RegisterHotKey 修饰键
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly nint _hwnd;
    private readonly IPluginLogger _log;
    private readonly Action<string> _invoke;
    // 热键 id → 动作名。必须在实例上（静态回调要靠它反查），且只被同一个实例读写
    private readonly Dictionary<int, string> _actions = new();

    /// <summary>子类化过程只能拿到窗口句柄，靠它找回当前实例的动作表。一个进程只挂一组。</summary>
    private static GlobalHotkeys? _current;
    private static readonly SubclassProc Proc = WndProcHandler;

    public GlobalHotkeys(nint hwnd, IPluginLogger log, Action<string> invoke)
    {
        _hwnd = hwnd;
        _log = log;
        _invoke = invoke;
    }

    /// <summary>
    /// 按设置重新注册全部热键。设置里存的是人类可读的组合串（例如 <c>Ctrl+Alt+I</c>），
    /// 空串 = 不启用。重复调用先全部注销再重挂，用户改键立刻生效。
    /// </summary>
    public void Apply(ISettingsStore settings)
    {
        UnregisterAll();
        _current = this;

        // 总开关：关掉时连一个热键都不注册（子类化保留，改开关不必重装）
        if (!settings.Get("island.hotkeys", true))
        {
            SetWindowSubclass(_hwnd, Proc, SubclassId, 0);
            return;
        }

        int id = BaseHotKeyId;
        foreach (string action in new[] { ToggleIslandAction, PlayPauseAction, QuietAction })
        {
            string text = settings.Get($"island.hotkey.{action}", DefaultFor(action)) ?? string.Empty;
            if (!TryParse(text, out uint modifiers, out uint vk)) continue;

            if (RegisterHotKey(_hwnd, id, modifiers | MOD_NOREPEAT, vk))
            {
                _actions[id] = action;
                id++;
            }
            else
            {
                _log.Warn($"全局快捷键「{text}」注册失败（可能被其他程序占用），已跳过。");
            }
        }

        // 子类化只装一次；没有热键时也保留（下次改键直接生效，不必重装）
        SetWindowSubclass(_hwnd, Proc, SubclassId, 0);
    }

    public static string DefaultFor(string action) => action switch
    {
        ToggleIslandAction => "Ctrl+Alt+I",
        PlayPauseAction => "Ctrl+Alt+P",
        QuietAction => "",
        _ => "",
    };

    /// <summary>设置页展示用的中文说明。</summary>
    public static string DescribeAction(string action) => action switch
    {
        ToggleIslandAction => "展开 / 收起灵动岛",
        PlayPauseAction => "播放 / 暂停当前媒体",
        QuietAction => "切换勿扰（静默 30 分钟）",
        _ => action,
    };

    /// <summary>把 <c>Ctrl+Alt+I</c> 这类组合串解析成 RegisterHotKey 的修饰键与虚拟键码。</summary>
    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint mods = 0;
        uint key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win" or "windows": mods |= MOD_WIN; break;
                default:
                    if (key != 0) return false;          // 两个主键：无效组合
                    if (!TryParseKey(raw, out key)) return false;
                    break;
            }
        }

        if (key == 0) return false;
        modifiers = mods;
        vk = key;
        return true;
    }

    private static bool TryParseKey(string token, out uint vk)
    {
        vk = 0;
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) { vk = c; return true; }
            return false;
        }

        if ((token[0] is 'F' or 'f') && int.TryParse(token.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        vk = token.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "enter" or "return" => 0x0D,
            "escape" or "esc" => 0x1B,
            "tab" => 0x09,
            "insert" => 0x2D,
            "delete" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            _ => 0,
        };
        return vk != 0;
    }

    /// <summary>把修饰键与键码反转成组合串（校验/回显用）。</summary>
    public static string Describe(uint modifiers, uint vk)
    {
        var parts = new List<string>(4);
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");

        parts.Add(vk switch
        {
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x70 and <= 0x87 => "F" + (vk - 0x70 + 1),
            0x20 => "Space",
            0x0D => "Enter",
            0x1B => "Esc",
            _ => vk.ToString(),
        });
        return string.Join("+", parts);
    }

    private void UnregisterAll()
    {
        if (_actions.Count == 0) return;
        foreach (int id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }

    private void Dispatch(int id)
    {
        if (!_actions.TryGetValue(id, out var action)) return;
        try
        {
            _invoke(action);
        }
        catch (Exception ex)
        {
            // 热键回调在窗口过程里：抛出去会变成未处理异常
            _log.Warn($"全局快捷键「{action}」的回调失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        UnregisterAll();
        RemoveWindowSubclass(_hwnd, Proc, SubclassId);
        if (ReferenceEquals(_current, this)) _current = null;
    }

    private static nint WndProcHandler(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData)
    {
        if (uMsg == WM_HOTKEY && _current is { } current)
        {
            int id = (int)wParam;
            if (id >= BaseHotKeyId && id < BaseHotKeyId + 16)
            {
                current.Dispatch(id);
                return 0;
            }
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private const uint WM_HOTKEY = 0x0312;

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
