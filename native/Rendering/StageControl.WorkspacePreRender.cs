namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal ulong Reference3DWorkspaceFrameLayerRenderState =>
        GetReference3DWorkspaceFrameLayerRenderState(Scene, Frame);

    internal bool TryPreRenderReference3DFrame(int frame)
    {
        return TryPreRenderReference3DFrameCore(
            frame,
            overrideScene: null,
            overrideComposition: null,
            overrideCompositionScene: null,
            overrideMaskClips: null,
            hasSceneOverride: false);
    }

    internal bool TryPreRenderReference3DFrame(
        int frame,
        VectorScene scene,
        SceneCompositionResult composition,
        IReadOnlyList<SceneCompositionMaskClip>? maskClips = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return TryPreRenderReference3DFrameCore(
            frame,
            scene,
            composition,
            scene,
            maskClips,
            hasSceneOverride: true);
    }

    private bool TryPreRenderReference3DFrameCore(
        int frame,
        VectorScene? overrideScene,
        SceneCompositionResult? overrideComposition,
        VectorScene? overrideCompositionScene,
        IReadOnlyList<SceneCompositionMaskClip>? overrideMaskClips,
        bool hasSceneOverride)
    {
        if (_disposingResources
            || _paintInProgress
            || _interactiveInputDepth > 0
            || !IsHandleCreated
            || UnderlayScene is not null
            || OnionSkinScene is not null
            || DragPreviewScene is not null)
        {
            return false;
        }

        var previousScene = Scene;
        var previousFrame = _frame;
        var previousSceneEditFrame = previousScene.EditFrame;
        var previousComposition = _sceneCompositionResult;
        var previousCompositionScene = _sceneCompositionResultScene;
        var previousHasSpatialPoses = _sceneCompositionHasSpatialPoses;
        var previousMaskClips = _sceneCompositionMaskClips;
        try
        {
            if (hasSceneOverride)
            {
                Scene = overrideScene!;
                _sceneCompositionResult = overrideComposition;
                _sceneCompositionResultScene = overrideCompositionScene;
                _sceneCompositionHasSpatialPoses = overrideComposition?.ObjectPoses.Any(
                    pose => pose.FlatToScene != System.Numerics.Matrix4x4.Identity) == true;
                _sceneCompositionMaskClips = overrideMaskClips?.ToArray() ?? [];
            }

            _frame = Math.Max(0, frame);
            if (ReferenceEquals(Scene, previousScene)) Scene.EditFrame = _frame;
            return _direct2DRenderer.TryPreRenderReference3DFrame(this);
        }
        finally
        {
            if (ReferenceEquals(Scene, previousScene)) previousScene.EditFrame = previousSceneEditFrame;
            Scene = previousScene;
            _frame = previousFrame;
            _sceneCompositionResult = previousComposition;
            _sceneCompositionResultScene = previousCompositionScene;
            _sceneCompositionHasSpatialPoses = previousHasSpatialPoses;
            _sceneCompositionMaskClips = previousMaskClips;
        }
    }
}
