# Timeline Contract

## Identity And Exposure

- `AnimationTimelineTrack.Id` is stable UI identity; `TargetId` points to a layer or instance.
- `EvaluateExposure` returns the held source key and its end frame.
- Drawing-object tracks list layer targets before nested-instance targets. Scene tracks target scene instances.
- Synchronization preserves surviving track IDs, keyframes, and compatible tween spans.

## Cel Ownership

- `VectorScene.ObjectKeyframeFrame` identifies the Cel that owns local geometry.
- Inserting, removing, clearing, duplicating, and materializing frames must keep exposure and object ownership consistent.
- New drawing layers begin blank. Instances use their owning layer's exposure; do not create synthetic instance tracks.

## Tweens

- Validate endpoint compatibility before creating classic or shape tween spans.
- Recalculate spans when endpoints, geometry, materials, transforms, or frame placement changes.
- Preserve monotone easing-curve anchors and stable endpoint values.
- Scalar, packed, preview, onion-skin, and composed evaluation must agree for the same frame.

## Interaction

- Batch related model updates so one command emits one logical notification and undo entry.
- Preserve selection by track ID and frame when rows reorder.
- Canvas shortcuts must not consume keys owned by timeline editors or numeric controls.
