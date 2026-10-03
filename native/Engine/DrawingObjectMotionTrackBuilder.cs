using System.Numerics;

namespace VectorAnimationEngine;

/// <summary>
/// Samples the motion track of one symbol instance over the onion-skin pointer range.
/// <para>
/// This is pure engine-side computation: it reads the symbol anchor, the instance's evaluated
/// per-frame state, and the owning layer's exposure/tween metadata, then returns an immutable
/// <see cref="DrawingObjectMotionTrack"/>. It performs no UI work, allocates only the anchor
/// array it returns, and never throws on malformed input, because the Stage calls it during
/// every onion-skin repaint.
/// </para>
/// </summary>
internal static class DrawingObjectMotionTrackBuilder
{
    /// <summary>
    /// Samples the motion track of <paramref name="instance"/> for the frames
    /// <c>[centerFrame - previousFrames, centerFrame + nextFrames]</c>.
    /// </summary>
    /// <param name="symbol">Tracked symbol definition. Supplies <c>Anchor</c> and is the pivot source.</param>
    /// <param name="instance">Tracked instance. Supplies <c>EvaluateState</c> and <c>StateKeyframes</c>.</param>
    /// <param name="track">Timeline track of the layer holding the instance. Supplies <c>EvaluateExposure</c>.</param>
    /// <param name="centerFrame">Onion-skin centre (the current playhead frame).</param>
    /// <param name="previousFrames">Frames sampled to the left; 0 disables that side.</param>
    /// <param name="nextFrames">Frames sampled to the right; 0 disables that side.</param>
    /// <returns>
    /// A track whose anchors are ordered by ascending frame, or <see cref="DrawingObjectMotionTrack.Empty"/>
    /// when an argument is missing or no frame in the range is inside the track.
    /// </returns>
    public static DrawingObjectMotionTrack Build(
        DrawingObjectDefinition symbol,
        DrawingObjectInstanceDefinition instance,
        AnimationTimelineTrack track,
        int centerFrame,
        int previousFrames,
        int nextFrames)
    {
        if (symbol is null || instance is null || track is null) return DrawingObjectMotionTrack.Empty;

        // A negative side count is meaningless; clamp rather than letting it widen the range.
        previousFrames = Math.Max(0, previousFrames);
        nextFrames = Math.Max(0, nextFrames);
        return BuildCore(symbol, instance, track, centerFrame, centerFrame - previousFrames, centerFrame + nextFrames);
    }

    /// <summary>
    /// Samples the motion track at an explicit frame list. Used by regression coverage and by callers
    /// that already resolved the range they want; <paramref name="centerFrame"/> is recorded on the
    /// result for the overlay's hit-testing.
    /// </summary>
    public static DrawingObjectMotionTrack BuildForFrames(
        DrawingObjectDefinition symbol,
        DrawingObjectInstanceDefinition instance,
        AnimationTimelineTrack track,
        int centerFrame,
        IEnumerable<int> frames)
    {
        if (symbol is null || instance is null || track is null || frames is null)
        {
            return DrawingObjectMotionTrack.Empty;
        }

        // Callers may pass an unordered or duplicated set; normalize before sampling so the sampled
        // region and the emitted order are both ascending.
        var ordered = frames.Distinct().Where(frame => frame >= 0).ToArray();
        Array.Sort(ordered);
        if (ordered.Length == 0) return DrawingObjectMotionTrack.Empty;

        return BuildCore(symbol, instance, track, centerFrame, ordered[0], ordered[^1], ordered);
    }

    private static DrawingObjectMotionTrack BuildCore(
        DrawingObjectDefinition symbol,
        DrawingObjectInstanceDefinition instance,
        AnimationTimelineTrack track,
        int centerFrame,
        int firstFrame,
        int lastFrame,
        int[]? explicitFrames = null)
    {
        var duration = track.Duration;
        var anchors = new List<MotionTrackAnchor>(explicitFrames?.Length ?? Math.Max(0, lastFrame - firstFrame + 1));

        // Frames outside the track are skipped, so an onion-skin pointer range that runs past either
        // end of the timeline simply yields a shorter track.
        var from = Math.Max(0, firstFrame);
        var to = Math.Min(duration - 1, lastFrame);
        if (to < from) return CreateEmpty(track, centerFrame);

        // The instance's own explicit state keys are the "adjusted" marker; the view is already
        // ordered by frame, so a single walk with a moving cursor keeps this linear.
        var stateKeyframeCursor = 0;
        var stateKeyframes = instance.StateKeyframes;

        if (explicitFrames is null)
        {
            for (var frame = from; frame <= to; frame++)
            {
                AppendAnchor(anchors, symbol, instance, track, stateKeyframes, ref stateKeyframeCursor, frame);
            }
        }
        else
        {
            foreach (var frame in explicitFrames)
            {
                if (frame < from || frame > to) continue;
                AppendAnchor(anchors, symbol, instance, track, stateKeyframes, ref stateKeyframeCursor, frame);
            }
        }

        if (anchors.Count == 0) return CreateEmpty(track, centerFrame);

        return new DrawingObjectMotionTrack
        {
            CenterFrame = centerFrame,
            FirstFrame = anchors[0].Frame,
            LastFrame = anchors[^1].Frame,
            Anchors = anchors
        };
    }

    private static void AppendAnchor(
        List<MotionTrackAnchor> anchors,
        DrawingObjectDefinition symbol,
        DrawingObjectInstanceDefinition instance,
        AnimationTimelineTrack track,
        IReadOnlyList<InstanceStateKeyframe> stateKeyframes,
        ref int stateKeyframeCursor,
        int frame)
    {
        var kind = ClassifyFrame(track, frame);
        var onTweenSegment = IsInsideTweenSpan(track, frame);

        var state = instance.EvaluateState(frame);
        var worldPosition = Vector2.Transform(
            new Vector2(symbol.Anchor.X, symbol.Anchor.Y),
            InstanceMatrix(symbol, state));

        // A non-finite transform (degenerate scale, hand-edited project data) would poison the whole
        // polyline, so drop that frame instead of propagating NaN into the overlay.
        if (!float.IsFinite(worldPosition.X) || !float.IsFinite(worldPosition.Y)) return;

        var adjusted = kind != MotionTrackFrameKind.BlankKeyframe
            && IsAdjustedFrame(instance, stateKeyframes, ref stateKeyframeCursor, frame);

        anchors.Add(new MotionTrackAnchor(
            frame,
            new PointF(worldPosition.X, worldPosition.Y),
            kind,
            adjusted,
            onTweenSegment));
    }

    /// <summary>
    /// Classifies one frame from the layer's exposure and tween metadata. Populated keys own their
    /// content, blank keys (whether cleared or held from a blank) share the blank ink, frames inside
    /// a valid tween span interpolate, and everything else is a held exposure.
    /// </summary>
    /// <remarks>
    /// The tween check precedes the plain keyframe check on purpose. When a tween is created the
    /// engine materializes a populated keyframe at <em>every</em> interior frame
    /// (<see cref="InstanceTimelineMaterialization.InsertLinearFrames"/>), so such a frame reports
    /// <c>IsKeyframe == true</c> even though the user never authored it. Testing <c>IsKeyframe</c>
    /// first would therefore make <see cref="MotionTrackFrameKind.TweenFrame"/> unreachable for
    /// exactly the instance tweens this track exists to describe.
    /// </remarks>
    private static MotionTrackFrameKind ClassifyFrame(AnimationTimelineTrack track, int frame)
    {
        var exposure = track.EvaluateExposure(frame);
        if (!exposure.HasContent) return MotionTrackFrameKind.BlankKeyframe;
        if (exposure.SourceKind == TimelineKeyframeKind.Blank) return MotionTrackFrameKind.BlankKeyframe;
        if (IsInsideTweenSpan(track, frame)) return MotionTrackFrameKind.TweenFrame;
        return exposure.IsKeyframe ? MotionTrackFrameKind.Keyframe : MotionTrackFrameKind.HeldFrame;
    }

    /// <summary>
    /// True when <paramref name="frame"/> lies strictly between the endpoints of a valid tween span.
    /// The endpoints themselves are plain keyframes, so only the interior is a tween segment.
    /// </summary>
    private static bool IsInsideTweenSpan(AnimationTimelineTrack track, int frame)
    {
        var tweens = track.Tweens;
        for (var index = 0; index < tweens.Count; index++)
        {
            var tween = tweens[index];
            if (!tween.IsValid) continue;
            if (tween.StartFrame >= frame) break;
            if (frame < tween.EndFrame) return true;
        }

        return false;
    }

    /// <summary>
    /// True when the frame carries its own explicit placement rather than inheriting one. Frame 0 is
    /// special: the instance folds its frame-0 state into the base state, so it counts as adjusted
    /// once the base position moved off the origin.
    /// </summary>
    private static bool IsAdjustedFrame(
        DrawingObjectInstanceDefinition instance,
        IReadOnlyList<InstanceStateKeyframe> stateKeyframes,
        ref int cursor,
        int frame)
    {
        if (frame == 0) return instance.X != 0f || instance.Y != 0f;

        // StateKeyframes is ordered by ascending frame; the cursor only ever moves forward because
        // callers sample frames in ascending order.
        if (cursor > 0 && stateKeyframes[cursor - 1].Frame >= frame) cursor = 0;
        while (cursor < stateKeyframes.Count && stateKeyframes[cursor].Frame < frame) cursor++;
        return cursor < stateKeyframes.Count && stateKeyframes[cursor].Frame == frame;
    }

    /// <summary>
    /// Rebuilds the composition transform used by <c>SceneCompositionBuilder.InstanceMatrix</c>:
    /// the symbol anchor is shifted to the origin and then placed by the instance's frame state.
    /// </summary>
    private static Matrix3x2 InstanceMatrix(DrawingObjectDefinition symbol, InstanceFrameState state)
    {
        return Matrix3x2.CreateTranslation(-symbol.Anchor.X, -symbol.Anchor.Y)
            * DrawingObjectInstanceDefinition.CreatePlanarTransform(state);
    }

    private static DrawingObjectMotionTrack CreateEmpty(AnimationTimelineTrack track, int centerFrame)
    {
        return ReferenceEquals(track, null)
            ? DrawingObjectMotionTrack.Empty
            : new DrawingObjectMotionTrack { CenterFrame = centerFrame };
    }
}
