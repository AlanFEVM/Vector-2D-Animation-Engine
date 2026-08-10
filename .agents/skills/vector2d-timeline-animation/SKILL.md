---
name: vector2d-timeline-animation
description: Implement and review Vector 2D Animation Engine timeline and animation behavior involving frames, tracks, Cels, populated or blank keyframes, held exposures, frame insertion/removal, Auto Key, onion skin, playback, classic or shape tween, easing curves, TimelineStrip interaction, and timeline undo/history.
---

# Vector2D Timeline Animation

Keep timeline identity, Cel ownership, tween evaluation, editor feedback, and composed playback aligned.

## Route The Task

- Read [module-map.md](references/module-map.md) first to locate the model, geometry, UI, and regression partials.
- Read [timeline-contract.md](references/timeline-contract.md) for exposure, keyframe, tween, Auto Key, synchronization, or snapshot work.
- Also use `$vector2d-project-model` when a change alters instance targets, ownership, recursion, or flattened composition.
- Also use `$vector2d-stage-workflow` when changing onion-skin rendering, Stage playback feedback, or pointer/keyboard routing.
- Also use `$vector2d-winforms-ui` for reusable timeline controls or curve-editor layout.

## Workflow

1. Define the owning context, target ID, playhead frame, held exposure source, and expected Cel owner.
2. Preserve stable track IDs and synchronize targets without discarding surviving keys or tween spans.
3. Apply frame/key/tween edits through the established command and snapshot paths.
4. Recalculate affected tween spans and active object ownership before refreshing Stage or composition.
5. Keep one undo unit for each command or continuous curve-edit gesture.
6. Extend `Benchmark.TimelineSuite.cs` or `Benchmark.TimelineTween.cs` rather than the dispatcher.
7. Run `$vector2d-validate-change` with `Timeline`; add `Render` when Stage presentation changes.
8. Update `docs/USER_GUIDE.md` for user-visible commands or semantics.

## Invariants

- Frame 0 retains a key; clearing it produces a blank key.
- A populated exposure and matching `ObjectKeyframeFrame` are both required for local geometry visibility.
- Auto Key materializes the playhead frame before edits; with Auto Key off, edits target the held source.
- Tween spans require valid endpoints and deterministic evaluation; rejected edits must not leave partial state.
- Timeline UI selections use stable track IDs and frame coordinates, not row indices alone.
