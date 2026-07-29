# VectorScene Map

## Storage

`VectorScene` stores most object data in parallel arrays:

- ownership: `ObjectLayer`, `ObjectKeyframeFrame`
- order: `ObjectOrder`, `ObjectSubOrder`
- transform/bounds: `X`, `Y`, `Width`, `Height`, `Angle`
- style: `Argb`, `StrokeArgb`, `Stroke`, endpoint styles
- kind/complexity: `ShapeKind`, `AtomCount`
- curve control: `CurveControlX/Y`
- sparse geometry: path contour and freehand point dictionaries

Any new per-object field must be covered by allocation/growth, append, compaction, snapshot, restore, composition, and rendering.

## Operation Families

| Change | Start at |
| --- | --- |
| Primitive or line creation | `AddObject`, `AddLineSegment`, `AddCurveSegment` |
| Path/compound fill | `AddPathObjectContours`, `TryGetPathWorldContours` |
| Pencil/brush | `AddFreehandStroke`, `AddSoftBrushStroke`, `AddPressureBrushStroke` |
| Eraser | `EraseWithBrushStroke`, `TryBuildBrushEraserPlan` |
| Transform | `TransformObjects`, `ShearObjects`, line endpoint setters |
| Timeline ownership | `IsObjectActive`, `PopulateActiveKeyframeFrames`, timeline commands |
| Hit/query | `HitTestElement`, `QueryDrawingElementsInsideBounds` |
| Split/materialize | `MaterializeSelectedParts`, `MaterializeMarquee*Parts` |
| Fill union | `MergeSameColorFillsAround` |
| Straight-line simplification | `MergeCompatibleLineSegments` |
| Derived state | `RebuildGeometryIndex`, `CompleteDeferredBuild` |

## Mutation Completion

After editing, check which of these must run:

- update populated/blank keyframe content after object count changes in a cel
- rebuild the spatial index after geometry or object membership changes
- rebuild/invalidate overview/detail summaries after visible geometry or color changes
- remap selection and topology hits after compaction
- refresh Stage, hierarchy, inspector, timeline, and composition underlay in `MainForm`

`BeginDeferredAppend` / `EndDeferredAppend` are for bulk composition or stress construction. Keep normal interactive edits on the established transactional path.

## Units

- `1 px = 25 vu`
- `1 stroke pt = 0.5 px = 12.5 vu`
- quantize committed coordinates with `VectorUnits.Quantize`
- keep screen-space smoothing/tolerance in pixels, then convert through Stage/VectorUnits APIs
