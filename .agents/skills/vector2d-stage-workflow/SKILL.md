---
name: vector2d-stage-workflow
description: Implement and review Vector 2D Animation Engine Stage/canvas behavior involving drawing tools, canvas context commands, pointer or keyboard routing, selection, drag/transform sessions, previews, overlays, cursors, coordinate conversion, 2D camera navigation, StageControl, Direct2D/GDI parity, render order, LOD, caches, fallback, and renderer performance. Use vector2d-scene-spatial for reference 3D, spatial cameras, and scene-mask projection.
---

# Vector2D Stage Workflow

Keep input state, editable model state, transient overlays, 2D camera state, and both render backends synchronized.

## Route The Task

- Read [module-map.md](references/module-map.md) first and open the smallest owning partial.
- Read [tool-lifecycle.md](references/tool-lifecycle.md) for tools, canvas commands, shortcuts, pointer sessions, selection, transforms, drag/drop, undo, or previews.
- Read [rendering-pipeline.md](references/rendering-pipeline.md) for Stage visuals, Direct2D/GDI, pass order, LOD, caches, fallback, or performance.
- Read both only when an interaction adds a preview or committed visual that changes rendering.
- Use `$vector2d-scene-spatial` for reference 3D, spatial gizmos/cameras, or scene-mask projection.
- Use `$vector2d-winforms-ui` for non-canvas panels, controls, and list/context menus.

## Workflow

1. Identify the active workspace and whether the operation is legal in Basic Drawing or Scene Building.
2. Define one state machine: start, update, commit, cancel, capture loss, tool switch, frame/workspace switch, and deactivation.
3. Convert through Stage coordinate APIs and quantize only at the established commit boundary.
4. Capture undo once before first mutation; keep transient previews non-destructive where the existing tool pattern does so.
5. Pair every `Set*` transient API with cleanup and invalidate without relayout.
6. After commit, synchronize model caches, selection, hierarchy, inspector, timeline, underlay, and overlays as affected.
7. Preserve logical parity and pass order in Direct2D and GDI.
8. Extend the nearest Stage regression partial, then run `$vector2d-validate-change` with `Freehand` and/or `Render`.
9. Update `docs/USER_GUIDE.md` for visible tools, shortcuts, navigation, or feedback.

## Invariants

- Finish or cancel every pointer session on all lifecycle exits.
- Do not consume shortcuts while an editor control owns input.
- Materialize virtual topology parts and consume index remaps before local geometry edits.
- Restore temporary scene bindings in `finally`.
- Direct2D failure leaves the GDI path usable and target-bound resources are disposed on reset/reload.
