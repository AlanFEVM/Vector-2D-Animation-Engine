---
name: vector2d-project-model
description: Implement and review Vector 2D Animation Engine in-memory project-document structure involving VectorProject, scenes, symbols/drawing objects, nested instances, layers, folders, masks, stable IDs, recursion rules, snapshots, composition flattening, transform propagation, provenance, and composition performance. Use vector2d-timeline-animation for frame/Cel/tween semantics and vector2d-assets-persistence for durable Save/Open or SVG storage.
---

# Vector2D Project Model

Keep object-graph ownership, stable identity, layer assignment, snapshots, and flattened composition aligned.

## Route The Task

- Read [model-map.md](references/model-map.md) for project graph, IDs, definitions, instances, layers, masks, duplication/removal, and snapshots.
- Read [composition-provenance.md](references/composition-provenance.md) only for flattening, transforms, masks, batching, previews, or owner provenance.
- Use `$vector2d-timeline-animation` for frames, Cels, exposures, Auto Key, onion skin, or tweens.
- Use `$vector2d-assets-persistence` for manifests, Vault files, checksums, journal recovery, or SVG I/O.
- Use `$vector2d-scene-spatial` for reference 3D, cameras, spatial handles, or mask rendering.

## Workflow

1. Locate the controlled mutation entry point in the owning project/definition type.
2. Define stable IDs, container/reference ownership, recursion rules, layer/mask assignment, and expected provenance.
3. Update each crossed representation: model, snapshot/clone, synchronization, scalar/packed composition, and bind/refresh callers.
4. Reject missing references, duplicate IDs, self-reference, cycles, and partially updated collections before publishing change events.
5. Extend `Benchmark.ProjectComposition.cs` or the nearest focused model regression partial.
6. Run `$vector2d-validate-change` with `Timeline`; add `Stress` for composition batching/performance and `Render` for visible mask/output changes.
7. Update user documentation for visible project behavior.

## Invariants

- A project retains at least one scene and one drawing object; the last drawing object cannot be removed.
- Stable IDs, not list indices, connect definitions, instances, layers, masks, tracks, UI selection, and provenance.
- Nested drawing objects reject self-reference and cycles.
- Snapshots and duplication deep-copy mutable arrays/contours and remap duplicated instance IDs.
- Flattened composition is derived data; destination owner metadata aligns exactly with appended destination objects.
