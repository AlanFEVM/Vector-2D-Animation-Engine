---
name: vector2d-scene-spatial
description: Implement and review Vector 2D Animation Engine Scene Building spatial workflows involving 2D or 3D reference views, XYZ instance transforms, spatial gizmos, orthographic or perspective cameras, smooth camera transitions, scene-layer folders or masks, mask clipping, reference projection, and Direct2D/GDI spatial rendering.
---

# Vector2D Scene Spatial

Keep scene ownership, spatial transforms, camera projection, mask semantics, interaction handles, and both renderers synchronized.

## Route The Task

- Read [module-map.md](references/module-map.md) first to locate model, Stage, UI, and regression owners.
- Read [spatial-contract.md](references/spatial-contract.md) for transform order, cameras, reference projection, masks, or renderer parity.
- Also use `$vector2d-project-model` when changing instance ownership, recursion, provenance, or composition flattening.
- Also use `$vector2d-stage-workflow` for shared pointer sessions, overlays, coordinate conversion, or base render-pass changes.
- Also use `$vector2d-winforms-ui` for spatial panels or reference-view controls.

## Workflow

1. Identify Scene Building context, selected instance IDs, view direction, projection mode, and active mask/folder scope.
2. Define model-space transform and camera projection before changing pointer or rendering code.
3. Keep spatial manipulation as one start/update/commit/cancel state machine with one undo capture.
4. Preserve root provenance so projected picks modify the intended scene instance.
5. Apply mask clipping and layer order consistently in reference projection, Direct2D, and GDI.
6. Extend camera, stage-interaction, project-composition, or stage-render regressions from the module map.
7. Run `$vector2d-validate-change` with `Timeline,Render`; add a real-HWND/manual check for visible camera or gizmo changes.
8. Update `docs/USER_GUIDE.md` for user-visible navigation or Scene behavior.

## Invariants

- Drawing-only tools are disabled or reinterpreted in reference views.
- Transform axes, focus, handedness, projection, and hit testing must use the same coordinate convention.
- Camera transitions are cancelable and finish at the exact target frame without accumulating drift.
- Masks clip the intended layer range before isolated folder or layer blending.
- Direct2D failure leaves a coherent GDI reference view.
