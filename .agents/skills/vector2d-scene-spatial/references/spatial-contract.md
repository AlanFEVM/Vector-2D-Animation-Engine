# Scene Spatial Contract

## Transform And Ownership

- Keep stable scene-instance IDs through selection, layer assignment, timeline targets, composition, and projected hit results.
- Apply the repository's established scale/skew/rotation/translation order consistently; preserve 2D behavior when spatial fields remain at defaults.
- Resolve nested provenance before writing a transform. Derived composition object indices are not editable identity.

## Camera And Interaction

- Use the same view/projection parameters for drawing, hit testing, handles, and status feedback.
- Define start/update/commit/cancel/capture-loss behavior for every spatial drag.
- Camera transition sampling must be time-based, monotonic, cancelable, and exact at completion.

## Masks And Rendering

- Validate mask relationships and layer ranges before composition.
- Apply mask clips before isolated blend groups and preserve deterministic layer/object order.
- Keep Direct2D and GDI reference passes equivalent for visibility, clipping, opacity, projection, and overlays.
- Dispose target-bound spatial resources after target reset or module reload.
