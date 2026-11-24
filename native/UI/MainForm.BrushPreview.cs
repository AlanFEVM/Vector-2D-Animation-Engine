namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private float _pressureBrushSizePoints = 10;
    private PressureBrushOptions _freehandPressureOptions = PressureBrushOptions.Default;
    private BrushShape? _pressureTipShape;
    private (int Roundness, int Angle) _pressureTipKey;

    private bool ActiveBrushContinuous() => _tool == ToolMode.PressureBrush || _drawSettings.BrushContinuous;

    private BrushShape ActiveBrushShape()
    {
        if (_tool != ToolMode.PressureBrush) return _brushShape;
        var options = _freehandDrawing ? _freehandPressureOptions : _drawSettings.PressureBrushOptions;
        var key = (options.Roundness, options.Angle);
        if (_pressureTipShape is null || _pressureTipKey != key)
        {
            _pressureTipKey = key;
            _pressureTipShape = BrushShape.CreateTraditionalBrush(
                TraditionalBrushTipKind.Round, options.Roundness, options.Angle);
        }
        return _pressureTipShape;
    }

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

    private int[] CommitPressureBrushPreview()
    {
        // Pointer-up can contain samples that have not reached the throttled preview yet.
        // Finish its mutable tail, then extract the displayed ink into editable geometry.
        if (_pressureInk is not null && UpdatePressureInkPreview())
        {
            return _pressureInk.Commit(_scene, _scene.ActiveLayer, _freehandColor);
        }
        if (!TryUpdateBrushAreaPreview(force: true)) return [];
        return BrushStrokeBuilder.AddPreparedBrushStroke(_scene, _scene.ActiveLayer, _brushAreaPreviewStage);
    }

    private BrushStrokeBuilder.PressureInk? _pressureInk;

    private bool UpdatePressureInkPreview()
    {
        if (!_freehandPressureBrush || _materialEditor.GetGradientPaintForTarget(strokeTarget: false).Kind != GradientKind.Solid)
            return false;
        FreehandStrokeProcessor.UpdatePressurePreviewProfile(_pressureBrushSamples, _freehandStrokeUnits,
            _brushAreaPressureProfile, _brushAreaPressureDistances, _freehandPressureOptions);
        var profile = ProcessBrushPressurePreview();
        _pressureInk ??= new BrushStrokeBuilder.PressureInk(ActiveBrushShape());
        _pressureInk.Update(profile, _brushPreviewPressureStableCount, _freehandBrushSmoothing, _freehandPencilWorldPerPixel);
        ClearShapeGradientBrushPreview();
        _stage.SetPressureInkPreview(_pressureInk.Pieces, _freehandColor);
        return true;
    }

    private readonly List<PointF> _brushPreviewTrajectory = [];
    private int _brushPreviewStableSourceCount;
    private int _brushPreviewStablePointCount;
    private readonly List<PressureBrushPoint> _brushPreviewPressureProfile = [];
    private int _brushPreviewPressureStableCount;
    private int _brushPreviewPressureSmoothing = -1;
    private int _brushPreviewPressureSealedCount;
    private BrushStrokeBuilder.PressurePreview? _brushPressureGeometry;

    private PressureBrushPoint[] ProcessBrushPressurePreview()
    {
        var smoothing = Math.Clamp(_freehandBrushSmoothing, 0, FreehandStrokeProcessor.MaximumBrushSmoothing);
        if (_brushPreviewPressureProfile.Count > _brushAreaPressureProfile.Count
            || _brushPreviewPressureSmoothing != smoothing)
        {
            _brushPreviewPressureProfile.Clear();
            _brushPreviewPressureStableCount = 0;
        }
        _brushPreviewPressureSmoothing = smoothing;
        var support = BrushStrokeBuilder.PressureSmoothingSupport(smoothing);
        var first = Math.Max(0, _brushPreviewPressureStableCount - support);
        var source = _brushAreaPressureProfile.GetRange(first, _brushAreaPressureProfile.Count - first);
        var tail = _freehandPressureBrush
            ? FreehandStrokeProcessor.SmoothPressureProfileSpatial(source, smoothing, _freehandPencilWorldPerPixel)
            : BrushStrokeBuilder.ProcessPressureTrajectory(source, smoothing);
        _brushPreviewPressureProfile.RemoveRange(_brushPreviewPressureStableCount,
            _brushPreviewPressureProfile.Count - _brushPreviewPressureStableCount);
        _brushPreviewPressureProfile.AddRange(tail.Skip(_brushPreviewPressureStableCount - first));
        _brushPreviewPressureStableCount = Math.Max(0, _brushAreaPressureProfile.Count - support - 1);
        var result = _brushPreviewPressureProfile.ToArray();
        var firstTapered = _freehandPressureOptions.ApplyEndTaper(result, _freehandStrokeUnits);
        _brushPreviewPressureStableCount = Math.Min(_brushPreviewPressureStableCount, firstTapered);
        return result;
    }

    private int[] BuildPressureBrushAreaPreview()
    {
        FreehandStrokeProcessor.UpdatePressurePreviewProfile(
            _pressureBrushSamples, _freehandStrokeUnits, _brushAreaPressureProfile, _brushAreaPressureDistances, _freehandPressureOptions);
        var profile = ProcessBrushPressurePreview();
        if (profile.Length == 0) return [];
        _brushPressureGeometry ??= new BrushStrokeBuilder.PressurePreview(ActiveBrushShape());
        // This scene contains one gesture only. Its union is built before fitting, rather
        // than repeatedly materializing and merging editable paths from preceding frames.
        var additions = _brushPressureGeometry.Build(
            _brushAreaPreviewStage, _scene.ActiveLayer, profile, _brushPreviewPressureStableCount,
            _freehandColor, _freehandBrushSmoothing, _freehandPencilWorldPerPixel);
        _brushPreviewPressureSealedCount = _brushPressureGeometry.SealedCount;
        return additions;
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
        _brushPressureGeometry = null;
    }
}
