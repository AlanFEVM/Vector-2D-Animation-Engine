---
name: vector2d-winforms-ui
description: Build and revise Vector 2D Animation Engine WinForms workbench UI under native/UI, including panels, inspectors, toolbars, dialogs, right-click/context menus, owner-drawn controls, icons, themes, motion, keyboard and accessibility behavior, model binding, and hot-reload refresh routing. Use when a task changes non-stage UI layout or reusable desktop controls.
---

# Vector2D WinForms UI

Follow the project's programmatic WinForms system. It does not use the Designer, WPF, or a separate design-system package.

## Workflow

1. Find the closest existing panel or control before designing a new pattern.
2. Read [control-patterns.md](references/control-patterns.md) for layout, theme, binding, interaction, accessibility, and disposal rules.
3. Keep reusable UI state inside the panel/control and expose intent through events. Let `MainForm` coordinate project mutations and undo.
4. Guard model-to-control refreshes with `_updating`; unsubscribe old model events before rebinding.
5. For continuous edits, expose `InteractionStarted`, `InteractionCompleted`, and `InteractionCanceled` so one gesture produces one undo entry.
6. Use `Theme`, `ModernSlider`, `ModernNumericUpDown`, `ModernToggleSwitch`, `SvgIconButton`, and `UiMotion` rather than restyling standard controls locally.
7. Register new top-level UI types in `HotReloadModuleResolver` and refresh only the owning module.
8. Build, then read [visual-verification.md](references/visual-verification.md) and capture fixed-size screenshots for layout changes.
9. Update `docs/USER_GUIDE.md` when controls, labels, shortcuts, or interaction behavior change.

## UI Rules

- Use stable `TableLayoutPanel` or `FlowLayoutPanel` tracks, explicit compact heights, and bounded widths. Avoid layout that changes size when text, hover, or selection changes.
- Use `Theme` colors, fonts, spacing, and style helpers. Reapply `StyleButton` or `StyleActiveButton` when active state changes.
- Use icons for familiar tool actions. `SvgIcons` is a code-native 24x24 GDI path set despite its name; extend it instead of embedding ad hoc bitmaps or SVG XML.
- Create every right-click/context menu with `AnimatedContextMenuStrip`, never a raw `ContextMenuStrip`. The component owns Theme styling, crisp high-contrast text, accessible menu-item defaults, and the opening scale-back animation.
- Set `AccessibleName` on icon-only buttons and meaningful accessible role/description/unit text on custom inputs.
- Preserve keyboard access and focus cues. Canvas shortcuts must not consume keys while a text, numeric, combo, or slider editor owns them.
- Handle hover, focus, disabled, mouse capture loss, cancellation, keyboard adjustment, and DPI-aware sizing in owner-drawn controls.
- Dispose every `Timer`, `ToolTip`, `ContextMenuStrip`, cached bitmap, brush, pen, and native resource owned by a control.
- Marshal background callbacks with `InvokeRequired`; do not update WinForms controls from worker threads.

## Targeted Search

```powershell
rg -n "Style(Button|TextBox|ComboBox|Numeric)|ControlHeight|IconButtonSize" native/UI/Theme.cs
rg -n "InteractionStarted|InteractionCompleted|InteractionCanceled|_updating" native/UI -g '*.cs'
rg -n "BindProject|BindScene|Refresh.*HotReload|HotReloadModule" native/UI native/App
```

Use `scripts/capture-control.ps1` for ordinary parameterless panels. Stage/Direct2D verification belongs to `$vector2d-stage-workflow`.
