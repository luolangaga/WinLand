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
    SettingsService.cs       — JSON key-value store at %LocalAppData%\WinIsland\settings.json
    TransparentBackdrop.cs   — Fully transparent window backdrop (Composition + DWM alpha)
    TrayIcon.cs              — Native Shell_NotifyIcon tray icon with Win32 popup menu
    Win32.cs                 — All P/Invoke: window styles, DWM, subclassing for border removal
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
    IslandWindow.xaml.cs     — island window: state machine, size animation, hover, hit region, AnimateMorphView guard
  Modules/
    Media/ Battery/ AiMonitor/ Messaging/ — built-in plugins (IslandPluginBase; complete shutdown, no leaks)
  Settings/
    SettingsWindow.xaml.cs   — settings shell; nav pages registered dynamically (ordered by SettingsPageDescriptor.Order)
    GeneralSettingsPage.xaml.cs — host-owned general page (island.* settings)
    PluginManagerPage.xaml   — plugin manager: install .lwp, enable/disable, reload, delete, logs, error details
```

## Key Design Rules

- **Host owns chrome; plugins own content.** IslandWindow controls size, corner radius, hover, expand/collapse, temp message overlay. Plugins only provide XAML content via `IslandLiveContent`.
- **MorphView mode preferred.** Single view with `AnimateToExpanded`/`AnimateToCompact` for element-level morph. Dual-view (CompactContent + ExpandedContent) is the fallback.
- **Every plugin callback must be guarded.** Plugin code reached from the host (`InitializeAsync`, `ShutdownAsync`, settings-page factories, `OnTap`, timer ticks, settings handlers, **and `IMorphView.AnimateToExpanded/Compact`**) is wrapped by `PluginInstance.InvokeGuarded` / `IslandWindow.AnimateMorphView`. An unguarded plugin exception surfaces as a WinUI stowed exception (0xc000027b) and **kills the process** — the hover-expand crash of 2026-09-20 was exactly this. Add a guard at any new call site.
- **Everything a plugin registers is revoked on disable/unload** via `PluginScope` (settings pages, live content, temp messages, timers, subscriptions). A plugin that cleans up badly still leaves zero residue; a plugin calling `IPluginContext` APIs while not `Active` is ignored and logged.
- **Provider assembly resolution**: `WinIsland.Core`, `System.*`/`Windows.*`/`WinRT.*`, and any assembly whose file exists in the app directory always bind to the **host**; every other dependency resolves from the **plugin directory** (via `AssemblyDependencyResolver` + the plugin's `deps.json`). Never widen this to "all `Microsoft.*`" — that breaks plugins shipping their own `Microsoft.*` dependencies.
- **Never delete files of a loaded plugin.** Updates/uninstalls rename the old directory aside and delete best-effort; leftovers are swept at next startup. `PluginInstance.UnloadAssemblyCore` verifies ALC collection with a `WeakReference` and logs the outcome.
- **All host-side API methods are thread-safe** (`IslandService.RunOnUI` marshals to the UI thread). UIElements must still be created on the UI thread; `IPluginContext.CreateTimer` requires the UI thread.
- **Settings keys**: plugin settings are auto-prefixed with `<pluginId>.` (a plugin cannot touch host keys); host/island keys use `island.*`; per-plugin disable flag is `plugin.<pluginId>.disabled`.
- **WinIsland.Core is the SDK** — only interfaces, attributes, and data types. No implementation. Plugin developers reference this DLL only. `luolan.winland.Core` version tracks the API version (2.0.0).

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

The window is a big transparent canvas with the island centered at the top. Four rules keep it free of "ghost copy" artifacts and keep the transparent area clickable-through:

1. **The canvas is reserved, not animated.** `IslandWindow.EnsureCanvas` keeps a grow-only canvas: the width is fixed at `max(420, content widths, 280) + 2*Pad`, the height is reserved for the current live set's expanded footprint (main island + queue). It only resizes when the live content set changes.
2. **Never resize the HWND inside a hover expand/collapse.** `AnimateIslandSize` must not call `AppWindow.MoveAndResize`. Changing the client width while content is animating makes DWM composite the previous frame (still at old client coordinates) into the new window rect → the island shows an offset copy for one frame (left on expand, right on collapse, offset = Δw/2). Height-only changes are harmless — the island is top-anchored and the window's y never changes with height.
3. **Click-through must use the window *shape* (`SetWindowRgn`), not `WM_NCHITTEST`.** `UpdateHitRegion` feeds the island's real layout rects to `Win32.ApplyWindowShape`. Pixels outside the shape are not part of the window at all, so clicks/touches reach windows below — including other processes. `WM_NCHITTEST` returning `HTTRANSPARENT` only ever passes through to windows **in the same thread**, so it is nothing but a fallback for same-thread overlaps; relying on it silently swallows every click in the transparent area (that was the pre-existing bug — the small dynamic window just hid it).
4. **The shape must be *tight* (the island's currently rendered size) and *rounded* (matching `CornerRadius`).**
   - Tight: never use `max(actual, animationTarget)`. The window surface paints any part of the shape that the XAML content does not cover with opaque white, so a shape larger than the rendered island flashes a white band for one frame on every expand (`CreateRectRgn`-style "safety" unions are exactly what caused it).
   - Rounded: `BuildRegion` uses `CreateRoundRectRgn` with `2 * CornerRadius` as the ellipse size, per island and per queue card. A plain rectangle leaves the corner wedges exposed → white blocks at the island's corners.
5. **The shape must never be empty or missing.** An unset region means the *entire* transparent canvas is part of the window and swallows input, so `Win32.SetHitRegion` ignores null handles and `UpdateHitRegion` never applies an empty region; the shape is re-asserted after canvas resizes and after `Win32.MakeIslandStyle` (SWP_FRAMECHANGED can drop it).

## No Tests / No CI

No test project, no CI workflows, no lint/typecheck commands exist. Verify by building and running the app.
