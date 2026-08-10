---
name: vector2d-winforms-ui
description: Build and revise non-Stage Vector 2D Animation Engine WinForms UI, including workbench panels, inspectors, toolbars, dialogs, list/context menus, owner-drawn controls, icons, themes, motion, keyboard/accessibility behavior, model binding, and hot-reload refresh routing. Use for UI layout or reusable desktop controls; use vector2d-stage-workflow for canvas interaction/rendering.
---

# Vector2D WinForms UI

Follow the programmatic WinForms design system; the repository does not use the Designer, WPF, or a separate UI package.

## Route The Task

- Read [module-map.md](references/module-map.md) to find the owning panel/control and related partial.
- Read [control-patterns.md](references/control-patterns.md) for layout, binding, owner draw, interaction, accessibility, and disposal.
- After layout/visual changes, read [visual-verification.md](references/visual-verification.md) and capture the required states.
- Use `$vector2d-stage-workflow` for canvas input/overlays and `$vector2d-scene-spatial` for spatial/camera semantics.
- Combine with the feature skill for timeline, assets, brush, release, or project behavior; UI owns presentation and event contracts, not domain mutation rules.

## Workflow

1. Reuse the closest established control or panel pattern.
2. Keep reusable UI state inside the control and expose intent through typed events.
3. Guard refreshes with `_updating`, unsubscribe before rebinding, and preserve selected stable IDs.
4. Give continuous editors start/completed/canceled events so one gesture creates one undo entry.
5. Use shared Theme, control, icon, context-menu, motion, and localization primitives.
6. Register new root UI types in hot-reload routing and refresh only the owning module.
7. Build and visually verify normal, narrow, selected/active, disabled, long-text, theme/language, and relevant DPI states.
8. Update `docs/USER_GUIDE.md` for visible controls or interactions.

## Invariants

- Keep dense workbench layouts stable; hover, selection, text, and value changes do not resize surrounding UI.
- Icon-only controls have accessible names and keyboard focus cues.
- Owner-drawn controls handle hover, focus, disabled, capture loss, cancellation, DPI, and disposal.
- Marshal background callbacks to the UI thread and dispose owned timers, menus, bitmaps, and GDI/native resources.
