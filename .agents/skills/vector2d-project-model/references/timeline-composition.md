# Timeline And Composition

## Timeline Semantics

- `ITimelineContext` supplies `Timeline`, `FrameCount`, target IDs, and synchronization.
- `AnimationTimelineTrack.Id` is stable UI identity; `TargetId` points to a layer or instance.
- `TimelineKeyframeKind.Populated` exposes content; `Blank` holds an empty exposure.
- `EvaluateExposure` returns the held source key and its end frame.
- Frame 0 always retains a key. Clearing it converts it to blank.
- Removing frames preserves a cel whose held content survives the removed range.
- Drawing layers need both a populated exposure and matching `VectorScene.ObjectKeyframeFrame` to expose an object.
- Batch related synchronization with `AnimationTimeline.BeginBatchUpdate` to emit one change notification.

Search these symbols before changing frame behavior:

```powershell
rg -n "EvaluateExposure|InsertFrame|RemoveFrame|InsertKeyframe|ClearKeyframe|SynchronizeTracks" native/Engine/AnimationTimeline.cs
rg -n "InsertTimeline|RemoveTimeline|ObjectKeyframeFrame|PopulateActiveKeyframeFrames" native/Engine/VectorScene.cs
```

## Composition Pipeline

`SceneCompositionBuilder` recursively resolves visible instances, evaluates local-frame exposure, accumulates 2D transforms, prepares source objects, appends them to a derived `VectorScene`, and records provenance.

Key entry points:

- `Build`: flatten a scene definition.
- `BuildDrawingObjectChildren`: create the nested underlay while editing a drawing object.
- `BuildDrawingObjectPreview`: create Vault/drag previews.
- `BuildLayers`: bucket active objects and choose scalar or packed/chunked append.
- `PrepareObject`: transform primitive, line, path, and freehand geometry.

## Composition Invariants

- Reject missing references and recursive containment before composition.
- Apply `ScaleX/Y`, `SkewX/Y`, `RotationZ`, then `X/Y` for current 2D flattening.
- Quantize transformed geometry with `VectorUnits`.
- Preserve fill/stroke ARGB, stroke width, atoms, line endpoint styles, object order, and source ownership.
- Materialize a sheared primitive as a path; keep paths, freehand strokes, and curves on their specialized append paths.
- Keep deterministic order across serial and parallel paths. Worker scheduling must not change layer or drawing order.
- Keep the owner array exactly aligned with appended destination indices.
- Changes affecting 8,192+ object batching must cover both packed and non-packed paths and run the stress benchmark.

Regression anchors are `RunProjectDocumentStructureRegression`, `RunSceneInstanceTimelineRegression`, and `RunSceneCompositionRegression` in `native/App/Benchmark.cs`.
