# Project Model Map

## Ownership

| Concern | Primary code |
| --- | --- |
| Project roots and controlled mutation | `native/Engine/VectorProject.cs` |
| Drawable reusable asset | `native/Engine/DrawingObjectDefinition.cs` |
| Non-drawable scene composition | `native/Engine/SceneDefinition.cs` |
| Shared instance transform fields | `native/Engine/SceneObjectInstanceDefinition.cs` |
| Composition/timeline contract | `native/Engine/CompositionDefinition.cs` |
| Editable packed geometry | `native/Engine/VectorScene.cs` |
| Scene undo/clone DTO | `native/Engine/VectorSceneSnapshot.cs` |

`Scenes`, `DrawingObjects`, and `Instances` expose read-only views. Route adds, removes, duplication, and reference cleanup through their owning model.

## Mutation Rules

- Validate that both container and referenced drawing object belong to the same project.
- Keep IDs non-empty and unique. A drawing-object instance ID must not collide with a drawing layer ID.
- After instance changes, call the owning definition's timeline synchronization method.
- Removing a drawing object must remove scene instances and nested drawing-object references to it.
- Duplicating a drawing object must create new nested-instance IDs and rewrite timeline snapshot `TargetId` values before restore.
- Raise `VectorProject.Changed` once after a successful project mutation; avoid exposing partially updated collections.

## Snapshot Checklist

When adding model or geometry state, update all applicable locations:

1. `VectorSceneSnapshot` or `AnimationTimelineSnapshot` field.
2. `CreateSnapshot` deep copy.
3. `RestoreSnapshot` validation/default handling.
4. Object compaction/remapping when the state is indexed by object.
5. Drawing-object duplication.
6. Benchmark coverage for round trip and independent ownership.

Snapshots currently support undo, cloning, and rollback. They are not a complete durable project file format, so do not imply save/load compatibility without implementing it explicitly.

## UI Rebind Points

Project mutations usually require targeted refreshes in `native/UI/MainForm.cs`:

- `RefreshDrawingObjectAssetPresentation`
- `BindActiveDrawingObjectScene`
- `BindSceneEditStage`
- `SceneEditorPanel.BindProject`
- `LibraryVaultPanel.BindProject`

Preserve the current project, active object, frame, and workspace whenever a refresh can do so.
