# Scene Spatial Module Map

| Concern | Primary code | Related surface |
| --- | --- | --- |
| Scene instances, layer assignment, cameras | `native/Engine/SceneDefinition.cs`, `SceneObjectInstanceDefinition.cs` | `LayeredInstanceIndex.cs` |
| Scene mask model | `native/Engine/SceneMaskDefinition.cs` | `SceneCompositionBuilder.cs` |
| Spatial workspace orchestration | `native/UI/MainForm.SceneSpatial.cs` | `MainForm.TransformInteractions.cs` |
| Reference view and spatial handles | `native/Rendering/StageControl.Reference3D.cs` | `StageControl.GdiReference3D.cs` |
| Camera interpolation | `native/Rendering/StageControl.CameraTransitions.cs` | `native/App/Benchmark.CameraTransitions.cs` |
| Direct2D reference rendering | `native/Rendering/Direct2DStageRenderer.Reference3D.cs` | `Direct2DStageRenderer.Caching.cs` |
| Spatial controls | `native/UI/ReferenceViewPad.cs`, `SpatialTransformPanel.cs` | `SceneEditorPanel.cs` |
| Interaction/render regressions | `Benchmark.StageInteraction.cs`, `Benchmark.StageRenderSuite.cs` | `Benchmark.ProjectComposition.cs` |

```powershell
rg -n "Reference3D|ReferenceCamera|SpatialTransform|SceneMask|MaskClip" native/Engine native/Rendering native/UI native/App
```
