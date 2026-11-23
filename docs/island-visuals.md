# Island contour and motion

PR #12 identifies a visible top-edge seam in Fluent light mode. Painting a separate
negative-margin line hides only one part of the seam and cannot follow rounded
corners. The host now masks the system material itself to the rendered main card,
queue cards, pager and activity badge. The material mask uses a 2x alpha surface
with linear downsampling; the native input region retains transparent slack for
antialiasing. Bounds come from `TransformToVisual` after layout, including fractional
DPI, user scaling and bottom anchoring. No external top-edge line is needed.

Controller changes rebuild the mask on the supplied brush's compositor. Composition
objects from Acrylic and Mica controllers must not be mixed. Older systems without
material support continue to use the solid fallback. Material controllers retain
their active-window and system-theme policy.

The main body uses direct native XAML width/height timelines and a matching radius timeline.
Interrupted morphs start from the displayed geometry. Fluent size motion is
monotonic; Apple retains a smaller optional overshoot. Content handoff uses a
100ms exit and a 190ms entrance delayed by 55ms, with 3/5 DIP travel and 0.985
entrance scale. Queue cards and their pager appear immediately at full size and
opacity. There is no queue entrance animation; plugin views are placed in their
expanded state immediately. Tail paging retains its separate content transition.
Fluent spotlight entry no longer adds perspective tilt. System reduced
animation preferences still apply through `Motion.Duration`.

During geometry motion the material uses the native window contour directly,
avoiding a separate offscreen-mask rasterization for every layout frame. The alpha
mask and its transparent slack return 40ms after geometry stops. This avoids a
second material silhouette lagging behind XAML; moving edges use the native binary
region during this short interval, and stationary edges retain the antialiased mask.
The fine XAML stroke/highlight is suppressed while material geometry moves, so an
old stroke cannot be presented inside the next native contour as a second outline.

## Verification

```powershell
dotnet build WinIsland/WinIsland.csproj
dotnet run --project tests/WinIsland.Regression/WinIsland.Regression.csproj
dotnet run --project WinIsland/WinIsland.csproj --no-build --no-launch-profile -- --motion-preview --edge-checks
```

The Debug-only edge diagnostic uses isolated preview settings and writes
`edge-checks.json` beside the executable, then exits. It exercises 72 settled states:
three palettes, two materials, two anchor positions, three scale factors and
expanded/compact states. Radius settings also cover 0%, 100% and 200%. Checks cover
transition completion, radius targets, composition resource ownership and opaque
queue surfaces. Runtime assertions also check immediate full-height queue placement and
four rapid expand/collapse reversals.
The `tools/Test-IslandTransitions.ps1 -AppPid <preview-pid>` script records a 30fps
transition sequence and JPEG frame evidence from the isolated preview window.
Visual quality is checked in the interactive `--motion-preview` using its edge controls.
These checks do not
measure frame pacing or replace testing on other GPUs and monitor DPI settings.
