# AgentHub（Windows 示例插件）

这是 Agent HUD 思路的 C# / WinUI 3 实现：代理记录 → provider → 统一会话和 Token 模型 → 岛体摘要 / 聚光卡；引擎负责登录和额度，hooks 负责实时事件和人工审批。Windows 使用当前用户专属命名管道，代替 Unix socket；工作目录使用 Windows 路径。**这是首版 Windows 实现，不是上游 17 个客户端的完整功能移植。**

参考版本：[agent-hud-open](https://github.com/jazzenchen/agent-hud-open) `faa3a06fcf06510855ada332ea454f1f6df30ab4`。协议语义来自其 providers、session lifecycle 和 permission hooks；许可证见 UPSTREAM-LICENSE.txt / THIRD_PARTY_NOTICES.txt。

## 已实现的接入

| 客户端 | 本地记录 / Token | 额度 | 实时 hook | 人工审批 |
|---|---|---|---|---|
| Claude Code | projects/**/*.jsonl；消息 ID 去重；cache write 计入 In | 自有引擎 get_usage | 提示、通知、Stop、会话结束 | 允许一次 / 拒绝 / 问答 |
| Codex CLI / Desktop | sessions + archived_sessions；累计差分；session_index 标题 | app-server account/rateLimits/read | PermissionRequest | 仅明确 approvals_reviewer=user 的 turn |
| Qwen Code | chats/*.jsonl 的 api_response telemetry；排除 fork / subagent 副本 | — | Stop | 工具审批；问题和计划回原生 UI |
| CodeBuddy / WorkBuddy | projects/**/*.jsonl 的 usage / rawUsage | — | Stop | 工具审批；问题和计划回原生 UI |
| Pi / OpenClaw | 本地 session JSONL 的 assistant usage | — | — | — |
| ZCode | — | — | PermissionRequest | 工具审批 / 问答 |
| Qoder / Qoder CN / QoderWork | —（上游也无 provider） | — | PermissionRequest | 工具审批 |

尚未移植：Cursor / Antigravity / OpenCode / Hermes / ZCode 的 SQLite 或原生服务读取；Grok / Kimi / DeepSeek / Copilot 的专有日志及账户查询；Codex Desktop 原生 IPC 的应用授权和异步问题；订阅余额、费用价格表、配额消耗预测、移动端共享。这些能力不能靠 Claude 风格日志或 Windows 窗口标题猜测。

展开活动岛后，通过左右按钮切换概览、会话、额度、Token 和待处理。额度用圆环和进度条（明确显示已用比例），Token 用 24 小时柱状图及输入/输出/缓存进度条；允许一次、拒绝和回答问题直接在岛内完成。斜箭头才打开更多历史和长补丁的聚光卡。翻页使用原生动画；所有图表保留实例，数据变化时从当前值过渡，未变化时不重播动画，关闭系统动画后直接到位。历史尚未读完时显示“统计中”，不会把阶段性合计显示成最终统计。

聚光卡统计窗口为 5 小时 / 24 小时 / 7 天 / 30 天，按客户端和模型分组。额度 ≥90% 时每个窗口至多每小时提醒一次。历史从代理自身日志重建，不保存凭证、工具输入或回答；没有时间或用量的记录不编造数值。

## 构建与安装

需要 .NET 10 和本仓库的 WinIsland.Core **2.5.0**。先构建宿主，再构建插件；不要装到旧的 2.4 宿主上。

```powershell
cd WinIsland
dotnet build
cd ../samples/AgentHub
dotnet build
```

示例项目自动复制到源码宿主 Debug 的 plugins/agenthub。未复制到正在运行的安装版。打包：

```powershell
pwsh ../../tools/pack-plugin.ps1 -ProjectDir . -OutputDir dist
```

插件设置可指定 Codex / Claude 数据目录和原生 exe。留空 exe 时查 PATH、~/.local/bin、正在运行的代理引擎以及 Codex Desktop 的 LocalAppData 原生引擎目录；npm 的 .cmd 启动器不会交给 shell 执行，请指定真正的 exe。额度每 5 分钟刷新（显示读取时间和可确认的账户），日志每 15 秒增量读取；修改数据目录或引擎路径会清除旧额度并重新查询。读取失败显示不可用，不会用 0% 冒充成功。关闭额度开关后仅读本地日志。

## 安装实时 hooks

在插件 hooks 目录运行以下命令。可先加 `-Preview` 查看合并后的 JSON。脚本先备份，只管理自己的命令，保留其他 hook 和设置；不会启用客户端禁用的 hooks，也不会设置 Codex 信任。

```powershell
./Install-AgentHubHooks.ps1 -Agent 'Claude Code'
./Install-AgentHubHooks.ps1 -Agent 'Codex'
```

其他支持的 `-Agent` 见表。自定义路径用 `-ConfigPath 'D:\client\settings.json'`。ZCode 写 ~/.zcode/cli/config.json 的 hooks.events；Qwen timeout 按毫秒写入。Codex 需要在 Desktop hook 设置或 CLI `/hooks` 中亲自检查并信任该命令；客户端版本也必须支持 PermissionRequest。无需重写 config.toml。

撤销同一路径的 AgentHub hooks：

```powershell
./Install-AgentHubHooks.ps1 -Agent 'Claude Code' -Remove
./Install-AgentHubHooks.ps1 -Agent 'Codex' -Remove
```

hooks 不会自动审批。请求等待最多 10 分钟；超时、连接中断、插件停用或 WinIsland 未运行都返回 `{}`，交回客户端原生审批。每个请求对应独立管道连接，最多 32 个并发。请求只接受同一个 Windows 用户连接，载荷上限 256 KiB。计划审批保留在原生客户端，避免未展示完整计划就批准。

会话提供复制 ID 和打开工作区。宿主新增激活已知进程窗口的能力，但本版没有可靠的 session → terminal PID 绑定，因此不会猜一个窗口或向终端模拟输入。每个窗口使用独立内容树，输入和主题由 WinIsland SDK 提供。

## 宿主 API

IPluginHostServices 是可选接口：`Context is IPluginHostServices host`。

* `DataDirectory`：%LocalAppData%/WinIsland/PluginData/<plugin-id>，升级后保留。
* `LifetimeToken`：注册撤销时取消（包含初始化失败），便于终止后台读取和管道。
* `TryActivateProcess(pid)`：只尝试激活已有进程的主窗口，失败返回 false；不启动进程、不模拟键盘。

没有给 IPluginContext 增加必需成员；旧插件保持兼容。新能力用 min_host_version=2.5.0 声明。

## 验证

```powershell
cd ../../tests/AgentHub.Regression
dotnet run
```

测试只使用临时合成日志和当前用户命名管道；不读取真实账户或修改客户端配置。

`tests/AgentHub.Preview` 默认读取本机真实记录和原生引擎额度。只有显式传入 `--demo` 才展示合成数据，窗口和岛内都标明“示例数据”；演示值与真实账户无关。`--light` 可检查浅色布局。回归程序的 `--live` 选项用于显式读取本机数据和核对原生额度，不发送模型消息。

