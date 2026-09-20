# 硬件监控插件（示例）

常驻硬件信息：小岛显示你勾选的指标，展开后显示前台窗口帧率、CPU、GPU 与上下传速度。

## 数据来源（全部系统 API，无第三方传感器库）

| 指标 | 来源 |
|------|------|
| CPU 名称 / 占用 | 注册表 `ProcessorNameString` + PDH `\Processor Information(*)\% Processor Time`（多路 CPU 可选组） |
| GPU 名称 / 占用 | 注册表显示适配器列表（过滤虚拟显示器）+ PDH `\GPU Engine(*)\Utilization Percentage`（取最忙引擎，Task Manager 口径） |
| 上下传速度 | `NetworkInterface.GetIPStatistics()`（按网卡，过滤无网关的虚拟网卡） |
| 前台窗口帧率 | ETW `Microsoft-Windows-DxgKrnl` / `Win32k` present 事件（PresentMon 口径，**需要以管理员身份运行 WinIsland**）；无管理员权限时降级为 DWM 桌面合成帧率 |

## 配置

- **小岛显示项**：CPU / GPU / 上下传 / 帧率 四个开关（默认 CPU + 上下传）。
- **设备**：只在系统里存在多个同类设备时才出现选择框（单 GPU / 单网卡机器不显示，避免无意义选项）。
- 优先级固定 95（低于媒体的 100，高于电池 50 与 AI 监控 80）；如需调整改 `settings.json` 的 `hw-monitor.priority`。

## 构建

```powershell
dotnet build
```

构建后自动拷到宿主 `plugins/hw-monitor/`（路径见 csproj 的 `WinIslandPluginsDir`）。
打包分发：`pwsh tools/pack-plugin.ps1 -ProjectDir samples\HardwareMonitor` → `samples/dist/hw-monitor.lwp`。

## 依赖说明

本插件依赖 `Microsoft.Diagnostics.Tracing.TraceEvent`（微软官方 ETW 封装，用于读前台窗口帧率）：

- 工程打开 `CopyLocalLockFileAssemblies`，依赖才会随插件输出；
- WinAppSDK 运行时由宿主提供，通过 `PrivateAssets/ExcludeAssets` + 拷贝时过滤避免打进插件包（否则会多出 40MB）。
