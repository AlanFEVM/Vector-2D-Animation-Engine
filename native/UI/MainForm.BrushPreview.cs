namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private int[] BuildBrushStrokeForCommit(DrawingToolCommit commit, Func<int[]> build)
    {
        try { return build(); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException
            or OverflowException or Clipper2Lib.ClipperLibException)
        {
            RestoreCanvasMutationSnapshot(commit.Snapshot);
            SetProjectDirty(commit.WasDirty);
            AppLog.Error($"Brush creation was rolled back: {exception}");
            return [];
        }
    }

    private bool TryUpdateBrushAreaPreviewSafely()
    {
        try { return TryUpdateBrushAreaPreview(); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException
            or OverflowException or Clipper2Lib.ClipperLibException)
        {
            ClearShapeGradientBrushPreview();
            AppLog.Warn($"Brush area preview failed; the trajectory preview will be used: {exception.Message}");
            return false;
        }
    }

    private readonly List<PointF> _brushPreviewTrajectory = [];
    private int _brushPreviewStableSourceCount;
    private int _brushPreviewStablePointCount;
    private readonly List<PressureBrushPoint> _brushPreviewPressureProfile = [];
    private int _brushPreviewPressureStableCount;
    private int _brushPreviewPressureSmoothing = -1;
    private int _brushPreviewPressureSealedCount;
    private VectorSceneSnapshot? _brushPreviewPressureSealedScene;

    private PressureBrushPoint[] ProcessBrushPressurePreview()
    {
        var smoothing = Math.Clamp(_freehandBrushSmoothing, 0, 100);
        if (_brushPreviewPressureProfile.Count > _brushAreaPressureProfile.Count
            || _brushPreviewPressureSmoothing != smoothing)
        {
            _brushPreviewPressureProfile.Clear();
            _brushPreviewPressureStableCount = 0;
        }
        _brushPreviewPressureSmoothing = smoothing;
        var support = BrushStrokeBuilder.PressureSmoothingSupport(smoothing);
        var first = Math.Max(0, _brushPreviewPressureStableCount - support);
        var tail = BrushStrokeBuilder.ProcessPressureTrajectory(
            _brushAreaPressureProfile.GetRange(first, _brushAreaPressureProfile.Count - first), smoothing);
        _brushPreviewPressureProfile.RemoveRange(_brushPreviewPressureStableCount,
            _brushPreviewPressureProfile.Count - _brushPreviewPressureStableCount);
        _brushPreviewPressureProfile.AddRange(tail.Skip(_brushPreviewPressureStableCount - first));
        _brushPreviewPressureStableCount = Math.Max(0, _brushAreaPressureProfile.Count - support - 1);
        return _brushPreviewPressureProfile.ToArray();
    }

    private int[] BuildPressureBrushAreaPreview()
    {
        const int sealedSpan = 64;
        FreehandStrokeProcessor.UpdatePressurePreviewProfile(
            _pressureBrushSamples, _freehandStrokeUnits, _brushAreaPressureProfile, _brushAreaPressureDistances);
        var profile = ProcessBrushPressurePreview();
        if (profile.Length == 0) return [];
        if (_brushPreviewPressureSealedScene is null)
            _brushPreviewPressureSealedScene = _brushAreaPreviewStage.CreateSnapshot();
        else _brushAreaPreviewStage.RestoreSnapshot(_brushPreviewPressureSealedScene);
        while (_brushPreviewPressureStableCount - _brushPreviewPressureSealedCount >= sealedSpan)
        {
            var last = _brushPreviewPressureSealedCount + sealedSpan;
            var additions = Append(last);
            _brushAreaPreviewStage.MergeSameColorFillsAroundNewObjects(additions, connectNearby: false, frame: 0);
            _brushPreviewPressureSealedCount = last;
            _brushPreviewPressureSealedScene = _brushAreaPreviewStage.CreateSnapshot();
        }
        return Append(profile.Length);

        int[] Append(int last)
        {
            var first = Math.Max(0, _brushPreviewPressureSealedCount - 1);
            return BrushStrokeBuilder.AddPressureProfile(
                _brushAreaPreviewStage, _scene.ActiveLayer, profile[first..last], _freehandColor, _brushShape,
                _freehandBrushSmoothing, _freehandPencilWorldPerPixel,
                _drawSettings.BrushFrequency, _drawSettings.BrushContinuous, trajectoryPrepared: true);
        }
    }

    private PointF[] ProcessBrushPreviewTrajectory()
    {
        _brushPreviewTrajectory.RemoveRange(_brushPreviewStablePointCount,
            _brushPreviewTrajectory.Count - _brushPreviewStablePointCount);
        while (_freehandSamples.Count - _brushPreviewStableSourceCount > BrushStrokeBuilder.TrajectoryFitSpan)
        {
            Append(BrushStrokeBuilder.TrajectoryFitSpan + 1);
            _brushPreviewStableSourceCount += BrushStrokeBuilder.TrajectoryFitSpan;
            _brushPreviewStablePointCount = _brushPreviewTrajectory.Count;
        }
        Append(_freehandSamples.Count - _brushPreviewStableSourceCount);
        return _brushPreviewTrajectory.ToArray();

        void Append(int count)
        {
            var span = _freehandSamples.GetRange(_brushPreviewStableSourceCount, count);
            foreach (var point in BrushStrokeBuilder.ProcessTrajectory(
                span, _freehandBrushSmoothing, _freehandPencilWorldPerPixel))
            {
                if (_brushPreviewTrajectory.Count == 0 || _brushPreviewTrajectory[^1] != point)
                    _brushPreviewTrajectory.Add(point);
            }
        }
    }

    private void ClearBrushPreviewTrajectory()
    {
        _brushPreviewTrajectory.Clear();
        _brushPreviewStableSourceCount = 0;
        _brushPreviewStablePointCount = 0;
        _brushPreviewPressureProfile.Clear();
        _brushPreviewPressureStableCount = 0;
        _brushPreviewPressureSmoothing = -1;
        _brushPreviewPressureSealedCount = 0;
        _brushPreviewPressureSealedScene = null;
    }
}
