using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinIsland.Core;

namespace WinIsland.Modules.AiMonitor;

public sealed partial class AiMonitorSettingsPage : UserControl
{
    private readonly ISettingsStore _settings;
    private readonly AiMonitorPlugin _plugin;
    private readonly string _pluginPath;
    private bool _loading = true;

    public AiMonitorSettingsPage(IPluginContext context, AiMonitorPlugin plugin)
    {
        _settings = context.Settings;
        _plugin = plugin;
        InitializeComponent();

        EnableToggle.IsOn = _settings.Get("enabled", true);
        PrioritySlider.Value = _settings.Get("priority", 80);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _pluginPath = Path.Combine(home, ".config", "opencode", "plugins", "winland-monitor.ts");

        UpdateStatus();
        _loading = false;
    }

    private void EnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("enabled", EnableToggle.IsOn);
    }

    private void PrioritySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("priority", (int)PrioritySlider.Value);
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(_pluginPath)!;
            Directory.CreateDirectory(dir);

            var sourcePath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                "Assets", "winland-monitor.ts");

            if (File.Exists(sourcePath))
                File.Copy(sourcePath, _pluginPath, overwrite: true);
            else
                File.WriteAllText(_pluginPath, GetEmbeddedPluginSource());

            UpdateStatus();
        }
        catch (Exception ex)
        {
            InstallHint.Text = $"安装失败: {ex.Message}";
        }
    }

    private static string GetEmbeddedPluginSource()
    {
        return """
/**
 * WinIsland Monitor Plugin for OpenCode
 * Place in ~/.config/opencode/plugins/
 */

const WINLAND_PORT = 47321
const WINLAND_URL = `http://127.0.0.1:${WINLAND_PORT}`

async function sendToWinLand(payload) {
  try {
    await fetch(WINLAND_URL, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    })
  } catch {}
}

export const WinLandMonitor = async ({ project, client, $, directory, worktree }) => {
  let sessionActive = false
  let toolCount = 0

  return {
    event: async ({ event }) => {
      switch (event.type) {
        case "session.created": {
          sessionActive = true
          toolCount = 0
          await sendToWinLand({
            type: "task_start",
            sessionId: event.properties?.sessionId ?? event.properties?.id,
            message: project?.name ?? "OpenCode",
            detail: "AI 任务已开始",
          })
          break
        }
        case "session.idle": {
          if (!sessionActive) break
          sessionActive = false
          await sendToWinLand({
            type: "task_end",
            sessionId: event.properties?.sessionId ?? event.properties?.id,
            message: project?.name ?? "OpenCode",
            detail: "任务已完成",
          })
          break
        }
        case "session.error": {
          if (!sessionActive) break
          sessionActive = false
          await sendToWinLand({
            type: "task_error",
            sessionId: event.properties?.sessionId ?? event.properties?.id,
            message: project?.name ?? "OpenCode",
            detail: "任务出错",
          })
          break
        }
        case "tool.execute.before": {
          toolCount++
          await sendToWinLand({
            type: "task_progress",
            message: project?.name ?? "OpenCode",
            detail: `正在执行: ${event.properties?.tool ?? "tool"}`,
            toolName: event.properties?.tool ?? "tool",
            progress: toolCount,
          })
          break
        }
        case "todo.updated": {
          const todos = event.properties?.todos
          if (todos && Array.isArray(todos)) {
            const completed = todos.filter((t) => t.status === "completed").length
            const total = todos.length
            if (total > 0) {
              await sendToWinLand({
                type: "task_progress",
                message: project?.name ?? "OpenCode",
                detail: `进度: ${completed}/${total}`,
                progress: completed / total,
              })
            }
          }
          break
        }
      }
    },
  }
}
""";
    }

    private void UpdateStatus()
    {
        var installed = File.Exists(_pluginPath);

        InstallButton.Content = installed ? "已安装" : "安装";
        InstallButton.IsEnabled = !installed;
        InstallHint.Text = installed
            ? "插件已安装于 ~/.config/opencode/plugins/"
            : "安装到 OpenCode 全局插件目录，自动连接 WinIsland";

        if (_plugin.IsEnabled)
        {
            StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F));
            StatusText.Text = _plugin.IsRunning
                ? $"运行中: {_plugin.CurrentTask} — {_plugin.CurrentDetail}"
                : "已连接 · 等待 AI 任务";
        }
        else
        {
            StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0xFF, 0x90, 0x90, 0x95));
            StatusText.Text = "未启用";
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatus();
    }
}
