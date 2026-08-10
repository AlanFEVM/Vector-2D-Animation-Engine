# Project Model Map

## Ownership

| Concern | Primary code |
| --- | --- |
| Project roots and controlled mutation | `native/Engine/VectorProject.cs` |
| Asset folders and tags in memory | `ProjectAssetFolder.cs`, `ProjectAssetTag.cs`, `VectorProject.AssetTags.cs` |
| Reusable symbol/drawing object | `DrawingObjectDefinition.cs` |
| Scene/layer composition | `SceneDefinition.cs`, `LayeredInstanceIndex.cs` |
| Scene masks | `SceneMaskDefinition.cs` |
| Instance transform/state | `SceneObjectInstanceDefinition.cs` |
| Composition/timeline-facing interfaces | `CompositionDefinition.cs` |
| Editable packed geometry | focused `VectorScene.*.cs` partials |
| Geometry undo/clone DTO | `VectorSceneSnapshot.cs` |

Collections expose read-only views. Route adds, removes, duplication, layer moves, mask changes, and reference cleanup through the owning model.

## Mutation Rules

- Validate container and referenced definitions belong to the same project.
- Keep IDs non-empty, unique, and stable; do not derive durable identity from row/object indices.
- Synchronize layer-based timeline targets after layer changes. Instances are assigned to layers; do not invent synthetic instance tracks.
- Remove references to deleted definitions and reject invalid mask/folder relationships.
- Duplicate nested content with new instance IDs and remap snapshot references.
- Raise project/model change events only after a complete valid mutation.

## Snapshot Checklist

When adding mutable model or geometry state, update the applicable DTO field, deep-copy creation path, restore validation/default, compaction/remap path, duplication path, composition preparation, and regression.

Snapshots support undo, cloning, rollback, and restart handoff. Durable project-file compatibility belongs to `$vector2d-assets-persistence`.
