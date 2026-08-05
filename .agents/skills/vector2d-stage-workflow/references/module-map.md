# Stage Source Module Map

Use this map before opening the large Stage owners. Private members remain shared across each partial class, but file ownership still follows the coordination contract.

| Task or behavior | Start here | Read next only when needed |
| --- | --- | --- |
| Selected fill/stroke movement, topology materialization, linked fill boundaries, coalesced drag updates | `native/UI/MainForm.SelectionDrag.cs` | `native/Engine/VectorScene.cs`, `native/UI/MainForm.cs` pointer routing |
| Tool activation, pointer dispatch, commit/cancel, workspace and undo orchestration | `native/UI/MainForm.cs` | the focused `MainForm.*.cs` partial named by the caller |
| Selection drag offset, synchronous first frame, 30 ms telemetry | `native/Rendering/StageControl.SelectionDrag.cs` | `native/Rendering/StageControl.cs` paint scheduling |
| Stage binding, camera, input events, overlay lifecycle, GDI fallback | `native/Rendering/StageControl.cs` | `StageControl.SelectionDrag.cs` for selection movement |
| Direct2D selection outlines, topology highlights, GPU drag translation | `native/Rendering/Direct2DStageRenderer.Selection.cs` | `native/Rendering/Direct2DStageRenderer.cs` geometry helpers and target state |
| Direct2D scene passes, target lifecycle, base-frame and geometry caches | `native/Rendering/Direct2DStageRenderer.cs` | `native/Rendering/SceneRenderOrder.cs` |
| First Fill/Line drag CPU transaction budget | `native/App/Benchmark.DragPerformance.cs` | `native/App/Benchmark.cs` suite caller |
| Stage HWND, overlay, cache, and Direct2D regression | `Benchmark.RunStageRendererRegression` in `native/App/Benchmark.cs` | the renderer partial owning the assertion |

## Focused Searches

```powershell
rg -n "MoveSelectedFromPointer|EnsureSelectedElementDetachedForMove|ApplyPendingLineDragPreview" native/UI/MainForm.SelectionDrag.cs
rg -n "PresentSelectionDragPreview|BeginDragFirstMoveTelemetry|ClearSelectionDragPreview" native/Rendering/StageControl.SelectionDrag.cs
rg -n "DrawSelection|DrawElementSelectionOutline|SelectionDragPreviewOffset" native/Rendering/Direct2DStageRenderer.Selection.cs
rg -n "RunFirstDragMovePerformanceRegression|drag_.*first_move" native/App/Benchmark.DragPerformance.cs
rg -n -g "MainForm*.cs" -g "StageControl*.cs" -g "Direct2DStageRenderer*.cs" "<caller-or-symbol>" native/UI native/Rendering
```

For topology semantics such as part identity, object-index remapping, detach/materialize behavior, or fill/stroke connectivity, switch to `$vector2d-drawing-geometry` before editing `VectorScene`.
