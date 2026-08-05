# Stage Rendering Pipeline

## Roles

- `StageControl`: camera, binding, render state, overlays, GDI fallback, cache ownership, and public Stage coordinate APIs.
- `StageControl.SelectionDrag.cs`: transient selection translation, immediate presentation, and first-move telemetry.
- `Direct2DStageRenderer`: HWND target, Direct2D resources, scene/object passes, geometry and LOD caches.
- `Direct2DStageRenderer.Selection.cs`: Direct2D selection outlines, topology-part highlights, and the GPU selection-drag transform.
- `SceneRenderOrder.cs`: stable visible-object ordering shared by render paths.
- `RenderStats`: telemetry shown by the workbench and asserted by benchmarks.

## Pass Order

Keep Direct2D and GDI logically equivalent:

1. background/reference grid
2. onion-skin reference frames
3. nested or scene underlay
4. editable scene
5. drag preview scene
6. selection outlines, selected topology parts, bounds, and handles
7. drawing/freehand/fill previews and fill animation
8. tool cursors and marquee

Within a scene, use `SceneRenderOrderBuffer`. Ordering is layer stack plus `ObjectOrder`, `ObjectSubOrder`, and stable index tie-break, with fills below strokes inside the same layer.

## Geometry Coverage

For a new shape or visual:

- add GDI drawing in `StageControl`
- add Direct2D drawing in `Direct2DStageRenderer`
- preserve even-odd compound fills and specialized line/freehand paths
- apply the same visibility, LOD, opacity, selection, and underlay rules
- update hit/overlay geometry if the visual is editable

Do not assume `DrawToBitmap` captures the HWND Direct2D target.

## Cache And Target Rules

- Direct2D brushes, path geometry, freehand geometry, LOD bitmaps, and target-dependent resources need explicit invalidation/disposal.
- Resetting the target must clear target-bound brushes and LOD bitmaps.
- LOD bitmap validity depends on the scene summary revision; freehand geometry validity depends on scene/point-array identity.
- Scene switching must not retain geometry caches for the previous underlay.
- Temporary `stage.Scene` replacement must restore the editable scene in `finally`.
- Repeated Direct2D failures eventually disable that path for the session; `ReloadRenderingModuleForHotReload` resets resources and allows retry.
- `OnPaintBackground` is intentionally empty to prevent flicker. Preserve the emergency background when Direct2D presentation fails.

## Performance

- Low zoom uses overview/detail tile summaries; object zoom uses the spatial index and draw limits.
- Keep LOD selection based on effective pixel scale (`zoom * VectorUnits.PixelsPerUnit`).
- Do not parallelize work in a way that changes command order.
- Extend `RenderStats` when adding a measurable stage path.
- Use `RunStageRendererRegression` for underlay, cache, LOD, hot-reload routing, and Direct2D checks; use the stress benchmark for collection/composition budgets.
- Keep the first Fill/Line transaction budget regression in `Benchmark.DragPerformance.cs` synchronized with `StageControl.DragFirstMoveBudgetMilliseconds`.

Visual verification requires a real shown HWND. Check `LastFrameUsedDirect2D`, `LastStats`, repeated frame behavior, cache rebuild counts, and the GDI fallback path.
