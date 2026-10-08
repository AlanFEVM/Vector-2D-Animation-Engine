namespace VectorAnimationEngine;

internal sealed class VectorSceneSnapshot
{
    public int LayerCount { get; init; }
    public int ObjectCount { get; init; }
    public long VirtualAtomCount { get; init; }
    public long NextObjectOrder { get; init; }
    public int ActiveLayer { get; init; }
    public float MaxHalfExtent { get; init; }
    public string[] LayerIds { get; init; } = [];
    public string[] LayerNames { get; init; } = [];
    public DrawingLayerKind[] LayerKinds { get; init; } = [];
    public string[] LayerParentIds { get; init; } = [];
    public string[] LayerMaskIds { get; init; } = [];
    public bool[] LayerLocked { get; init; } = [];
    public bool[] LayerVisible { get; init; } = [];
    public float[] LayerOpacity { get; init; } = [];
    public LayerBlendMode[] LayerBlendModes { get; init; } = [];
    public Dictionary<string, SymbolFilters> LayerSymbolFilters { get; init; } = new();
    public int[] LayerColorArgb { get; init; } = [];
    public bool[] LayerOutline { get; init; } = [];
    public bool? OnionSkinEnabled { get; init; }
    public bool[] LayerOnionSkin { get; init; } = [];
    public int? OnionSkinPreviousFrames { get; init; }
    public int? OnionSkinNextFrames { get; init; }
    public int[] LayerStart { get; init; } = [];
    public int[] LayerEnd { get; init; } = [];
    public ushort[] ObjectLayer { get; init; } = [];
    public int[] ObjectKeyframeFrame { get; init; } = [];
    public long[] ObjectOrder { get; init; } = [];
    public double[] ObjectSubOrder { get; init; } = [];
    public float[] X { get; init; } = [];
    public float[] Y { get; init; } = [];
    public float[] Width { get; init; } = [];
    public float[] Height { get; init; } = [];
    public float[] Angle { get; init; } = [];
    public float[] Stroke { get; init; } = [];
    public float[] CurveControlX { get; init; } = [];
    public float[] CurveControlY { get; init; } = [];
    public float[] CurveControl2X { get; init; } = [];
    public float[] CurveControl2Y { get; init; } = [];
    public LineEndpointStyle[] LineEndpointStyles { get; init; } = [];
    public LineEndpointStyle[] LineEndEndpointStyles { get; init; } = [];
    public ShapeKind[] ShapeKind { get; init; } = [];
    public int[] ShapeVertexCounts { get; init; } = [];
    public uint[] AtomCount { get; init; } = [];
    public int[] Argb { get; init; } = [];
    public int[] StrokeArgb { get; init; } = [];
    public bool[] FillAutoMergeProtected { get; init; } = [];
    public bool[] LinearGradientEnabled { get; init; } = [];
    public GradientKind[] GradientKinds { get; init; } = [];
    public int[] GradientStartArgb { get; init; } = [];
    public int[] GradientEndArgb { get; init; } = [];
    public float[] GradientStartX { get; init; } = [];
    public float[] GradientStartY { get; init; } = [];
    public float[] GradientEndX { get; init; } = [];
    public float[] GradientEndY { get; init; } = [];
    public Dictionary<int, GradientStop[]> GradientStops { get; init; } = new();
    public Dictionary<int, PointF[]> GradientPathLocalPoints { get; init; } = new();
    public Dictionary<int, PointF[][]> ShapeGradientMappingLocalContours { get; init; } = new();
    public AnimationTimelineSnapshot? Timeline { get; init; }
    public Dictionary<int, PointF[][]> PathLocalContours { get; init; } = new();
    public Dictionary<int, PathBezierNode[][]> PathBezierLocalContours { get; init; } = new();
    public Dictionary<int, PointF[]> FreehandLocalPoints { get; init; } = new();
    public Dictionary<int, PathBezierNode[]> FreehandBezierLocalNodes { get; init; } = new();
    public Dictionary<int, MixingBrushTrajectorySample[]> MixingStrokeLocalSamples { get; init; } = new();
    public Dictionary<int, MixingBrushRegionData> MixingStrokeLocalRegions { get; init; } = new();
    public Dictionary<int, string> ImportedSvgSources { get; init; } = new();
    public Dictionary<int, string> ImportedSvgNames { get; init; } = new();
    public Dictionary<int, BitmapObjectData> BitmapObjects { get; init; } = new();
    public Dictionary<int, TextObjectData> TextObjects { get; init; } = new();
    public Dictionary<int, DistortWarp[]> ObjectDistortions { get; init; } = new();

    internal void DetachSharedGeometry()
    {
        foreach (var key in GradientStops.Keys.ToArray())
        {
            GradientStops[key] = GradientStops[key].ToArray();
        }
        foreach (var key in GradientPathLocalPoints.Keys.ToArray())
        {
            GradientPathLocalPoints[key] = GradientPathLocalPoints[key].ToArray();
        }
        foreach (var key in ShapeGradientMappingLocalContours.Keys.ToArray())
        {
            ShapeGradientMappingLocalContours[key] = CloneContours(ShapeGradientMappingLocalContours[key]);
        }
        foreach (var key in PathLocalContours.Keys.ToArray())
        {
            PathLocalContours[key] = CloneContours(PathLocalContours[key]);
        }
        foreach (var key in PathBezierLocalContours.Keys.ToArray())
        {
            PathBezierLocalContours[key] = CloneContours(PathBezierLocalContours[key]);
        }
        foreach (var key in FreehandLocalPoints.Keys.ToArray())
        {
            FreehandLocalPoints[key] = FreehandLocalPoints[key].ToArray();
        }
        foreach (var key in FreehandBezierLocalNodes.Keys.ToArray())
        {
            FreehandBezierLocalNodes[key] = FreehandBezierLocalNodes[key].ToArray();
        }
        foreach (var key in MixingStrokeLocalSamples.Keys.ToArray())
        {
            MixingStrokeLocalSamples[key] = MixingStrokeLocalSamples[key].ToArray();
        }
        foreach (var key in MixingStrokeLocalRegions.Keys.ToArray())
        {
            MixingStrokeLocalRegions[key] = MixingStrokeLocalRegions[key].DeepClone();
        }
        foreach (var key in ObjectDistortions.Keys.ToArray())
        {
            ObjectDistortions[key] = ObjectDistortions[key]
                .Select(distortion => distortion.DeepClone())
                .ToArray();
        }
    }

    internal void DetachSharedGeometry(IEnumerable<int> objectIndices)
    {
        foreach (var key in objectIndices.Distinct())
        {
            if (GradientStops.TryGetValue(key, out var stops))
            {
                GradientStops[key] = stops.ToArray();
            }
            if (GradientPathLocalPoints.TryGetValue(key, out var gradientPath))
            {
                GradientPathLocalPoints[key] = gradientPath.ToArray();
            }
            if (ShapeGradientMappingLocalContours.TryGetValue(key, out var shapeMapping))
            {
                ShapeGradientMappingLocalContours[key] = CloneContours(shapeMapping);
            }
            if (PathLocalContours.TryGetValue(key, out var pathContours))
            {
                PathLocalContours[key] = CloneContours(pathContours);
            }
            if (PathBezierLocalContours.TryGetValue(key, out var bezierContours))
            {
                PathBezierLocalContours[key] = CloneContours(bezierContours);
            }
            if (FreehandLocalPoints.TryGetValue(key, out var freehandPoints))
            {
                FreehandLocalPoints[key] = freehandPoints.ToArray();
            }
            if (FreehandBezierLocalNodes.TryGetValue(key, out var freehandBezierNodes))
            {
                FreehandBezierLocalNodes[key] = freehandBezierNodes.ToArray();
            }
            if (MixingStrokeLocalSamples.TryGetValue(key, out var mixingSamples))
            {
                MixingStrokeLocalSamples[key] = mixingSamples.ToArray();
            }
            if (MixingStrokeLocalRegions.TryGetValue(key, out var mixingRegion))
            {
                MixingStrokeLocalRegions[key] = mixingRegion.DeepClone();
            }
            if (ObjectDistortions.TryGetValue(key, out var distortions))
            {
                ObjectDistortions[key] = distortions
                    .Select(distortion => distortion.DeepClone())
                    .ToArray();
            }
        }
    }

    private static PointF[][] CloneContours(PointF[][] contours)
        => contours.Select(contour => contour.ToArray()).ToArray();

    private static PathBezierNode[][] CloneContours(PathBezierNode[][] contours)
        => contours.Select(contour => contour.ToArray()).ToArray();

    internal long EstimateMemoryBytes()
    {
        long bytes = 384;
        bytes += StringArrayBytes(LayerIds);
        bytes += StringArrayBytes(LayerNames);
        bytes += ArrayBytes(LayerKinds.Length, 1);
        bytes += StringArrayBytes(LayerParentIds);
        bytes += StringArrayBytes(LayerMaskIds);
        bytes += ArrayBytes(LayerLocked.Length, 1);
        bytes += ArrayBytes(LayerVisible.Length, 1);
        bytes += ArrayBytes(LayerOpacity.Length, 4);
        bytes += ArrayBytes(LayerBlendModes.Length, 1);
        bytes += ArrayBytes(LayerColorArgb.Length, 4);
        bytes += ArrayBytes(LayerOutline.Length, 1);
        bytes += ArrayBytes(LayerOnionSkin.Length, 1);
        bytes += ArrayBytes(LayerStart.Length, 4);
        bytes += ArrayBytes(LayerEnd.Length, 4);
        bytes += ArrayBytes(ObjectLayer.Length, 2);
        bytes += ArrayBytes(ObjectKeyframeFrame.Length, 4);
        bytes += ArrayBytes(ObjectOrder.Length, 8);
        bytes += ArrayBytes(ObjectSubOrder.Length, 8);
        bytes += ArrayBytes(X.Length, 4);
        bytes += ArrayBytes(Y.Length, 4);
        bytes += ArrayBytes(Width.Length, 4);
        bytes += ArrayBytes(Height.Length, 4);
        bytes += ArrayBytes(Angle.Length, 4);
        bytes += ArrayBytes(Stroke.Length, 4);
        bytes += ArrayBytes(CurveControlX.Length, 4);
        bytes += ArrayBytes(CurveControlY.Length, 4);
        bytes += ArrayBytes(CurveControl2X.Length, 4);
        bytes += ArrayBytes(CurveControl2Y.Length, 4);
        bytes += ArrayBytes(LineEndpointStyles.Length, 4);
        bytes += ArrayBytes(LineEndEndpointStyles.Length, 4);
        bytes += ArrayBytes(ShapeKind.Length, 4);
        bytes += ArrayBytes(ShapeVertexCounts.Length, 4);
        bytes += ArrayBytes(AtomCount.Length, 4);
        bytes += ArrayBytes(Argb.Length, 4);
        bytes += ArrayBytes(StrokeArgb.Length, 4);
        bytes += ArrayBytes(FillAutoMergeProtected.Length, 1);
        bytes += ArrayBytes(LinearGradientEnabled.Length, 1);
        bytes += ArrayBytes(GradientKinds.Length, 4);
        bytes += ArrayBytes(GradientStartArgb.Length, 4);
        bytes += ArrayBytes(GradientEndArgb.Length, 4);
        bytes += ArrayBytes(GradientStartX.Length, 4);
        bytes += ArrayBytes(GradientStartY.Length, 4);
        bytes += ArrayBytes(GradientEndX.Length, 4);
        bytes += ArrayBytes(GradientEndY.Length, 4);

        bytes += 72L * GradientStops.Count;
        foreach (var stops in GradientStops.Values) bytes += ArrayBytes(stops.Length, 8);

        bytes += 64L * GradientPathLocalPoints.Count;
        foreach (var points in GradientPathLocalPoints.Values) bytes += ArrayBytes(points.Length, 8);

        bytes += 72L * ShapeGradientMappingLocalContours.Count;
        foreach (var contours in ShapeGradientMappingLocalContours.Values)
        {
            bytes += ArrayBytes(contours.Length, IntPtr.Size);
            foreach (var contour in contours) bytes += ArrayBytes(contour.Length, 8);
        }

        bytes += 72L * PathLocalContours.Count;
        foreach (var contours in PathLocalContours.Values)
        {
            bytes += ArrayBytes(contours.Length, IntPtr.Size);
            foreach (var contour in contours) bytes += ArrayBytes(contour.Length, 8);
        }

        bytes += 72L * PathBezierLocalContours.Count;
        foreach (var contours in PathBezierLocalContours.Values)
        {
            bytes += ArrayBytes(contours.Length, IntPtr.Size);
            foreach (var contour in contours) bytes += ArrayBytes(contour.Length, 24);
        }

        bytes += 64L * FreehandLocalPoints.Count;
        foreach (var points in FreehandLocalPoints.Values) bytes += ArrayBytes(points.Length, 8);

        bytes += 64L * FreehandBezierLocalNodes.Count;
        foreach (var nodes in FreehandBezierLocalNodes.Values) bytes += ArrayBytes(nodes.Length, 24);

        bytes += 64L * MixingStrokeLocalSamples.Count;
        foreach (var samples in MixingStrokeLocalSamples.Values) bytes += ArrayBytes(samples.Length, 16);

        bytes += 72L * MixingStrokeLocalRegions.Count;
        foreach (var region in MixingStrokeLocalRegions.Values)
        {
            bytes += 48L;
            bytes += ArrayBytes(region.Vertices?.Length ?? 0, 12);
            bytes += ArrayBytes(region.TriangleIndices?.Length ?? 0, 4);
        }

        bytes += 72L * ImportedSvgSources.Count;
        foreach (var source in ImportedSvgSources.Values) bytes += StringBytes(source);

        bytes += 72L * ImportedSvgNames.Count;
        foreach (var name in ImportedSvgNames.Values) bytes += StringBytes(name);

        // Each entry is a small payload: the asset id string plus two floats and the
        // dictionary overhead. Pixel data lives in the project image library, not here.
        bytes += 64L * BitmapObjects.Count;
        foreach (var bitmap in BitmapObjects.Values)
        {
            bytes += StringBytes(bitmap.ImageAssetId);
            if (bitmap.VisibleContours is not { } contours) continue;
            bytes += ArrayBytes(contours.Length, IntPtr.Size);
            foreach (var contour in contours) bytes += ArrayBytes(contour.Length, 8);
        }

        bytes += 72L * ObjectDistortions.Count;
        foreach (var distortions in ObjectDistortions.Values)
        {
            bytes += ArrayBytes(distortions.Length, 80);
            foreach (var distortion in distortions)
            {
                bytes += 80;
                bytes += ArrayBytes(distortion.Envelope.Top.Length, 48);
                bytes += ArrayBytes(distortion.Envelope.Right.Length, 48);
                bytes += ArrayBytes(distortion.Envelope.Bottom.Length, 48);
                bytes += ArrayBytes(distortion.Envelope.Left.Length, 48);
            }
        }

        bytes += 88L * TextObjects.Count;
        foreach (var text in TextObjects.Values)
        {
            bytes += StringBytes(text.Content) + StringBytes(text.FontFamilyName);
        }

        if (Timeline is { } timeline)
        {
            bytes += ArrayBytes(timeline.Tracks.Length, IntPtr.Size);
            bytes += ArrayBytes(timeline.TabGroups.Length, IntPtr.Size)
                + StringBytes(timeline.ActiveTabGroupId);
            foreach (var group in timeline.TabGroups)
            {
                bytes += 40 + StringBytes(group.Id) + StringBytes(group.Name);
            }
            foreach (var track in timeline.Tracks)
            {
                bytes += 96
                    + StringBytes(track.Id)
                    + StringBytes(track.TargetId)
                    + StringBytes(track.TabGroupId)
                    + ArrayBytes(track.Keyframes.Length, 8)
                    + ArrayBytes(track.Tweens?.Length ?? 0, 12);
            }
        }

        return bytes;
    }

    private static long StringArrayBytes(IReadOnlyList<string> values)
    {
        var bytes = ArrayBytes(values.Count, IntPtr.Size);
        foreach (var value in values) bytes += StringBytes(value);
        return bytes;
    }

    private static long StringBytes(string? value) => string.IsNullOrEmpty(value) ? 0 : 26L + value.Length * 2L;

    private static long ArrayBytes(int length, int elementBytes) => 24L + Math.Max(0, length) * (long)elementBytes;
}
