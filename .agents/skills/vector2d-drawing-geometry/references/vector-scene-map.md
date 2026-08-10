# VectorScene Module Map

## Storage

`VectorScene.cs` declares the packed arrays and shared state. New per-object fields must cross allocation/growth, append, compaction, snapshots, composition, persistence, and rendering.

| Operation family | Owning partial | Common regression owner |
| --- | --- | --- |
| Allocation, append, remove, compact, order | `VectorScene.ObjectLifecycle.cs` | `Benchmark.DrawingTools.cs` |
| Snapshots and restore | `VectorScene.Snapshots.cs`, `VectorSceneSnapshot.cs` | nearest affected suite |
| Primitive/path/freehand transforms | `VectorScene.Transforms.cs` | `Benchmark.BezierGeometry.cs` |
| Distort envelopes and geometry | `VectorScene.Distortions.cs`, `DistortEnvelope.cs` | `Benchmark.DrawingTools.cs` |
| Hit testing | `VectorScene.HitTesting.cs` | `Benchmark.DrawingTools.cs` |
| Marquee/spatial queries | `VectorScene.SpatialQueries.cs` | `Benchmark.TopologyMaterialization.cs` |
| Topology candidates and connections | `VectorScene.TopologyLinks.cs` | `Benchmark.TopologyMaterialization.cs` |
| Element/anchor editing | `VectorScene.ElementEditing.cs` | `Benchmark.BezierGeometry.cs` |
| Split, detach, materialize, fill/line merge | `VectorScene.MaterializationAndMerging.cs` | `Benchmark.TopologyMaterialization.cs` |
| Boolean fill/path geometry | `VectorScene.BooleanGeometry.cs` | `Benchmark.FillRendering.cs` |
| Curve/path helpers | `VectorScene.GeometryUtilities.cs` | `Benchmark.BezierGeometry.cs` |
| Layer/Cel visibility | `VectorScene.LayersAndTimeline.cs` | `$vector2d-timeline-animation` |
| Brush/eraser and painted regions | `VectorScene.BrushesAndEraser.cs`, `VectorScene.Paint.cs` | `$vector2d-brush-paint` |
| Tween evaluation | `VectorScene.Tweens.cs` | `$vector2d-timeline-animation` |

## Mutation Completion

After editing, update all affected contracts:

- populated/blank Cel content after membership changes
- spatial index and LOD summaries after geometry/visibility changes
- renderer caches after scene identity or revision changes
- selection and topology hits after compaction
- Stage, hierarchy, inspector, timeline, and composition underlay at the owning UI boundary

Use `BeginDeferredAppend` / `EndDeferredAppend` only for established bulk composition or stress paths.

## Units

- `1 px = 25 vu`
- `1 stroke pt = 0.5 px = 12.5 vu`
- quantize committed coordinates with `VectorUnits.Quantize`
- keep screen-space tolerances in pixels until conversion through Stage/VectorUnits APIs
