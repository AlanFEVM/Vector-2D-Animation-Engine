# Brush And Paint Contract

## Input And Commit

- Keep raw pointer/pressure sampling separate from smoothing and scene commit.
- Convert tolerances and brush radii at the Stage boundary; scene geometry remains in vector units.
- Pair every preview/cursor setter with cleanup on commit, cancel, capture loss, tool switch, frame switch, and deactivation.
- Capture one undo snapshot before the first mutation in a gesture.

## Painted Regions

- Preserve per-vertex ARGB, deterministic triangle/region order, layer, Cel, and object order.
- Merge only compatible topmost regions in the intended scope; never absorb unrelated paint islands.
- Hit testing, marquee, transform, delete, fill erasing, snapshots, and persistence must operate on the painted region rather than only its legacy trajectory.
- New writes may use the current region representation, but old trajectory payloads remain load/render compatible.

## Rendering And Caches

- Keep Direct2D and GDI output logically equivalent for opacity, blend, clipping, and render order.
- Key raster caches by the scene/region data they consume and dispose target-bound resources after target reset.
- A rendering optimization must not change color interpolation, boundary coverage, or draw order.

## Regression Selection

- Pressure sampling or continuity: `Pressure`.
- Brush commit, mixing, region merge, hit testing, or erasing: `Freehand`.
- Raster cache, GDI/Direct2D parity, or visible output: add `Render`.
