using System.Numerics;

namespace VectorAnimationEngine;

internal readonly record struct SceneCompositionObjectOwner(string InstanceId, string DrawingObjectId);

internal sealed class SceneCompositionResult
{
    private readonly Dictionary<int, SceneCompositionObjectOwner> _owners;

    public SceneCompositionResult(Dictionary<int, SceneCompositionObjectOwner>? owners = null)
    {
        _owners = owners ?? [];
    }

    public static SceneCompositionResult Empty { get; } = new();
    public IReadOnlyDictionary<int, SceneCompositionObjectOwner> ObjectOwners => _owners;

    public bool TryGetOwner(int objectIndex, out SceneCompositionObjectOwner owner)
    {
        return _owners.TryGetValue(objectIndex, out owner);
    }
}

internal static class SceneCompositionBuilder
{
    private readonly record struct CompositionLayer(
        VectorScene Source,
        int SourceLayer,
        string Name,
        Matrix3x2 Transform,
        SceneCompositionObjectOwner Owner);

    public static SceneCompositionResult Build(
        VectorScene destination,
        SceneDefinition? sceneDefinition,
        IReadOnlyList<DrawingObjectDefinition> drawingObjects,
        int frame)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(drawingObjects);

        if (sceneDefinition is null)
        {
            destination.CreateEmpty();
            return SceneCompositionResult.Empty;
        }

        var definitionsById = DefinitionsById(drawingObjects);
        var layers = new List<CompositionLayer>();
        sceneDefinition.SynchronizeTimelineTracks();
        foreach (var instance in sceneDefinition.Instances)
        {
            if (!instance.Visible
                || !sceneDefinition.Timeline.EvaluateTargetExposure(instance.Id, frame).HasContent
                || !definitionsById.TryGetValue(instance.DrawingObjectId, out var drawingObject))
            {
                continue;
            }

            CollectDrawingObjectLayers(
                drawingObject,
                instance,
                InstanceMatrix(instance),
                frame,
                definitionsById,
                layers,
                new HashSet<string>(StringComparer.Ordinal),
                instance.Name);
        }

        return BuildLayers(destination, layers, Math.Max(AnimationTimeline.DefaultDuration, sceneDefinition.FrameCount), frame);
    }

    public static SceneCompositionResult BuildDrawingObjectChildren(
        VectorScene destination,
        DrawingObjectDefinition? drawingObject,
        IReadOnlyList<DrawingObjectDefinition> drawingObjects,
        int frame)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(drawingObjects);
        if (drawingObject is null || drawingObject.Instances.Count == 0)
        {
            destination.CreateEmpty();
            return SceneCompositionResult.Empty;
        }

        var definitionsById = DefinitionsById(drawingObjects);
        var layers = new List<CompositionLayer>();
        drawingObject.SynchronizeInstanceTimelineTracks();
        var localFrame = Math.Clamp(frame, 0, Math.Max(0, drawingObject.FrameCount - 1));
        var ancestry = new HashSet<string>(StringComparer.Ordinal) { drawingObject.Id };
        foreach (var instance in drawingObject.Instances)
        {
            if (!instance.Visible
                || !drawingObject.InstanceTimeline.EvaluateTargetExposure(instance.Id, localFrame).HasContent
                || !definitionsById.TryGetValue(instance.DrawingObjectId, out var child))
            {
                continue;
            }

            CollectDrawingObjectLayers(
                child,
                instance,
                InstanceMatrix(instance),
                localFrame,
                definitionsById,
                layers,
                ancestry,
                instance.Name);
        }

        return BuildLayers(destination, layers, Math.Max(AnimationTimeline.DefaultDuration, drawingObject.FrameCount), localFrame);
    }

    private static Dictionary<string, DrawingObjectDefinition> DefinitionsById(IReadOnlyList<DrawingObjectDefinition> drawingObjects)
    {
        return drawingObjects
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private static void CollectDrawingObjectLayers(
        DrawingObjectDefinition drawingObject,
        DrawingObjectInstanceDefinition instance,
        Matrix3x2 transform,
        int frame,
        IReadOnlyDictionary<string, DrawingObjectDefinition> definitionsById,
        ICollection<CompositionLayer> layers,
        ISet<string> ancestry,
        string path)
    {
        if (!ancestry.Add(drawingObject.Id)) return;
        try
        {
            var source = drawingObject.Scene;
            source.SynchronizeTimelineTracks();
            for (var sourceLayer = 0; sourceLayer < source.LayerCount; sourceLayer++)
            {
                layers.Add(new CompositionLayer(
                    source,
                    sourceLayer,
                    $"{path} / {source.LayerNames[sourceLayer]}",
                    transform,
                    new SceneCompositionObjectOwner(instance.Id, drawingObject.Id)));
            }

            drawingObject.SynchronizeInstanceTimelineTracks();
            var localFrame = Math.Clamp(frame, 0, Math.Max(0, drawingObject.FrameCount - 1));
            foreach (var childInstance in drawingObject.Instances)
            {
                if (!childInstance.Visible
                    || !drawingObject.InstanceTimeline.EvaluateTargetExposure(childInstance.Id, localFrame).HasContent
                    || !definitionsById.TryGetValue(childInstance.DrawingObjectId, out var child))
                {
                    continue;
                }

                CollectDrawingObjectLayers(
                    child,
                    childInstance,
                    InstanceMatrix(childInstance) * transform,
                    localFrame,
                    definitionsById,
                    layers,
                    ancestry,
                    $"{path} / {childInstance.Name}");
            }
        }
        finally
        {
            ancestry.Remove(drawingObject.Id);
        }
    }

    private static SceneCompositionResult BuildLayers(
        VectorScene destination,
        IReadOnlyList<CompositionLayer> layers,
        int duration,
        int frame)
    {
        destination.CreateEmpty(Math.Max(1, layers.Count));
        destination.EditFrame = 0;
        destination.SynchronizeTimelineTracks();
        foreach (var track in destination.Timeline.Tracks)
        {
            destination.Timeline.SetTrackDuration(track.Id, Math.Max(1, duration));
        }

        var owners = new Dictionary<int, SceneCompositionObjectOwner>();
        for (var destinationLayer = 0; destinationLayer < layers.Count; destinationLayer++)
        {
            var layer = layers[destinationLayer];
            var source = layer.Source;
            var sourceFrame = Math.Clamp(frame, 0, Math.Max(0, source.FrameCount - 1));
            destination.LayerNames[destinationLayer] = layer.Name;
            destination.LayerVisible[destinationLayer] = true;
            destination.LayerOpacity[destinationLayer] = source.LayerOpacity[layer.SourceLayer];

            var sourceObjects = Enumerable.Range(0, source.ObjectCount)
                .Where(index => source.ObjectLayer[index] == layer.SourceLayer && source.IsObjectActive(index, sourceFrame))
                .OrderBy(index => source.ObjectOrder[index])
                .ThenBy(index => source.ObjectSubOrder[index]);
            foreach (var sourceObject in sourceObjects)
            {
                var destinationObject = CopyObject(source, sourceObject, destination, destinationLayer, layer.Transform);
                if (destinationObject >= 0) owners[destinationObject] = layer.Owner;
            }
        }

        destination.RebuildGeometryIndex();
        return new SceneCompositionResult(owners);
    }

    private static int CopyObject(
        VectorScene source,
        int sourceObject,
        VectorScene destination,
        int destinationLayer,
        Matrix3x2 transform)
    {
        var shape = source.ShapeKind[sourceObject];
        var determinant = transform.M11 * transform.M22 - transform.M12 * transform.M21;
        var stroke = source.Stroke[sourceObject] * MathF.Sqrt(Math.Abs(determinant));
        var fill = Color.FromArgb(source.Argb[sourceObject]);
        var strokeColor = Color.FromArgb(source.StrokeArgb[sourceObject]);
        var atoms = source.AtomCount[sourceObject];

        if (shape == ShapeKind.Path && source.TryGetPathWorldContours(sourceObject, out var contours))
        {
            var transformed = contours
                .Select(contour => contour.Select(point => Transform(point, transform)).ToArray())
                .ToArray();
            return destination.AddPathObjectContours(destinationLayer, transformed, stroke, fill, strokeColor, atoms);
        }

        if (shape == ShapeKind.Freeform && source.TryGetFreehandWorldPoints(sourceObject, out var freehand))
        {
            var transformed = freehand.Select(point => Transform(point, transform)).ToArray();
            return destination.AddFreehandStroke(destinationLayer, transformed, stroke, strokeColor, brushStroke: false, atoms);
        }

        if (shape == ShapeKind.Line
            && source.TryGetLineEndpoint(sourceObject, startEndpoint: true, out var start)
            && source.TryGetLineEndpoint(sourceObject, startEndpoint: false, out var end))
        {
            var control = new PointF(source.CurveControlX[sourceObject], source.CurveControlY[sourceObject]);
            return destination.AddCurveSegment(
                destinationLayer,
                Transform(start, transform),
                Transform(control, transform),
                Transform(end, transform),
                stroke,
                fill,
                strokeColor,
                atoms);
        }

        var center = Transform(new PointF(source.X[sourceObject], source.Y[sourceObject]), transform);
        var sourceAngle = source.Angle[sourceObject];
        var cos = MathF.Cos(sourceAngle);
        var sin = MathF.Sin(sourceAngle);
        var widthAxis = Vector2.TransformNormal(new Vector2(cos * source.Width[sourceObject], sin * source.Width[sourceObject]), transform);
        var heightAxis = Vector2.TransformNormal(new Vector2(-sin * source.Height[sourceObject], cos * source.Height[sourceObject]), transform);
        var size = new SizeF(Math.Max(1, widthAxis.Length()), Math.Max(1, heightAxis.Length()));
        var angle = MathF.Atan2(widthAxis.Y, widthAxis.X);
        return destination.AddObject(destinationLayer, center, size, angle, stroke, fill, strokeColor, atoms, shape);
    }

    private static Matrix3x2 InstanceMatrix(DrawingObjectInstanceDefinition instance)
    {
        return Matrix3x2.CreateScale(instance.ScaleX, instance.ScaleY)
            * Matrix3x2.CreateRotation(instance.RotationZ * MathF.PI / 180f)
            * Matrix3x2.CreateTranslation(instance.X, instance.Y);
    }

    private static PointF Transform(PointF point, Matrix3x2 transform)
    {
        var transformed = Vector2.Transform(new Vector2(point.X, point.Y), transform);
        return VectorUnits.Quantize(new PointF(transformed.X, transformed.Y));
    }
}
