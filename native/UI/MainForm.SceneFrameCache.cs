namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private const int MaximumSceneFrameCompositions = 8;
    private const long MaximumSceneFrameCompositionAtoms = 1_000_000;
    private readonly List<SceneFrameComposition> _sceneFrameCompositions = [];

    private sealed record SceneFrameComposition(int Frame, VectorScene Scene,
        SceneCompositionResult Composition, long GeometryRevision, long SummaryRevision);

    private bool PrepareSceneFrameCompositionCache(SceneDefinition? definition, bool preserveWorkspaceFrames)
    {
        var enabled = !_playing && IsSceneCompositionContext();
        var sourcesUnchanged = _sceneCompositionCacheValid
            && ReferenceEquals(definition, _sceneCompositionCacheScene)
            && _sceneCompositionCacheFps == _playbackSettings.Fps
            && !_sceneInstancePreviewDirty
            && _sceneCompositionSourceRevisions.Length == _drawingObjects.Count;
        if (sourcesUnchanged)
        {
            for (var index = 0; index < _drawingObjects.Count; index++)
            {
                var source = _drawingObjects[index].Scene;
                var previous = _sceneCompositionSourceRevisions[index];
                if (!ReferenceEquals(source, previous.Scene)
                    || source.GeometryRevision != previous.GeometryRevision
                    || source.SummaryRevision != previous.SummaryRevision)
                {
                    sourcesUnchanged = false;
                    break;
                }
            }
        }
        if (!enabled || !preserveWorkspaceFrames || !sourcesUnchanged)
        {
            _sceneFrameCompositions.Clear();
            ClearPreparedWorkspaceFrames();
        }
        if (!enabled) return false;
        if (TryBindPreparedWorkspaceFrame()) return true;

        var cachedIndex = _sceneFrameCompositions.FindIndex(entry => entry.Frame == _frame
            && entry.Scene.GeometryRevision == entry.GeometryRevision
            && entry.Scene.SummaryRevision == entry.SummaryRevision);
        if (cachedIndex >= 0)
        {
            var cached = _sceneFrameCompositions[cachedIndex];
            _sceneFrameCompositions.RemoveAt(cachedIndex);
            _sceneFrameCompositions.Add(cached);
            BindDerivedSceneFrame(cached.Scene);
            _sceneCompositionResult = cached.Composition;
            _sceneCompositionCacheFrame = _frame;
            return true;
        }

        // Cached scenes are immutable derived frames. Build the next frame in
        // a fresh destination so its geometry and mask plan retain identity.
        if (_sceneFrameCompositions.Any(entry => ReferenceEquals(entry.Scene, _sceneEditStage))
            || _preparedWorkspaceFrames.Values.Any(entry => ReferenceEquals(entry.Scene, _sceneEditStage)))
            BindDerivedSceneFrame(new VectorScene());
        return false;
    }

    private void BindDerivedSceneFrame(VectorScene scene)
    {
        if (ReferenceEquals(_sceneEditStage, scene)) return;
        var previous = _sceneEditStage;
        _sceneEditStage = scene;
        if (!ReferenceEquals(_scene, previous)) return;
        _scene = scene;
        // This is a frame change in the same editing context, like playback's
        // immutable scene handoff. BindScene would reset camera/selection state
        // and discard the separately validated workspace frame cache.
        _stage.Scene = scene;
        _libraryVaultPanel.BindScene(scene, () => _selectedObject);
    }

    private void StoreSceneFrameCompositionCache(SceneDefinition? definition)
    {
        if (_playing || definition is null || !IsSceneCompositionContext()
            || _sceneEditStage.VirtualAtomCount > MaximumSceneFrameCompositionAtoms) return;
        _sceneFrameCompositions.RemoveAll(entry => entry.Frame == _frame);
        while (_sceneFrameCompositions.Count > 0
            && (_sceneFrameCompositions.Count >= MaximumSceneFrameCompositions
                || _sceneFrameCompositions.Sum(entry => entry.Scene.VirtualAtomCount)
                    + _sceneEditStage.VirtualAtomCount > MaximumSceneFrameCompositionAtoms))
            _sceneFrameCompositions.RemoveAt(0);
        _sceneFrameCompositions.Add(new(_frame, _sceneEditStage, _sceneCompositionResult,
            _sceneEditStage.GeometryRevision, _sceneEditStage.SummaryRevision));
    }
}
