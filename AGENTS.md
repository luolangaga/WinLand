# AGENTS.md — WinIsland

## Project

Windows Dynamic Island desktop app (WinUI 3 / Windows App SDK 2.3.1). Unpackaged WinExe — no MSIX, no store packaging. Projects: `WinIsland.Core/` (plugin SDK, v2.0), `WinIsland/` (main app), `samples/` (HelloPlugin / XamlPlugin / HardwareMonitor example plugins), `tools/pack-plugin.ps1` (`.lwp` packaging).

Plugin authoring docs: `PLUGIN.md`.

## Build & Run

```powershell
cd WinIsland
dotnet build
dotnet run
```

No solution file (`.sln`) exists. Build from the `WinIsland/` directory directly.

Target: `net10.0-windows10.0.26100.0`, min `10.0.17763.0`. Requires .NET 10 SDK + Windows App SDK 2.3.1.

`AllowUnsafeBlocks` is enabled (used by Win32 P/Invoke in `Core/Win32.cs`).

## Architecture

```
WinIsland.Core/              — SDK DLL (luolan.winland.Core 2.2.1, distribute to plugin developers)
  IslandApi.cs               — ISettingsStore / IMorphView / IslandLiveContent / IslandMessage / IslandDropKind / IslandDropTarget / IslandDropContext / SettingsPageDescriptor
  IIslandPlugin.cs           — plugin entry point (InitializeAsync(IPluginContext) / ShutdownAsync)
  IPluginContext.cs          — IPluginContext / IIslandSurface / IPluginLogger
  IslandSdk.cs               — ApiVersion, HostVersion, manifest/package constants
  PluginManifest.cs          — manifest data type (plugin.json)
  IslandPluginBase.cs        — convenience base (Context/Log/Settings/SetContent/UpdateContent/RunOnUI)
  PluginXaml.cs              — supported way to load XAML from a plugin assembly (LoadComponent workaround)
  ActionDisposable.cs        — tiny IDisposable helper for plugin authors

WinIsland/                    — Main application
  App.xaml.cs                — Entry: IslandWindow, IslandService, PluginHost, built-in manifests, global crash logging
  Core/
    IslandService.cs         — host-side content/temp/settings-page API (RemoveSettingsPage, page Order)
    SpotlightHost.cs         — 「超级展开」聚光卡的生命周期：单实例/替换/关闭后放回岛体/通知旧 owner
    SettingsService.cs       — JSON key-value store at %LocalAppData%\WinIsland\settings.json
    Marketplace/             — plugin marketplace client: MarketplaceModels.cs (index.json schema 1) + MarketplaceService.cs
                               (single index.json request, ETag/TTL cache, mirror fallback, sha256-verified .lwp download)
    DropTargets/             — host-built-in file drop actions (open / reveal in Explorer / copy paths), registered
                               into IslandService from App.xaml.cs with Order 900+ so plugin targets sort first
    Update/                  — self-update check: UpdateModels.cs + UpdateService.cs (GitCode release API first, GitHub
                               fallback; update.autoCheck / update.lastCheck / update.skippedVersion; notify only —
                               it never downloads or installs anything, the browser does that)
    TransparentBackdrop.cs   — Fully transparent window backdrop (Composition + DWM alpha)
    TrayIcon.cs              — Native Shell_NotifyIcon tray icon with Win32 popup menu
    Win32.cs                 — All P/Invoke: window styles, DWM, subclassing for border removal, taskbar strip detection (`GetTaskbarStrip`), topmost-band self-heal (`EnsureTopmost`)
    TaskbarLayout.cs         — UI-Automation probe of the taskbar's *occupied* bands (so the island can be placed in a real free one); background-thread only, degrades to empty
    Plugins/                 — Plugin engine v2
      PluginHost.cs          — facade: discover/install/enable/disable/reload/uninstall, per-plugin serialization
      PluginInstance.cs      — state machine + timeouts + guarded callbacks + ALC unload & GC verification
      PluginAssemblyContext.cs — collectible ALC + AssemblyDependencyResolver + host-provided-assembly policy
      PluginScope.cs         — registration ledger; revoke = remove pages, clear content, dispose timers/subscriptions
      ScopedIslandSurface.cs / ScopedSettings.cs / PluginContext.cs — per-plugin scoped API surface
      PluginManifestReader.cs — plugin.json parsing + field-level validation
      LwpInstaller.cs        — .lwp (zip) validate/extract/atomic install/update/uninstall
      PluginLogService.cs    — per-plugin ring buffer + file logs (logs/plugin.<id>.log)
      PluginInfo.cs / PluginStateText.cs — runtime state shown in the plugin manager
  Island/
    IslandWindow.xaml.cs     — island window: state machine, size animation, hover, hit region, style switch, AnimateMorphView guard, position (top / bottom taskbar strip) + horizontal placement/offset, free-band placement, taskbar-follow poll, file-drop session (drag enter/over/leave/drop, edge auto-scroll, armed tile)
    DropStripView.cs         — 文件投放面板（拖文件进岛时岛体展开成的那一条）：左侧载荷摘要 + 右侧可横向滚动的投放卡片，卡片悬停放大/高亮全用组合级动画；只管长相与命中，会话状态在 IslandWindow
    MessageView.cs           — 临时消息卡（IslandMessage）的视图：图标芯片 + 标题 + 正文，宽度跟随内容、高度跟随行数；尺寸写死在视图上，过渡期间不重排
    SpotlightWindow.xaml(.cs) — 「超级展开」聚光卡的全屏覆盖窗：暗化遮罩 + 居中大卡片，Composition 做"从主岛位置带倾角飞入 / 反向飞回"，点遮罩（卡片外）或 Esc 收起；窗口铺满主岛所在显示器，卡片按工作区居中并把插件声明的尺寸夹到工作区内
    IslandStyle.cs           — appearance-style policy (Apple / Fluent): corner radius, surface fill, stroke, idle dot color, spotlight card radius/fill
    IslandBackdrop.cs        — window-level system material (Desktop Acrylic / Mica controllers, IsInputActive pinned true)
  Modules/
    Media/ Battery/ AiMonitor/ Messaging/ — built-in plugins (IslandPluginBase; complete shutdown, no leaks)
  Settings/
    SettingsWindow.xaml.cs   — settings shell; nav pages registered dynamically (ordered by SettingsPageDescriptor.Order)
    GeneralSettingsPage.xaml.cs — host-owned general page (island.* settings)
    MarketplacePage.xaml(.cs) — plugin marketplace: list/detail/install from the community index.json
    PluginManagerPage.xaml   — plugin manager: install .lwp, enable/disable, reload, delete, logs, error details
    AboutSettingsPage.xaml(.cs) — 关于: version/build info, update check (GitCode first), release notes, repo/镜像 links
```

## Key Design Rules

- **Host owns chrome; plugins own content.** IslandWindow controls size, corner radius, hover, expand/collapse, temp message overlay. Plugins only provide XAML content via `IslandLiveContent`.
- **「超级展开」聚光卡是独立的全屏覆盖窗，岛窗口的几何不变式完全不受影响。** 插件调 `IIslandSurface.OpenSpotlight(IslandSpotlight { Content, Size, OnClosed })`（SDK 2.1 增量 API，`api_version` 仍为 2，插件用 `min_host_version: "2.1.0"` 做门槛）；`SpotlightHost` 负责「同一时刻只有一张 / 被替换时先通知旧 owner / 插件停用即自动收起」，`SpotlightWindow` 负责全屏遮罩 + Composition 飞入飞回 + 点卡片外或 Esc 收起。岛体只经过 `IslandWindow.SetSpotlightOccluded(bool)`：遮挡时先置 `_spotlightOccluded = true` 与 `_shown = false`（否则看门狗会把岛重新顶到卡片之上），再整块淡出后 `Hide()`；恢复时 `UpdateVisibility(force:true)` + 淡入 + 还原内容（临时消息优先）。**卡片内容必须是插件自己的另一棵可视树**（每个窗口一棵树），尺寸由插件声明、宿主夹到工作区 92%，飞入起点/飞回落点用 `MainIslandScreenRect()` + `MainIslandSizeDip()`。覆盖窗的缩放比必须取 `Win32.GetDpiForWindow()` —— 刚搬到目标显示器时 `XamlRoot.RasterizationScale` 还是旧值，会把卡片摆偏。
- **文件投放（拖文件/文本/图片进岛）是岛体的第三种内容状态**（与临时消息同级），不是新窗口也不是新面板：`IslandWindow` 的 `_dropping` 会话把岛体展开成 `DropStripView`（宽胶囊 460×104：左侧固定载荷摘要 —— 文件名 / 文本前两行 / 图片缩略图 —— 加右侧横向滚动的投放卡片，卡片由插件经 `IIslandSurface.AddDropTarget` 注册，宿主内置「打开 / 所在位置 / 复制路径 / 复制文本 / 保存图片」Order 900+ 排在插件之后）。载荷按 **文件 > 图片 > 文本** 的顺序判定（`DescribeDrag`，从浏览器拖图片时同时带文本，那种情况要当图片处理），读出来的是宿主内部表示 `DropPayload`（在 `DropStripView.cs` 里，对插件暴露 `IslandDropContext`）；**可投状态只有一份规则**（`DropStripView.Accepts`：类型 `Kinds` + 文件扩展名白名单），**卡片的显隐**（不接受的直接 `Visibility.Collapsed`，不是变暗 —— 索引仍与 `_dropTargets` 一一对应，Drop 靠索引取目标）与 Drop 时按真实载荷的复核共用它 —— 载荷没读完时卡片先全亮，松手后不匹配就当落空，一张都不匹配时摘要写「没有卡片能接收它」。图片上限 `MaxDropImageBytes`（32MB）。三条不能破的规矩：①**投放卡片必须长在岛体内部**（`ContentHost.Content`）—— 点击穿透形状只取主岛矩形 + 队列卡片，面板因此天然在形状里，`UpdateHitRegion` 一行都不用改；②**面板尺寸是常量，必须由 `ComputeCanvasFootprint` 提前预留**，拖拽全程绝不 `MoveAndResize`（客户区一变 DWM 就用上一帧合成出残影，还会打断正在进行的 OLE 拖放命中）；③**`_dropping` 期间所有既有状态都要让路**：`SetLive`、`OnHoverEnter`、`OnHoverExit`、`HoverGuardTick`、指针进出、`IslandRoot_Tapped`、`ApplyPriorityChange` 一律早退，`TransitionToLiveState` 改为回到投放面板（临时消息结束/聚光卡关闭因此自动复原），`SetSpotlightOccluded(true)` 走 `ClearDropSession()`。拖放前端用 XAML `AllowDrop` + `DragEnter/Over/Leave/Drop`（已实测在 `WS_POPUP + WS_EX_NOACTIVATE + SetWindowRgn` 的形状窗上可用；载荷读取隔离在 `ReadPayloadAsync`，真要换成原生 `RegisterDragDrop`/`IDropTarget` 也只动这一处）。OLE 拖放期间**收不到任何指针事件**（指针被源进程捕获），悬停位置只能从 `DragOver` 坐标推、边缘自动滚动必须由 16ms 定时器按"最后位置"推进（速度做一阶滤波，进出边缘带才顺滑；指针停在边缘带不动时 OLE 不再发 `DragOver`，靠事件永远滚不起来）。松手落空（没对准卡片）**什么都不做**；`Drop` 先 `EndDropSession()` 再读载荷/执行动作（插件动作可能等几秒，不能让面板吊在屏幕上）。`island.dropEnabled` 是总开关（关掉即 `IslandRoot.AllowDrop = false`，正在投放的会话立刻收回）。系统拖拽缩略图（窗口类 `SysDragImage`）在 `Win32.FindCoverer` 里被排除，否则置顶自愈会把岛顶到缩略图之上、把用户正拖的东西盖住。
- **MorphView mode preferred.** Single view with `AnimateToExpanded`/`AnimateToCompact` for element-level morph. Dual-view (CompactContent + ExpandedContent) is the fallback.
- **岛体配色必须明暗成对（Fluent 跟随系统主题，Apple 恒为深色）。** `IslandStyle` 是唯一的调色板（底衬 / 描边 / 空闲点 / 消息卡 / 投放卡片 / 聚光卡 + 面板文字），每个取色方法都带 `light` 参数；`SystemTheme` 是主题来源（读注册表 `AppsUseLightTheme`，用 `UISettings.ColorValuesChanged` 当"变了"的信号，变更一律编组到 UI 线程），`IslandStyle.IsLightChrome(style, light)` 是"岛体现在是不是浅色"的唯一判断（聚光卡、设置页预览、插件侧都走它）。三处曾经把主题钉死、别再钉回去：两个 XAML 根节点的 `RequestedTheme="Dark"`（现在由 `IslandWindow.ApplyStyle` / `SpotlightWindow.ApplyChrome` 按需写）、`IslandBackdrop` 的 `SystemBackdropTheme.Dark`（现在跟随主题，且只改配置不重建控制器）、以及各处写死的白色画刷。**浅色主题下岛体底衬不压纱**（`FluentSurfaceLight` 是透明的）：深色那层黑纱是为白字对比度，浅色再压白纱就把 Acrylic 冲成平白板 —— 看起来正是"浅色没有材质效果"。插件内容有两条保障：换主题时翻岛体根节点的 `RequestedTheme`（XAML 里的 `{ThemeResource ...}` 自己跟着换）＋ `IIslandTheme`（`IPluginContext.Theme`，`IsLight` + `Changed`，给代码搭视图用，宿主按插件逐个守卫广播）；内置模块与示例插件的**代码搭视图都必须订阅 `Changed` 就地重刷**，只在构造时取主题会留下"深色岛上的黑字"。
- **岛体内容切换只有两个入口：`IslandWindow.SwapContent(next, animate)` 与 `SetContentImmediate(content)`** —— 前者带交接动画，后者用于树结构修正（`TeardownQueue`）、聚光卡恢复、投放面板接管。交接的实物是 XAML 里 `ContentHost` 之上的 `ContentLeaving` 层：旧内容**按此刻的渲染尺寸钉住**挪到那一层上快速淡出 + 上移（130ms），新内容延后 80ms 从下方淡入 + 微缩放回位（200ms）—— 两段错开一点点，读起来是「岛换了口气」而不是两张画面叠在一起；钉住尺寸是为了让插件视图不在收缩动画里被逐帧挤扁。全程只动 Opacity / RenderTransform（组合级），与尺寸 morph 逐帧共存。任何时候直接写 `ContentHost.Content` 都会让内容层停在半途的透明/位移上，**新增内容入口必须走这两个方法之一**；`TeardownQueue` 的那句修正也会在临时消息/投放面板占用岛体时跳过（否则紧随其后的交接动画会从错误的内容起步，屏幕上闪一下插件视图）。注：Storyboard 的每条动画都必须 `SetTarget` + `SetTargetProperty`，漏掉就是运行期 `Animation target not specified`（启动消息直接抛在 `SendMessage` 上）。
- **临时内容（消息 / `Island.Show`）的尺寸 morph 不复用岛体那套回弹**：`SizeEasing` 在**收缩**方向用纯 `CubicEase`（BackEase 会越过目标 —— 胶囊缩得比目标还矮一截、内容被裁剪框切一刀再弹回来），只有长大时才用 `BackEase { Amplitude = 0.22 }`；时长是 `TemporaryMorphDuration`（280ms，与内容交接同拍）。圆角走 `ApplyTemporaryRadius`：高过 `IslandStyle.MessageCardHeight`（44）就按卡片取圆角，两行的高卡再用 `height/2` 会圆成跑道形。
- **消息卡（`IslandMessage`）由 `MessageView` 建**：图标芯片（28：给了 `AccentColor` 用它，没给用与空闲点同色的中性深灰 —— 宿主自己的消息不会平白冒出一个蓝色圆点）+ 标题（13 SemiBold）+ 可选正文（11.5，六成白，最多两行）。宽度跟随内容（152..340）、高度跟随行数（40..72），尺寸写成视图自己的 Width/Height 钉住（尺寸 morph 期间正文不重排），最大尺寸 `MessageView.MaxSize` 参与 `ComputeCanvasFootprint` 的画布预留。**正文曾经根本没被渲染**，调用方（App 启动提示、更新提示、Battery、投放反馈、插件）传了也白传。
- **Expanded queue cards are uniform.** The expanded stack is the main island plus up to `MaxExpandedItems - 1` cards. Every element shares one width (`StackWidth` = max expanded width over all live contents) and every card shares one height (`QueueCardHeight` = tallest shown card), so plugins declaring 420×152 and 400×110 still render as one aligned column — the host stretches the card, the plugin view must therefore use star/auto layouts instead of assuming its declared size. Sizes must only flow through `ComputeTotalSize` / `ExpandedMainSize`: `EnsureCanvas` and the `SetWindowRgn` hit shape are derived from them, so a size change that bypasses these drifts the reserved canvas and the click-through shape apart.
- **Every plugin callback must be guarded.** Plugin code reached from the host (`InitializeAsync`, `ShutdownAsync`, settings-page factories, `OnTap`, timer ticks, settings handlers, **and `IMorphView.AnimateToExpanded/Compact`**) is wrapped by `PluginInstance.InvokeGuarded` / `IslandWindow.AnimateMorphView`. An unguarded plugin exception surfaces as a WinUI stowed exception (0xc000027b) and **kills the process** — the hover-expand crash of 2026-09-20 was exactly this. Add a guard at any new call site.
- **Everything a plugin registers is revoked on disable/unload** via `PluginScope` (settings pages, live content, temp messages, timers, subscriptions). A plugin that cleans up badly still leaves zero residue; a plugin calling `IPluginContext` APIs while not `Active` is ignored and logged.
- **Provider assembly resolution**: `WinIsland.Core`, `System.*`/`Windows.*`/`WinRT.*`, and any assembly whose file exists in the app directory always bind to the **host**; every other dependency resolves from the **plugin directory** (via `AssemblyDependencyResolver` + the plugin's `deps.json`). Never widen this to "all `Microsoft.*`" — that breaks plugins shipping their own `Microsoft.*` dependencies.
- **Never delete files of a loaded plugin.** Updates/uninstalls rename the old directory aside and delete best-effort; leftovers are swept at next startup. `PluginInstance.UnloadAssemblyCore` verifies ALC collection with a `WeakReference` and logs the outcome.
- **All host-side API methods are thread-safe** (`IslandService.RunOnUI` marshals to the UI thread). UIElements must still be created on the UI thread; `IPluginContext.CreateTimer` requires the UI thread.
- **Settings keys**: plugin settings are auto-prefixed with `<pluginId>.` (a plugin cannot touch host keys); host/island keys use `island.*`; per-plugin disable flag is `plugin.<pluginId>.disabled`; per-plugin island order is `plugin.<pluginId>.priority` (host-level override of the plugin's declared `IslandLiveContent.Priority`, editable in 设置 → 插件管理, applied live by `IslandWindow.ApplyPriorityChange` — ties keep registration order). 开机自启动是 `island.autostart`（`off` / `user` / `admin`，见 `Core/AutoStartService.cs`：普通权限写 HKCU 的 Run 键，管理员权限建一个 `/RL HIGHEST` 的登录计划任务，增删任务需要一次 UAC）。文件投放总开关是 `island.dropEnabled`（默认 `true`，见「文件投放」那条规则）。
- **WinIsland.Core is the SDK** — only interfaces, attributes, and data types. No implementation. Plugin developers reference this DLL only (from NuGet: `luolan.winland.Core`, or a source `ProjectReference`). `luolan.winland.Core` version tracks the API version (2.3.0); incremental APIs keep `api_version = 2` and are gated by the plugin's `min_host_version` (spotlight = 2.1.0, file drop targets = 2.2.0, `IIslandTheme` / `IPluginContext.Theme` = 2.3.0).
  **Packing gotcha**: `dotnet pack -c Release` has silently packed a *stale* Release build output before (it shipped a 2.0-era DLL as 2.2.0 — the 2.2.0 on nuget.org is broken). Always `dotnet build -c Release --no-incremental` first, then `dotnet pack -c Release --no-build`, and verify the DLL inside the nupkg before uploading.
- **The marketplace is index.json-first.** 设置 → 插件市场 (`Core/Marketplace` + `Settings/MarketplacePage`) gets the *entire* list — metadata **and** base64 logos — from one request to `luolangaga/WinLandPlugin`'s root `index.json`, cached with ETag + 6h TTL and remembered per source. Never add a per-plugin request to the list path; the README is fetched only when the detail dialog opens and the `.lwp` only on install (size + SHA-256 verified before `PluginHost.InstallAsync`). Source order: GitHub raw (the single source of truth — the repo's Action generates the index there) → GitCode mirror (`api.gitcode.com/api/v5/repos/luolangaga/WinLandPlugin/raw`) → jsDelivr. `marketplace.baseUrl` overrides the list (a local directory also works, for offline testing).
- **Self-update is GitCode-first and notification-only.** `UpdateService` asks GitCode's release API first and falls back to GitHub when GitCode fails, has no release, or reports a version older than the running one (the newer of the two wins). It never downloads, installs, or restarts anything: 「设置 → 关于」(`AboutSettingsPage`) renders the release notes and opens the release page in the browser — 下载安装交给用户. Release identity always comes from the tag (`v<semver>`); prereleases (`prerelease` / GitCode `release_status=pre`) and drafts are never announced. The version shown is `FileVersionInfo` of `Environment.ProcessPath` (CI injects it via `WINISLAND_VERSION`; local builds stay at 1.0.0.0 and display as 开发版). Host-owned keys: `update.autoCheck` (≤1 check per 6h after startup), `update.lastCheck`, `update.skippedVersion`; `update.repo` (`owner/repo`) is a dev-only override. `.github/workflows/release.yml` mirrors the installers onto the GitCode release when a `GITCODE_ACCESS_TOKEN` secret exists — that step must stay fail-soft, GitHub publishing is the source of truth. The mirroring itself lives in `.github/workflows/sync-to-gitcode.yml` (a `workflow_call` + `workflow_dispatch` reusable workflow, same mechanics as `luolangaga/tubatool`: `api.atomgit.com` + `access_token` + the official proxy IP in `/etc/hosts`), so it can be re-run by hand for one tag. **A job that calls a reusable workflow may only set name/uses/with/secrets/strategy/needs/if/concurrency/permissions — `continue-on-error` and `steps` are not allowed there**, which is why the reusable workflow soft-fails internally (warnings + skip) instead.
- **Taskbar embedding (`island.position = bottom`).** The island docks into the *real* taskbar strip, not the screen edge: the strip rect comes from `Win32.GetTaskbarStrip` (docked rect via `SHAppBarMessage(ABM_GETTASKBARPOS)`, which also reports the docked position while auto-hidden; visibility is a real overlap test because a hidden taskbar keeps a ~2px sliver on screen). Horizontal placement uses the taskbar's *measured* free bands (`TaskbarLayout`, UI Automation, background thread, cached + throttled, empty ⇒ fall back to strip-edge alignment) — never assume the left edge is empty, it is only empty when the taskbar's icon cluster is centered. The free bands affect **position only, never size**: the island's size stays exactly what the active plugin declares (`CompactSize`/`ExpandedSize`) in every position mode (an earlier revision narrowed the compact width to fit the band, which made the island shrink inside the taskbar and grow outside it — do not reintroduce that). The island also follows the taskbar's visibility (120ms poll) and re-asserts topmost whenever *any visible window that overlaps the island's own rect* is above it (`Win32.EnsureTopmost` + `IslandScreenRect`) — the shell (taskbar, Start menu, search flyout) raises itself above topmost windows at will, so "is the taskbar above me" is not a sufficient test. **The topmost self-heal is not bottom-mode-specific**: `IslandWindow.WatchdogTick` (taskbar follow *and* heal) and `Win32.InstallForegroundWatcher` (heal on foreground change) run in **every** position mode, because after startup `HealTopmost` is the only thing that ever re-asserts the band — `WS_EX_TOPMOST` is never cleared by anyone, the band is simply decided by the window that asserted last (an earlier revision gated both on `_bottomAnchored`, which left `island.position = top` covered until restart; do not reintroduce that gate). Not winnable by design: exclusive-fullscreen / fullscreen-optimized apps, the UAC secure desktop and the lock screen composite outside the topmost band — don't pile on more re-asserts for those. **Never move the window while the expansion animates**: a re-placement that arrives mid-hover is applied on the next poll tick after collapse. Placement width must come from `PlacementWidth()` (actual ∪ the content's compact width), not `ActualWidth` alone — right after `SetLive` the width animation has not run yet, and the window will not move again once it has.

## Adding a Plugin

### Built-in plugin
1. Create `Modules/<Name>/<Name>Plugin.cs` extending `IslandPluginBase` (see `MediaPlugin` for a full example).
2. Register it in `App.xaml.cs`: `_plugins.RegisterBuiltIn(manifest, () => new YourPlugin());` — the manifest carries id/name/icon/description.
3. Host-owned chrome (e.g. the general island settings page) is registered directly via `_service.AddSettingsPage(...)`, not as a plugin.

### External plugin
1. Create a class library + `plugin.json` (`samples/HardwareMonitor` is the reference, `samples/HelloPlugin` the minimal one).
2. Class: `public sealed class X : IslandPluginBase`; any dependencies must be copied into the plugin folder (`CopyLocalLockFileAssemblies=true`, see PLUGIN.md §6).
3. Build (the sample csproj copies to `plugins/<id>/`), or pack with `pwsh tools/pack-plugin.ps1 -ProjectDir <dir>` and install via the plugin manager / by dropping the `.lwp` into `plugins/`.
4. Full API reference and pitfalls: `PLUGIN.md`.

## Animation Constants

- Expand: 333ms, Collapse: 250ms
- Easing: `BackEase { EasingMode = EaseOut, Amplitude = 0.45 }`
- Temporary content (message / `Island.Show`): 280ms via `SizeEasing` — `CubicEase` when shrinking (never past the target), `BackEase { Amplitude = 0.22 }` when growing
- Content exchange (`SwapContent`): outgoing 130ms, incoming starts at 80ms and runs 200ms (`CubicEase`); total 280ms
- Idle size: 128×34, default compact: 230×40, default expanded: 420×150
- Message card (`MessageView`): width 152..340 (hugs its content), height 40..72; chip 28, title 13 SemiBold, body 11.5 at 60% white

## Win32 Transparency Stack

Achieving a truly transparent borderless window requires a specific sequence:
1. `TransparentBackdrop` (zero-alpha CompositionColorBrush as SystemBackdrop)
2. `Win32.EnableWindowAlpha` (DwmEnableBlurBehindWindow with empty region)
3. `Win32.MakeIslandStyle` (WS_POPUP + WS_EX_TOOLWINDOW|NOACTIVATE|TOPMOST)
4. `Win32.InstallStyleGuard` (subclass to block WM_STYLECHANGING from re-adding frame styles)
5. `Win32.RemoveDwmBorder` after every `AppWindow.MoveAndResize` (DWM resets attributes on resize)

Do not simplify or reorder these steps — each layer prevents a specific white-border regression.

## Window Geometry Invariants (do not break)

The window is a big transparent canvas with the island anchored to the configured edge (`island.position`: `top` = work-area top, `bottom` = docked inside the taskbar strip; `island.horizontal`: center/left/right). Four rules keep it free of "ghost copy" artifacts and keep the transparent area clickable-through:

1. **The canvas is reserved, not animated.** `IslandWindow.EnsureCanvas` keeps a grow-only canvas: the width is fixed at `max(420, content widths, 280) + 2*Pad`, the height is reserved for the current live set's expanded footprint (main island + queue). It only resizes when the live content set changes.
2. **Never resize the HWND inside a hover expand/collapse.** `AnimateIslandSize` must not call `AppWindow.MoveAndResize`. Changing the client width while content is animating makes DWM composite the previous frame (still at old client coordinates) into the new window rect → the island shows an offset copy for one frame (left on expand, right on collapse, offset = Δw/2). Height-only changes are harmless — the island is edge-anchored (`IslandTopInWindow`, `IslandStack.VerticalAlignment`), so the window's anchor edge never moves with height: top mode grows downward, bottom mode grows upward from the fixed window bottom.
3. **Click-through must use the window *shape* (`SetWindowRgn`), not `WM_NCHITTEST`.** `UpdateHitRegion` feeds the island's real layout rects to `Win32.ApplyWindowShape`. Pixels outside the shape are not part of the window at all, so clicks/touches reach windows below — including other processes. `WM_NCHITTEST` returning `HTTRANSPARENT` only ever passes through to windows **in the same thread**, so it is nothing but a fallback for same-thread overlaps; relying on it silently swallows every click in the transparent area (that was the pre-existing bug — the small dynamic window just hid it).
4. **The shape must be *tight* (the island's currently rendered size) and *rounded* (matching `CornerRadius`).**
   - Tight: never use `max(actual, animationTarget)`. The window surface paints any part of the shape that the XAML content does not cover with opaque white, so a shape larger than the rendered island flashes a white band for one frame on every expand (`CreateRectRgn`-style "safety" unions are exactly what caused it).
   - Rounded: `BuildRegion` uses `CreateRoundRectRgn` with `2 * CornerRadius` as the ellipse size, per island and per queue card. A plain rectangle leaves the corner wedges exposed → white blocks at the island's corners.
5. **The shape must never be empty or missing.** An unset region means the *entire* transparent canvas is part of the window and swallows input, so `Win32.SetHitRegion` ignores null handles and `UpdateHitRegion` never applies an empty region; the shape is re-asserted after canvas resizes and after `Win32.MakeIslandStyle` (SWP_FRAMECHANGED can drop it).

## No Tests / No CI

No test project, no lint/typecheck commands exist. `.github/workflows/release.yml` only builds and publishes
installers on `v*` tags (GitHub Release first, GitCode mirror when a `GITCODE_TOKEN` secret is set) — nothing runs
per commit. Verify by building and running the app.
