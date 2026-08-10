# Benchmark Partial Map

`native/App/Program.cs` exposes five native entry points. `Benchmark.cs` coordinates the default stress path; feature regressions belong in focused partials.

| Partial | Primary suite(s) | Contract |
| --- | --- | --- |
| `Benchmark.cs` | `Stress` | dispatcher, stress scene, shared order/performance checks |
| `Benchmark.AppIntegration.cs` | `Render` | release-note loading and hot-reload routing |
| `Benchmark.AssetTags.cs` | `Timeline` | asset tags and Vault previews |
| `Benchmark.BezierGeometry.cs` | `Freehand` | Bezier geometry/editing |
| `Benchmark.BrushSuite.cs` | `Pressure`, `Freehand`, `Render` | brush/pressure/eraser shared checks |
| `Benchmark.CameraTransitions.cs` | `Render` | reference camera transitions |
| `Benchmark.DragPerformance.cs` | `Freehand` | first-move transaction budgets |
| `Benchmark.DrawingTools.cs` | `Freehand` | drawing tool and element-edit workflows |
| `Benchmark.FillRendering.cs` | `Freehand`, `Render` | fill geometry and visible rendering |
| `Benchmark.FreehandSuite.cs` | `Freehand` | freehand entrypoint orchestration |
| `Benchmark.MixingBrush.cs` | `Freehand`, `Render` | paint mixing and region geometry/raster behavior |
| `Benchmark.PencilBezier.cs` | `Freehand` | Pencil Bezier generation |
| `Benchmark.ProjectComposition.cs` | `Timeline`, `Stress` | project graph, persistence, instances, composition |
| `Benchmark.RegressionHelpers.cs` | shared | assertion/fixture helpers; no standalone entrypoint |
| `Benchmark.StageInteraction.cs` | `Render` | Stage selection, transforms, pointer and spatial interaction |
| `Benchmark.StageRenderSuite.cs` | `Render` | render entrypoint orchestration and shared app checks |
| `Benchmark.StageVisualRegression.cs` | `Render` | visual pixels/passes/fallback checks |
| `Benchmark.TimelineSuite.cs` | `Timeline` | timeline/project entrypoint orchestration |
| `Benchmark.TimelineTween.cs` | `Timeline` | classic/shape tween and easing |
| `Benchmark.TopologyMaterialization.cs` | `Freehand` | topology split/materialize/merge/query |

When adding a partial, add it here and connect it from an existing CLI entrypoint. New CLI modes require synchronized updates to `native/App/Program.cs`, `invoke-validation.ps1`, `suite-map.md`, and the workflow drift checks.
