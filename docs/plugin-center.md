# 插件中心

设置中的「插件中心」合并原插件市场与插件管理。旧 `marketplace` 导航 ID 仍会定位到插件中心。

- **发现**：社区目录，直接呈现本地安装状态，支持安装与兼容更新。
- **已安装**：包含内置插件和本地插件，即使商店不可用也能启停、管理。
- **可更新**：只列出兼容且版本高于本地的社区插件；本地较新时不会提供降级安装，内置插件不通过商店替换。
- 搜索在三种视图间共享，匹配名称、ID、说明、作者和社区标签。
- 卡片的管理面板提供两种独立优先级、插件设置入口、重载、日志和社区说明；外部插件另有目录与卸载操作，卸载保留确认。
- 本地 `.lwp` 安装始终可用。源设置、刷新、社区仓库和 AI 创作教程位于右上角菜单。

`PluginCenterPage.xaml` / `.xaml.cs` 管理共享界面与目录；`.Marketplace.cs` 保留原市场的下载、SHA-256 校验、README 与镜像源逻辑；`.Management.cs` 承接原管理页的宿主操作。SDK 与插件加载协议未作更改。

界面使用有限视口的 `GridView`，按可用宽度显示一到三列。卡片错峰淡入上移、悬停抬升，切换视图平滑入场，管理面板使用图标连续动画。所有新增动画遵循系统动画开关，卸载页面时停止动画并取消网络请求。

验证命令：

```powershell
cd WinIsland
dotnet build
dotnet build -c Release
winapp run . --detach --json -- --plugin-center-preview
# 使用返回的进程与窗口 HWND；预览使用独立目录和离线目录源。
../tools/Test-PluginCenter.ps1 -AppPid <PID> -WindowHandle <HWND>
```

Debug 参数 `--preview-dark`、`--preview-narrow` 可与 `--plugin-center-preview` 组合检查主题和窄窗口。预览中的下载包为故意不可用的测试条目，用于验证失败反馈和操作恢复，不代表验证真实社区包的下载成功。

`--reliability-tests --plugin-list-only` 检查 80 个插件的卡片绘制、容器虚拟化、滚动复用、导航返回，以及设置导航变化不重新创建插件中心。界面交互结果与截图保存在 `artifacts/plugin-center/`。
