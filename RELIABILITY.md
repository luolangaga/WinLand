# Reliability changes and regression checks

The plugin boundary now checks the calling activation scope when work actually runs on the UI thread. Revoked contexts cannot register content, pages, drop targets or settings, even after the same plugin is enabled again. UI callbacks and each settings subscriber are guarded independently. A late async exception from an older activation is logged without changing the current activation's error state.

Failed initialization revokes registrations immediately and calls ShutdownAsync after the initialization task finishes. A pending initialization cleanup or timed-out shutdown blocks another activation of the same instance. ALC unloading requests collection and observes it asynchronously without forcing full-process GC on the UI thread. In-process code that never returns cannot be forcibly cancelled; its host registrations remain revoked.

Plugin mutations are serialized. Installation retains the old directory until the new directory has moved into place, restores it if commit fails, and recovers `.rollback-*` directories at startup. An unreadable recovery directory is retained and logged. A failed uninstall stays in the manager and reports failure. External packages cannot replace built-in plugins.

Settings changes notify consumers immediately in memory. Disk writes are coalesced for 150ms, serialized, and use an adjacent temporary file plus atomic replacement. The previous valid version remains in `settings.json.bak`; corrupt input is preserved as `settings.json.corrupt-*`. A failed save retains its dirty revision for the next change or explicit flush. Normal app exit awaits FlushAsync before terminating. Persistence errors are logged.

IslandWindow is divided into State, Placement, Drop, Content, Animation and Surface partial files. QueueLayoutPolicy holds independently testable geometry and tail selection. Content switching and hit-region ownership retain the existing host entry points.

The plugin manager uses a finite ListView viewport and presents prebuilt card UI elements directly while virtualizing item containers. Named settings controls have stable automation IDs; plugin actions, toggles and priority fields have automation IDs and names.

## Run checks

```powershell
./tools/Test-Reliability.ps1
./tools/Test-Reliability.ps1 -Ui
```

The first command runs ten filesystem/settings/scope/queue scenarios and builds the app. `-Ui` also runs seven real WinUI lifecycle and virtualization scenarios with isolated settings and plugin directories. Close a running WinIsland instance before running UI diagnostics; the script does not terminate a user's existing instance. UI diagnostics are Debug-only and exit automatically. The JSON result is written under the app output's `reliability-tests/results.json`.

The GitHub checks workflow builds the app and all example projects and runs the ten core scenarios on pushes and pull requests. Real WinUI checks can be requested via the workflow's manual `ui` input. Existing motion/artwork/effect scripts remain available separately; these changes do not claim a complete visual audit.
