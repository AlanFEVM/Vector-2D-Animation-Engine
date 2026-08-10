# Composition And Provenance

## Pipeline

`SceneCompositionBuilder` recursively resolves visible instances, evaluates the requested frame through the owning timeline contract, applies transforms/masks/layer state, appends to a derived `VectorScene`, and records source ownership.

Key entry points include scene flattening, drawing-object child underlays, Vault/drag previews, layer batching, object preparation, and owner alignment. Search the requested builder method and its callers before opening the full file.

## Invariants

- Reject missing references and recursive containment before traversal.
- Preserve deterministic layer, object, and suborder across serial and parallel paths.
- Apply established instance transforms consistently; specialized spatial projection belongs to `$vector2d-scene-spatial`.
- Preserve fill/stroke/material data, atoms, endpoint styles, paths, freehand data, painted regions, masks, and source ownership.
- Materialize a sheared primitive as a path and keep specialized geometry on its established append path.
- Keep every owner/provenance array exactly aligned with appended destination indices.
- Changes to high-object-count batching cover packed and scalar paths and run `Stress` with an uncontended machine.

## Regression Anchors

Use `native/App/Benchmark.ProjectComposition.cs`. Add timeline evaluation cases through `$vector2d-timeline-animation`, persistence round trips through `$vector2d-assets-persistence`, and visible mask/reference cases through `$vector2d-scene-spatial`.
