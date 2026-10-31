using System.Numerics;
using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal long SelectionRenderRevision { get; private set; }
    internal long SelectionGeometryRevision { get; private set; }
    private bool _selectionZoomPreviewActive;
    internal bool SelectionZoomPreviewActive => _selectionZoomPreviewActive;
}

internal sealed partial class Direct2DStageRenderer
{
    private ID2D1CommandList? _selectionCommands;
    private SelectionCommandKey _selectionCommandKey;
    private PointF _selectionCommandCamera;
    private float _selectionCommandZoom;
    private bool _recordingSelectionCommands;
    internal long SelectionCommandBuilds { get; private set; }
    internal long SelectionCommandReuses { get; private set; }

    private readonly record struct SelectionCommandKey(
        StageControl Stage, VectorScene Scene, long Revision, long Geometry, long Summary,
        long ActiveContent, int Frame, Size Size, float Pulse, ulong Layers, int ObjectAccent, int Dpi, float PatternZoom);

    private bool TryDrawCachedSelection(StageControl stage)
    {
        if (_target is not ID2D1DeviceContext context
            || stage.SelectionFeedbackSuppressed
            || stage.SelectedObjects.Count < 64
            || stage.UsesReferenceProjection
            || stage.MarqueeVisible
            || stage.HasDistortPreview)
        {
            ClearSelectionCommands();
            return false;
        }

        var pulse = stage.UsesObjectSelectionOverlay ? 0 : stage.SelectionZoomPreviewActive && _selectionCommands is not null
            ? _selectionCommandKey.Pulse
            : stage.SelectionHighlightPulse;
        var key = new SelectionCommandKey(stage, stage.Scene, stage.SelectionRenderRevision,
            stage.Scene.GeometryRevision, stage.Scene.SummaryRevision, stage.Scene.ActiveContentRevision,
            stage.Frame, stage.ClientSize, pulse,
            StageControl.GetReference3DWorkspaceFrameLayerRenderState(stage.Scene, stage.Frame),
            stage.UsesObjectSelectionOverlay ? Theme.Accent.ToArgb() : 0, stage.DeviceDpi,
            stage.UsesObjectSelectionOverlay ? stage.Zoom : 0);
        if (_selectionCommands is null || key != _selectionCommandKey)
        {
            ClearSelectionCommands();
            using var previousTarget = context.Target;
            var previousTransform = context.Transform;
            context.EndDraw().CheckError();
            var commands = context.CreateCommandList();
            try
            {
                context.Target = commands;
                context.BeginDraw();
                try
                {
                    // The recorded commands stay anchored to the committed selection. A
                    // selection drag is a pure screen translation, so it is applied to the
                    // replayed image below instead of rebuilding the whole overlay every
                    // pointer move.
                    _recordingSelectionCommands = true;
                    DrawSelectionCore(stage);
                }
                finally
                {
                    _recordingSelectionCommands = false;
                    context.EndDraw().CheckError();
                }
                commands.Close().CheckError();
                _selectionCommands = commands;
                _selectionCommandKey = key;
                _selectionCommandCamera = new PointF(stage.CameraX, stage.CameraY);
                _selectionCommandZoom = stage.Zoom;
                SelectionCommandBuilds++;
            }
            catch
            {
                commands.Dispose();
                throw;
            }
            finally
            {
                context.Target = previousTarget;
                context.Transform = previousTransform;
                context.BeginDraw();
            }
        }
        else SelectionCommandReuses++;

        var dragOffset = stage.SelectionDragPreviewOffset;
        var dragTranslateX = VectorUnits.ToPixels(dragOffset.X) * stage.Zoom;
        var dragTranslateY = VectorUnits.ToPixels(dragOffset.Y) * stage.Zoom;
        var scale = stage.Zoom / _selectionCommandZoom;
        var translateX = stage.Width * 0.5f * (1f - scale)
            + VectorUnits.ToPixels(_selectionCommandCamera.X - stage.CameraX) * stage.Zoom
            + dragTranslateX;
        var translateY = stage.Height * 0.5f * (1f - scale)
            + VectorUnits.ToPixels(_selectionCommandCamera.Y - stage.CameraY) * stage.Zoom
            + dragTranslateY;
        var transform = context.Transform;
        try
        {
            context.Transform = transform
                * Matrix3x2.CreateScale(scale)
                * Matrix3x2.CreateTranslation(translateX, translateY);
            context.DrawImage(_selectionCommands, null, null,
                stage.UsesObjectSelectionOverlay ? InterpolationMode.NearestNeighbor : InterpolationMode.Linear,
                CompositeMode.SourceOver);
        }
        finally { context.Transform = transform; }
        return true;
    }

    private void ClearSelectionCommands()
    {
        _selectionCommands?.Dispose();
        _selectionCommands = null;
        _selectionCommandKey = default;
        _selectionCommandZoom = 0;
        ClearMergedSelectionGeometry();
    }

    internal void InvalidateSelectionCommands() => ClearSelectionCommands();
}
