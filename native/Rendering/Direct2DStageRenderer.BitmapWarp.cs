using System.Numerics;
using Vortice.Direct2D1;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private StageGpuBitmapWarp? _bitmapWarp;
    private bool _bitmapWarpUnavailable;
    private bool _bitmapWarpMeshPrimed;
    private BitmapDistortRasterizer.PreviewMeshScratch? _bitmapWarpMeshScratch;
    private Vector4[] _bitmapWarpGrid = [];
    private BitmapWarpFrame? _bitmapWarpFrame;
    internal double LastBitmapWarpMeshMilliseconds { get; private set; }
    internal double LastBitmapWarpDrawMilliseconds { get; private set; }

    private sealed record BitmapWarpFrame(VectorScene Scene, int Index, BitmapImageRasterKey Image,
        float X, float Y, float Width, float Height, float Angle, Size Viewport,
        float CameraX, float CameraY, float Zoom, float Rotation, bool Nearest,
        IReadOnlyList<DistortWarp> Warps, ID2D1Bitmap Bitmap);

    private static bool IsBitmapWarpEditing(StageControl stage) => (stage.DistortMode || stage.HasDistortPreview)
        && !stage.UsesReferenceProjection && !stage.RenderingCanvasOutput && !stage.ShotWorkspacePreviewActive;

    private bool CanDrawGpuBitmapWarps(StageControl stage)
    {
        if (_gpuDevice is null || _bitmapWarpUnavailable || !IsBitmapWarpEditing(stage)) return false;
        var selected = stage.SelectedObject;
        var warmSelection = !stage.HasDistortPreview && (uint)selected < stage.Scene.ObjectCount
            && stage.Scene.ShapeKind[selected] == ShapeKind.Bitmap;
        if (!warmSelection && !stage.SceneRequiresSoftwareDistortion(stage.Scene)
            && !stage.SceneRequiresSoftwareDistortion(stage.UnderlayScene)
            && !stage.SceneRequiresSoftwareDistortion(stage.OnionSkinScene)
            && !stage.SceneRequiresSoftwareDistortion(stage.DragPreviewScene)) return true;
        try
        {
            _bitmapWarp ??= new StageGpuBitmapWarp(_gpuDevice);
            if (warmSelection && !stage.Scene.TryGetObjectDistortionsView(selected, out _)
                && stage.Scene.TryGetBitmapObjectData(selected, out var data)
                && stage.TryDecodeBitmapImage(data, out var image))
            {
                if (_bitmapWarpFrame is { } cached && cached.Viewport != stage.ClientSize) _bitmapWarpFrame = null;
                if (!_bitmapWarpMeshPrimed && _bitmapWarpFrame is null)
                {
                    _bitmapWarpMeshPrimed = true;
                    // Warm the curved mesh and driver upload during selection. The
                    // result stays offscreen and never enters the frame cache.
                    var bounds = stage.Scene.GetRawObjectWorldBounds(selected);
                    var frame = TransformOverlayFrame.FromBounds(bounds);
                    var envelope = DistortEnvelope.FromBounds(bounds);
                    if (frame.IsValid && envelope.IsValid)
                    {
                        var anchor = envelope.GetAnchors(DistortSide.Top)[0];
                        var handle = new DistortHandleRef(DistortSide.Top, anchor.Id, DistortHandleKind.OutgoingControl);
                        envelope = envelope.WithHandle(handle, anchor.OutgoingControl,
                            new PointF(anchor.OutgoingControl.X, anchor.OutgoingControl.Y - bounds.Height * .2f));
                        var density = Math.Min(stage.WorldLengthToScreen(1), 2048 / Math.Max(bounds.Width, bounds.Height));
                        DrawGpuBitmapWarp(stage, stage.Scene, selected, [new DistortWarp(frame, envelope)], density);
                    }
                }
                _bitmapWarp.Prime(BitmapImageRasterizer.ApplyObjectClip(image, data), stage.ClientSize);
            }
            return true;
        }
        catch (Exception error)
        {
            _bitmapWarpUnavailable = true;
            AppLog.Warn($"GPU image warp unavailable: {error.Message}");
            return false;
        }
    }

    private void DrawGpuBitmapWarp(StageControl stage, VectorScene scene, int index,
        IReadOnlyList<DistortWarp> distortions, double? warmupDensity = null)
    {
        if (!scene.TryGetBitmapObjectData(index, out var data)
            || !stage.TryDecodeBitmapImage(data, out var source)) return;
        source = BitmapImageRasterizer.ApplyObjectClip(source, data);
        var nearest = stage.BitmapImageSampling(data.ImageAssetId) == BitmapSampling.Point;
        if (warmupDensity is null && _bitmapWarpFrame is { } cached && ReferenceEquals(cached.Scene, scene) && cached.Index == index
            && cached.Image == source.Key && cached.X == scene.X[index] && cached.Y == scene.Y[index]
            && cached.Width == scene.Width[index] && cached.Height == scene.Height[index] && cached.Angle == scene.Angle[index]
            && cached.Viewport == stage.ClientSize && cached.CameraX == stage.CameraX && cached.CameraY == stage.CameraY
            && cached.Zoom == stage.Zoom && cached.Rotation == stage.PlanarCanvasRotation && cached.Nearest == nearest
            && VectorScene.DistortionsEqual(cached.Warps, distortions))
        {
            LastBitmapWarpMeshMilliseconds = LastBitmapWarpDrawMilliseconds = 0;
            DrawBitmapWarpFrame(stage, scene, index, cached.Bitmap);
            return;
        }
        _bitmapWarpFrame = null;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var vertexCount = PrepareBitmapWarpGrid(stage, scene, index, distortions, warmupDensity);
        if (vertexCount == 0)
        {
            if (warmupDensity.HasValue) return;
            throw new InvalidOperationException("Image warp mesh exceeds its precision budget.");
        }
        LastBitmapWarpMeshMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        var bitmap = _bitmapWarp!.Draw(source, _bitmapWarpGrid, vertexCount, stage.ClientSize, nearest, flushD2D: warmupDensity is null);
        _bitmapWarpMeshPrimed = true;
        LastBitmapWarpDrawMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (warmupDensity.HasValue) return;
        _bitmapWarpFrame = new(scene, index, source.Key, scene.X[index], scene.Y[index], scene.Width[index],
            scene.Height[index], scene.Angle[index], stage.ClientSize, stage.CameraX, stage.CameraY, stage.Zoom,
            stage.PlanarCanvasRotation, nearest, distortions, bitmap);
        DrawBitmapWarpFrame(stage, scene, index, bitmap);
    }

    private int PrepareBitmapWarpGrid(StageControl stage, VectorScene scene, int index,
        IReadOnlyList<DistortWarp> distortions, double? warmupDensity = null)
    {
        var mesh = BitmapDistortRasterizer.BuildPreviewMesh(scene, index, distortions,
            warmupDensity ?? stage.WorldLengthToScreen(1), _bitmapWarpMeshScratch ??= new());
        if (mesh is null) return 0;
        var vertexCount = checked(mesh.Columns * mesh.Rows * 6);
        var gridLength = checked((mesh.Columns + 1) * (mesh.Rows + 1) + 1);
        if (_bitmapWarpGrid.Length < gridLength) _bitmapWarpGrid = new Vector4[gridLength];
        _bitmapWarpGrid[0] = new Vector4(mesh.Columns, 0, 0, 0);
        var stride = mesh.Columns + 1;
        var sx = 2f / stage.Width;
        var sy = 2f / stage.Height;
        for (var row = 0; row <= mesh.Rows; row++)
        for (var column = 0; column <= mesh.Columns; column++)
        {
            var i = row * stride + column;
            var world = mesh.Points[i];
            var screen = stage.WorldToScreen(world.X, world.Y);
            _bitmapWarpGrid[i + 1] = new Vector4(screen.X * sx - 1, 1 - screen.Y * sy,
                (float)column / mesh.Columns, (float)row / mesh.Rows);
        }
        return vertexCount;
    }

    private void DrawBitmapWarpFrame(StageControl stage, VectorScene scene, int index, ID2D1Bitmap bitmap)
    {
        var previous = _target!.Transform;
        try
        {
            _target.Transform = Matrix3x2.Identity;
            _target.DrawBitmap(bitmap, Rect(0, 0, stage.Width, stage.Height),
                System.Drawing.Color.FromArgb(scene.Argb[index]).A / 255f,
                BitmapInterpolationMode.NearestNeighbor, Rect(0, 0, stage.Width, stage.Height));
        }
        finally { _target.Transform = previous; }
    }
}
