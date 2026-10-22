using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace VectorAnimationEngine;

internal enum ShotWorkspacePreviewMode
{
    ObserveCamera,
    LockCamera,
    CameraView
}

internal sealed partial class StageControl
{
    private PointF[] _shotPreviewAnchor = [];

    internal ShotWorkspacePreviewMode ShotPreviewMode { get; private set; }
    internal bool CanPreviewPlanarShot => _shotFramingVisible
        && _shotFramingSettings.PinsPlanarRotation && CanRenderPlanarShotPreview;
    private bool CanRenderPlanarShotPreview => ReferenceDimension == SceneDimension.TwoD
        && _reference2DViewDirection == ReferenceViewDirection.Front && !_referenceCameraTransitionActive;
    internal bool ShotWorkspacePreviewActive => CanPreviewPlanarShot
        && ShotPreviewMode != ShotWorkspacePreviewMode.ObserveCamera;

    internal void SetShotWorkspacePreviewMode(ShotWorkspacePreviewMode mode, bool reanchor = false)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!CanPreviewPlanarShot) mode = ShotWorkspacePreviewMode.ObserveCamera;
        if (ShotPreviewMode == mode && !reanchor) return;
        if (mode == ShotWorkspacePreviewMode.LockCamera)
        {
            if (!TryResolveShotFramingGeometry(_shotFramingSettings, out var geometry)) return;
            _shotPreviewAnchor = geometry.Corners
                .Select(point => new PointF(point.X - Width * 0.5f, point.Y - Height * 0.5f))
                .ToArray();
        }
        else
        {
            _shotPreviewAnchor = [];
        }
        ShotPreviewMode = mode;
        ClearGdiBaseFrameCache();
        Invalidate();
    }

    internal bool TryGetShotWorkspaceFrame(out PointF[] corners)
    {
        corners = [];
        if (!ShotWorkspacePreviewActive || Width <= 0 || Height <= 0) return false;
        if (ShotPreviewMode == ShotWorkspacePreviewMode.LockCamera && _shotPreviewAnchor.Length == 4)
        {
            corners = _shotPreviewAnchor.Select(point => new PointF(
                point.X + Width * 0.5f, point.Y + Height * 0.5f)).ToArray();
            var right = new PointF(corners[1].X - corners[0].X, corners[1].Y - corners[0].Y);
            var down = new PointF(corners[3].X - corners[0].X, corners[3].Y - corners[0].Y);
            var width = MathF.Sqrt(right.X * right.X + right.Y * right.Y);
            var height = MathF.Sqrt(down.X * down.X + down.Y * down.Y);
            if (width <= 0 || height <= 0) return false;
            var scale = width / height / _shotFramingSettings.ResolveAspectRatio(CanvasWorldWidth, CanvasWorldHeight);
            var center = new PointF((corners[0].X + corners[2].X) * 0.5f, (corners[0].Y + corners[2].Y) * 0.5f);
            var horizontal = new PointF(right.X * 0.5f, right.Y * 0.5f);
            var vertical = new PointF(down.X * scale * 0.5f, down.Y * scale * 0.5f);
            corners =
            [
                new(center.X - horizontal.X - vertical.X, center.Y - horizontal.Y - vertical.Y),
                new(center.X + horizontal.X - vertical.X, center.Y + horizontal.Y - vertical.Y),
                new(center.X + horizontal.X + vertical.X, center.Y + horizontal.Y + vertical.Y),
                new(center.X - horizontal.X + vertical.X, center.Y - horizontal.Y + vertical.Y)
            ];
            return true;
        }
        var margin = 24f * DeviceDpi / 96f;
        var bounds = FitPlanarShotFrame(_shotFramingSettings, new RectangleF(
            margin, margin, Math.Max(1, Width - margin * 2), Math.Max(1, Height - margin * 2)));
        corners = RectangleCorners(bounds);
        return true;
    }

    private void DrawShotWorkspacePreview(Graphics graphics)
    {
        ResetLastGdiFrameTelemetry();
        graphics.Clear(Color.FromArgb(255, 18, 20, 24));
        if (!TryGetShotWorkspaceFrame(out var corners)) return;
        var fitted = ShotPreviewMode == ShotWorkspacePreviewMode.CameraView;
        LastStats = DrawPlanarShotScene(graphics, _shotFramingSettings, corners,
            ClientSize, clipToCamera: fitted, drawGrid: !fitted);
        using var pen = new Pen(Theme.Accent, Math.Max(1f, DeviceDpi / 96f));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawPolygon(pen, corners);
    }

    private RenderStats DrawPlanarShotScene(Graphics graphics, SceneShotSettings settings,
        PointF[] destination, Size outputSize, bool clipToCamera, bool drawGrid)
    {
        var world = settings.ResolveWorldFrameCorners(CanvasWorldWidth, CanvasWorldHeight);
        var source = new[]
        {
            new PointF(world[0].X, world[0].Y),
            new PointF(world[1].X, world[1].Y),
            new PointF(world[3].X, world[3].Y)
        };
        var target = new[] { destination[0], destination[1], destination[3] };
        if (!TryCreateQuadTransform(source, target, out var worldToOutput))
        {
            worldToOutput.Dispose();
            return default;
        }
        using (worldToOutput)
        using (var inverse = worldToOutput.Clone())
        {
            if (!inverse.IsInvertible) return default;
            inverse.Invert();
            var visible = clipToCamera ? destination.ToArray()
                : RectangleCorners(new RectangleF(PointF.Empty, outputSize));
            inverse.TransformPoints(visible);
            var left = visible.Min(point => point.X);
            var top = visible.Min(point => point.Y);
            var right = visible.Max(point => point.X);
            var bottom = visible.Max(point => point.Y);
            if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) || !float.IsFinite(bottom)
                || right <= left || bottom <= top) return default;
            var cameraX = CameraX;
            var cameraY = CameraY;
            var zoom = Zoom;
            var reference = CaptureCurrentReferenceCameraFrame();
            var referenceProjection = RendersReferenceProjection;
            var gpuOptics = Reference3DGpuOpticsEnabled;
            var renderingCanvasOutput = _renderingCanvasOutput;
            var state = graphics.Save();
            try
            {
                _renderingCanvasOutput = clipToCamera && !drawGrid;
                CameraX = (left + right) * 0.5f;
                CameraY = (top + bottom) * 0.5f;
                Zoom = Math.Min(Width / VectorUnits.ToPixels(right - left), Height / VectorUnits.ToPixels(bottom - top));
                if (referenceProjection)
                {
                    ClearShotPreviewProjectionCaches();
                    ApplyReferenceCameraFrame(Ordinary2DReferenceCameraFrame(reference));
                }
                var projected = source.Select(WorldToScreen).ToArray();
                if (!TryCreateQuadTransform(projected, target, out var transform))
                {
                    transform.Dispose();
                    return default;
                }
                using (transform)
                {
                    if (clipToCamera)
                    {
                        using var clip = new GraphicsPath();
                        clip.AddPolygon(destination);
                        graphics.SetClip(clip, CombineMode.Intersect);
                    }
                    if (!_renderingCanvasOutput || CanvasPresentation is null)
                        graphics.FillRectangle(BrushFor(BackColor.ToArgb()), 0, 0, outputSize.Width, outputSize.Height);
                    using var commands = new MemoryStream();
                    var device = graphics.GetHdc();
                    Metafile recording;
                    try
                    {
                        recording = new Metafile(commands, device, ClientRectangle,
                            MetafileFrameUnit.Pixel, EmfType.EmfPlusOnly);
                    }
                    finally
                    {
                        graphics.ReleaseHdc(device);
                    }
                    using (recording)
                    {
                        RenderStats stats;
                        using (var sceneGraphics = Graphics.FromImage(recording))
                        {
                            sceneGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                            sceneGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            BeginScenePassOrder();
                            if (drawGrid) DrawGrid(sceneGraphics);
                            DrawCanvasBackgroundGdi(sceneGraphics);
                            stats = referenceProjection ? DrawReference3DScene(sceneGraphics)
                                : DrawGdiBase2D(sceneGraphics, backgroundAlreadyDrawn: true);
                        }
                        graphics.Transform = transform;
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.DrawImage(recording, ClientRectangle, ClientRectangle, GraphicsUnit.Pixel);
                        return stats;
                    }
                }
            }
            finally
            {
                CameraX = cameraX;
                CameraY = cameraY;
                Zoom = zoom;
                if (referenceProjection)
                {
                    ApplyReferenceCameraFrame(reference);
                    ClearShotPreviewProjectionCaches();
                }
                Reference3DGpuOpticsEnabled = gpuOptics;
                _renderingCanvasOutput = renderingCanvasOutput;
                graphics.Restore(state);
            }
        }
    }

    private void ClearShotPreviewProjectionCaches()
    {
        _reference3DProjectedContourCache.Clear();
        _reference3DProjectedSolidCache.Clear();
        _reference3DProjectiveMeshCache.Clear();
        _reference3DSortedLayerObjectsEpoch = -1;
    }

    private RectangleF FitPlanarShotFrame(SceneShotSettings settings, RectangleF available)
    {
        var aspect = settings.ResolveAspectRatio(CanvasWorldWidth, CanvasWorldHeight);
        var width = Math.Min(available.Width, available.Height * aspect);
        var height = width / aspect;
        return new RectangleF(available.Left + (available.Width - width) * 0.5f,
            available.Top + (available.Height - height) * 0.5f, width, height);
    }

    private static PointF[] RectangleCorners(RectangleF bounds) =>
    [
        new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top),
        new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)
    ];

    private bool TryCapturePlanarShotPreview(SceneShotSettings settings, Size targetSize, out Bitmap? bitmap)
    {
        bitmap = null;
        if (Width <= 8 || Height <= 8) return false;
        var bounds = FitPlanarShotFrame(settings, new RectangleF(0, 0,
            Math.Clamp(targetSize.Width, 8, 4096), Math.Clamp(targetSize.Height, 8, 4096)));
        var size = new Size(Math.Max(1, (int)MathF.Round(bounds.Width)), Math.Max(1, (int)MathF.Round(bounds.Height)));
        var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(result);
            DrawPlanarShotScene(graphics, settings,
                RectangleCorners(new RectangleF(PointF.Empty, size)), size, clipToCamera: true, drawGrid: false);
            bitmap = result;
            return true;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!ShotWorkspacePreviewActive) base.OnMouseWheel(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (!ShotWorkspacePreviewActive) base.OnMouseDoubleClick(e);
    }
}
