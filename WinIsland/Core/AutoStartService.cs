using System.Diagnostics;
using Microsoft.Win32;

namespace WinIsland.Core;

/// <summary>开机自启动模式。</summary>
internal enum AutoStartMode
{
    /// <summary>不随系统启动。</summary>
    Off,

    /// <summary>普通权限自启动：写 HKCU 的 Run 键，登录后直接启动，不弹 UAC。</summary>
    User,

    /// <summary>管理员权限自启动：建一个「最高权限」的登录计划任务，登录时静默以管理员身份启动。</summary>
    Admin,
}

/// <summary>
/// 开机自启动的落地实现。普通权限走注册表 Run 键；管理员权限走计划任务
/// （Run 键没法提权，登录时想拿到管理员身份只有计划任务的 /RL HIGHEST 这一条路）。
/// 计划任务的增删需要管理员权限，会弹一次 UAC 由用户确认。
/// </summary>
internal static class AutoStartService
{
    public const string SettingKey = "island.autostart";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WinIsland";
    private const string TaskName = "WinIsland";

    /// <summary>等 UAC 确认 + 系统命令执行的上限。</summary>
    private const int ElevatedTimeoutMs = 60_000;

    /// <summary>UAC 被用户点「否」时的 Win32 错误码。</summary>
    private const int ErrorCancelled = 1223;

    public static string ExecutablePath => Environment.ProcessPath ?? "";

    public static AutoStartMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "user" => AutoStartMode.User,
        "admin" => AutoStartMode.Admin,
        _ => AutoStartMode.Off,
    };

    public static string ToValue(AutoStartMode mode) => mode switch
    {
        AutoStartMode.User => "user",
        AutoStartMode.Admin => "admin",
        _ => "off",
    };

    /// <summary>
    /// 实际生效的模式（以系统里的注册为准）。<paramref name="fallback"/> 是设置里记的值：
    /// 查不到计划任务状态时（权限不足等）用它兜底，避免把已开启的状态误报成关闭。
    /// </summary>
    public static AutoStartMode Current(AutoStartMode fallback)
    {
        var task = TaskExists();
        if (task == true) return AutoStartMode.Admin;
        if (task == null) return fallback;
        return RunEntryMatches() ? AutoStartMode.User : AutoStartMode.Off;
    }

    /// <summary>应用目标模式；失败返回 false 并给出原因（界面会回滚到实际生效值）。</summary>
    public static bool TryApply(AutoStartMode mode, out string error)
    {
        string exe = ExecutablePath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            error = "取不到程序路径，无法设置自启动";
            return false;
        }

        switch (mode)
        {
            case AutoStartMode.User:
                // 先摘掉管理员任务（存在才需要提权），再写 Run 键
                return RemoveTask(out error) && WriteRunEntry(exe, out error);

            case AutoStartMode.Admin:
                return RemoveRunEntry(out error) && CreateTask(exe, out error);

            default:
                return RemoveTask(out error) && RemoveRunEntry(out error);
        }
    }

    /// <summary>
    /// 异步版 <see cref="TryApply"/>：管理员模式会弹 UAC 并同步等待系统命令（最长 <see cref="ElevatedTimeoutMs"/> 毫秒），
    /// 绝不能在 UI 线程上同步等待——那会把整个应用（含岛与所有窗口）冻结到用户响应 UAC 为止。
    /// </summary>
    public static Task<(bool Success, string Error)> TryApplyAsync(AutoStartMode mode)
        => Task.Run(() =>
        {
            bool success = TryApply(mode, out var error);
            return (success, error);
        });

    // ---- 计划任务（管理员权限）----

    /// <summary>
    /// 计划任务是否存在；无法判断时返回 null（多半是查询权限不足）。
    /// schtasks 对"任务不存在"和"拒绝访问"都返回 1，所以必须看输出文案区分。
    /// </summary>
    private static bool? TaskExists()
    {
        try
        {
            if (Run("schtasks.exe", $"/Query /TN {TaskName}", elevate: false, out _, out var output))
            {
                return true;
            }

            if (output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                || output.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return false;
        }
        catch
        {
            return null;
        }
    }

    private static bool CreateTask(string exe, out string error)
    {
        // /RL HIGHEST：以最高权限运行（登录时静默提权，不弹 UAC）
        // /DELAY：等桌面起来再启动
        // 不要加 /NP：它会让 schtasks 交互式索要账号密码（"Please enter the run as password for ..."），
        // 而提权进程的控制台是隐藏的，没人能回答，命令会永远卡在等输入（实机事故，
        // 表现为"UAC 已批准但一直等待"）。不带 /NP、/RP 时 schtasks 会把任务建成
        // 「仅在用户登录时运行」（交互式令牌 + /RL HIGHEST），正是我们需要的形态。
        string arguments =
            $"/Create /TN {TaskName} /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /RU \"{Environment.UserName}\" " +
            "/DELAY 0000:10 /F";
        if (!Run("schtasks.exe", arguments, elevate: true, out error, out _))
        {
            return false;
        }

        // 提权进程的退出码不完全可信：以「任务是否真的出现」为准（查询失败时不误报）
        if (TaskExists() == false)
        {
            error = "命令已执行，但系统里没有出现计划任务";
            return false;
        }

        error = "";
        return true;
    }

    private static bool RemoveTask(out string error)
    {
        if (TaskExists() != true)
        {
            error = "";
            return true;            // 本来就没有任务，不用提权
        }

        if (!Run("schtasks.exe", $"/Delete /TN {TaskName} /F", elevate: true, out error, out _))
        {
            return false;
        }

        if (TaskExists() == true)
        {
            error = "计划任务仍然存在（删除未生效）";
            return false;
        }

        error = "";
        return true;
    }

    // ---- 注册表 Run 键（普通权限）----

    private static bool WriteRunEntry(string exe, out string error)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                error = "打不开注册表 Run 键";
                return false;
            }

            key.SetValue(RunValueName, $"\"{exe}\"");
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"写注册表失败：{ex.Message}";
            return false;
        }
    }

    private static bool RemoveRunEntry(out string error)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"清理注册表失败：{ex.Message}";
            return false;
        }
    }

    private static bool RunEntryMatches()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key?.GetValue(RunValueName) is not string value) return false;

            string recorded = value.Trim().Trim('"');
            return string.Equals(recorded, ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ---- 执行外部命令（可选择提升到管理员）----

    private static bool Run(string fileName, string arguments, bool elevate, out string error, out string output)
    {
        output = "";
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = elevate,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = !elevate,
                RedirectStandardError = !elevate,
            };

            if (elevate) startInfo.Verb = "runas";

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                error = $"{fileName} 启动失败";
                return false;
            }

            if (!elevate)
            {
                output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            }

            if (!process.WaitForExit(ElevatedTimeoutMs))
            {
                error = "等待管理员授权超时（已放弃）";
                return false;
            }

            if (process.ExitCode != 0)
            {
                error = $"{fileName} 返回错误码 {process.ExitCode}";
                return false;
            }

            error = "";
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            error = "已取消管理员授权";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
