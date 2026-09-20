# Hello 插件（示例）

演示 WinIsland 插件 SDK 2.0 的标准写法：

- 代码构建 UI（推荐路径，零 XAML 加载问题）
- 自带私有依赖（`HelloLib.dll` 随插件分发，由 `AssemblyDependencyResolver` 解析）
- 注册设置页、常驻内容（`CompactContent` + `ExpandedContent` 双视图）
- 定时器、设置变更订阅、注册项自动清理
- 受管异常演示（宿主捕获 → 记日志 → 计 5 次自动停用）

## 构建与调试

```powershell
dotnet build -c Debug
```

构建后会自动拷贝到宿主的 `plugins/hello/` 目录（路径见 csproj 的 `WinIslandPluginsDir`）。
手动安装时把整个输出目录 + `plugin.json` 打成 zip 并改名 `.lwp` 即可，见仓库 `tools/pack-plugin.ps1`。
