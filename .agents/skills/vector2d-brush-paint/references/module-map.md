# Brush And Paint Module Map

| Concern | Start here | Load next only when needed |
| --- | --- | --- |
| Brush shapes, soft layers, traditional tips | `native/Engine/BrushShape.cs` | `native/UI/BrushTipPanel.cs` |
| Freehand/pressure/soft brush commit and brush eraser | `native/Engine/VectorScene.BrushesAndEraser.cs` | `FreehandStrokeProcessor.cs`, `VectorScene.ObjectLifecycle.cs` |
| Mixing models and runtime sampling | `native/Engine/PaintMixing.cs` | `MixingBrushPaintSampler.cs` |
| Painted-region accumulation and merge | `MixingBrushRegionAccumulator.cs`, `MixingBrushRegionData.cs`, `MixingBrushRegionMerger.cs` | `native/Engine/VectorScene.Paint.cs` |
| Mixing-region raster cache | `native/Rendering/MixingBrushRegionRasterizer.cs` | `Direct2DStageRenderer.Caching.cs`, `StageControl.GdiSceneRendering.cs` |
| Tool input and previews | `native/UI/MainForm.DrawingTools.cs`, `MainForm.StagePointerInput.cs` | `native/Rendering/StageControl.cs` |
| Brush settings UI | `native/UI/MixingBrushSettingsPanel.cs`, `BrushTipPanel.cs` | `DrawSettingsPanel.cs` |
| Brush regressions | `native/App/Benchmark.BrushSuite.cs`, `Benchmark.MixingBrush.cs` | `Benchmark.FreehandSuite.cs`, `Benchmark.DrawingTools.cs` |

Use targeted searches instead of reopening the full partial families:

```powershell
rg -n "Add(Soft|Pressure|Mixing)|EraseWithBrush|MixingBrush" native/Engine native/UI native/Rendering
rg -n "Run.*(Brush|Mixing|Pressure|Eraser).*Regression" native/App -g "Benchmark*.cs"
```
