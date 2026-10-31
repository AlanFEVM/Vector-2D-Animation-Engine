using System.Diagnostics;
using System.Numerics;
using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private bool TryRenderShotWorkspacePreview(StageControl stage, out RenderStats stats)
    {
        stats = default;
        if (_target is not ID2D1DeviceContext context || !stage.TryGetShotWorkspaceFrame(out var corners)) return false;
        ClearBaseFrameCache();
        var started = Stopwatch.GetTimestamp();
        using var previousTarget = context.Target;
        using var commands = context.CreateCommandList();
        var transform = Matrix3x2.Identity;
        var drawing = false;
        var fitted = stage.ShotPreviewMode == ShotWorkspacePreviewMode.CameraView;
        try
        {
            context.Target = commands;
            context.Transform = Matrix3x2.Identity;
            context.BeginDraw();
            drawing = true;
            stats = stage.RenderShotWorkspaceScene(projection =>
            {
                var elements = projection.Elements;
                transform = new Matrix3x2(elements[0], elements[1], elements[2], elements[3], elements[4], elements[5]);
                stage.BeginScenePassOrder();
                if (!fitted) DrawGrid(stage);
                DrawCanvasBackground(stage);
                if (stage.RendersReferenceProjection) return DrawReference3DScene(stage);
                var editable = stage.Scene;
                var combined = default(RenderStats);
                try
                {
                    if (stage.OnionSkinScene is { } onionSkin)
                    {
                        stage.Scene = onionSkin;
                        stage.RecordOnionSkinScenePass();
                        combined = DrawScene(stage, int.MaxValue, forceObjectRenderer: true);
                    }
                    if (stage.UnderlayScene is { } underlay)
                    {
                        stage.RecordUnderlayScenePass();
                        if (stage.IsUnderlayHostScene(editable))
                        {
                            _underlayRenderOrder.Collect(underlay, stage.VisibleWorldBounds(), stage.Frame);
                            _underlayDrawLimit = int.MaxValue;
                            _underlayDrawnObjects = 0;
                            combined = RenderStats.Combine(combined, new RenderStats(
                                _underlayRenderOrder.VisibleCount, _underlayRenderOrder.VisibleCount,
                                _underlayRenderOrder.VisibleAtoms, 0, _underlayRenderOrder.ScannedCount, false));
                        }
                        else
                        {
                            stage.Scene = underlay;
                            combined = RenderStats.Combine(combined, DrawScene(stage, int.MaxValue, forceObjectRenderer: true));
                        }
                    }
                    stage.Scene = editable;
                    stage.RecordEditableScenePass();
                    return RenderStats.Combine(combined, DrawScene(stage, int.MaxValue, forceObjectRenderer: true));
                }
                finally { stage.Scene = editable; }
            });
            var result = context.EndDraw();
            drawing = false;
            result.CheckError();
            commands.Close().CheckError();
            context.Target = previousTarget;
            context.Transform = Matrix3x2.Identity;
            context.BeginDraw();
            drawing = true;
            var background = ToD2D(System.Drawing.Color.FromArgb(255, 18, 20, 24));
            context.Clear(in background);
            if (fitted)
                context.PushAxisAlignedClip(Rect(corners[0].X, corners[0].Y,
                    corners[2].X - corners[0].X, corners[2].Y - corners[0].Y), AntialiasMode.Aliased);
            try
            {
                if (!fitted || stage.CanvasPresentation is null)
                    context.FillRectangle(Rect(0, 0, stage.Width, stage.Height), BrushFor(stage.BackColor.ToArgb()));
                context.Transform = transform;
                context.DrawImage(commands);
            }
            finally
            {
                context.Transform = Matrix3x2.Identity;
                if (fitted) context.PopAxisAlignedClip();
            }
            var border = BrushFor(Theme.Accent.ToArgb());
            for (var index = 0; index < corners.Length; index++)
                context.DrawLine(ToVector(corners[index]), ToVector(corners[(index + 1) % corners.Length]),
                    border, Math.Max(1f, stage.DeviceDpi / 96f));
            LastCommandMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var presentStarted = Stopwatch.GetTimestamp();
            result = context.EndDraw();
            drawing = false;
            result.CheckError();
            _gpuDevice?.Present();
            LastPresentMilliseconds = Stopwatch.GetElapsedTime(presentStarted).TotalMilliseconds;
            _consecutiveFailures = 0;
            return true;
        }
        finally
        {
            if (drawing) context.EndDraw();
            context.Target = previousTarget;
            context.Transform = Matrix3x2.Identity;
        }
    }
}
