---
name: vector2d-drawing-geometry
description: Implement and review Vector 2D Animation Engine drawing geometry changes involving VectorScene object storage, shapes, lines, paths, freehand or brush strokes, erasing, hit testing, fill/stroke/boundary topology, intersection splitting, part detach or materialization, marquee edits, fill or line merging, object-index remapping, and render-order preservation.
---

# Vector2D Drawing Geometry

Treat geometry edits as transactions over packed structure-of-arrays storage. Search the relevant symbol first; `VectorScene.cs` is intentionally large.

## Workflow

1. Read [vector-scene-map.md](references/vector-scene-map.md) to identify the storage and mutation family involved.
2. Read [topology-invariants.md](references/topology-invariants.md) for element parts, split/materialize/merge work, or any edit that removes and appends objects.
3. Define the active layer, frame, cel, element kind, and expected drawing order before changing code.
4. Reuse existing curve sampling, topology candidates, fill-region, Clipper2, and quantization helpers.
5. For multi-object edits, plan first: snapshot, calculate removals/additions, compact once, append replacements, then rebuild derived state.
6. Preserve material, atoms, keyframe ownership, endpoint styles, `ObjectOrder`, and `ObjectSubOrder`.
7. Return and consume old-to-new object mappings. Never continue using pre-compaction indices.
8. Add the smallest regression beside the related cases in `native/App/Benchmark.cs`.
9. Run `$vector2d-validate-change` with `Freehand`; add `Render` when Stage or Direct2D behavior is affected.

## Core Invariants

- Geometry is stored in vector units, quantized to integer-valued floats. Convert UI pixels and stroke points through `VectorUnits`.
- Topology only relates objects on the same layer and active cel.
- `DrawingElementKey.PartIndex` is derived from current candidates and is not durable identity.
- Array object indices are unstable after removal/compaction. Sparse path/freehand dictionaries must be remapped with the arrays.
- Drawing order is `ObjectOrder`, then `ObjectSubOrder`, then index; do not use array order as semantic z-order.
- Fill uses even-odd compound contours. Holes and multiple islands must survive copy, snapshot, transform, merge, and materialization.
- Straight lines are quadratic curves whose control point is the midpoint; curve and line intersections share the same split path.
- Derived spatial index, LOD summaries, active-keyframe content, and renderer caches must be invalidated or rebuilt after the matching mutation.
- Undo snapshots belong before the first mutation in an interaction, not on every pointer move.

## Targeted Search

```powershell
rg -n "Add(Line|Curve|Path|Freehand)|EraseWithBrushStroke|TransformObjects" native/Engine/VectorScene.cs
rg -n "HitTestElement|Get(Fill|Stroke|Boundary)Parts|Materialize|DetachElement" native/Engine/VectorScene.cs
rg -n "MergeSameColorFillsAround|MergeCompatibleLineSegments|RebuildGeometryIndex" native/Engine/VectorScene.cs native/UI/MainForm.cs
```
