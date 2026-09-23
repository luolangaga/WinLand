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
WinIsland.Core/              — SDK DLL (luolan.winland.Core 2.0.0, distribute to plugin developers)
  IslandApi.cs               — ISettingsStore / IMorphView / IslandLiveContent / IslandMessage / SettingsPageDescriptor
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
    IslandWindow.xaml.cs     — island window: state machine, size animation, hover, hit region, style switch, AnimateMorphView guard, position (top / bottom taskbar strip) + horizontal placement/offset, free-band placement, taskbar-follow poll
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
- **MorphView mode preferred.** Single view with `AnimateToExpanded`/`AnimateToCompact` for element-level morph. Dual-view (CompactContent + ExpandedContent) is the fallback.
- **Expanded queue cards are uniform.** The expanded stack is the main island plus up to `MaxExpandedItems - 1` cards. Every element shares one width (`StackWidth` = max expanded width over all live contents) and every card shares one height (`QueueCardHeight` = tallest shown card), so plugins declaring 420×152 and 400×110 still render as one aligned column — the host stretches the card, the plugin view must therefore use star/auto layouts instead of assuming its declared size. Sizes must only flow through `ComputeTotalSize` / `ExpandedMainSize`: `EnsureCanvas` and the `SetWindowRgn` hit shape are derived from them, so a size change that bypasses these drifts the reserved canvas and the click-through shape apart.
- **Every plugin callback must be guarded.** Plugin code reached from the host (`InitializeAsync`, `ShutdownAsync`, settings-page factories, `OnTap`, timer ticks, settings handlers, **and `IMorphView.AnimateToExpanded/Compact`**) is wrapped by `PluginInstance.InvokeGuarded` / `IslandWindow.AnimateMorphView`. An unguarded plugin exception surfaces as a WinUI stowed exception (0xc000027b) and **kills the process** — the hover-expand crash of 2026-09-20 was exactly this. Add a guard at any new call site.
- **Everything a plugin registers is revoked on disable/unload** via `PluginScope` (settings pages, live content, temp messages, timers, subscriptions). A plugin that cleans up badly still leaves zero residue; a plugin calling `IPluginContext` APIs while not `Active` is ignored and logged.
- **Provider assembly resolution**: `WinIsland.Core`, `System.*`/`Windows.*`/`WinRT.*`, and any assembly whose file exists in the app directory always bind to the **host**; every other dependency resolves from the **plugin directory** (via `AssemblyDependencyResolver` + the plugin's `deps.json`). Never widen this to "all `Microsoft.*`" — that breaks plugins shipping their own `Microsoft.*` dependencies.
- **Never delete files of a loaded plugin.** Updates/uninstalls rename the old directory aside and delete best-effort; leftovers are swept at next startup. `PluginInstance.UnloadAssemblyCore` verifies ALC collection with a `WeakReference` and logs the outcome.
- **All host-side API methods are thread-safe** (`IslandService.RunOnUI` marshals to the UI thread). UIElements must still be created on the UI thread; `IPluginContext.CreateTimer` requires the UI thread.
- **Settings keys**: plugin settings are auto-prefixed with `<pluginId>.` (a plugin cannot touch host keys); host/island keys use `island.*`; per-plugin disable flag is `plugin.<pluginId>.disabled`; per-plugin island order is `plugin.<pluginId>.priority` (host-level override of the plugin's declared `IslandLiveContent.Priority`, editable in 设置 → 插件管理, applied live by `IslandWindow.ApplyPriorityChange` — ties keep registration order). 开机自启动是 `island.autostart`（`off` / `user` / `admin`，见 `Core/AutoStartService.cs`：普通权限写 HKCU 的 Run 键，管理员权限建一个 `/RL HIGHEST` 的登录计划任务，增删任务需要一次 UAC）。
- **WinIsland.Core is the SDK** — only interfaces, attributes, and data types. No implementation. Plugin developers reference this DLL only. `luolan.winland.Core` version tracks the API version (2.0.0).
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
- Idle size: 128×34, default compact: 230×40, default expanded: 420×150

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
