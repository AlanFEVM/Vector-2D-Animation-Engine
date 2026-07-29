# Vector 2D Animation Engine

Native Windows desktop prototype for a large-scale 2D vector animation engine.

## Run

Source development requires the stable .NET 8 SDK selected by `global.json`. Build or refresh the ignored root development launcher, then start it with:

```powershell
scripts\publish-development-launcher.ps1
.\VectorAnimationEngine.exe
```

The root `VectorAnimationEngine.exe` is exclusively the source development launcher. It restores the first build when needed, then starts non-interactive `dotnet watch` with Metadata Hot Reload. Changed engine and rendering method bodies apply in-process; shell, inspector, workspace, and timeline changes rebuild the workbench while preserving the open project, selection, frame, and view. Use the editor's restart button for unsupported structural edits. Never overwrite the root EXE with a formal release package.

```powershell
winget install --id Microsoft.DotNet.SDK.8 --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
```

`dotnet run --project launcher\VectorAnimationEngine.Launcher.csproj` remains the equivalent source command. Use `.\VectorAnimationEngine.exe --no-hot-reload` for a single source run without file watching. Formal Windows packages are produced separately by the single-EXE script described below.

The app opens with an empty project and one visible layer. The stress scene is not
generated on startup; use `Run Stress Scene` when you explicitly want to benchmark.

## Current Target Workload

- 1000+ layers.
- 100000+ draw objects.
- 100000000+ virtual vector primitives tracked as object complexity metadata.
- Fixed unit model: 1 px = 25 vector units, and 1 stroke point = 0.5 px.
- Adobe Animate-style stage/tools/layers/timeline.
- Dense production-workbench layout inspired by Blender.
- Free Transform with edge/corner scaling, four-corner rotation, edge skew handles, and a movable rotation focus.

## Current Features

- Workspace tabs: `Basic Drawing`, `Scene Edit`, and `Animation`.
- Custom borderless desktop window shell with draggable title area, resizable edges, and in-app minimize/maximize/close controls.
- The title-bar menu includes an offline, bilingual `Release Notes...` viewer for the bundled version history; it follows the active application language and color theme.
- Basic Drawing is the drawing-object edit mode: the header shows one active drawing object at a time plus `+ Object`, and drawing/material tools focus on that object.
- Scene Edit is the non-drawable assembly mode: scenes store drawing-object instances rather than standalone geometry. Drawing objects may recursively instance other drawing objects, with project-level self/cycle rejection, accumulated 2D transforms, fractional per-instance FPS plus play-once/loop/held-frame playback, current-frame exposure evaluation, source provenance retained for double-click editor navigation, green instance bounds for 2D select/move/free-transform editing, and an inspector action that restores X/Y scale without changing other transform state.
- Drawing-object timelines contain only stable drawing-layer tracks. Each layer can own multiple nested drawing-object instances, and its exposure controls both local cel geometry and those instances without generating synthetic instance rows. Scene timelines likewise target stable scene-layer IDs, with multiple instances allowed per layer.
- 3D scenes use Blender-style viewport navigation: middle mouse orbits, Shift + middle mouse pans, Ctrl + middle mouse dollies, mouse wheel dollies, Ctrl + wheel changes the view zoom scale, Numpad 1/3/7 switches front/right/top views, Numpad 5 toggles perspective/orthographic, and Home resets the reference view.
- 2D stages use an origin-aware decimal world grid at 10% default opacity: the minimum cell is 10 vu, every tenth line becomes a major line, and line opacity/weight transitions continuously as zoom changes.
- New projects start with one empty drawing object. All shape and line drawing is stored inside the active drawing object.
- Basic Drawing includes an editable Text tool (`T` in the Traditional Flash preset). It creates multiline text objects, supports in-place secondary editing, double-click entry from Select/Free Transform, green text-area width handles with automatic reflow, installed font families, 4-512 pt sizes, regular/bold/italic/bold-italic styles, left/center/right alignment, Fill/opacity, undo, transforms, clipboard operations, timeline cel cloning, project persistence, and stage `Break Apart` conversion to one compound Path per text object. The tool is disabled outside Basic Drawing.
- `Import SVG...` in Basic Drawing, or dragging one `.svg` file from File Explorer onto the stage, imports a validated SVG as exactly one opaque drawing object. A dropped SVG is placed at the pointer. The object can be selected, transformed, copied, layered, deleted, undone, and saved with the project. Stage right-click `Break Apart` converts supported SVG paths and shapes into editable compound Path fills and Freeform strokes; the same command freezes selected nested drawing-object instances at the current frame into ordinary local geometry, including vectorizing SVG content, as one undoable transaction.
- Directory-backed project persistence stores software/project metadata and project playback settings in one `.v2dProject` manifest, reversible SVG drawing assets under `.Vault`, and drawing/scene layer and frame data under `.TimeLine`; Save/Open validate stable IDs, paths, references, timelines, aggregate limits, and SHA-256 checksums before replacing the live project, while refusing unmanaged target folders or manifests owned by another project. A durable save journal rolls an interrupted replacement back or finishes cleanup before the next Save/Open operation.
- Vector-unit coordinate model with quantized geometry edits and point-based stroke authoring.
- Default project view shows a 4000 vector-unit wide work range; `Fit Stage` still frames the full stage.
- Adobe Animate-style combined layer/timeline strip with fixed-width frame cells, `+` layer insertion, layer/instance track selection, visibility toggles, Solo/All controls, ruler, red playhead, low-contrast held exposure spans, populated/blank keyframe dots, horizontal/vertical scrolling, rectangular multi-frame selection, and themed right-click commands.
- Timeline tracks use ordered populated/blank keyframes. Empty drawing layers start with a hollow blank key; marker fill follows actual cel content, so deleting the last object turns the key hollow and F6 on empty exposure stays blank. In Scene Edit, tracks belong to scene layers, and each scene layer can contain multiple drawing-object instances.
- Drawing-object keyframes own independent cels. F6 clones the currently held layer content into a new cel, later edits or deletion stay isolated from the source cel, F7 creates a blank cel, and drawing on a blank exposure promotes it to a populated keyframe automatically. F6/F7 extend the timeline when needed; a single-frame command advances the playhead, while a multi-frame command keeps the selection in place. Undoing an inserted keyframe restores the timeline content, playhead, frame selection, selection anchor, and active track from before the command.
- Drawing inside a held blank exposure populates that exposure's source key rather than silently creating a key at the playhead; use F6 or F7 first when content must begin on a new frame.
- F5 extends the selected exposure, shifts later keys right, and moves the playhead to the newly added exposure end. A scrollable future-frame grid remains selectable beyond the current timeline end, and inserting there extends the target track through the chosen cell. Frame removal preserves any cel whose held frames survive the removed range, and shifts key markers plus object ownership together. Batch frame/key commands apply to the selected timeline cells and create one undo entry.
- Timeline `Ctrl+C` / `Ctrl+V` copies independent drawing cels with their geometry, paths, and strokes, then pastes them using the copied track/frame offsets. Layer rows can be dragged to reorder; their stable timeline IDs and object ownership remain aligned. Right-click also exposes layer color and per-drawing-layer onion skin, rendered as a non-editable warm previous-frame/cool next-frame preview in both Direct2D and GDI; changing a non-zero onion range enables it for the active layer.
- Adobe Animate timeline shortcuts: Enter play/pause, comma/period previous/next frame, Shift+comma/period first/last playback frame, F5 insert frame, Shift+F5 remove frame, F6 copy/insert keyframe, Shift+F6 clear keyframe, and F7 insert blank keyframe.
- The title bar exposes animation FPS with three-decimal precision and digit-targeted mouse-wheel adjustment; the timeline cursor keeps a three-decimal seconds readout derived from that active FPS.
- The mouse wheel scrolls tracks vertically by default; holding Shift routes it horizontally through frames without axis fallback at an edge.
- Playback settings panel with synchronized fractional FPS, loop playback, and frame range controls; all four values are saved with the project and restored within the active timeline's valid range.
- Bottom status bar showing render FPS, animation FPS, and zoom.
- Playback processes fixed simulation steps in batches at 300 logical UPS. Stage redraw requests remain capped at 144 FPS, and one-second telemetry reports completed stage frames rather than invalidation requests; idle render and paused UPS rates display as `--` instead of extrapolating isolated work.
- Drawing tools for pencil, brush, line, fill, select, pan, and a grouped shape tool with rectangle, ellipse, triangle, polygon, and star flyout choices.
- SVG vector icon buttons for the floating tool palette and the Basic Drawing header snapping strip.
- Global view navigation: middle mouse drags the 2D canvas, and Ctrl + middle mouse drag zooms the 2D canvas from any tool.
- Drawing preview overlay while dragging shape, line, pencil, and brush tools.
- Flash-style freehand drawing: Pencil creates an open stroke with the stroke swatch, while Brush and Pressure Brush create closed filled outlines with the fill swatch; Pencil, Brush, and Eraser retain independent pt sizes. Pressure Brush derives fluid width from mouse speed and press duration. Brush Tip exposes Soft Round, Traditional Brush, and imported 128x128 tips, plus size in document units, frequency, continuity, and Soft Round hardness.
- Freehand input is sampled in screen space and simplified on pointer-up. Pencil commits one selectable open stroke per gesture; Brush and Pressure Brush expand the sampled centerline into closed Path fills and immediately merge them with intersecting or nearby fills on the same layer that have the same ARGB color, then leave the Stage selection clear. Linear brush gradients follow path length on ordinary strokes and switch to a stable principal spatial axis on self-crossing strokes, avoiding conflicting colors at intersections. Intersecting shape-gradient brush strokes with identical stops and layer opacity preview and commit as one union whose center, boundary, and distance field are recalculated; different gradient materials retain normal overpaint behavior. Nearby solid-fill boundaries merge when their distance is under `10 vu`.
- The title-bar menu opens persistent application settings with live English and Simplified Chinese interfaces. Tool shortcuts default to the Traditional Flash preset (V/Q/H/R/O/N/P/Y/T/B/K/S/I/G/E), with an optional number-key preset that follows the toolbar from 1 through 9; bracket keys adjust Pencil and Brush width. While Brush or Pressure Brush is active, hold Shift to open recent-color swatches around the cursor and hover a swatch to set the brush fill color.
- Animated tool-name hints when hovering drawing tools.
- Animated selection highlights use distinct visual semantics: fills pulse with a slimmer cool cyan/mint boundary, while line and boundary-stroke segments use a stronger warm amber/orange path highlight. Filled shapes retain boundary handles and selected boundary-stroke segments retain partial edge highlights.
- Marquee selection for selecting, highlighting, moving, and deleting multiple objects, with `1 vu`-aligned fill and stroke part materialization for boxed regions.
- Selected fills under the pointer take drag priority, so dragging an already selected fill moves it instead of starting a new selection.
- Selected lines immediately expose start, end, and Bezier control handles, including topology-part selections; connected endpoints move together, holding Ctrl temporarily snaps edited or newly drawn endpoints to nearby line endpoints, and holding Shift while drawing with the Line tool constrains the preview and committed line to the configured angle step.
- Basic edit shortcuts: Ctrl+Z undo, Ctrl+C copy selected objects, and Ctrl+V paste copied objects with a small stage offset.
- Drawing element topology foundation: fills, free strokes, and filled-shape boundary strokes are separate selectable elements. Rectangle edges are individually selectable by default; crossing strokes and boundaries split each other at valid intersections, and sub-1vu stroke fragments are suppressed.
- Topology parts are materialized into editable objects: split stroke parts become independent line objects, boundary-stroke parts become movable line segments, and a rectangle fill cut by a through-line becomes two movable path fills when its region is marquee-selected or moved.
- Same-color fill regions can merge automatically after drawing, moving, resizing, pasting, or recoloring when their distance is under 10 vector units, using compound fill contours that can preserve closed holes such as ring and donut regions.
- Collinear straight line segments on the same layer and cel automatically merge after drawing edits when their directions, stroke ARGB, and stroke widths match and a pair of endpoints is strictly under `5 vu` apart.
- Path fills support compound contours: one fill object can contain an outer boundary plus inner hole boundaries, rendered and hit-tested with even-odd fill rules.
- Rendering follows the Flash layer stack: timeline row 0 is the top layer; inside each layer all fills render first, then lines, Pencil strokes, and filled-shape boundary strokes render above them.
- Stroke splitting uses a unified quadratic-curve model, so curve-curve and line-curve intersections share the same topology path; straight lines are treated as quadratic curves with a midpoint control handle.
- Delete key removes the currently selected drawing object or marquee-selected objects.
- Left-edge Vault drawer opened from the floating tool palette, with distinct Library, Project, and Stored sources, contextual actions, real-geometry hover previews, and semi-transparent drag placement previews for drawing objects and nested content.
- Vault persists stored object references, snapshots, notes, file references, and library presets to `data/vault.json`; live Project Objects remain project data and are not duplicated into the Vault file.
- Basic Drawing header snapping strip for snap, grid snap, object snap, tight fit, align, angle snap, grid size, and angle step.
- Draw settings panel for shape, aspect-ratio, and freehand smoothing controls.
- Blender-style material editor for Fill/Stroke targets, RGB/HSV/HSL/Hex modes, gradient component controls, built-in/recent/custom palettes, stroke width, and opacity inside Basic Drawing.
- Scene Edit panel for multi-scene, multi-drawing-object, and scene-instance management, with editable scene dimension and camera projection metadata.
- Shape-aware scene model via `ShapeKind`.

## User Guide

See the operation tutorial in:

```text
docs/USER_GUIDE.md
```

Chinese release notes for the current formal package are available in
[docs/RELEASE_NOTES_0.1.7.md](docs/RELEASE_NOTES_0.1.7.md). The previous
[0.1.6 notes](docs/RELEASE_NOTES_0.1.6.md) and
[0.1.5 notes](docs/RELEASE_NOTES_0.1.5.md) remain available in the release history.

Project rule: when a new user-facing feature is added or an interaction changes,
update the user guide in the same change.

## Git Workflow

This project is expected to keep a local git history. Each completed feature or
bug fix should be committed with a concise message so changes can be rolled back
or investigated later.

Agent-assisted changes follow [AGENTS.md](AGENTS.md). Multi-Agent tasks use the
project coordination skill for explicit file ownership and handoff, while one
validation owner runs builds and regression suites after integration.

## Runtime Logs

Runtime logs are written to `logs/` in the repository root when launched through the
development launcher. Native app logs use `native-yyyyMMdd-HHmmss-pidN.log`; launcher startup
events and captured `dotnet watch` build diagnostics use `launcher.log`, rotated once at 8 MB
to `launcher.previous.log`. A development watch
process that exits unexpectedly with a nonzero code is restarted up to three times. Unhandled UI thread exceptions, domain crashes,
Direct2D fallback failures, application startup, and stress-scene generation
events are recorded there. The distribution bootstrap writes runtime acquisition, validation,
installation, and startup failures to `bootstrap.log` in the packaged root `logs/` directory.

## Performance Model

The manual stress scene avoids scanning every object every frame.

- Stage rendering uses a Direct2D hardware HWND render target through Vortice bindings, with immediate GPU presentation so pointer interaction does not block on vertical synchronization. Solid and gradient fill silhouettes use per-primitive subpixel antialiasing plus a subpixel material-colored edge reinforcement on the GPU, with equivalent high-quality geometry coverage in the GDI fallback.
- The desktop shell remains WinForms; WPF is not used.
- If Direct2D initialization fails on a machine, the stage falls back to the legacy GDI path instead of preventing startup.
- Low zoom uses tile LOD summaries.
- Object zoom uses a fixed spatial index to visit only visible cells.
- Scene data is stored in packed arrays instead of per-object managed models.
- Stress generation, LOD summaries, spatial-index rebuilds, visible-object collection, and composition preparation use deterministic multi-core batches with stable ordered merges.
- LOD selection uses effective pixel scale, so vector-unit zoom changes do not accidentally submit the full stress scene as individual draw calls.
- Direct2D uploads versioned LOD summaries as premultiplied bitmap batches, reducing a Fit Stage frame from thousands of rectangle commands to one bitmap submission.
- The combined layer/timeline strip limits visible rows and uses lightweight custom painting to keep the workbench responsive.

The Direct2D renderer still prepares complex vector topology from the packed scene
arrays on the CPU before GPU rasterization. The next renderer-level step is moving
large object batches into persistent GPU-side geometry buffers and tile caches.

## Source Layout

```text
native/App/              Application host, diagnostics, hot reload, and regressions.
native/Engine/           Project model, timeline, vector geometry, and persistence.
native/Rendering/        Stage control plus Direct2D/GDI rendering.
native/UI/               WinForms workbench and reusable controls.
launcher/                Source development and hot-reload coordinator.
distribution-launcher/   Stable single-EXE bootstrap.
scripts/                 Release packaging entry points.
.agents/                 Repository-specific Agent and validation workflows.
```

## Development And Validation

```powershell
dotnet run --project launcher\VectorAnimationEngine.Launcher.csproj
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Build
```

Use the same validation entry for focused regressions; it serializes builds and benchmarks through the repository validation lock:

```powershell
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Timeline
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Freehand
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Render
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Stress -EnforcePerformanceBudget
```

Every release creates one replaceable EXE:

```powershell
scripts\publish-single-exe.ps1
```

The script builds the framework-dependent application, compresses the complete software payload,
and embeds it into a small Windows bootstrap that can start before .NET 8 is installed. The release
directory contains exactly one file and the script rejects an EXE of 5 MiB or larger:

```text
VectorAnimationEngine-<version>-win-x64.exe
```

Formal packages exist only under `artifacts\release` (or an explicitly supplied empty release
directory). They must never be copied over the repository-root `VectorAnimationEngine.exe`; that
file is the development hot-reload launcher generated by `scripts\publish-development-launcher.ps1`.

For an update, close the application and replace only this EXE. On every launch the bootstrap
atomically regenerates the external `.V2DEngine` directory from the current EXE's embedded payload.
It skips provisioning when a valid `.Runtime` already exists; when the runtime is absent or incomplete it reads
Microsoft's official .NET 8 release metadata, downloads the matching Core and Windows Desktop x64
ZIPs, verifies both SHA-512 hashes, and atomically installs them into `.Runtime`. Network access and
write permission to the software directory are required only for first-time runtime acquisition.
Logs, user data, `.Runtime`, and the extracted `.V2DEngine` stay outside the EXE and survive EXE
replacement.
