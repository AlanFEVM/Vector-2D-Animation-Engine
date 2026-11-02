# Benchmark Partial Map

`native/App/Program.cs` exposes seven routed benchmark entry points plus the manual `--bench-collision` project probe. `Benchmark.cs` coordinates the default stress path; feature regressions belong in focused partials.

| Partial | Primary suite(s) | Contract |
| --- | --- | --- |
| `Benchmark.cs` | `Stress` | dispatcher, stress scene, shared order/performance checks |
| `Benchmark.AppIntegration.cs` | `Render` | UI composition/refresh, inspector selection routing and manual navigation, object/tool isolation, bottom-ordered transforms, numeric undo/cancel, image preview/sampling, text defaults, instances and Auto Key; release-note loading and hot-reload routing |
| `Benchmark.AssetLibraryCategories.cs` | `Timeline` | asset categories, external SVG persistence, spatial component assets |
| `Benchmark.AssetTags.cs` | `Timeline` | asset tags and Vault previews |
| `Benchmark.BezierGeometry.cs` | `Freehand` | Bezier geometry/editing |
| `Benchmark.BitmapTransforms.cs` | `Render` | bitmap affine transforms and free-transform pointer interaction |
| `Benchmark.BrushSuite.cs` | `Pressure`, `Freehand`, `Render` | brush/pressure/eraser shared checks |
| `Benchmark.CameraNavigation.cs` | `Render` | reference camera keyboard and middle-button navigation; queued key-up waits for actual timer settlement within a bounded deadline, then verifies the camera remains stopped |
| `Benchmark.CameraTransitions.cs` | `Render` | reference camera transitions |
| `Benchmark.CollisionProject.cs` | manual `--bench-collision` only | local project loading, playback and render performance probe; requires its external fixture |
| `Benchmark.CodexAuthoring.cs` | `Render` | MCP document authoring, spatial tools and animation bundle import |
| `Benchmark.DragPerformance.cs` | `Freehand` | first-move transaction budgets |
| `Benchmark.DrawingTools.cs` | `Freehand` | drawing tool and element-edit workflows |
| `Benchmark.EnterPlaybackView.cs` | `Timeline` | playback view settings, persistence and canvas geometry isolation |
| `Benchmark.FillRendering.cs` | `Freehand`, `Render` | fill geometry and visible rendering |
| `Benchmark.FreehandSuite.cs` | `Freehand` | freehand entrypoint orchestration |
| `Benchmark.BrushStrokeGeometry.cs` | `Freehand` | tool-generated swept Bezier brush contours, smoothing/node reduction, pressure, stamp cadence, holes and snapshots; exhaustive distance/coverage oracles, deterministic parallel output and bounded Boolean-worker reuse |
| `Benchmark.PressurePreviewStability.cs` | `Pressure`, `Freehand`, `Render` | bounded pressure smoothing, immutable completed preview samples, stationary pressure changes, replaceable shape-tip tails and preview/commit parity |
| `Benchmark.SelectionPicking.cs` | `Freehand` | selection-arrow screen tolerance, small fills, visible contour distance, zoom/pan pointer clicks and geometry preservation |
| `Benchmark.ObjectDrawing.cs` | `Freehand` | independent symbol-backed drawing gestures, shape/brush/Pen commits, stacking, single-step undo, scope gating, Auto Key and Save/Open |
| `Benchmark.HierarchyFocus.cs` | `Render` | hierarchy selection routing and camera focus |
| `Benchmark.ImageAssets.cs` | `Timeline`, `Render` | image asset persistence, crop/import dialogs, previews and bitmap rendering |
| `Benchmark.ImageDrop.cs` | `Timeline` | clipboard image paste, file classification, import progress and Stage drop preview |
| `Benchmark.InstanceSkewPreview.cs` | `Freehand` | instance skew preview and transform behavior |
| `Benchmark.MotionTrack.cs` | `Timeline`, `Render` | motion track lifecycle, editing, availability and scene interaction |
| `Benchmark.SelectionPerformance.cs` | `Render`, focused `SelectionPerf` | large and overlapping artwork selection, marquee and interaction budgets; focused entry runs the large-selection frame-rate regression |
| `Benchmark.SelectionArrows.cs` | `Freehand`, `Render` | selection-arrow cursors, arc editing, marquee hover lifetime, move-only selection, origin/occlusion feedback and fill overwrite; pointer-derived marquee bounds and exact premultiplied reference pixels |
| `Benchmark.MarqueeCurvePerformance.cs` | `Freehand`, `Render` | indexed versus exhaustive curve matching, dense partial brush marquee latency, exact split coverage/holes/materials, UI feedback and undo |
| `Benchmark.TimelineParenting.cs` | `Timeline` | motion parenting, transforms, pointer routing and detach commands |
| `Benchmark.ImportedSvgConcurrency.cs`, `Benchmark.ProjectiveSvgClipping.cs` | `Render` | concurrent SVG cache use and bounded perspective texture rendering |
| `Benchmark.MixingBrush.cs` | `Freehand`, `Render` | paint mixing and region geometry/raster behavior |
| `Benchmark.PencilBezier.cs` | `Freehand` | Pencil Bezier generation |
| `Benchmark.ProjectComposition.cs` | `Timeline`, `Stress` | project graph, persistence, instances, composition |
| `Benchmark.SceneDrawing.cs` | `Timeline`, `Render` | direct scene geometry mixed with instances, shared layers/timeline, Cels, composition and persistence through project document structure; drawing tools, F6/F7 and undo through Stage interaction |
| `Benchmark.ProjectCompression.cs` | `Timeline` | compressed project storage, legacy migration, bounded decoding |
| `Benchmark.PackageSaveSafety.cs`, `Benchmark.FolderSaveSafety.cs` | `Timeline` | package write failure preserves asset bindings; failed folder Save As preserves package target, dirty state and retry |
| `Benchmark.FlashImport.cs` | `Timeline` | Flash / Animate XFL import, geometry and exposures, nested symbols, safe input rejection, legacy conversion bridge and V2D save/reopen |
| `Benchmark.RandomFracture.cs` | `Freehand`, `Timeline` | fracture geometry, gradients and timeline commands |
| `Benchmark.RegressionHelpers.cs` | shared | assertions, temporary fixtures, reflection, GC preparation, window/pixel sampling and native cursor capture onto an opaque GDI bitmap; no standalone entrypoint |
| `Benchmark.SceneComponents.cs` | `Freehand` | spatial component shortcuts |
| `Benchmark.SceneOpticsModel.cs` | `Timeline` | scene optical model and persistence |
| `Benchmark.SceneCanvas.cs` | `Timeline`, `Render` | per-scene canvas/background validation, managed images, snapshots/legacy persistence, proportional image layout and GDI background pixels |
| `Benchmark.SceneOpticsRender.cs` | `Render` | optical panels, light gizmos and dense lighting rendering |
| `Benchmark.GpuOptics.cs` | `GpuOptics`; target lifetime also in `Render` | GPU/CPU optical colour-delta diagnostics, alpha assertions, local-light holes, hardware resize/present |
| `Benchmark.ScenePerformance.cs` | manual `--bench-gpu-optics` with `VECTOR_BENCH_SCENE_PERFORMANCE=1` | 112,000 visible units (56,000 independent line segments + 56,000 closed fills), 1920×1080 real HWND, moving 2D/3D views, explicit GPU, strict average/P95/max 16.667 ms budget; `VECTOR_BENCH_SCENE_DIMENSION=2d` or `3d` selects one view |
| `Benchmark.ScenePerformanceRegression.cs` | `Timeline`, `GpuOptics` | SVG composition source reuse, exact transform geometry, larger-raster reuse and content/aspect/resolution isolation |
| `Benchmark.SceneLightingBrdf.cs`, `Benchmark.SceneLightingVisual.cs` | `Render` | GGX material response, numerical stability, lighting screenshots and renderer parity |
| `Benchmark.SnapPoints.cs` | `Freehand`, `Timeline`, `Render` | snap-point model, Vault storage, tools and overlays |
| `Benchmark.StageInteraction.cs` | `Render` | Stage selection, transforms, pointer and spatial interaction; nearest physical/virtual endpoint routing, narrow editing versus drawing snap radii and true line-centerline snapping |
| `Benchmark.StageShortcuts.cs` | `Freehand`, `Render` | Stage shortcut profiles, geometry/instance duplication, selection, appearance and document undo/redo |
| `Benchmark.CanvasRotation.cs` | `Freehand` | 2D view rotation, coordinate/navigation invariants, gesture cancellation and real-HWND Direct2D/GDI pixels |
| `Benchmark.StageRenderSuite.cs` | `Render` | render orchestration and shared app checks; the separately callable frame-cache regression remains in Render and checks invalidation before warming a second unchanged frame for the current cache-settlement policy |
| `Benchmark.StageVisualRegression.cs` | `Freehand`, `Render` | Bezier operation budgets and visual pixels/passes/fallback checks |
| `Benchmark.SymbolSvgExport.cs` | `Timeline` | single-symbol portable SVG export: layer/stack paint order and visible-range filtering |
| `Benchmark.SymbolConversion.cs` | `Freehand` | symbol conversion context commands and selection ownership |
| `Benchmark.SymbolPackage.cs` | `Timeline` | `.V2DSymbol` round trip: nested symbol closure, timeline state, layer/cel ownership, folder paths, tag names and colours, fresh-id import isolation, malformed-package rejection |
| `Benchmark.SymbolFilters.cs`, `Benchmark.SymbolFilterPanel.cs` | `Timeline`, `Render` | instance filter snapshots/persistence, nested isolation, premultiplied blur/glow/shadow, exact content bounds, padded viewport/projection, hardware/software presentation and playback routing, inspector parameters and undo/Auto Key |
| `Benchmark.TimelineSuite.cs` | `Timeline` | timeline/project entrypoint orchestration |
| `Benchmark.TimelineTween.cs` | `Timeline` | classic/shape tween and easing |
| `Benchmark.TopologyMaterialization.cs` | `Freehand` | topology split/materialize/merge/query |

`Freehand` runs the fitted brush commit budget checks after its functional checks. Each brush fixture reuses one scene, resetting it outside the timed commit just as it previously excluded scene construction. Brush tips are configured once and reused, matching `MainForm`'s persistent `_brushShape`; tip configuration is reported separately and the first warmup still builds any missing contour cache. Snapshot, geometry build, commit normalization and any GC during the commit remain measured; three warmups, seven measured samples and the existing maximum-time budgets are unchanged. Output names both setup conditions explicitly so this is not confused with project startup or first-use tip configuration performance.

When adding a partial, add it here and connect it from an existing CLI entrypoint. New CLI modes require synchronized updates to `native/App/Program.cs`, `invoke-validation.ps1`, `suite-map.md`, and the workflow drift checks.

Keep shared test setup in `Benchmark.RegressionHelpers.cs`. Reflection helpers locate members only: retain explicit parameter types for overloads and explicit flags for public or static members. Keep warmups, timed loops, allocation boundaries and performance budgets at their call sites; sharing setup or output must not change measurement semantics. Completed features retain their regression assertions.
