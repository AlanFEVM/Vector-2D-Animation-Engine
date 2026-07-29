---
name: vector2d-project-model
description: Implement and review Vector 2D Animation Engine project-document changes involving scenes, drawing objects, nested instances, timeline tracks, cel ownership, snapshots, composition previews, transform propagation, provenance, or composition performance. Use for edits around VectorProject, DrawingObjectDefinition, SceneDefinition, AnimationTimeline, VectorSceneSnapshot, or SceneCompositionBuilder.
---

# Vector2D Project Model

Keep project ownership, timeline identity, and flattened composition aligned. Read only the reference that matches the change instead of loading the large engine files wholesale.

## Workflow

1. Locate the mutation entry point in `VectorProject`; do not mutate the read-only project collections directly.
2. Read [model-map.md](references/model-map.md) for ownership, identifiers, duplication/removal, and snapshot rules.
3. Read [timeline-composition.md](references/timeline-composition.md) when changing tracks, cels, nested instances, transforms, flattening, or preview ownership.
4. Define the invariant before editing: stable target IDs, active exposure, object keyframe ownership, recursion rejection, or destination-owner alignment.
5. Update every representation crossed by the change: source model, snapshot/clone path, timeline synchronization, scalar and packed composition paths, and UI bind/refresh callers.
6. Add a focused regression to `native/App/Benchmark.cs`; extend the nearest existing regression rather than creating an unrelated harness.
7. Run `$vector2d-validate-change` with `Timeline`; add `Stress` for composition batching or performance work.
8. Update `README.md` and `docs/USER_GUIDE.md` when behavior or interaction is user-visible.

## Non-Negotiable Invariants

- A project starts with at least one scene and one drawing object; the last drawing object cannot be removed.
- Scene instances reference drawing objects. Drawing-object instances may nest, but self-reference and cycles are rejected by `VectorProject.CanContainDrawingObject`.
- Drawing-object timelines use one shared `Scene.Timeline`: layer IDs first, nested-instance IDs after them. Scene timelines target scene-instance IDs only.
- Track synchronization preserves surviving track IDs and keyframes. New empty drawing layers start blank; new instance tracks start populated.
- `ObjectKeyframeFrame` owns cel content. A populated exposure alone does not make an object active.
- Snapshots and duplication must deep-copy mutable arrays/contours and remap duplicated instance target IDs.
- Flattened composition is derived data. `SceneCompositionResult` owner index `i` must describe destination object `i`.
- New packed object fields must cross snapshot, scalar append, packed append, composition preparation, and renderer consumption paths.

## Targeted Search

```powershell
rg -n "CanContainDrawingObject|TryAdd.*Instance|TryDuplicateDrawingObject|TryRemoveDrawingObject" native/Engine/VectorProject.cs
rg -n "SynchronizeTracks|EvaluateExposure|Insert.*Frame|Snapshot" native/Engine/AnimationTimeline.cs native/Engine/VectorScene.cs
rg -n "BuildDrawingObject|BuildLayers|PrepareObject|ObjectOwners" native/Engine/SceneCompositionBuilder.cs
```

Avoid reading all of `VectorScene.cs` or `MainForm.cs`; search the symbol and inspect its callers first.
