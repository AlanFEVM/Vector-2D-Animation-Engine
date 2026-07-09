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
- Fixed unit model: 1 px = 25 vector units, and 1 stroke point = 0.5 px.
- Adobe Animate-style stage/tools/layers/timeline.
- Dense production-workbench layout inspired by Blender.

## Current Features

- Workspace tabs: `Basic Drawing`, `Scene Edit`, `Animation`, `Materials`, and `Hierarchy`.
- Custom borderless desktop window shell with draggable title area, resizable edges, and in-app minimize/maximize/close controls.
- Basic Drawing has secondary Flash-style drawing object tabs that can be dragged into Vault for reuse.
- Vector-unit coordinate model with quantized geometry edits and point-based stroke authoring.
- Default project view shows a 4000 vector-unit wide work range; `Fit Stage` still frames the full stage.
- `Basic Drawing` hides the bottom timeline to prioritize drawing space.
- Modern timeline strip with ruler, playhead dragging, exposure bars, and keyframe placeholders.
- Playback settings panel with FPS, loop playback, and frame range controls.
- Bottom status bar showing render FPS, animation FPS, and zoom.
- Main loop targets 300 UPS with stage redraw requests capped at 144 FPS.
- Drawing tools for rectangle, ellipse, triangle, polygon, star, line, fill, select, and pan.
- SVG vector icon buttons for the tool rail and snapping controls.
- Global view navigation: middle mouse drags the canvas, and Ctrl + middle mouse drag zooms the canvas from any tool.
- Drawing preview overlay while dragging shape and line tools.
- Animated tool-name hints when hovering drawing tools.
- Selection highlight with boundary handles for filled shapes and partial edge highlights for selected boundary-stroke segments.
- Marquee selection for selecting, highlighting, moving, and deleting multiple objects, with `1 vu`-aligned fill and stroke part materialization for boxed regions.
- Endpoint and Bezier control handle editing for selected lines, including linked endpoint movement for connected line segments.
- Basic edit shortcuts: Ctrl+Z undo, Ctrl+C copy selected objects, and Ctrl+V paste copied objects with a small stage offset.
- Drawing element topology foundation: fills, free strokes, and filled-shape boundary strokes are separate selectable elements. Rectangle edges are individually selectable by default; crossing strokes and boundaries split each other at valid intersections, and sub-1vu stroke fragments are suppressed.
- Topology parts are materialized into editable objects: split stroke parts become independent line objects, boundary-stroke parts become movable line segments, and a rectangle fill cut by a through-line becomes two movable path fills when its region is marquee-selected or moved.
- Same-color fill regions can merge automatically after drawing or recoloring when their distance is under 10 vector units, using a stable orthogonal merged outline.
- Stroke splitting uses a unified quadratic-curve model, so curve-curve and line-curve intersections share the same topology path; straight lines are treated as quadratic curves with a midpoint control handle.
- Delete key removes the currently selected drawing object or marquee-selected objects.
- Right-side auxiliary panel next to the Inspector for layers.
- Left-edge Vault drawer opened from the tool rail for Library/Vault asset storage.
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

## Runtime Logs

Runtime logs are written to `logs/` in the project root when launched through the
root EXE. Native app logs use `native-yyyyMMdd-HHmmss-pidN.log`; launcher startup
events use `launcher.log`. Unhandled UI thread exceptions, domain crashes,
Direct2D fallback failures, hot reload form rebuilds, and stress-scene generation
events are recorded there.

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
    AppLog.cs             Runtime logging and crash diagnostics.
    ToolMode.cs           Shared tool enum.
    Benchmark.cs          Stress-scene benchmark helper.
  Engine/
    VectorScene.cs        Packed scene arrays, stress generator, spatial index.
    VectorSceneSnapshot.cs
                          Undo snapshot data for scene-level edit history.
    DrawingElementTopology.cs
                          Fill/stroke element identity and topology hit contracts.
    DrawingObjectDefinition.cs
                          Flash-style reusable drawing object metadata.
    VectorUnits.cs        Vector unit, pixel, and stroke point conversion rules.
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
    SceneEditorPanel.cs   Scene and active drawing object context panel.
    TimelineStrip.cs      Timeline renderer.
    Theme.cs              Shared desktop colors and control styling.
    SvgIcons.cs           Code-native SVG primitive icon set.
    SvgIconButton.cs      SVG icon button and toggle controls.
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
