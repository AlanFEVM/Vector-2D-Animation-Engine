# Benchmark Partial Map

`native/App/Program.cs` exposes five suite entry points plus the manual `--bench-collision` project probe. `Benchmark.cs` coordinates the default stress path; feature regressions belong in focused partials.

| Partial | Primary suite(s) | Contract |
| --- | --- | --- |
| `Benchmark.cs` | `Stress` | dispatcher, stress scene, shared order/performance checks |
| `Benchmark.AppIntegration.cs` | `Render` | release-note loading and hot-reload routing |
| `Benchmark.AssetLibraryCategories.cs` | `Timeline` | asset categories, external SVG persistence, spatial component assets |
| `Benchmark.AssetTags.cs` | `Timeline` | asset tags and Vault previews |
| `Benchmark.BezierGeometry.cs` | `Freehand` | Bezier geometry/editing |
| `Benchmark.BrushSuite.cs` | `Pressure`, `Freehand`, `Render` | brush/pressure/eraser shared checks |
| `Benchmark.CameraNavigation.cs` | `Render` | reference camera keyboard and middle-button navigation |
| `Benchmark.CameraTransitions.cs` | `Render` | reference camera transitions |
| `Benchmark.CollisionProject.cs` | manual `--bench-collision` only | local project loading, playback and render performance probe; requires its external fixture |
| `Benchmark.CodexAuthoring.cs` | `Render` | MCP document authoring, spatial tools and animation bundle import |
| `Benchmark.DragPerformance.cs` | `Freehand` | first-move transaction budgets |
| `Benchmark.DrawingTools.cs` | `Freehand` | drawing tool and element-edit workflows |
| `Benchmark.FillRendering.cs` | `Freehand`, `Render` | fill geometry and visible rendering |
| `Benchmark.FreehandSuite.cs` | `Freehand` | freehand entrypoint orchestration |
| `Benchmark.HierarchyFocus.cs` | `Render` | hierarchy selection routing and camera focus |
| `Benchmark.ImageAssets.cs` | `Timeline`, `Render` | image asset persistence, crop/import dialogs, previews and bitmap rendering |
| `Benchmark.ImageDrop.cs` | `Timeline` | clipboard image paste, file classification, import progress and Stage drop preview |
| `Benchmark.InstanceSkewPreview.cs` | `Freehand` | instance skew preview and transform behavior |
| `Benchmark.MotionTrack.cs` | `Timeline`, `Render` | motion track lifecycle, editing, availability and scene interaction |
| `Benchmark.SelectionPerformance.cs` | `Render` | large and overlapping artwork selection, marquee and interaction budgets |
| `Benchmark.TimelineParenting.cs` | `Timeline` | motion parenting, transforms, pointer routing and detach commands |
| `Benchmark.ImportedSvgConcurrency.cs`, `Benchmark.ProjectiveSvgClipping.cs` | `Render` | concurrent SVG cache use and bounded perspective texture rendering |
| `Benchmark.MixingBrush.cs` | `Freehand`, `Render` | paint mixing and region geometry/raster behavior |
| `Benchmark.PencilBezier.cs` | `Freehand` | Pencil Bezier generation |
| `Benchmark.ProjectComposition.cs` | `Timeline`, `Stress` | project graph, persistence, instances, composition |
| `Benchmark.ProjectCompression.cs` | `Timeline` | compressed project storage, legacy migration, bounded decoding |
| `Benchmark.PackageSaveSafety.cs`, `Benchmark.FolderSaveSafety.cs` | `Timeline` | package write failure preserves asset bindings; failed folder Save As preserves package target, dirty state and retry |
| `Benchmark.FlashImport.cs` | `Timeline` | Flash / Animate XFL import, geometry and exposures, nested symbols, safe input rejection, legacy conversion bridge and V2D save/reopen |
| `Benchmark.RandomFracture.cs` | `Freehand`, `Timeline` | fracture geometry, gradients and timeline commands |
| `Benchmark.RegressionHelpers.cs` | shared | assertions, temporary fixtures, reflection, GC preparation and pixel sampling; no standalone entrypoint |
| `Benchmark.SceneComponents.cs` | `Freehand` | spatial component shortcuts |
| `Benchmark.SceneOpticsModel.cs` | `Timeline` | scene optical model and persistence |
| `Benchmark.SceneCanvas.cs` | `Timeline`, `Render` | per-scene canvas/background validation, managed images, snapshots/legacy persistence, proportional image layout and GDI background pixels |
| `Benchmark.SceneOpticsRender.cs` | `Render` | optical panels, light gizmos and dense lighting rendering |
| `Benchmark.GpuOptics.cs` | `Render` | GPU/CPU optical pixel parity, alpha, local-light holes, hardware resize/present |
| `Benchmark.ScenePerformance.cs` | manual `--bench-gpu-optics` with `VECTOR_BENCH_SCENE_PERFORMANCE=1` | 112,000 visible units (56,000 independent line segments + 56,000 closed fills), 1920×1080 real HWND, moving 2D/3D views, explicit GPU, strict average/P95/max 16.667 ms budget; `VECTOR_BENCH_SCENE_DIMENSION=2d` or `3d` selects one view |
| `Benchmark.ScenePerformanceRegression.cs` | `Timeline`, manual `--bench-gpu-optics` | SVG composition source reuse, exact transform geometry, larger-raster reuse and content/aspect/resolution isolation |
| `Benchmark.SceneLightingBrdf.cs`, `Benchmark.SceneLightingVisual.cs` | `Render` | GGX material response, numerical stability, lighting screenshots and renderer parity |
| `Benchmark.SnapPoints.cs` | `Freehand`, `Timeline`, `Render` | snap-point model, Vault storage, tools and overlays |
| `Benchmark.StageInteraction.cs` | `Render` | Stage selection, transforms, pointer and spatial interaction |
| `Benchmark.StageRenderSuite.cs` | `Render` | render entrypoint orchestration and shared app checks |
| `Benchmark.StageVisualRegression.cs` | `Freehand`, `Render` | Bezier operation budgets and visual pixels/passes/fallback checks |
| `Benchmark.SymbolSvgExport.cs` | `Timeline` | single-symbol portable SVG export: layer/stack paint order and visible-range filtering |
| `Benchmark.SymbolPackage.cs` | `Timeline` | `.V2DSymbol` round trip: nested symbol closure, timeline state, layer/cel ownership, folder paths, tag names and colours, fresh-id import isolation, malformed-package rejection |
| `Benchmark.SymbolFilters.cs`, `Benchmark.SymbolFilterPanel.cs` | `Timeline`, `Render` | instance filter snapshots/persistence, nested isolation, premultiplied blur/glow/shadow, exact content bounds, padded viewport/projection, hardware/software presentation and playback routing, inspector parameters and undo/Auto Key |
| `Benchmark.TimelineSuite.cs` | `Timeline` | timeline/project entrypoint orchestration |
| `Benchmark.TimelineTween.cs` | `Timeline` | classic/shape tween and easing |
| `Benchmark.TopologyMaterialization.cs` | `Freehand` | topology split/materialize/merge/query |

When adding a partial, add it here and connect it from an existing CLI entrypoint. New CLI modes require synchronized updates to `native/App/Program.cs`, `invoke-validation.ps1`, `suite-map.md`, and the workflow drift checks.

Keep shared test setup in `Benchmark.RegressionHelpers.cs`. Reflection helpers locate members only: retain explicit parameter types for overloads and explicit flags for public or static members. Keep warmups, timed loops, allocation boundaries and performance budgets at their call sites; sharing setup or output must not change measurement semantics. Completed features retain their regression assertions.
