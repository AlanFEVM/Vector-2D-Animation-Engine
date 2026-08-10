# Stage Source Module Map

| Task or behavior | Start here | Read next only when needed |
| --- | --- | --- |
| Shared Stage binding, 2D camera, preview state, coordinate APIs | `native/Rendering/StageControl.cs` | focused GDI/selection partial |
| Tool activation and drawing workflow | `native/UI/MainForm.DrawingTools.cs` | `MainForm.StagePointerInput.cs` |
| Pointer dispatch, capture, hover, tool sessions | `native/UI/MainForm.StagePointerInput.cs` | owning tool/transform partial |
| Selection and topology commands | `native/UI/MainForm.SelectionAndTopology.cs` | `$vector2d-drawing-geometry` |
| Selected part movement and coalesced drag | `native/UI/MainForm.SelectionDrag.cs` | `StageControl.SelectionDrag.cs`, renderer Selection partial |
| Free Transform/distort interactions | `native/UI/MainForm.TransformInteractions.cs` | `VectorScene.Transforms.cs` or `Distortions.cs` |
| Stage-facing workbench commands, drop/import | `native/UI/MainForm.WorkbenchCommands.cs` | `$vector2d-assets-persistence` |
| Workspace/inspector Stage binding | `native/UI/MainForm.InspectorAndWorkspace.cs` | `$vector2d-winforms-ui` |
| GDI scene objects | `native/Rendering/StageControl.GdiSceneRendering.cs` | Direct2D core renderer |
| GDI overlays, previews, handles, cursors | `native/Rendering/StageControl.GdiOverlays.cs` | renderer Selection partial |
| Direct2D scene passes | `native/Rendering/Direct2DStageRenderer.cs` | `Direct2DStageRenderer.Caching.cs` |
| Direct2D selection/topology/drag | `native/Rendering/Direct2DStageRenderer.Selection.cs` | `StageControl.SelectionDrag.cs` |
| LOD and renderer caches | `native/Rendering/Direct2DStageRenderer.Caching.cs` | scene revision producers |
| Reference 3D/spatial cameras/masks | `$vector2d-scene-spatial` | spatial module map |
| Tool/selection regressions | `Benchmark.DrawingTools.cs`, `Benchmark.StageInteraction.cs` | `Benchmark.DragPerformance.cs` |
| Rendering/visual regressions | `Benchmark.StageRenderSuite.cs`, `Benchmark.StageVisualRegression.cs` | `Benchmark.FillRendering.cs` |

```powershell
rg -n -g "MainForm*.cs" "ActivateTool|StageMouse(Down|Move|Up)|Finish.*Pointer|ProcessCmdKey" native/UI
rg -n -g "StageControl*.cs" "Set.*Preview|Clear.*Preview|SetSelection|ScreenToWorld|WorldToScreen" native/Rendering
rg -n -g "Direct2DStageRenderer*.cs" "DrawObject|ResetTarget|LOD|Underlay|SceneRenderOrder" native/Rendering
```
