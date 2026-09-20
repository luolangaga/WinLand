# AGENTS.md — WinIsland

## Project

Windows Dynamic Island desktop app (WinUI 3 / Windows App SDK 1.8+). Unpackaged WinExe — no MSIX, no store packaging. Two-project solution: `WinIsland.Core/` (SDK class library) + `WinIsland/` (main app).

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
WinIsland.Core/              — SDK DLL (distribute to plugin developers)
  IslandApi.cs               — All public interfaces (IDynamicIslandApi, IIslandModule, IMorphView, etc.)
  IslandPluginAttribute.cs   — Attribute for plugin discovery and metadata
  IslandPluginBase.cs        — Convenience base class for plugins
  IPluginManifest.cs         — Plugin metadata interface + PluginManifest class
  IPluginContext.cs           — Plugin context and logger interfaces
  PluginInfo.cs              — Plugin runtime state (PluginState enum + PluginInfo class)
  PluginConstants.cs         — Plugin folder name, manifest file name constants

WinIsland/                    — Main application
  App.xaml.cs                — Entry point: creates IslandWindow, IslandService, PluginLoader
  Core/
    IslandService.cs         — IDynamicIslandApi implementation; marshals calls to UI thread
    PluginLoader.cs          — Plugin engine: scan, load DLL, enable/disable, unload
    SettingsService.cs       — JSON key-value store at %LocalAppData%\WinIsland\settings.json
    TransparentBackdrop.cs   — Fully transparent window backdrop (Composition + DWM alpha)
    TrayIcon.cs              — Native Shell_NotifyIcon tray icon with Win32 popup menu
    Win32.cs                 — All P/Invoke: window styles, DWM, subclassing for border removal
  Island/
    IslandWindow.xaml.cs     — The island window: state machine (idle->compact->expanded->temp), size animation, hover detection
  Modules/
    GeneralModule.cs         — Built-in: registers general settings page
    Media/                   — Built-in: GSMTC media session -> MorphView island content
    Messaging/               — Built-in: message-send test module
  Settings/
    SettingsWindow.xaml.cs   — Settings shell; pages are registered dynamically by modules
    PluginManagerPage.xaml   — Plugin manager UI: load/disable/unload plugins
```

## Key Design Rules

- **Host owns chrome; modules own content.** IslandWindow controls size, corner radius, hover, expand/collapse, temp message overlay. Modules only provide XAML content via `IslandLiveContent`.
- **MorphView mode preferred.** Single view with `AnimateToExpanded`/`AnimateToCompact` for element-level morph. Dual-view (CompactContent + ExpandedContent) is the fallback.
- **All `IDynamicIslandApi` methods are thread-safe** — `IslandService.RunOnUI` marshals to UI thread automatically. But UIElements must still be created on the UI thread.
- **Settings keys follow `<moduleId>.<key>` convention** (e.g. `media.enabled`, `island.visible`).
- **Plugin disable keys follow `plugin.<moduleId>.disabled` convention**.
- **WinIsland.Core is the SDK** — only interfaces, attributes, and data types. No implementation. Plugin developers reference this DLL only.

## Adding a Module

### Built-in module
1. Create a class implementing `IIslandModule` (in `Modules/` or a subfolder)
2. Register in `App.xaml.cs` via `_pluginLoader.RegisterBuiltIn(new YourModule())`
3. See `PLUGIN.md` for full API reference

### External plugin (DLL)
1. Create a class library project referencing `WinIsland.Core.dll`
2. Implement `IIslandModule` (or extend `IslandPluginBase`)
3. Optionally mark with `[IslandPlugin("id", "name", ...)]` for metadata
4. Build and place DLL in `plugins/` directory, or load via Plugin Manager UI

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
