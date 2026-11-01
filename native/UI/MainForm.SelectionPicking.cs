namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    internal static DrawingElementHit HitTestSelectionArrow(
        VectorScene scene, PointF world, int frame, float toleranceWorld)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!float.IsFinite(world.X) || !float.IsFinite(world.Y)
            || !float.IsFinite(toleranceWorld)) return DrawingElementHit.None;
        var ordinaryHit = scene.HitTestElement(world, frame, toleranceWorld);
        if (ordinaryHit.IsValid || toleranceWorld <= 0) return ordinaryHit;

        var bounds = new RectangleF(world.X - toleranceWorld, world.Y - toleranceWorld,
            toleranceWorld * 2, toleranceWorld * 2);
        if (!float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Top)
            || !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom)) return ordinaryHit;

        var best = DrawingElementHit.None;
        var toleranceSquared = (double)toleranceWorld * toleranceWorld;
        foreach (var objectIndex in scene.QueryObjects(bounds, frame, int.MaxValue))
        {
            if (!scene.IsObjectSelectable(objectIndex, frame) || !scene.HasFill(objectIndex)
                || scene.ShapeKind[objectIndex] is not (ShapeKind.Rectangle or ShapeKind.Ellipse
                    or ShapeKind.Triangle or ShapeKind.Polygon or ShapeKind.Star or ShapeKind.Path)
                || !SelectionPickingFillIsVisible(scene, objectIndex)) continue;

            foreach (var part in scene.GetFillParts(objectIndex, frame))
            {
                var contours = SelectionPickingWorldContours(scene, objectIndex, part.Contours);
                var visible = scene.GetVisibleSelectionFillContours(objectIndex, frame, contours);
                var distanceSquared = SelectionPickingFillDistanceSquared(world, visible);
                if (!double.IsFinite(distanceSquared) || distanceSquared > toleranceSquared) continue;
                var candidate = new DrawingElementHit(
                    new DrawingElementKey(objectIndex, DrawingElementKind.Fill, part.PartIndex),
                    (float)Math.Sqrt(distanceSquared), 0, 1);
                if (SelectionPickingIsBetterHit(scene, candidate, best)) best = candidate;
            }
        }
        return best;
    }

    private static bool SelectionPickingFillIsVisible(VectorScene scene, int objectIndex)
    {
        var layer = (int)scene.ObjectLayer[objectIndex];
        if (!scene.ShouldRenderLayerContent(layer) || scene.IsLayerEffectivelyOutlined(layer)) return false;
        for (var hop = 0; hop < scene.LayerCount && layer >= 0; hop++)
        {
            if (!float.IsFinite(scene.LayerOpacity[layer]) || scene.LayerOpacity[layer] <= 0) return false;
            layer = scene.GetLayerParentIndex(layer);
        }
        return true;
    }

    private static PointF[][] SelectionPickingWorldContours(
        VectorScene scene, int objectIndex, PointF[][] contours)
    {
        if (!scene.TryGetObjectDistortions(objectIndex, out _)) return contours;
        return contours.Select(contour =>
        {
            if (contour.Length == 0) return Array.Empty<PointF>();
            var mapped = new List<PointF> { scene.MapObjectPoint(objectIndex, contour[0]) };
            for (var index = 0; index < contour.Length; index++)
            {
                SelectionPickingAppendMappedSegment(scene, objectIndex, contour[index],
                    contour[(index + 1) % contour.Length], mapped[^1],
                    scene.MapObjectPoint(objectIndex, contour[(index + 1) % contour.Length]), mapped, 0);
            }
            return mapped.ToArray();
        }).ToArray();
    }

    private static void SelectionPickingAppendMappedSegment(
        VectorScene scene, int objectIndex, PointF start, PointF end,
        PointF mappedStart, PointF mappedEnd, List<PointF> destination, int depth)
    {
        var middle = new PointF(start.X * 0.5f + end.X * 0.5f, start.Y * 0.5f + end.Y * 0.5f);
        var mappedMiddle = scene.MapObjectPoint(objectIndex, middle);
        if (depth >= 10 || SelectionPickingSegmentDistanceSquared(mappedMiddle, mappedStart, mappedEnd) <= 0.25)
        {
            if (destination[^1] != mappedEnd) destination.Add(mappedEnd);
            return;
        }
        SelectionPickingAppendMappedSegment(scene, objectIndex, start, middle,
            mappedStart, mappedMiddle, destination, depth + 1);
        SelectionPickingAppendMappedSegment(scene, objectIndex, middle, end,
            mappedMiddle, mappedEnd, destination, depth + 1);
    }

    private static double SelectionPickingFillDistanceSquared(PointF world, PointF[][] contours)
    {
        var distanceSquared = double.PositiveInfinity;
        var inside = false;
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            for (var index = 0; index < contour.Length; index++)
            {
                var start = contour[index];
                var end = contour[(index + 1) % contour.Length];
                distanceSquared = Math.Min(distanceSquared, SelectionPickingSegmentDistanceSquared(world, start, end));
                if ((start.Y > world.Y) != (end.Y > world.Y)
                    && world.X < start.X + ((double)world.Y - start.Y) * (end.X - (double)start.X) / (end.Y - (double)start.Y))
                    inside = !inside;
            }
        }
        return inside ? 0 : distanceSquared;
    }

    private static double SelectionPickingSegmentDistanceSquared(PointF world, PointF start, PointF end)
    {
        var deltaX = end.X - (double)start.X;
        var deltaY = end.Y - (double)start.Y;
        var lengthSquared = deltaX * deltaX + deltaY * deltaY;
        var parameter = lengthSquared > 0
            ? Math.Clamp(((world.X - (double)start.X) * deltaX + (world.Y - (double)start.Y) * deltaY) / lengthSquared, 0, 1)
            : 0;
        var offsetX = world.X - (start.X + deltaX * parameter);
        var offsetY = world.Y - (start.Y + deltaY * parameter);
        return offsetX * offsetX + offsetY * offsetY;
    }

    private static bool SelectionPickingIsBetterHit(VectorScene scene, DrawingElementHit candidate, DrawingElementHit best)
    {
        if (!best.IsValid) return true;
        var comparison = candidate.Distance.CompareTo(best.Distance);
        if (comparison != 0) return comparison < 0;
        var candidateIndex = candidate.Key.ObjectIndex;
        var bestIndex = best.Key.ObjectIndex;
        comparison = scene.ObjectLayer[candidateIndex].CompareTo(scene.ObjectLayer[bestIndex]);
        if (comparison != 0) return comparison < 0;
        comparison = scene.ObjectOrder[candidateIndex].CompareTo(scene.ObjectOrder[bestIndex]);
        if (comparison != 0) return comparison > 0;
        comparison = scene.ObjectSubOrder[candidateIndex].CompareTo(scene.ObjectSubOrder[bestIndex]);
        return comparison != 0 ? comparison > 0 : candidateIndex > bestIndex;
    }
}
