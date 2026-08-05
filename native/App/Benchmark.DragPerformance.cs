using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunFirstDragMovePerformanceRegression()
    {
        RunDraggedFillFrontRenderOrderRegression();

        const int unrelatedObjectCount = 4096;
        const int pointsPerObject = 128;
        const double budgetMilliseconds = StageControl.DragFirstMoveBudgetMilliseconds;
        var scene = new VectorScene();
        scene.CreateEmpty();
        var fill = AddTopologyFill(scene, 0, 0);
        var cutter = AddTopologyLine(scene, 0, new PointF(-320, 0), new PointF(320, 0));

        scene.BeginDeferredAppend(unrelatedObjectCount, [0]);
        try
        {
            for (var objectIndex = 0; objectIndex < unrelatedObjectCount; objectIndex++)
            {
                var points = new PointF[pointsPerObject];
                var baseX = 20_000 + objectIndex % 64 * 80f;
                var baseY = -20_000 + objectIndex / 64 * 48f;
                for (var pointIndex = 0; pointIndex < points.Length; pointIndex++)
                {
                    points[pointIndex] = new PointF(
                        baseX + pointIndex * 2f,
                        baseY + MathF.Sin(pointIndex * 0.2f) * 8f);
                }

                scene.AppendFreehandStroke(
                    0,
                    points,
                    VectorUnits.StrokePointsToUnits(2),
                    Color.CornflowerBlue,
                    (uint)points.Length);
            }
        }
        finally
        {
            scene.EndDeferredAppend();
        }
        scene.CompleteDeferredBuild();

        var originalObjectCount = scene.ObjectCount;
        var unrelatedSource = 2;
        if (!scene.TryGetFreehandLocalPoints(unrelatedSource, out var unrelatedGeometry))
        {
            throw new InvalidOperationException("The first-drag benchmark could not capture unrelated geometry identity.");
        }

        var fillHit = scene.HitTestElement(new PointF(0, -80), 0, 0);
        var fillResult = MeasureDrag(fillHit, fill, DrawingElementKind.Fill, "fill");
        scene.RestoreSnapshot(fillResult.Snapshot);
        AssertRestored("fill");
        if (!scene.TryGetFreehandLocalPoints(unrelatedSource, out unrelatedGeometry))
        {
            throw new InvalidOperationException("The first-drag benchmark could not refresh restored geometry identity.");
        }

        var lineHit = scene.HitTestElement(PointF.Empty, 0, 0);
        var lineResult = MeasureDrag(lineHit, cutter, DrawingElementKind.Stroke, "line");
        scene.RestoreSnapshot(lineResult.Snapshot);
        AssertRestored("line");

        var totalMilliseconds = Math.Max(fillResult.TotalMilliseconds, lineResult.TotalMilliseconds);
        var snapshotMilliseconds = Math.Max(fillResult.SnapshotMilliseconds, lineResult.SnapshotMilliseconds);
        var materializeMilliseconds = Math.Max(fillResult.MaterializeMilliseconds, lineResult.MaterializeMilliseconds);
        var budgetMet = totalMilliseconds <= budgetMilliseconds;
        Console.WriteLine($"drag_fill_first_move_ms={fillResult.TotalMilliseconds:0.000}");
        Console.WriteLine($"drag_line_first_move_ms={lineResult.TotalMilliseconds:0.000}");
        Console.WriteLine($"drag_first_move_total_ms={totalMilliseconds:0.000}");
        Console.WriteLine($"drag_snapshot_ms={snapshotMilliseconds:0.000}");
        Console.WriteLine($"drag_materialize_ms={materializeMilliseconds:0.000}");
        Console.WriteLine($"drag_first_move_budget_ms={budgetMilliseconds:0.000}");
        Console.WriteLine($"drag_first_move_budget_met={budgetMet.ToString().ToLowerInvariant()}");
        if (!budgetMet)
        {
            throw new InvalidOperationException(
                $"The first visible Fill/Line drag exceeded its budget: fill={fillResult.TotalMilliseconds:0.000} ms, line={lineResult.TotalMilliseconds:0.000} ms.");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        (VectorSceneSnapshot Snapshot, double TotalMilliseconds, double SnapshotMilliseconds, double MaterializeMilliseconds) MeasureDrag(
            DrawingElementHit hit,
            int expectedObject,
            DrawingElementKind expectedKind,
            string context)
        {
            if (!hit.IsValid || hit.Key.ObjectIndex != expectedObject || hit.Key.Kind != expectedKind)
            {
                throw new InvalidOperationException($"The first-drag {context} setup did not hit the expected drawing element.");
            }

            var totalWatch = Stopwatch.StartNew();
            var stageWatch = Stopwatch.StartNew();
            var snapshot = scene.CreateWholeObjectTranslationSnapshot();
            _ = snapshot.EstimateMemoryBytes();
            var snapshotMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
            stageWatch.Restart();
            var materialized = scene.MaterializeSelectedParts([hit.Key], 0, snapshot);
            var materializeMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
            if (!materialized.Success || !materialized.Changed || materialized.Parts.Length != 1)
            {
                throw new InvalidOperationException($"The first-drag {context} setup did not materialize one selected part.");
            }

            var target = materialized.Parts[0].Result.ObjectIndex;
            scene.X[target] += 12;
            scene.Y[target] -= 8;
            totalWatch.Stop();

            var mappedUnrelated = materialized.OldToNewObjectIndex[unrelatedSource];
            if (mappedUnrelated < 0
                || !scene.TryGetFreehandLocalPoints(mappedUnrelated, out var movedGeometry)
                || !ReferenceEquals(movedGeometry, unrelatedGeometry))
            {
                throw new InvalidOperationException($"The first-drag {context} compaction cloned unrelated packed geometry.");
            }

            return (snapshot, totalWatch.Elapsed.TotalMilliseconds, snapshotMilliseconds, materializeMilliseconds);
        }

        void AssertRestored(string context)
        {
            if (scene.ObjectCount != originalObjectCount
                || scene.ShapeKind[fill] != ShapeKind.Rectangle
                || scene.ShapeKind[cutter] != ShapeKind.Line
                || scene.GetFillParts(fill, 0).Length != 2)
            {
                throw new InvalidOperationException($"Undo after the first {context} drag did not restore the original topology.");
            }
        }
    }

    private static void RunDraggedFillFrontRenderOrderRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        var solid = scene.AddObject(
            0,
            new PointF(-240, 0),
            new SizeF(160, 120),
            0,
            0,
            Color.FromArgb(128, Color.Coral),
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var linear = scene.AddObject(
            0,
            new PointF(-80, 0),
            new SizeF(160, 120),
            0,
            0,
            Color.Gold,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var radial = scene.AddObject(
            0,
            new PointF(80, 0),
            new SizeF(160, 120),
            0,
            0,
            Color.MediumPurple,
            Color.Transparent,
            8,
            ShapeKind.Ellipse);
        var shapeRadial = scene.AddObject(
            0,
            new PointF(240, 0),
            new SizeF(160, 120),
            0,
            0,
            Color.DeepSkyBlue,
            Color.Transparent,
            8,
            ShapeKind.Star);
        var stroke = scene.AddLineSegment(
            0,
            new PointF(-360, 0),
            new PointF(360, 0),
            VectorUnits.StrokePointsToUnits(4),
            Color.Transparent,
            Color.White,
            8);

        scene.SetLinearGradient(linear, Color.Gold, Color.Crimson);
        scene.SetGradientStops(linear,
        [
            new GradientStop(0, Color.Gold),
            new GradientStop(0.45f, Color.Coral),
            new GradientStop(1, Color.Crimson)
        ]);
        scene.SetRadialGradient(radial, Color.White, Color.MediumPurple);
        scene.SetShapeRadialGradient(shapeRadial, Color.White, Color.DeepSkyBlue);
        scene.SetShapeGradientMapping(shapeRadial, scene.GetObjectBoundaryContours(shapeRadial));

        var fills = new[] { solid, linear, radial, shapeRadial };
        var objects = fills.Append(stroke).ToArray();
        var originalOrders = scene.ObjectOrder.Take(scene.ObjectCount).ToArray();
        var originalSubOrders = scene.ObjectSubOrder.Take(scene.ObjectCount).ToArray();
        if (scene.GetGradientKind(solid) != GradientKind.Solid
            || scene.GetGradientKind(linear) != GradientKind.Linear
            || scene.GetGradientKind(radial) != GradientKind.Radial
            || scene.GetGradientKind(shapeRadial) != GradientKind.ShapeRadial
            || Color.FromArgb(scene.Argb[solid]).A != 128)
        {
            throw new InvalidOperationException("The dragged-fill render-order regression did not configure all fill paint types.");
        }

        foreach (var selectedFill in fills)
        {
            var commands = new List<(int ObjectIndex, SceneRenderPass Pass)>();
            SceneRenderOrder.DrawObjectPasses(
                scene,
                objects,
                0,
                new HashSet<int> { selectedFill },
                objectIndex => commands.Add((objectIndex, SceneRenderPass.Fill)),
                objectIndex => commands.Add((objectIndex, SceneRenderPass.Stroke)));
            var expected = fills
                .Where(objectIndex => objectIndex != selectedFill)
                .Select(objectIndex => (objectIndex, SceneRenderPass.Fill))
                .Append((stroke, SceneRenderPass.Stroke))
                .Append((selectedFill, SceneRenderPass.Fill))
                .ToArray();
            if (!commands.SequenceEqual(expected)
                || commands.Count(command => command == (selectedFill, SceneRenderPass.Fill)) != 1)
            {
                throw new InvalidOperationException(
                    $"Dragging {scene.GetGradientKind(selectedFill)} fill did not render it exactly once above same-layer fills and strokes.");
            }
        }

        var multiCommands = new List<(int ObjectIndex, SceneRenderPass Pass)>();
        SceneRenderOrder.DrawObjectPasses(
            scene,
            objects,
            0,
            new HashSet<int> { linear, radial },
            objectIndex => multiCommands.Add((objectIndex, SceneRenderPass.Fill)),
            objectIndex => multiCommands.Add((objectIndex, SceneRenderPass.Stroke)));
        var expectedMultiCommands = new[]
        {
            (solid, SceneRenderPass.Fill),
            (shapeRadial, SceneRenderPass.Fill),
            (stroke, SceneRenderPass.Stroke),
            (linear, SceneRenderPass.Fill),
            (radial, SceneRenderPass.Fill)
        };
        if (!multiCommands.SequenceEqual(expectedMultiCommands)
            || !scene.ObjectOrder.Take(scene.ObjectCount).SequenceEqual(originalOrders)
            || !scene.ObjectSubOrder.Take(scene.ObjectCount).SequenceEqual(originalSubOrders))
        {
            throw new InvalidOperationException(
                "Multi-fill drag promotion changed relative or persistent drawing order.");
        }

        var limitedCommands = new List<(int ObjectIndex, SceneRenderPass Pass)>();
        SceneRenderOrder.DrawObjectPasses(
            scene,
            objects,
            2,
            new HashSet<int> { solid },
            objectIndex => limitedCommands.Add((objectIndex, SceneRenderPass.Fill)),
            objectIndex => limitedCommands.Add((objectIndex, SceneRenderPass.Stroke)));
        var expectedLimitedCommands = new[]
        {
            (radial, SceneRenderPass.Fill),
            (shapeRadial, SceneRenderPass.Fill),
            (stroke, SceneRenderPass.Stroke),
            (solid, SceneRenderPass.Fill)
        };
        if (!limitedCommands.SequenceEqual(expectedLimitedCommands))
        {
            throw new InvalidOperationException(
                "A dragged fill omitted by the dense-scene draw limit was not promoted into the visible pass.");
        }

        using var stage = new StageControl(scene);
        stage.SetSelectionFillDragFrontObjects([linear, stroke, -1]);
        var stageFillObjects = stage.SelectionFillDragFrontObjectsFor(scene);
        if (!stage.SelectionFillDragFrontActive
            || stageFillObjects is null
            || stageFillObjects.Count != 1
            || !stageFillObjects.Contains(linear))
        {
            throw new InvalidOperationException(
                "Stage fill-drag promotion did not bind one valid fill to the active scene.");
        }

        stage.ClearSelectionFillDragFront();
        if (stage.SelectionFillDragFrontActive
            || stage.SelectionFillDragFrontObjectsFor(scene) is not null)
        {
            throw new InvalidOperationException(
                "Stage fill-drag promotion did not clear at pointer completion.");
        }
    }
}
