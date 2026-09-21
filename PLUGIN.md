# WinIsland 插件开发指南（SDK 2.0）

> **SDK 2.0 与 1.x 不兼容。** 插件必须以 `plugins/<id>/ + plugin.json`（或 `.lwp` 包）提供，入口类型为 `IIslandPlugin`；
> 1.x 的散装 DLL、`IIslandModule`、`IslandPluginAttribute` 全部不再支持。迁移见文末。

---

## 1. 插件格式

```
plugins/
  hw-monitor/                  ← 一个插件一个目录，目录名建议等于插件 Id
    plugin.json                ← 清单（唯一元数据来源）
    HardwareMonitor.dll        ← 入口程序集（plugin.json 的 entry_dll）
    HardwareMonitor.deps.json  ← 依赖清单（依赖解析需要，构建自动生成）
    <私有依赖>.dll  *.xbf  assets/…
  demo.lwp                     ← 放在 plugins/ 根目录的插件包，启动时自动安装并删除
```

### plugin.json

```json
{
  "id": "hw-monitor",
  "name": "硬件监控",
  "version": "1.0.0",
  "entry_dll": "HardwareMonitor.dll",
  "api_version": 2,
  "min_host_version": "2.0.0",
  "description": "可选描述",
  "author": "可选作者",
  "icon_glyph": "\uE950",
  "homepage": "https://example.com",
  "license": "MIT",
  "tags": ["hardware"]
}
```

| 字段 | 必填 | 规则 |
|------|------|------|
| `id` | ✔ | `^[a-z0-9][a-z0-9-]{1,63}$`，全局唯一 |
| `name` | ✔ | 显示名称 |
| `version` | ✔ | 数字点分版本号（`1.2.3`） |
| `entry_dll` | ✔ | 包内文件名，不能含路径；不能是 `WinIsland.Core.dll` |
| `api_version` | ✔ | 当前为 `2`；必须 ≤ 宿主支持的版本 |
| `min_host_version` | ✖ | 低于此宿主版本时拒绝加载 |
| 其余 | ✖ | 展示用 |

校验失败时插件会以 **错误** 状态出现在「插件管理」里，并写明具体字段和原因；日志在
`%LocalAppData%\WinIsland\logs\plugin.<id>.log`。

---

## 2. 最小插件

```csharp
using WinIsland.Core;

namespace MyPlugin;

public sealed class MyPlugin : IslandPluginBase
{
    protected override Task OnInitializeAsync()
    {
        Log.Info("插件已启动");
        Context.Island.ShowMessage(new IslandMessage { Title = "你好", Text = "来自我的插件" });
        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        Log.Info("插件已停用");
        return Task.CompletedTask;
    }
}
```

要求：`public sealed class`、实现 `IIslandPlugin`（或继承 `IslandPluginBase`）、有公共无参构造函数。
**一个插件包只允许一个 `IIslandPlugin` 实现**，多于一个或没有都会以明确错误终止加载。

---

## 3. 生命周期与状态

```
Discovered → Loaded → Active         正常运行
                     ↘ Disabled       被用户禁用（持久化为 plugin.<id>.disabled）
                     ↘ Faulted        初始化失败 / 运行期反复出错
Dispose/卸载 → Unloading → 程序集请求卸载并验证回收
```

* `InitializeAsync` 在**启用循环中可能被多次调用**（禁用→启用、重新加载），必须可重复执行。
* `ShutdownAsync` 在停用前调用；**即使它抛异常或写得不干净，宿主也会兜底撤销**：
  移除该插件注册的设置页、清空常驻内容、关闭它创建的定时器、退订设置变更、收回临时消息。
* 非 `Active`/`Loaded` 状态下调用 Island API 会被**忽略并记日志**（不会产生"停用后的幽灵行为"）。
* 初始化超过 10 秒会被判定失败；单会话内 5 次未处理异常会自动停用插件并记为错误。

---

## 4. API 速查

插件通过 `IPluginContext`（`IslandPluginBase` 里是 `Context`，并提供了 `Log`/`Settings`/`SetContent`/`RunOnUI` 快捷方式）与宿主交互：

| 成员 | 说明 |
|------|------|
| `Manifest` | 当前插件清单（Id/Name/Version/…） |
| `PluginDirectory` | 插件自己的目录（读资源文件用） |
| `HostVersion` / `Dispatcher` | 宿主版本 / UI 线程调度器 |
| `Log` | `Debug/Info/Warn/Error`，写入插件日志（内存环形缓冲 + 文件） |
| `Settings` | **作用域化**设置存储，键自动加 `<id>.` 前缀（`Settings.Set("enabled", true)` → `hw-monitor.enabled`） |
| `Island.SetContent(content)` | 注册常驻内容（`null` 取消）。owner 由宿主绑定为插件 Id |
| `Island.ShowMessage(msg)` | 临时消息（标题 + 正文 + 图标 + 时长） |
| `Island.Show(uiElement, size, duration)` | 临时展示任意控件 |
| `Island.AddSettingsPage(desc)` | 注册设置页（停用时自动移除） |
| `Register(IDisposable)` | 登记需要在停用时释放的资源（订阅、原生句柄…） |
| `OnSettingsChanged(key, handler)` | 监听某个设置键（handler 在 UI 线程调用） |
| `CreateTimer(interval, repeat, tick)` | UI 线程定时器（停用时自动停止并解绑） |
| `RunOnUI` / `RunOnUIAsync` | 把工作编组回 UI 线程 |

### 常驻内容

```csharp
// 1) 单视图变形（推荐）：一个视图同时承载紧凑/展开形态
Context.Island.SetContent(new IslandLiveContent
{
    Priority = 60,
    MorphView = new MyMorphView(),      // 实现 IMorphView：View + AnimateToExpanded/Compact
    CompactSize = new Size(250, 40),
    ExpandedSize = new Size(420, 150),
});

// 2) 双视图：紧凑与展开是两个独立控件
Context.Island.SetContent(new IslandLiveContent
{
    CompactContent = compactPanel,
    ExpandedContent = expandedPanel,
    CompactSize = new Size(250, 40),
    ExpandedSize = new Size(420, 150),
});
```

`Priority` 决定多个插件同时注册内容时谁占据主岛（数值大者优先，其余进入展开后的队列）。
小岛只显示主内容；展开后主内容 + 最多 3 个队列内容按大岛样式排列。
宿主可以在「设置 → 插件管理」里用 `plugin.<pluginId>.priority` 覆盖你声明的 `Priority`，所以不要依赖固定顺序。

**尺寸由宿主统一**：展开态所有元素同宽（取所有活动里最大的展开宽度）、所有队列卡片同高（取最高的一张），
宿主会把卡片拉伸到这个统一尺寸。视图请用自适应布局（`Grid` 的 `*` 行列、`HorizontalAlignment/VerticalAlignment=Stretch`），
不要假设自己一定能拿到 `ExpandedSize` 声明的精确尺寸。

**形态动画不要用 `Storyboard`，必须逐帧直接赋值**（适用于所有插件视图，包括纯代码自绘的可视树）：

```csharp
// 对：DispatcherQueueTimer 每帧直接给属性赋值
_morphTimer = DispatcherQueue.CreateTimer();
_morphTimer.Interval = TimeSpan.FromMilliseconds(16);
_morphTimer.IsRepeating = true;
_morphTimer.Tick += (_, _) => OnMorphTick();

void OnMorphTick()
{
    var t = Math.Clamp((DateTimeOffset.UtcNow - _start).TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
    _detail.Height = Math.Max(0, ExpandedDetailHeight * Ease(t));   // Height 会被缓动曲线推成负数，要夹住
    _icon.Width = _icon.Height = CompactIcon + (ExpandedIcon - CompactIcon) * Ease(t);
}
```

原因是**属性路径**动画（`Storyboard.SetTargetProperty(anim, "Height")`）在动态加载的插件程序集里解析不出类型信息，
会在动画 tick 上抛 `COMException (0x800F1001): Invalid attribute value Unknown for property Height`。
这个异常发生在 tick 里，**调用点的 `try/catch` 拦不住**，会冒到宿主的未处理异常处理器：岛体尺寸已经收回去了、
插件的 `Height` 却卡在中间值，整块内容错位并且不再响应 hover（表现为"大岛变小之后卡死"）。
宿主内置视图（Media/Battery）能安全使用 `Storyboard`，是因为它们在宿主程序集里，类型元数据可解析——插件侧不具备这个条件。

`IMorphView` 的调用点也要注意：视图可能被宿主放进展开队列的卡片里（`QueuePanel`），
契约要求 `AnimateToExpanded/AnimateToCompact` 能在任意时刻、任意父级下被调用；
动画进度要在视图里自己累计（如 `_progress`），这样"展开到一半又收起"才能从当前位置接着走，而不是跳回起点。

需要「临时不占岛」时，推荐像 `samples/HardwareMonitor` 那样加一个 `enabled` 设置：
关掉时 `SetContent(null)`，打开时重新 `SetContent(content)`，插件本身继续运行。

---

## 5. 线程模型

* `InitializeAsync` / `ShutdownAsync` / 设置页工厂 / `OnSettingsChanged` / `CreateTimer` 回调都在 **UI 线程** 执行。
* 采样、网络、文件等耗时工作放到后台线程，然后用 `Context.RunOnUI(...)` 更新 UI。
* `CreateTimer` 必须在 UI 线程调用；插件里创建的 `UIElement` 必须在 UI 线程创建。

---

## 6. 依赖与资源（最容易踩的坑）

插件可以自带任意 NuGet 依赖和本机 DLL，宿主会用 `AssemblyDependencyResolver` + 插件的 `deps.json` 从插件目录解析：

```xml
<PropertyGroup>
  <!-- 关键：库工程默认不复制 NuGet 依赖，插件必须打开 -->
  <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
</PropertyGroup>

<ItemGroup>
  <!-- 运行时由宿主提供，不要打进插件包 -->
  <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.3.1" PrivateAssets="all" ExcludeAssets="runtime" />
  <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.28000.2526" PrivateAssets="all" ExcludeAssets="runtime" />
</ItemGroup>
```

解析规则（宿主侧）：

1. 框架程序集（`System.*` / `Windows.*` / `WinRT.*`）与 `WinIsland.Core` → **始终绑定宿主**，防止类型身份分裂。
2. 其它程序集：**宿主目录里存在同名 DLL → 用宿主那份**；否则用插件目录里的副本。
3. 因此：WinAppSDK/WinUI 运行时不必随插件分发（浪费 40MB），自己的依赖必须真的复制到插件目录
   （`samples/HardwareMonitor` 的 csproj 里有一份可直接抄的过滤写法）。

本机（native）依赖也放在插件目录里，`LoadUnmanagedDll` 会一并在该目录解析。

---

## 7. 开发循环

```powershell
# 插件工程里加一个构建后拷贝目标（见样例 csproj）
dotnet build
# 然后在「设置 → 插件管理」点「重新加载」，或用开关禁用→启用
```

日志：`%LocalAppData%\WinIsland\logs\plugin.<id>.log`（界面里「查看日志」也能看到最近 500 行）。

---

## 8. XAML 视图（可选路径）

动态加载的程序集不在应用的 `resources.pri` 里，生成的 `InitializeComponent` 找不到自己的 XBF。
需要 XAML 时用 SDK 提供的方法：

```csharp
public sealed partial class MyView : UserControl, IMorphView
{
    public MyView() => PluginXaml.Load(this);   // 代替 InitializeComponent()
}
```

约束：

* XAML 文件与类同名，所在文件夹与命名空间一致（`MyPlugin.Views.MyView` ↔ `Views/MyView.xaml`）。
* XAML 里只用框架类型，不要引用插件自己的自定义控件。
* 图片等资源用 `Context.PluginDirectory` 拼绝对路径加载，不要用 `ms-appx:`。
* **视图根元素保持透明背景**：岛体材质由宿主绘制（Apple 风格是纯黑胶囊，Windows Fluent 风格是 Desktop Acrylic / Mica
  系统材质），插件自绘不透明底色会在切换风格时露出与材质不一致的色块。卡片、徽标用半透明白（如 `#33FFFFFF`）即可适配两种材质。
* 形态动画同样要遵守上面的「逐帧直接赋值」规则——这条与是否使用 XAML 无关，XBF 树只是更容易触发的场景。

---

## 9. 打包与安装

```powershell
# 把构建输出 + plugin.json 打成 .lwp（zip）
pwsh tools/pack-plugin.ps1 -ProjectDir samples\HelloPlugin
```

* **安装**：把 `.lwp` 拖进 `plugins/`（启动时自动安装），或「插件管理 → 安装插件包…」。
* **更新**：同 Id 的包会停用旧版本 → 卸载程序集 → 替换目录 → 重新启用。
* **删除**：「插件管理 → 删除」，会真正移除插件目录；被占用的文件会在下次启动时清理。
* 包内不需要（也不应该）包含 `WinIsland.Core.dll` 与 WinAppSDK 运行时文件，校验会拒绝前者。

### 发布到社区插件市场

「设置 → 插件市场」读取社区仓库 `luolangaga/WinLandPlugin` 根目录的 `index.json`：**整个列表只发一次请求**
（图标以 base64 内嵌在清单里，不做逐插件请求），只有点「安装」才下载 `.lwp` 并强制校验 SHA-256。

* 投稿：把 `plugin.json` + `<id>.lwp` + `logo.png`（可选）+ `README.md`（可选）放进 `plugins/<id>/` 后提 PR，
  合并后 Action 自动重建清单；仓库里的 `tools/submit-plugin.ps1` 负责本地打包与校验。
* 详情页的 README 由客户端自带的轻量渲染器渲染（`Settings/MarkdownRenderer.cs`）：支持标题、列表、表格、
  围栏代码块、引用、行内样式与链接；**图片不渲染**（相对路径无从解析，退化为 alt 文本）。
* `plugin.json` 的 `id` 必须等于目录名，`version` / `entry_dll` 必须与 `.lwp` 包内那份**完全一致**（CI 会校验）。
* 市场源（可用 `marketplace.baseUrl` 覆盖，留空即自动）：官方 `raw.githubusercontent.com` →
  **GitCode 国内镜像** `api.gitcode.com/api/v5/repos/luolangaga/WinLandPlugin/raw` → jsDelivr。
  GitHub 是唯一源头，其余都只是镜像；客户端按顺序尝试并记住上次成功的源。

---

## 10. 样例

| 样例 | 内容 |
|------|------|
| `samples/HelloPlugin` | 代码构建 UI、私有依赖、设置页、定时器、完整清理、受管异常演示 |
| `samples/XamlPlugin` | XAML 视图 + `PluginXaml.Load` + 逐帧形变动画 |
| `samples/HardwareMonitor` | 真实功能插件：CPU/GPU/网络/帧率，自带 NuGet 依赖，设备选择，管理员提示 |

---

## 11. 从 1.x 迁移

| 1.x | 2.x |
|-----|-----|
| `IIslandModule`（含 `Id`/`DisplayName`） | `IIslandPlugin` + `plugin.json` 提供元数据 |
| `[IslandPlugin(...)]` | 删除，改用 `plugin.json` |
| `InitializeAsync(IDynamicIslandApi)` | `InitializeAsync(IPluginContext)`（或重写 `OnInitializeAsync`） |
| `Api.SetLiveContent(Id, content)` | `Context.Island.SetContent(content)` |
| `Api.Settings`（全局键） | `Context.Settings`（自动加 `<id>.` 前缀，旧键名不变） |
| `Api.Settings.Changed += …` | `Context.OnSettingsChanged(key, handler)` 或 `Register(...)` 包装 |
| `Api.Dispatcher.CreateTimer()` | `Context.CreateTimer(...)`（自动随停用释放） |
| 根目录散装 `*.dll` | `plugins/<id>/` 目录或 `.lwp` 包 |

旧插件不迁移就不会被加载，并会在「插件管理」里提示检测到旧格式 DLL。

---

## 12. 故障排查

| 现象/日志 | 原因 |
|-----------|------|
| 程序集里没有找到 IIslandPlugin 实现 | 入口类不是 `public sealed`、抽象、或缺无参构造 |
| 缺少依赖程序集：xxx | 依赖没复制到插件目录（见 §6）或 deps.json 缺失 |
| 插件针对 WinIsland.Core x.y 构建，与宿主不兼容 | SDK 大版本不一致，用 SDK 2.x 重新编译 |
| 已忽略调用 xxx：插件当前状态为 已停用 | 停用后仍有回调（定时器/网络回调），属正常保护 |
| 形态动画执行失败 | 插件动画抛异常（多为 §8 的 Storyboard 问题），宿主已忽略并记日志 |
| 帧率显示 `--` | 读取前台窗口帧率需要**以管理员身份运行 WinIsland**（ETW）；普通权限下显示桌面合成帧率 |
