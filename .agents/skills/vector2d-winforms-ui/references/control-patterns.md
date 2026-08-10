# WinForms Control Patterns

## File Map

| Concern | Primary code |
| --- | --- |
| Palette, type, spacing, standard styles | `native/UI/Theme.cs` |
| Button state animation | `native/UI/UiMotion.cs` |
| Icon drawing and icon button | `native/UI/SvgIcons.cs`, `SvgIconButton.cs` |
| Right-click menu | `AnimatedContextMenuStrip.cs` |
| Continuous numeric input | `ModernSlider.cs`, `ModernNumericUpDown.cs` |
| Binary input | `ModernToggleSwitch.cs` |
| Dense inspector example | `MaterialEditorPanel.cs` |
| Bound workspace panel example | `SceneEditorPanel.cs`, `LibraryVaultPanel.cs` |
| Large owner-drawn surface example | `TimelineStrip.cs` |
| Dense dock/workspace example | `DashDock.cs`, `WorkspaceTabs.cs` |
| Color flyout/dialog examples | `WorkspaceColorFlyout.cs`, `ProfessionalColorPickerDialog.cs` |
| Spatial control examples | `ReferenceViewPad.cs`, `SpatialTransformPanel.cs` |

## Layout

- Build UI in constructors or a `BuildUi` method with programmatic docking and layout panels.
- Prefer `Theme.ControlHeightCompact`, `ControlHeight`, `IconButtonSize`, and gap constants.
- Give rows and tool buttons stable dimensions. Use `AutoEllipsis` or measured owner-drawn text for bounded labels.
- Call `SuspendLayout` / `ResumeLayout` around multi-control refreshes that would otherwise relayout repeatedly.
- Keep inspector layouts dense and work-focused; do not wrap page sections in decorative cards.

## Binding

- A panel exposes typed events such as `AddSceneRequested` or `SettingsChanged`; `MainForm` applies model changes.
- A `Bind*` method stores the model, detaches handlers from the previous model, attaches the new model, and performs one refresh.
- Set `_updating = true` while assigning control values and test it in UI event handlers to prevent feedback loops.
- Update text only when the value changed when refreshes are frequent.
- Preserve selected IDs rather than relying on list indices when the underlying collection can reorder.

## Continuous Interaction

Use the established lifecycle for sliders, numeric scrubbing, and color channels:

1. `InteractionStarted`: capture one undo snapshot/session.
2. `ValueChanged`: preview/apply intermediate values without adding undo entries.
3. `InteractionCompleted`: commit and run deferred merge/rebuild work once.
4. `InteractionCanceled`: restore the snapshot and UI value.

Handle `MouseCaptureChanged`, Escape, focus loss, and disposal so a session cannot remain open.

## Owner-Draw Checklist

- paint background, border, content, focus, hover, pressed, selected, and disabled states
- use `TextRenderer` for WinForms text and bounded ellipsis/word-break flags
- invalidate only when visual state changes
- support keyboard increments/toggles and accessible value reporting
- dispose GDI objects created per paint with `using`
- dispose long-lived timers and cached images in `Dispose(bool)`

## Right-Click Menus

Use `AnimatedContextMenuStrip` for every context menu. It is the component contract for Theme colors, 30px menu rows, immediate pointer hit highlighting, high-contrast `TextRenderer` labels, keyboard accessibility, and the 180ms scale-back opening animation. Populate it with normal `ToolStripMenuItem` and `ToolStripSeparator` instances; set enablement in `Opening`, then call `Show` or assign it to `Control.ContextMenuStrip`.

Do not create or theme a raw `ContextMenuStrip` at individual call sites.
