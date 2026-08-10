---
name: vector2d-drawing-geometry
description: Implement and review Vector 2D Animation Engine engine-side drawing geometry involving packed VectorScene object storage, primitives, lines, paths, Bezier nodes, fill/stroke/boundary topology, hit or spatial queries, Boolean fill operations, intersection splitting, materialization/merging, object-index remapping, transforms, distortions, snapshots, and render-order preservation. Use vector2d-brush-paint for brush sampling, Mixing Brush, or brush erasing.
---

# Vector2D Drawing Geometry

Treat geometry edits as transactions over packed structure-of-arrays storage and open the focused partial that owns the operation.

## Route The Task

- Read [vector-scene-map.md](references/vector-scene-map.md) first to select the owning `VectorScene.*.cs` partial and regression file.
- Read [topology-invariants.md](references/topology-invariants.md) only for element parts, intersections, Boolean operations, split/materialize/merge work, or edits that remove and append objects.
- Use `$vector2d-brush-paint` for brush/pressure/mixing sampling, paint regions, or brush erasing.
- Use `$vector2d-stage-workflow` for pointer state, previews, overlays, or renderer behavior.
- Use `$vector2d-assets-persistence` for SVG document encoding, project Save/Open, or durable compatibility.

## Workflow

1. Define active layer, frame, Cel, element kind, units, and drawing order.
2. Search the symbol and callers, then edit the focused partial from the module map.
3. Reuse existing curve sampling, Clipper2, topology, fill-region, quantization, and spatial helpers.
4. For multi-object edits: snapshot, validate, plan replacements, compact once, append, remap, then rebuild derived state.
5. Preserve materials, atoms, keyframe ownership, endpoint styles, `ObjectOrder`, and `ObjectSubOrder`.
6. Extend the nearest focused regression partial; do not add feature regressions to `Benchmark.cs`.
7. Run `$vector2d-validate-change` with `Freehand`; add `Render` when visible Stage output changes.

## Core Invariants

- Geometry is stored in vector units and committed coordinates use established quantization boundaries.
- Object indices are unstable after compaction; remap arrays, sparse dictionaries, selections, and topology hits.
- Drawing order is `ObjectOrder`, then `ObjectSubOrder`, then stable index tie-break.
- Compound even-odd fills retain holes and islands through snapshots, transforms, merging, and materialization.
- Derived spatial, LOD, timeline-content, and renderer state is rebuilt or invalidated after the matching mutation.
