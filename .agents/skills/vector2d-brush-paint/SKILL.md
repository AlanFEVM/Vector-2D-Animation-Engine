---
name: vector2d-brush-paint
description: Implement and review Vector 2D Animation Engine Brush, Pressure Brush, Mixing Brush, paint sampling, pigment or optical color mixing, painted-region meshes, brush-tip previews, and brush erasing. Use for brush settings, pressure/smoothing, MixingBrushRegion data, paint rasterization, or erasing filled paint; use drawing geometry for non-brush topology edits.
---

# Vector2D Brush And Paint

Keep sampling, committed geometry, rendering, hit testing, and tool feedback consistent across the brush pipeline.

## Route The Task

- Read [module-map.md](references/module-map.md) first and open only the owning brush modules.
- Read [brush-contract.md](references/brush-contract.md) for sampling, pressure, painted-region, merge, erase, snapshot, or renderer work.
- Also use `$vector2d-stage-workflow` when changing pointer capture, preview APIs, cursors, or Stage pass order.
- Also use `$vector2d-drawing-geometry` when brush output changes packed-object lifecycle, topology, compaction, or index remapping.
- Also use `$vector2d-winforms-ui` only for reusable brush controls or settings layout.

## Workflow

1. Identify the tool variant, input units, sampling cadence, smoothing, and committed object kind.
2. Preserve one interaction lifecycle from pointer start through cancel, capture loss, and commit.
3. Keep previews transient; create editable scene data only at the established commit boundary.
4. Preserve pressure, tip shape, color-mixing mode, vertex colors, layer/Cel ownership, order, and undo semantics.
5. Invalidate region raster caches and derived scene state after committed paint changes.
6. Extend the nearest brush regression partial from the module map.
7. Use `$vector2d-validate-change` with `Pressure` for pressure-only contracts and `Freehand` for brush, mixing, erase, or geometry behavior; add `Render` for raster/cache changes.
8. Update `docs/USER_GUIDE.md` for user-visible tools or settings.

## Invariants

- Convert Stage pixels to vector units through established APIs and quantize committed coordinates only.
- Capture undo once per gesture, not once per sample.
- Keep Mixing Brush legacy trajectory payloads readable while new content uses painted-region geometry.
- Preserve deterministic region ordering and topmost compatible-region merge scope.
- Dispose or invalidate renderer resources when scene identity, paint data, or the Direct2D target changes.
