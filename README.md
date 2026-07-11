# Vector 2D Animation Engine

Native Windows desktop prototype for a large-scale 2D vector animation engine.

## Run

Double-click:

```text
VectorAnimationEngine.exe
```

During development, the root EXE is a lightweight launcher. It starts the source
project under `native/` through `dotnet watch --no-hot-reload`, so source changes
restart the native process cleanly without republishing the self-contained app.
You only need to rebuild the launcher when the launcher itself changes.

The repository selects the stable .NET 8 SDK line through `global.json` and refuses
preview SDKs. If no compatible .NET 8 SDK is installed, install one or use a
published standalone native build instead.

This means the root `VectorAnimationEngine.exe` is intentionally small. It is the
stable entry point for fast iteration, not the final distributable package.

The app opens with an empty project and one visible layer. The stress scene is not
generated on startup; use `Run Stress Scene` when you explicitly want to benchmark.

Development watching uses full-process restart because metadata hot reload can
corrupt long-running WinForms/Direct2D sessions after structural edits. Launch with
`VectorAnimationEngine.exe --no-hot-reload` when you need a plain `dotnet run`
session without file watching.

## Current Target Workload

- 1000+ layers.
- 100000+ draw objects.
- 100000000+ virtual vector primitives tracked as object complexity metadata.
- Fixed unit model: 1 px = 25 vector units, and 1 stroke point = 0.5 px.
- Adobe Animate-style stage/tools/layers/timeline.
- Dense production-workbench layout inspired by Blender.

## Current Features

- Workspace tabs: `Basic Drawing`, `Scene Edit`, and `Animation`.
- Custom borderless desktop window shell with draggable title area, resizable edges, and in-app minimize/maximize/close controls.
- Basic Drawing is the drawing-object edit mode: the header shows one active drawing object at a time plus `+ Object`, and drawing/material tools focus on that object.
- Scene Edit is the non-drawable assembly mode: scenes store drawing-object instances rather than standalone geometry. Drawing objects may recursively instance other drawing objects, with project-level self/cycle rejection, accumulated 2D transforms, current-frame exposure evaluation, and source provenance retained for double-click editor navigation.
- Drawing-object timelines combine stable drawing-layer IDs and nested-instance IDs in one editable track list, while scene timelines target stable scene-instance IDs. Synchronization preserves surviving track IDs and keyframe data when the target list changes.
- 3D scenes use Blender-style viewport navigation: middle mouse orbits, Shift + middle mouse pans, Ctrl + middle mouse dollies, mouse wheel dollies, Ctrl + wheel changes the view zoom scale, Numpad 1/3/7 switches front/right/top views, Numpad 5 toggles perspective/orthographic, and Home resets the reference view.
- New projects start with one empty drawing object. All shape and line drawing is stored inside the active drawing object.
- Vector-unit coordinate model with quantized geometry edits and point-based stroke authoring.
- Default project view shows a 4000 vector-unit wide work range; `Fit Stage` still frames the full stage.
- Flash-style combined layer/timeline strip with fixed-width frame cells, layer/instance track selection, visibility toggles, Solo/All controls, ruler, playhead dragging, held exposure spans, populated keyframes, blank keyframes, and horizontal/vertical scrolling.
- Timeline tracks use ordered populated/blank keyframes. Empty drawing layers start with a hollow blank key; marker fill follows actual cel content, so deleting the last object turns the key hollow and F6 on empty exposure stays blank.
- Drawing-object keyframes own independent cels. F6 clones the currently held layer content into a new cel, later edits or deletion stay isolated from the source cel, F7 creates a blank cel, and drawing on a blank exposure promotes it to a populated keyframe automatically.
- Drawing inside a held blank exposure populates that exposure's source key rather than silently creating a key at the playhead; use F6 or F7 first when content must begin on a new frame.
- F5 extends the selected exposure while shifting later keys right. Frame removal preserves any cel whose held frames survive the removed range, and shifts key markers plus object ownership together.
- Adobe Animate timeline shortcuts: Enter play/pause, comma/period previous/next frame, Shift+comma/period first/last playback frame, F5 insert frame, Shift+F5 remove frame, F6 copy/insert keyframe, Shift+F6 clear keyframe, and F7 insert blank keyframe.
- Playback settings panel with FPS, loop playback, and frame range controls.
- Bottom status bar showing render FPS, animation FPS, and zoom.
- Playback processes fixed simulation steps in batches at 300 logical UPS. Stage redraw requests remain capped at 144 FPS, and render FPS reports completed stage frames rather than invalidation requests.
- Drawing tools for pencil, brush, line, fill, select, pan, and a grouped shape tool with rectangle, ellipse, triangle, polygon, and star flyout choices.
- SVG vector icon buttons for the floating tool palette and the Basic Drawing header snapping strip.
- Global view navigation: middle mouse drags the 2D canvas, and Ctrl + middle mouse drag zooms the 2D canvas from any tool.
- Drawing preview overlay while dragging shape, line, pencil, and brush tools.
- Flash-style freehand drawing: Pencil creates an open stroke with the stroke swatch, while Brush creates a closed filled outline with the fill swatch; each tool remembers its own width, and Draw Settings exposes smoothing control.
- Freehand input is sampled in screen space and simplified on pointer-up. Pencil commits one selectable open stroke per gesture; Brush expands the sampled centerline into a closed Path fill and automatically merges it with intersecting or nearby fills on the same layer that have the same ARGB color. Nearby fill boundaries merge when their distance is under `10 vu`.
- Flash-style tool shortcuts include V/H/N/Y/B/K for Select/Pan/Line/Pencil/Brush/Fill; bracket keys adjust Pencil and Brush width.
- Animated tool-name hints when hovering drawing tools.
- Selection highlight with boundary handles for filled shapes and partial edge highlights for selected boundary-stroke segments.
- Marquee selection for selecting, highlighting, moving, and deleting multiple objects, with `1 vu`-aligned fill and stroke part materialization for boxed regions.
- Selected fills under the pointer take drag priority, so dragging an already selected fill moves it instead of starting a new selection.
- Endpoint and Bezier control handle editing for selected lines, including linked endpoint movement for connected line segments.
- Basic edit shortcuts: Ctrl+Z undo, Ctrl+C copy selected objects, and Ctrl+V paste copied objects with a small stage offset.
- Drawing element topology foundation: fills, free strokes, and filled-shape boundary strokes are separate selectable elements. Rectangle edges are individually selectable by default; crossing strokes and boundaries split each other at valid intersections, and sub-1vu stroke fragments are suppressed.
- Topology parts are materialized into editable objects: split stroke parts become independent line objects, boundary-stroke parts become movable line segments, and a rectangle fill cut by a through-line becomes two movable path fills when its region is marquee-selected or moved.
- Same-color fill regions can merge automatically after drawing, moving, resizing, pasting, or recoloring when their distance is under 10 vector units, using compound fill contours that can preserve closed holes such as ring and donut regions.
- Path fills support compound contours: one fill object can contain an outer boundary plus inner hole boundaries, rendered and hit-tested with even-odd fill rules.
- Rendering follows the Flash layer stack: timeline row 0 is the top layer; inside each layer all fills render first, then lines, Pencil strokes, and filled-shape boundary strokes render above them.
- Stroke splitting uses a unified quadratic-curve model, so curve-curve and line-curve intersections share the same topology path; straight lines are treated as quadratic curves with a midpoint control handle.
- Delete key removes the currently selected drawing object or marquee-selected objects.
- Left-edge Vault drawer opened from the floating tool palette for Library/Vault asset storage.
- Vault can persist object snapshots, notes, file references, and library presets to `data/vault.json`.
- Basic Drawing header snapping strip for snap, grid snap, object snap, tight fit, align, angle snap, grid size, and angle step.
- Draw settings panel for shape, aspect-ratio, and freehand smoothing controls.
- Material editor panel for fill, stroke color, stroke width, and opacity inside Basic Drawing.
- Scene Edit panel for multi-scene, multi-drawing-object, and scene-instance management, with editable scene dimension and camera projection metadata.
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
Direct2D fallback failures, application startup, and stress-scene generation
events are recorded there.

## Performance Model

The manual stress scene avoids scanning every object every frame.

- Stage rendering now prefers a Direct2D HWND render target through Vortice bindings.
- The desktop shell remains WinForms; WPF is not used.
- If Direct2D initialization fails on a machine, the stage falls back to the legacy GDI path instead of preventing startup.
- Low zoom uses tile LOD summaries.
- Object zoom uses a fixed spatial index to visit only visible cells.
- Scene data is stored in packed arrays instead of per-object managed models.
- Stress generation, LOD summaries, spatial-index rebuilds, visible-object collection, and composition preparation use deterministic multi-core batches with stable ordered merges.
- LOD selection uses effective pixel scale, so vector-unit zoom changes do not accidentally submit the full stress scene as individual draw calls.
- Direct2D uploads versioned LOD summaries as premultiplied bitmap batches, reducing a Fit Stage frame from thousands of rectangle commands to one bitmap submission.
- The combined layer/timeline strip limits visible rows and uses lightweight custom painting to keep the workbench responsive.

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
    AnimationTimeline.cs Timeline tracks, held exposure evaluation, frame commands, target-ID synchronization, and snapshots.
    SceneCompositionBuilder.cs
                          Current-frame scene instance composition preview builder.
    VectorScene.cs        Packed scene arrays, stress generator, spatial index.
    VectorSceneSnapshot.cs
                          Undo snapshot data for scene-level edit history, including compound path contours and timeline state.
    DrawingElementTopology.cs
                          Fill/stroke element identity and topology hit contracts.
    DrawingObjectDefinition.cs
                          Flash-style reusable drawing object metadata plus isolated drawing storage.
    SceneDefinition.cs    Lightweight scene composition metadata.
    SceneObjectInstanceDefinition.cs
                          Scene-level drawing object instance transforms.
    VectorProject.cs      Project root, scene dimension, and camera projection model.
    VectorUnits.cs        Vector unit, pixel, and stroke point conversion rules.
    RenderStats.cs        Renderer telemetry contract.
    CompactFormat.cs      Human-readable metric formatting.
    DrawSettings.cs       Snap, align, grid and shape drawing settings.
    FreehandStrokeProcessor.cs
                          Screen-space freehand smoothing and path simplification.
    ShapeKind.cs          Basic shape kind enum.
  Rendering/
    StageControl.cs       Stage viewport renderer and camera.
    Direct2DStageRenderer.cs
                          Direct2D HWND renderer with GDI fallback support.
  UI/
    MainForm.cs           Desktop workbench shell and interaction logic.
    LibraryVaultPanel.cs  Library presets and persistent generic Vault.
    SceneEditorPanel.cs   Scene mode, scene list, drawing object manager.
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

Run the full 1000-layer / 100000-object stress build, parallel render collection,
spatial-index validation, and scene-composition benchmark with:

```powershell
dotnet native\bin\Release\net8.0-windows\VectorAnimationEngine.dll --bench
```

The benchmark checks the CPU visible-object collection phase against the 144 FPS
frame budget and prints per-collection capacity plus composition phase timings.

Run the freehand sampling, commit, hit-test, and snapshot regression benchmark with:

```powershell
dotnet native\bin\Release\net8.0-windows\VectorAnimationEngine.dll --bench-freehand
```

Run the timeline exposure, Adobe-style frame command, independent cel ownership,
snapshot, track synchronization, and scene-instance track regression with:

```powershell
dotnet native\bin\Release\net8.0-windows\VectorAnimationEngine.dll --bench-timeline
```

Run the Direct2D editable-scene plus nested-underlay renderer regression with:

```powershell
dotnet native\bin\Release\net8.0-windows\VectorAnimationEngine.dll --bench-render
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
