using Clipper2Lib;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private bool _highlightOcclusionPrepared;
    private (VectorScene Scene, long Geometry, long Summary, long Content, int Frame, ulong Layers,
        StageViewState View, Size Size, int Paint) _highlightOcclusionKey;
    private long _highlightOcclusionState;
    private int? _bitmapHighlightWorldState;
    internal long HighlightCoverageBuilds { get; private set; }
    private readonly Dictionary<int, PointF[][]> _highlightOcclusionContours = new();
    private readonly Dictionary<int, PointF[][]> _opaqueHighlightContours = new();

    // Include presentation and raster identity: an image can become transparent after
    // relinking/import-setting changes without changing its placement geometry.
    internal long HighlightOcclusionState
    {
        get
        {
            if (_paintInProgress && _highlightOcclusionPrepared) return _highlightOcclusionState;
            var paint = new System.HashCode();
            for (var index = 0; index < Scene.ObjectCount; index++) paint.Add(Scene.Argb[index]);
            var state = (Scene, Scene.GeometryRevision, Scene.SummaryRevision, Scene.ActiveContentRevision,
                Frame, GetReference3DWorkspaceFrameLayerRenderState(Scene, Frame), CaptureViewState(), Size, paint.ToHashCode());
            if (_highlightOcclusionPrepared && _highlightOcclusionKey == state) return _highlightOcclusionState;
            _highlightOcclusionPrepared = true;
            _highlightOcclusionKey = state;
            var hash = new System.HashCode();
            hash.Add(Scene); hash.Add(Scene.GeometryRevision); hash.Add(Scene.SummaryRevision);
            hash.Add(Scene.ActiveContentRevision); hash.Add(Frame);
            hash.Add(GetReference3DWorkspaceFrameLayerRenderState(Scene, Frame));
            // Only ordinary 2D bitmap contours are independent of view scale.
            // Sparse local queries use a screen-sized padding and retain their
            // original per-view invalidation, as do projection and previews.
            var reusable = Scene.ObjectCount <= 2048 && !UsesReferenceProjection
                && !HasDistortPreview && !Scene.HasDisplayLayerEffects && !HasSceneCompositionMaskClips(Scene);
            for (var index = 0; index < Scene.ObjectCount; index++)
            {
                reusable &= Scene.ShapeKind[index] == ShapeKind.Bitmap
                    && !Scene.TryGetObjectDistortionsView(index, out _);
                hash.Add(Scene.X[index]); hash.Add(Scene.Y[index]);
                hash.Add(Scene.Width[index]); hash.Add(Scene.Height[index]); hash.Add(Scene.Angle[index]);
                hash.Add(Scene.ObjectHidden[index]);
                hash.Add(Scene.Argb[index]);
                if (Scene.ShapeKind[index] == ShapeKind.Bitmap && Scene.TryGetBitmapObjectData(index, out var bitmap)
                    && TryGetBitmapImageKey(bitmap.ImageAssetId, out var key))
                { hash.Add(key); hash.Add(IsBitmapImageKnownOpaque(bitmap.ImageAssetId, key)); }
            }
            var worldState = hash.ToHashCode();
            if (!reusable || _bitmapHighlightWorldState != worldState)
            {
                _highlightOcclusionContours.Clear();
                _opaqueHighlightContours.Clear();
            }
            _bitmapHighlightWorldState = reusable ? worldState : null;
            // Screen command lists still need fresh stroke widths and clipping.
            hash.Add(CaptureViewState()); hash.Add(Size);
            _highlightOcclusionState = hash.ToHashCode();
            return _highlightOcclusionState;
        }
    }

    internal PointF[][] GetHighlightOcclusionContours(int objectIndex)
    {
        _ = HighlightOcclusionState;
        if ((uint)objectIndex >= Scene.ObjectCount) return [];
        if (_highlightOcclusionContours.TryGetValue(objectIndex, out var cached)) return cached;
        // Sparse large scenes use local queries, avoiding quadratic prefix storage.
        if (Scene.ObjectCount > 2048)
        {
            var localCoverage = new PathsD();
            var bounds = Scene.GetObjectWorldBounds(objectIndex);
            bounds.Inflate(ScreenLengthToWorld(4), ScreenLengthToWorld(4));
            foreach (var candidate in Scene.QueryObjects(bounds, Frame, int.MaxValue))
            {
                if (candidate == objectIndex || Scene.ObjectLayer[candidate] > Scene.ObjectLayer[objectIndex]
                    || Scene.ObjectLayer[candidate] == Scene.ObjectLayer[objectIndex]
                        && SceneRenderOrderBuffer.CompareObjects(Scene, candidate, objectIndex) <= 0) continue;
                localCoverage.AddRange(HighlightFillPaths(OpaqueHighlightContours(candidate)));
            }
            var localUnion = Clipper.Union(localCoverage, new PathsD(), FillRule.NonZero, 4);
            var local = HighlightPoints(localUnion);
            _highlightOcclusionContours[objectIndex] = local;
            return local;
        }
        // Build the union above every object in one front-to-back sweep. Adjacent
        // selections share the same prefix instead of repeating spatial queries,
        // source lookups and polygon unions for each of hundreds of objects.
        HighlightCoverageBuilds++;
        var coverage = new PathsD();
        PointF[][] visibleCoverage = [];
        foreach (var candidate in Scene.GetActiveObjectIndices(Frame).ToArray()
            .OrderBy(index => Scene.ObjectLayer[index]).ThenByDescending(index => Scene.ObjectOrder[index])
            .ThenByDescending(index => Scene.ObjectSubOrder[index]).ThenByDescending(index => index))
        {
            _highlightOcclusionContours[candidate] = visibleCoverage;
            var fill = HighlightFillPaths(OpaqueHighlightContours(candidate));
            if (fill.Count == 0) continue;
            coverage = Clipper.Union(coverage, fill, FillRule.NonZero, 4);
            visibleCoverage = HighlightPoints(coverage);
        }
        return _highlightOcclusionContours.GetValueOrDefault(objectIndex, []);
    }

    private PointF[][] OpaqueHighlightContours(int candidate)
    {
        if (_opaqueHighlightContours.TryGetValue(candidate, out var cached)) return cached;
        if (!Scene.IsObjectActive(candidate, Frame) || IsObjectHiddenForRendering(Scene, candidate)) return [];
        var opaque = Scene.IsOpaqueSelectionFill(candidate);
        if (Scene.ShapeKind[candidate] == ShapeKind.Bitmap)
        {
            opaque = (uint)Scene.Argb[candidate] >> 24 == 255
                && Scene.TryGetBitmapObjectData(candidate, out var bitmap)
                && TryGetBitmapImageKey(bitmap.ImageAssetId, out var key)
                && BitmapImageRasterizer.ObjectClipKey(key, bitmap) == key
                && IsBitmapImageKnownOpaque(bitmap.ImageAssetId, key);
            for (var layer = (int)Scene.ObjectLayer[candidate]; opaque && layer >= 0; layer = Scene.GetLayerParentIndex(layer))
                opaque = Scene.ShouldRenderLayerContent(layer) && !Scene.IsLayerEffectivelyOutlined(layer)
                    && Scene.LayerOpacity[layer] == 1 && Scene.LayerBlendModes[layer] == LayerBlendMode.Normal
                    && !Scene.GetLayerSymbolFilters(layer).HasEnabled && !Scene.TryGetMaskLayerIndex(layer, out _);
        }
        if (!opaque || HasSceneCompositionMaskClips(Scene)) return [];
        var worldContours = Scene.ShapeKind[candidate] == ShapeKind.Bitmap
            ? GetObjectBoundaryContoursForRendering(Scene, candidate)
            : Scene.ShapeKind[candidate] == ShapeKind.Text && Scene.TryGetTextWorldContours(candidate, out var glyphs)
                ? glyphs.Select(contour => contour.Select(point => MapObjectPointForRendering(Scene, candidate, point)).ToArray()).ToArray()
                : Scene.SelectionOccluderContours(candidate);
        _opaqueHighlightContours[candidate] = worldContours;
        return worldContours;
    }

    private static PathsD HighlightFillPaths(PointF[][] contours) => Clipper.Union(new PathsD(contours
        .Where(contour => contour.Length >= 3).Select(contour =>
            new PathD(contour.Select(point => new PointD(point.X, point.Y))))), new PathsD(), FillRule.EvenOdd, 4);

    private static PointF[][] HighlightPoints(PathsD contours) => contours.Select(contour =>
        contour.Select(point => new PointF((float)point.x, (float)point.y)).ToArray()).ToArray();

    internal PointF[][] ClipHighlightPolylines(int objectIndex, PointF[][] contours, bool closed, bool clipToView = false)
    {
        var occlusion = GetHighlightOcclusionContours(objectIndex);
        if (occlusion.Length == 0) return closed
            ? contours.Select(contour => contour.Length > 1 && contour[0] != contour[^1] ? contour.Append(contour[0]).ToArray() : contour).ToArray()
            : contours;
        var subjects = new PathsD();
        foreach (var contour in contours)
        {
            if (contour.Length < 2) continue;
            var points = closed && contour[0] != contour[^1] ? contour.Append(contour[0]) : contour;
            subjects.Add(new(points.Select(point => new PointD(point.X, point.Y))));
        }
        if (clipToView)
        {
            var viewport = VisibleWorldBounds();
            viewport.Inflate(ScreenLengthToWorld(8), ScreenLengthToWorld(8));
            var viewportClip = new ClipperD(4);
            viewportClip.AddOpenSubject(subjects);
            viewportClip.AddClip(new PathsD { new() { new(viewport.Left, viewport.Top), new(viewport.Right, viewport.Top),
                new(viewport.Right, viewport.Bottom), new(viewport.Left, viewport.Bottom) } });
            var onScreen = new PathsD();
            if (!viewportClip.Execute(ClipType.Intersection, FillRule.NonZero, new PathsD(), onScreen))
                throw new InvalidOperationException("Highlight viewport clipping failed.");
            subjects = onScreen;
            if (subjects.Count == 0) return [];
        }
        var clipper = new ClipperD(4);
        clipper.AddOpenSubject(subjects);
        clipper.AddClip(new PathsD(occlusion.Select(contour => new PathD(contour.Select(point => new PointD(point.X, point.Y))))));
        var visible = new PathsD();
        if (!clipper.Execute(ClipType.Difference, FillRule.NonZero, new PathsD(), visible))
            throw new InvalidOperationException("Highlight outline clipping failed.");
        return visible.Select(contour => contour.Select(point => new PointF((float)point.x, (float)point.y)).ToArray()).ToArray();
    }

    private PointF[][] ClipHighlightFill(int objectIndex, PointF[][] contours)
    {
        var occlusion = GetHighlightOcclusionContours(objectIndex);
        if (occlusion.Length == 0) return contours;
        var visible = Clipper.Difference(
            new PathsD(contours.Select(contour => new PathD(contour.Select(point => new PointD(point.X, point.Y))))),
            new PathsD(occlusion.Select(contour => new PathD(contour.Select(point => new PointD(point.X, point.Y))))),
            FillRule.EvenOdd, 4);
        return visible.Select(contour => contour.Select(point => new PointF((float)point.x, (float)point.y)).ToArray()).ToArray();
    }
}
