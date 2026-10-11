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
    internal long SelectionCommandBuilds { get; private set; }
    internal long SelectionCommandReuses { get; private set; }

    private readonly record struct SelectionCommandKey(
        StageControl Stage, VectorScene Scene, long Revision, long Geometry, long Summary,
        long ActiveContent, int Frame, Size Size, float Pulse, ulong Layers);

    private bool TryDrawCachedSelection(StageControl stage)
    {
        if (_target is not ID2D1DeviceContext context
            || stage.SelectedObjects.Count < 64
            || stage.UsesReferenceProjection
            || stage.MarqueeVisible
            || stage.SelectionDragPreviewActive
            || stage.HasDistortPreview)
        {
            ClearSelectionCommands();
            return false;
        }

        var pulse = stage.SelectionZoomPreviewActive && _selectionCommands is not null
            ? _selectionCommandKey.Pulse
            : stage.SelectionHighlightPulse;
        var key = new SelectionCommandKey(stage, stage.Scene, stage.SelectionRenderRevision,
            stage.Scene.GeometryRevision, stage.Scene.SummaryRevision, stage.Scene.ActiveContentRevision,
            stage.Frame, stage.ClientSize, pulse,
            StageControl.GetReference3DWorkspaceFrameLayerRenderState(stage.Scene, stage.Frame));
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
                try { DrawSelectionCore(stage); }
                finally { context.EndDraw().CheckError(); }
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

        var scale = stage.Zoom / _selectionCommandZoom;
        var translateX = stage.Width * 0.5f * (1f - scale)
            + VectorUnits.ToPixels(_selectionCommandCamera.X - stage.CameraX) * stage.Zoom;
        var translateY = stage.Height * 0.5f * (1f - scale)
            + VectorUnits.ToPixels(_selectionCommandCamera.Y - stage.CameraY) * stage.Zoom;
        var transform = context.Transform;
        try
        {
            context.Transform = transform
                * Matrix3x2.CreateScale(scale)
                * Matrix3x2.CreateTranslation(translateX, translateY);
            context.DrawImage(_selectionCommands);
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
    }

    internal void InvalidateSelectionCommands() => ClearSelectionCommands();
}
