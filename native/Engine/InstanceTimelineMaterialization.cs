namespace VectorAnimationEngine;

// Shared instance interpolation only. The owning composition retains validation,
// batching, snapshots, rollback, and any geometry/content synchronization.
internal static class InstanceTimelineMaterialization
{
    internal static void InsertLinearFrames(
        AnimationTimeline timeline,
        string trackId,
        DrawingObjectInstanceDefinition instance,
        int startFrame,
        int endFrame,
        InstanceFrameState source,
        InstanceFrameState target)
    {
        for (var frame = startFrame + 1; frame < endFrame; frame++)
        {
            if (!timeline.InsertKeyframe(trackId, frame))
            {
                throw new InvalidOperationException(
                    "The tween span could not create an intermediate keyframe.");
            }

            instance.SetStateAtFrame(
                frame,
                DrawingObjectInstanceDefinition.InterpolateState(
                    source,
                    target,
                    (float)(frame - startFrame) / (endFrame - startFrame)));
        }
    }

    internal static bool RefreshFrames(DrawingObjectInstanceDefinition instance, TimelineTween tween)
    {
        var source = instance.EvaluateState(tween.StartFrame);
        var target = instance.EvaluateState(tween.EndFrame);
        var changed = false;
        for (var frame = tween.StartFrame + 1; frame < tween.EndFrame; frame++)
        {
            changed |= instance.SetStateAtFrame(
                frame,
                DrawingObjectInstanceDefinition.InterpolateState(
                    source,
                    target,
                    tween.ProgressAt(frame)));
        }

        return changed;
    }
}
