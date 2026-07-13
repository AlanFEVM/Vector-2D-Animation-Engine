# Stage Tool Lifecycle

## Registration Map

When adding a tool, inspect all applicable locations:

- `native/App/ToolMode.cs`
- toolbar/flyout construction in `MainForm`
- `ToolIconKind`, tool grouping, and `Is*Tool` predicates
- `ActivateTool` and cursor application
- `ProcessCmdKey` shortcut handling and input-control exclusions
- `StageMouseDown`, `StageMouseMove`, `StageMouseUp`
- inspector/brush-tip/settings visibility
- workspace enablement in `RefreshToolButtons`
- `docs/USER_GUIDE.md`

Use `SvgIconKind`/`SvgIcons` for the icon and set an accessible tool name.

## Interaction State Machine

For each tool, define:

| Phase | Required behavior |
| --- | --- |
| Start | validate context/button; focus Stage; capture pointer if needed; capture undo before mutation |
| Update | convert coordinates; update non-destructive preview or active edit; invalidate only required visuals |
| Commit | materialize if needed; apply model change; rebuild required caches; sync selection/UI |
| Cancel | restore snapshot if mutated; clear preview/cursors/hover and session fields |
| Capture loss | choose commit/cancel consistently with the nearest existing tool |

Also call the same cleanup path during tool activation, frame change, workspace change, form deactivation, and disposal.

## Selection And Editing

- Selection supports Shift additive behavior and delayed drag-vs-click decisions; preserve existing thresholds and priority rules.
- Selected fill dragging has priority over starting a new marquee.
- Topology hits may represent virtual parts. Call `DetachElementForMove` or materialization APIs before geometry edits.
- Connected line endpoint moves must retain shared connections and snapping semantics.
- Scene-instance selection uses composed owner provenance, not source object indices alone.
- After compaction, remap selected objects/elements before calling Stage selection APIs.

## Preview APIs

Keep transient Stage state paired and self-clearing:

- drawing: `SetDrawingPreview` / `ClearDrawingPreview`
- freehand: `SetFreehandPreview` / `ClearFreehandPreview`
- marquee: `SetMarquee` / `ClearMarquee`
- fill: `SetFillPreview`, fill cursor, and fill animation clear methods
- brush cursor: `SetBrushTipCursor` / `ClearBrushTipCursor`
- transform/selection overlays: update after every selection or geometry change

Do not store preview geometry in `VectorScene` unless it is intended to become editable project data.
