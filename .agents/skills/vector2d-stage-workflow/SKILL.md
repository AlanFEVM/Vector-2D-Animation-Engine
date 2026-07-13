---
name: vector2d-stage-workflow
description: Implement and review Vector 2D Animation Engine stage changes involving canvas tools, pointer and keyboard interaction, selection, drag or transform sessions, previews, overlays, cursors, coordinate conversion, 2D/3D navigation, StageControl, Direct2D/GDI rendering parity, render order, LOD, caches, camera behavior, fallback, and renderer performance.
---

# Vector2D Stage Workflow

Keep input state, editable model state, Stage overlays, and both render backends synchronized.

## Route The Task

- Read [tool-lifecycle.md](references/tool-lifecycle.md) for tools, shortcuts, pointer sessions, selection, transforms, drag/drop, undo, or preview state.
- Read [rendering-pipeline.md](references/rendering-pipeline.md) for new visuals/shapes, Direct2D or GDI drawing, render order, LOD, caches, camera, fallback, or performance.
- Read both when a new tool introduces a preview or committed geometry that must render.

## Workflow

1. Identify the active workspace and whether the operation is allowed in Basic Drawing, Scene Edit 2D, Scene Edit 3D, or all contexts.
2. Define one interaction state machine: start, update, commit, cancel, capture loss, tool switch, frame/workspace switch, and form deactivation.
3. Convert through `StageControl.ScreenToWorld`, `WorldToScreen`, `ScreenLengthToWorld`, and `VectorUnits`; quantize only committed geometry at the established boundary.
4. Capture undo once before the first mutation. Keep drag previews non-destructive until commit where the existing tool pattern does so.
5. Expose transient visuals through paired `StageControl.Set*` / `Clear*` methods and invalidate without changing layout.
6. After commit, synchronize model caches, selection, hierarchy, inspector, timeline, scene composition underlay, and Stage overlay.
7. Implement the visual in both Direct2D and GDI paths, preserving pass order and fallback.
8. Add or extend a focused benchmark regression.
9. Run `$vector2d-validate-change` with `Freehand` for geometry/tool behavior and `Render` for Stage/Direct2D behavior.
10. Update `docs/USER_GUIDE.md` for user-visible tools, shortcuts, navigation, or feedback.

## Non-Negotiable Rules

- Finish or cancel every pointer session on `MouseUp`, `MouseCaptureChanged`, Escape, tool switch, frame/workspace change, and window deactivation.
- Do not let canvas shortcuts consume input owned by text, numeric, combo, or slider editors.
- Scene composition and 3D reference views disable or reinterpret drawing-only tools; do not assume every Stage binding is an editable drawing scene.
- Materialize/detach topology parts before editing their local geometry; consume returned index remaps.
- Keep renderer pass order stable: reference/grid, underlay, editable scene, drag preview, selection/handles, then transient previews and cursors.
- Restore the editable `stage.Scene` in `finally` after temporary underlay/preview rendering.
- Direct2D failure must leave the GDI path usable. Do not reintroduce `OnPaintBackground` flicker.
- Dispose or invalidate renderer resources on target reset, scene switch, geometry revision, and hot reload as appropriate.

## Targeted Search

```powershell
rg -n "ActivateTool|StageMouse(Down|Move|Up)|Finish.*Pointer|ProcessCmdKey" native/UI/MainForm.cs
rg -n "Set.*Preview|Clear.*Preview|SetSelection|HitTest.*Handle|ScreenToWorld" native/Rendering/StageControl.cs
rg -n "DrawObject|ResetTarget|LOD|Underlay|SceneRenderOrder" native/Rendering native/App/Benchmark.cs
```

Search symbols and callers rather than reading all of `MainForm.cs`, `StageControl.cs`, or `Direct2DStageRenderer.cs`.
