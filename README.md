# Vector 2D Animation Engine

Native Windows desktop prototype for a large-scale 2D vector animation engine.

## Run

Double-click:

```text
VectorAnimationEngine.exe
```

During development, the root EXE is a lightweight launcher. It starts the source
project under `native/` through `dotnet watch run`, so normal code changes can be
hot reloaded without republishing the self-contained app. You only need to rebuild
the launcher when the launcher itself changes.

This means the root `VectorAnimationEngine.exe` is intentionally small. It is the
stable entry point for fast iteration, not the final distributable package.

The app opens with an empty project and one visible layer. The stress scene is not
generated on startup; use `Run Stress Scene` when you explicitly want to benchmark.

The native app includes a development hot-reload hook. When .NET Hot Reload applies
source changes, the main workbench window is rebuilt automatically so UI layout and
control changes can be inspected quickly. Launch with `VectorAnimationEngine.exe
--no-hot-reload` only when you need the older plain `dotnet run` behavior.

## Current Target Workload

- 1000+ layers.
- 100000+ draw objects.
- 100000000+ virtual vector primitives tracked as object complexity metadata.
- Adobe Animate-style stage/tools/layers/timeline.
- Dense production-workbench layout inspired by Blender.

## Current Features

- Workspace tabs: `Basic Drawing`, `Animation`, `Materials`, and `Hierarchy`.
- `Basic Drawing` hides the bottom timeline to prioritize drawing space.
- Modern timeline strip with ruler, playhead dragging, exposure bars, and keyframe placeholders.
- Playback settings panel with FPS, loop playback, and frame range controls.
- Bottom status bar showing render FPS, animation FPS, and zoom.
- Drawing tools for rectangle, ellipse, triangle, polygon, star, line, fill, select, and pan.
- Global view navigation: middle mouse drags the canvas, and Ctrl + middle mouse drag zooms the canvas from any tool.
- Drawing preview overlay while dragging shape and line tools.
- Animated tool-name hints when hovering drawing tools.
- Selection highlight with boundary handles for filled shapes.
- Bezier control handle editing for selected lines.
- Delete key removes the currently selected drawing object.
- Right-side auxiliary panel next to the Inspector for layers plus Library/Vault asset storage.
- Vault can persist object snapshots, notes, file references, and library presets to `data/vault.json`.
- Draw settings panel for snap, grid snap, object snap placeholder, tight fit, align, angle snap, grid size, and aspect ratio.
- Material editor panel for fill, stroke color, stroke width, and opacity.
- Hierarchy panel showing scene, layers, and object placeholders/tree entries.
- Shape-aware scene model via `ShapeKind`.

## User Guide

See the operation tutorial in:

```text
docs/USER_GUIDE.md
```

Project rule: when a new user-facing feature is added or an interaction changes,
update the user guide in the same change.

## Git Workflow

This project is expected to keep a local git history. Each completed feature or
bug fix should be committed with a concise message so changes can be rolled back
or investigated later.

## Performance Model

The manual stress scene avoids scanning every object every frame.

- Stage rendering now prefers a Direct2D HWND render target through Vortice bindings.
- The desktop shell remains WinForms; WPF is not used.
- If Direct2D initialization fails on a machine, the stage falls back to the legacy GDI path instead of preventing startup.
- Low zoom uses tile LOD summaries.
- Object zoom uses a fixed spatial index to visit only visible cells.
- Scene data is stored in packed arrays instead of per-object managed models.
- Layer list and timeline are UI-virtualized to keep the workbench responsive.

The current Direct2D renderer still shares the packed scene arrays and spatial
index with the original GDI path. The next renderer-level step is moving large
object batches into GPU-side geometry buffers and tile caches.

## Source Layout

```text
native/
  App/
    Program.cs            Application entry point.
    ToolMode.cs           Shared tool enum.
    Benchmark.cs          Stress-scene benchmark helper.
  Engine/
    VectorScene.cs        Packed scene arrays, stress generator, spatial index.
    RenderStats.cs        Renderer telemetry contract.
    CompactFormat.cs      Human-readable metric formatting.
    DrawSettings.cs       Snap, align, grid and shape drawing settings.
    ShapeKind.cs          Basic shape kind enum.
  Rendering/
    StageControl.cs       Stage viewport renderer and camera.
    Direct2DStageRenderer.cs
                          Direct2D HWND renderer with GDI fallback support.
  UI/
    MainForm.cs           Desktop workbench shell and interaction logic.
    LibraryVaultPanel.cs  Library presets and persistent generic Vault.
    TimelineStrip.cs      Timeline renderer.
    Theme.cs              Shared desktop colors and control styling.
    PlaybackSettingsPanel.cs
    DrawSettingsPanel.cs
    WorkspaceTabs.cs
    HierarchyPanel.cs
    MaterialEditorPanel.cs
launcher/
  Program.cs              Lightweight root EXE launcher for development.
```

## Build For Development

```powershell
dotnet build native\VectorAnimationEngine.Native.csproj -c Release
```

## Build Launcher Entry

```powershell
dotnet publish launcher\VectorAnimationEngine.Launcher.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish-launcher
Move-Item publish-launcher\VectorAnimationEngine.exe .\VectorAnimationEngine.exe -Force
Remove-Item publish-launcher -Recurse -Force
```

## Publish Standalone Release EXE

Use this only when creating a distributable build:

```powershell
dotnet publish native\VectorAnimationEngine.Native.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:EnableCompressionInSingleFile=true -o publish-native
Move-Item publish-native\VectorAnimationEngine.exe .\VectorAnimationEngine.exe -Force
Remove-Item publish-native -Recurse -Force
```

## Next Engineering Steps

- Move rendering from GDI to Direct2D/D3D.
- Add tile cache invalidation for edits instead of full spatial-index rebuilds.
- Add command-log undo/redo.
- Add binary scene serialization.
- Add true vector path tessellation for fills and strokes.
