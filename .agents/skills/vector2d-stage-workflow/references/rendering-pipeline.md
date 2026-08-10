# Stage Rendering Pipeline

## Roles

- `StageControl.cs`: binding, 2D camera, shared render state, public coordinates, and preview state.
- `StageControl.GdiSceneRendering.cs`: GDI object rendering and fallback.
- `StageControl.GdiOverlays.cs`: GDI selection, handles, previews, cursors, and marquee.
- `StageControl.SelectionDrag.cs`: transient selection translation and first-move telemetry.
- `Direct2DStageRenderer.cs`: Direct2D target and scene/object passes.
- `Direct2DStageRenderer.Caching.cs`: target-bound geometry, bitmap, and LOD caches.
- `Direct2DStageRenderer.Selection.cs`: selection/topology outlines and GPU drag translation.
- Reference 3D renderers belong to `$vector2d-scene-spatial`.

## Pass Order

Keep Direct2D and GDI logically equivalent:

1. background/reference grid
2. onion-skin frames and nested/scene underlay
3. editable scene and drag-preview scene
4. selection, topology, bounds, and handles
5. drawing/freehand/fill previews and animations
6. tool cursors and marquee

Within a scene, use `SceneRenderOrderBuffer`; order by layer stack, `ObjectOrder`, `ObjectSubOrder`, then stable index tie-break.

## Geometry And Cache Coverage

- Add equivalent GDI and Direct2D handling for visible editable geometry.
- Preserve even-odd fills, specialized line/freehand/paint paths, visibility, opacity, blend, selection, underlay, and LOD rules.
- Invalidate target-bound resources on target reset and data-bound caches on scene identity/revision changes.
- Restore temporary `stage.Scene` replacement in `finally`.
- Preserve the empty `OnPaintBackground` flicker contract and a coherent emergency GDI background.

## Verification

Use `Benchmark.StageRenderSuite.cs` and `Benchmark.StageVisualRegression.cs` through the `Render` suite. A real Direct2D HWND is not reliably captured by `DrawToBitmap`; verify `LastFrameUsedDirect2D`, `LastStats`, repeated-frame/cache counters, nonblank presentation, and GDI fallback.
