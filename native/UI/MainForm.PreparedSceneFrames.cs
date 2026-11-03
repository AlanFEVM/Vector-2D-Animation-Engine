using System.Collections.Concurrent;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private readonly ToolStripStatusLabel _preparedFramesStatus = new();
    private WorkspaceFramePreparation? _workspacePreparation;
    private long _workspacePreparationRevision = long.MinValue;
    private readonly Dictionary<int, PreparedWorkspaceFrame> _preparedWorkspaceFrames = [];

    private sealed record PreparedWorkspaceFrame(int Frame, VectorScene Scene,
        SceneCompositionResult Composition, Bitmap Bitmap, RenderStats Stats);

    private void ClearPreparedWorkspaceFrames()
    {
        _stage.ReleasePreparedWorkspaceBaseBitmap();
        _workspacePreparation?.Cancel();
        _workspacePreparation = null;
        foreach (var frame in _preparedWorkspaceFrames.Values) frame.Bitmap.Dispose();
        _preparedWorkspaceFrames.Clear();
        _preparedFramesStatus.Text = "";
    }

    private bool TickPreparedWorkspaceFrames()
    {
        // The complete-project clone still has excessive peak memory on the
        // converted Flash fixture. Keep this preparation path experimental
        // until its source snapshot and geometry residency are bounded.
        if (Environment.GetEnvironmentVariable("VECTOR_PREPARE_ALL_WORKSPACE_FRAMES") != "1")
        {
            if (_workspacePreparation is not null) ClearPreparedWorkspaceFrames();
            return false;
        }
        // Full-quality GDI preparation is currently for the ordinary 2D scene
        // viewport. Other modes retain their existing rendering path.
        if (_playing || !IsSceneCompositionContext() || ActiveScene() is not { } definition
            || !_stage.Visible || _stage.ClientSize.Width <= 0 || _stage.ClientSize.Height <= 0
            || _stage.ReferenceDimension != SceneDimension.TwoD
            || _stage.ReferenceCameraTransitionActive || _stage.OnionSkinScene is not null
            || _stage.SceneCompositionMaskClips.Count != 0
            || definition.Layers.Any(layer => layer.Kind == SceneLayerKind.Mask
                || !string.IsNullOrWhiteSpace(layer.MaskLayerId)))
        {
            if (_workspacePreparation is not null) ClearPreparedWorkspaceFrames();
            return false;
        }
        var revision = _stage.Reference3DWorkspaceFrameCacheRevision;
        if (_workspacePreparation is not null && _workspacePreparationRevision != revision)
            ClearPreparedWorkspaceFrames();
        if (_workspacePreparation is null)
        {
            var start = _playbackSettings.StartFrame;
            var end = Math.Min(_playbackSettings.EndFrame, _timeline.Context.FrameCount - 1);
            var count = (long)end - start + 1;
            if (count <= 0 || count > 32
                || count * _stage.Width * _stage.Height * 4 > 128L * 1024 * 1024) return false;
            var assets = _project.ImageAssets.ToDictionary(asset => asset.Id,
                asset => (Path: ResolveImageAssetPath(asset), Settings: asset.ImportSettings));
            _workspacePreparationRevision = revision;
            _workspacePreparation = new WorkspaceFramePreparation(_project.CreateRestartSnapshot(),
                definition.Id, start, end, _playbackSettings.Fps,
                _stage.CaptureReference3DPlaybackPreparationState(),
                id => assets.TryGetValue(id, out var asset) && asset.Path is not null
                    ? BitmapImageRasterizer.Decode(asset.Path, asset.Settings) : null,
                id => assets.TryGetValue(id, out var asset) && asset.Settings.FilterMode == ImageFilterMode.Point
                    ? BitmapSampling.Point : BitmapSampling.Linear);
            _workspacePreparation.Start();
        }
        while (_workspacePreparation.Ready.TryDequeue(out var prepared))
            _preparedWorkspaceFrames.Add(prepared.Frame, prepared);
        var chinese = UiLocalization.CurrentLanguage == UiLanguage.SimplifiedChinese;
        _preparedFramesStatus.Text = _workspacePreparation.Error is { } error
            ? (chinese ? "帧准备失败" : "Frame preparation failed")
            : _preparedWorkspaceFrames.Count == _workspacePreparation.Count
                ? (chinese ? "帧已准备" : "Frames ready")
                : $"{(chinese ? "准备帧" : "Preparing frames")} {_preparedWorkspaceFrames.Count}/{_workspacePreparation.Count}";
        _preparedFramesStatus.ToolTipText = _workspacePreparation.Error ?? "";
        return true;
    }

    private bool TryBindPreparedWorkspaceFrame()
    {
        if (_workspacePreparation is null
            || _workspacePreparationRevision != _stage.Reference3DWorkspaceFrameCacheRevision
            || !_preparedWorkspaceFrames.TryGetValue(_frame, out var prepared)) return false;
        BindDerivedSceneFrame(prepared.Scene);
        _sceneCompositionResult = prepared.Composition;
        CaptureSceneCompositionCache(ActiveScene());
        return true;
    }

    private void ApplyPreparedWorkspaceBitmap()
    {
        if (_workspacePreparation is not null
            && _workspacePreparationRevision == _stage.Reference3DWorkspaceFrameCacheRevision
            && _preparedWorkspaceFrames.TryGetValue(_frame, out var prepared)
            && ReferenceEquals(prepared.Scene, _stage.Scene))
            _stage.UsePreparedWorkspaceBaseBitmap(prepared.Bitmap, prepared.Stats);
    }

    private sealed class WorkspaceFramePreparation
    {
        private readonly object _gate = new();
        private bool _cancelled;
        private readonly Thread _thread;
        internal readonly ConcurrentQueue<PreparedWorkspaceFrame> Ready = new();
        internal int Count { get; }
        internal volatile string? Error;

        internal WorkspaceFramePreparation(ProjectRestartSnapshot snapshot, string sceneId,
            int start, int end, decimal fps, Reference3DPlaybackPreparationState state,
            Func<string, BitmapImageRaster?> images, Func<string, BitmapSampling> sampling)
        {
            Count = end - start + 1;
            _thread = new Thread(() =>
            {
                try
                {
                    var project = VectorProject.RestoreRestartSnapshot(snapshot);
                    var definition = project.Scenes.First(scene => scene.Id == sceneId);
                    state = state with { SceneDefinition = definition };
                    using var stage = new StageControl(new VectorScene());
                    stage.Size = new Size(state.Width, state.Height);
                    stage.BitmapImageResolver = images;
                    stage.BitmapImageSamplingProvider = sampling;
                    long atoms = 0;
                    for (var frame = start; frame <= end; frame++)
                    {
                        lock (_gate) { if (_cancelled) return; }
                        var scene = new VectorScene();
                        var composition = SceneCompositionBuilder.Build(scene, definition,
                            project.DrawingObjects, frame, fps);
                        atoms += scene.VirtualAtomCount;
                        if (atoms > 2_000_000) throw new InvalidOperationException("Prepared geometry memory budget exceeded.");
                        stage.ConfigureReference3DPlaybackPreparation(scene, composition, frame, state);
                        var bitmap = stage.PrepareWorkspaceBaseBitmap();
                        lock (_gate)
                        {
                            if (_cancelled) { bitmap.Bitmap.Dispose(); return; }
                            Ready.Enqueue(new(frame, scene, composition, bitmap.Bitmap, bitmap.Stats));
                        }
                    }
                }
                catch (Exception exception)
                {
                    Error = exception.Message;
                    AppLog.Warn($"Workspace frame preparation failed: {exception}");
                }
            }) { IsBackground = true, Name = "Workspace frame preparation", Priority = ThreadPriority.BelowNormal };
            _thread.SetApartmentState(ApartmentState.STA);
        }
        internal void Start() => _thread.Start();
        internal void Cancel()
        {
            lock (_gate)
            {
                _cancelled = true;
                while (Ready.TryDequeue(out var frame)) frame.Bitmap.Dispose();
            }
        }
    }
}
