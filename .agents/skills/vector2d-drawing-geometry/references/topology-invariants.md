# Topology Editing Invariants

## Element Model

- `Fill`: a filled region, possibly with compound contours and holes.
- `Stroke`: Line or Pencil geometry.
- `BoundaryStroke`: the outlined edge owned by a fill object.
- `DrawingElementHit` carries source object, element kind, derived part index, distance, and parameter range.
- `DrawingTopologyRules.MinStrokeSegmentUnits = 1`; suppress smaller fragments.
- `DrawingTopologyRules.UnitIntersectionTolerance = 0.001`.

Candidate collection, intersection sampling, and part numbering depend on the active geometry. Materialize or detach before editing a part.

## Transaction Pattern

1. Capture a `VectorSceneSnapshot`.
2. Validate every selected part against the current candidate set; abort the entire operation on stale input.
3. Build replacements without mutating arrays.
4. Compute one removal/compaction plan.
5. Copy retained objects and sparse geometry consistently.
6. Append replacement fill, stroke, and boundary objects with replacement suborders.
7. Return `OldToNewObjectIndex` and source-to-result part mappings.
8. Synchronize cel content and rebuild derived geometry state.
9. Restore the snapshot on failure.

## Preservation Checklist

Preserve the source's:

- layer and `ObjectKeyframeFrame`
- fill/stroke ARGB and stroke width
- virtual atom count
- outer line endpoint style; newly created internal cut endpoints normally use the established round/internal convention
- `ObjectOrder` and deterministic `ObjectSubOrder`
- compound contour holes and islands

Fill materialization may also emit boundary-stroke objects. Selection callers must handle both result element kinds.

## Merge Rules

- Same-color fill merging is same layer/cel and exact ARGB. Nearby merging uses the existing 10 vu boundary rule and must not invent extra filled area.
- Straight-line merging requires straight, collinear, direction-compatible segments with matching material, width, endpoint compatibility, layer, and cel. The endpoint distance boundary is strictly under 5 vu.
- Merge scope matters: a contextual line-chain command must not change compatible lines outside its requested scope.
- Snapshot first because merge paths can reject the computed result and roll back.

## Regression Anchors

- `RunDrawingTopologyRegression`
- `RunLineSegmentMergeRegression`
- `RunMarqueeElementQueryRegression`
- `RunMarqueeLineMaterializationRegression`
- `RunMarqueeFillMaterializationRegression`
- `RunOutlinedFillMergeRegression`
- `RunBrushEraserRegression`

Place new cases in `Benchmark.TopologyMaterialization.cs`, `Benchmark.BezierGeometry.cs`, or the nearest focused partial, and run the `Freehand` suite.
