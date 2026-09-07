using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunSnapPointModelRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        var addedFirst = project.TryAddDrawingObjectSnapPoint(
            drawingObject.Id,
            100,
            200,
            0,
            out var first);
        var addedSecond = project.TryAddDrawingObjectSnapPoint(
            drawingObject.Id,
            -300,
            400,
            50,
            out var second);
        AssertTimeline(
            addedFirst
            && addedSecond
            && drawingObject.SnapPoints.Count == 2
            && drawingObject.SnapPoints[0] == first
            && drawingObject.SnapPoints[1] == second
            && first.Id != second.Id,
            "Adding a symbol snap point discarded an existing point or reused its stable ID.");

        AssertTimeline(
            project.TryMoveDrawingObjectSnapPoint(drawingObject.Id, second.Id, -275, 425, 75)
            && drawingObject.SnapPoints.Single(point => point.Id == second.Id).Position == new PointF(-275, 425)
            && project.TryRemoveDrawingObjectSnapPoint(drawingObject.Id, first.Id)
            && drawingObject.SnapPoints.Count == 1
            && drawingObject.SnapPoints[0].Id == second.Id
            && !project.TryMoveDrawingObjectSnapPoint(drawingObject.Id, second.Id, float.NaN, 0, 0)
            && !project.TryAddDrawingObjectSnapPoint(drawingObject.Id, 0, float.PositiveInfinity, 0, out _),
            "Moving, deleting, or validating a symbol snap point failed.");

        AssertTimeline(
            project.TryDuplicateDrawingObject(drawingObject.Id, out var duplicate)
            && duplicate is not null
            && duplicate.SnapPoints.Count == drawingObject.SnapPoints.Count
            && duplicate.SnapPoints[0].Id != drawingObject.SnapPoints[0].Id
            && duplicate.SnapPoints[0].Position == drawingObject.SnapPoints[0].Position,
            "Duplicating a symbol did not deep-copy snap-point coordinates with new stable IDs.");
        var duplicatePoint = duplicate!.SnapPoints[0];
        project.TryMoveDrawingObjectSnapPoint(drawingObject.Id, second.Id, 900, 800, 700);
        AssertTimeline(
            duplicate.SnapPoints[0] == duplicatePoint,
            "Editing a source symbol snap point also mutated its duplicate.");

        var restartRestored = VectorProject.RestoreRestartSnapshot(
            EditorRestartStore.RoundTripProjectSnapshot(project.CreateRestartSnapshot()));
        AssertTimeline(
            restartRestored.DrawingObjects.SingleOrDefault(item => item.Id == drawingObject.Id)?.SnapPoints
                .SequenceEqual(drawingObject.SnapPoints) == true
            && restartRestored.DrawingObjects.SingleOrDefault(item => item.Id == duplicate.Id)?.SnapPoints
                .SequenceEqual(duplicate.SnapPoints) == true,
            "Editor restart JSON did not preserve symbol snap points and stable IDs.");

        var invalidBase = project.CreateRestartSnapshot();
        var validSnapshotPoint = invalidBase.DrawingObjects[0].SnapPoints[0];
        ExpectInvalidSnapPointRestart(
            ReplaceFirstDrawingSnapPoints(
                invalidBase,
                [validSnapshotPoint, new DrawingObjectSnapPointRestartSnapshot
                {
                    Id = validSnapshotPoint.Id,
                    X = validSnapshotPoint.X + 1,
                    Y = validSnapshotPoint.Y,
                    Z = validSnapshotPoint.Z
                }]),
            "duplicate snap-point IDs");
        ExpectInvalidSnapPointRestart(
            ReplaceFirstDrawingSnapPoints(
                invalidBase,
                [new DrawingObjectSnapPointRestartSnapshot
                {
                    Id = Guid.NewGuid().ToString("N"),
                    X = float.NaN
                }]),
            "non-finite snap-point coordinates");
        ExpectInvalidSnapPointRestart(
            ReplaceFirstDrawingSnapPoints(
                invalidBase,
                Enumerable.Range(0, VectorProject.MaximumSnapPointsPerDrawingObject + 1)
                    .Select(index => new DrawingObjectSnapPointRestartSnapshot
                    {
                        Id = $"{index + 1:x32}",
                        X = index
                    })
                    .ToArray()),
            "the per-symbol snap-point limit");

        var capacityProject = VectorProject.CreateEmpty();
        var capacityDrawing = capacityProject.DrawingObjects[0];
        for (var index = 0; index < VectorProject.MaximumSnapPointsPerDrawingObject; index++)
        {
            if (!capacityProject.TryAddDrawingObjectSnapPoint(
                    capacityDrawing.Id,
                    index,
                    -index,
                    0,
                    out _))
            {
                throw new InvalidOperationException(
                    $"Snap-point capacity rejected valid point {index} before the documented limit.");
            }
        }
        AssertTimeline(
            !capacityProject.TryAddDrawingObjectSnapPoint(capacityDrawing.Id, 0, 0, 0, out _),
            "A symbol accepted more than the maximum snap-point count.");

        RunSnapPointTransformRegression();
        RunSnapPointVaultRegression(project, drawingObject.Id);
        Console.WriteLine("snap_point_model_regression=ok");
    }

    private static void RunSnapPointTransformRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.SetAnchor(new PointF(40, -25));
        project.TryAddDrawingObjectSnapPoint(drawingObject.Id, 190, 55, 35, out var snapPoint);
        var instance = new DrawingObjectInstanceDefinition
        {
            X = 700,
            Y = -350,
            Z = 120,
            RotationX = 18,
            RotationY = -27,
            RotationZ = 33,
            SkewX = 7,
            SkewY = -5,
            ScaleX = 1.4f,
            ScaleY = 0.65f,
            ScaleZ = 2.2f,
            RotationPivot = new Vector3(28, -16, 9),
            ScalePivot = new Vector3(-12, 21, -6)
        };
        var state = instance.EvaluateState(0);
        var skew = Matrix3x2.CreateSkew(
            state.SkewX * MathF.PI / 180f,
            state.SkewY * MathF.PI / 180f);
        var liftedSkew = new Matrix4x4(
            skew.M11, skew.M12, 0, 0,
            skew.M21, skew.M22, 0, 0,
            0, 0, 1, 0,
            skew.M31, skew.M32, 0, 1);
        var expected = Vector3.Transform(
            new Vector3(
                snapPoint.X - drawingObject.Anchor.X,
                snapPoint.Y - drawingObject.Anchor.Y,
                snapPoint.Z),
            Matrix4x4.CreateTranslation(-state.ScalePivot)
            * Matrix4x4.CreateScale(state.ScaleX, state.ScaleY, state.ScaleZ)
            * Matrix4x4.CreateTranslation(state.ScalePivot)
            * liftedSkew
            * Matrix4x4.CreateTranslation(-state.RotationPivot)
            * Matrix4x4.CreateRotationZ(state.RotationZ * MathF.PI / 180f)
            * Matrix4x4.CreateRotationX(state.RotationX * MathF.PI / 180f)
            * Matrix4x4.CreateRotationY(state.RotationY * MathF.PI / 180f)
            * Matrix4x4.CreateTranslation(state.RotationPivot)
            * Matrix4x4.CreateTranslation(state.X, state.Y, state.Z));
        var actual = DrawingObjectSnapPointMath.Transform(drawingObject, state, snapPoint);
        AssertTimeline(
            Vector3.Distance(expected, actual) <= 0.001f,
            "Snap-point projection did not apply Anchor, spatial pivots, XYZ scale, XY skew, Z/X/Y rotation, and XYZ translation in instance order.");
    }

    private static void RunSnapPointVaultRegression(VectorProject project, string drawingObjectId)
    {
        var temporaryRoot = CreateTemporaryDirectory("snap-point-regression");
        try
        {
            var manifestPath = Path.Combine(temporaryRoot, "SnapPoints.v2dProject");
            ProjectVaultStore.Save(project, manifestPath);
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
                ?? throw new InvalidOperationException("Snap-point regression could not parse the project manifest.");
            var manifestDrawings = manifest["drawingObjects"]?.AsArray()
                ?? throw new InvalidOperationException("Snap-point manifest had no symbol array.");
            var savedDrawing = manifestDrawings
                .Select(node => node?.AsObject())
                .Single(node => node?["id"]?.GetValue<string>() == drawingObjectId)!;
            AssertTimeline(
                manifest["formatVersion"]?.GetValue<int>() == 4
                && savedDrawing["snapPoints"] is JsonArray { Count: 1 },
                "Project Vault did not write symbol snap points to manifest v4.");
            var restored = ProjectVaultStore.Load(manifestPath);
            var restoredDrawing = restored.DrawingObjects.SingleOrDefault(item => item.Id == drawingObjectId);
            var sourceDrawing = project.DrawingObjects.Single(item => item.Id == drawingObjectId);
            AssertTimeline(
                restoredDrawing?.SnapPoints.SequenceEqual(sourceDrawing.SnapPoints) == true,
                "Project Vault v4 did not round-trip symbol snap points.");

            savedDrawing["snapPoints"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "11111111111111111111111111111111",
                    ["x"] = 0,
                    ["y"] = 0,
                    ["z"] = 0
                },
                new JsonObject
                {
                    ["id"] = "11111111111111111111111111111111",
                    ["x"] = VectorProject.MaximumSnapPointCoordinate + 1,
                    ["y"] = 0,
                    ["z"] = 0
                });
            File.WriteAllText(manifestPath, manifest.ToJsonString(new() { WriteIndented = true }));
            ExpectInvalidData(
                () => ProjectVaultStore.Load(manifestPath),
                "Manifest v4 accepted duplicate or out-of-range symbol snap points.");

            foreach (var legacyVersion in new[] { 1, 2 })
            {
                var legacyRoot = Path.Combine(temporaryRoot, $"legacy-v{legacyVersion}");
                Directory.CreateDirectory(legacyRoot);
                var legacyPath = Path.Combine(legacyRoot, $"LegacyV{legacyVersion}.v2dProject");
                ProjectVaultStore.Save(VectorProject.CreateEmpty(), legacyPath);
                ConvertProjectCompressionFixtureToLegacy(legacyPath, legacyVersion);
                var legacyManifest = JsonNode.Parse(File.ReadAllText(legacyPath))!.AsObject();
                legacyManifest["formatVersion"] = legacyVersion;
                foreach (var node in legacyManifest["drawingObjects"]!.AsArray())
                {
                    node!.AsObject().Remove("snapPoints");
                }
                File.WriteAllText(legacyPath, legacyManifest.ToJsonString(new() { WriteIndented = true }));
                AssertTimeline(
                    ProjectVaultStore.Load(legacyPath).DrawingObjects.All(item => item.SnapPoints.Count == 0),
                    $"Manifest v{legacyVersion} without snap-point fields did not load with empty symbol data.");
            }
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    private static void RunSnapPointToolRegression()
    {
        var projectField = RequireField(typeof(MainForm), "_project");
        var sceneField = RequireField(typeof(MainForm), "_scene");
        var stageField = RequireField(typeof(MainForm), "_stage");
        var activateTool = RequireMethod(typeof(MainForm), "ActivateTool");
        var resolvePlacement = RequireMethod(typeof(MainForm), "ResolveSnapPointPlacement");
        var beginPointer = RequireMethod(typeof(MainForm), "BeginSnapPointPointer");
        var updatePointer = RequireMethod(typeof(MainForm), "UpdateSnapPointPointer");
        var completePointer = RequireMethod(typeof(MainForm), "CompleteSnapPointPointer");
        var cancelPointer = RequireMethod(typeof(MainForm), "CancelSnapPointPointer");
        var undoLastEdit = RequireMethod(typeof(MainForm), "UndoLastEdit");
        var projectDirtyField = RequireField(typeof(MainForm), "_projectDirty");

        using var form = CreateOffscreenSnapPointForm();
        form.Show();
        Application.DoEvents();
        var project = (VectorProject)projectField.GetValue(form)!;
        var scene = (VectorScene)sceneField.GetValue(form)!;
        var stage = (StageControl)stageField.GetValue(form)!;
        var drawingObject = project.DrawingObjects[0];
        var endpoint = new PointF(-1_000, -200);
        scene.AddLineSegment(
            0,
            endpoint,
            new PointF(-500, -200),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            6);
        var fillObject = scene.AddObject(
            0,
            new PointF(800, 100),
            new SizeF(400, 400),
            0,
            0,
            Color.CornflowerBlue,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        stage.BindScene(scene);
        activateTool.Invoke(form, [ToolMode.SnapPoint]);

        var endpointScreen = Point.Round(stage.WorldToScreen(endpoint.X + 1, endpoint.Y + 1));
        var endpointPlacementArgs = new object?[] { endpointScreen, false };
        var resolvedEndpoint = (PointF)resolvePlacement.Invoke(form, endpointPlacementArgs)!;
        if (endpointPlacementArgs[1] is not true || resolvedEndpoint != endpoint)
        {
            throw new InvalidOperationException("Snap Point Tool did not prioritize a nearby line endpoint.");
        }

        var contour = scene.GetObjectBoundaryContours(fillObject)
            .FirstOrDefault(points => points.Length >= 2)
            ?? throw new InvalidOperationException("Snap-point tool fixture had no fill boundary.");
        var boundary = VectorUnits.Quantize(new PointF(
            (contour[0].X + contour[1].X) * 0.5f,
            (contour[0].Y + contour[1].Y) * 0.5f));
        var boundaryScreen = Point.Round(stage.WorldToScreen(boundary.X, boundary.Y));
        var boundaryPlacementArgs = new object?[] { boundaryScreen, false };
        var resolvedBoundary = (PointF)resolvePlacement.Invoke(form, boundaryPlacementArgs)!;
        var boundaryError = MathF.Sqrt(
            (resolvedBoundary.X - boundary.X) * (resolvedBoundary.X - boundary.X)
            + (resolvedBoundary.Y - boundary.Y) * (resolvedBoundary.Y - boundary.Y));
        if (boundaryPlacementArgs[1] is not true
            || boundaryError > stage.ScreenLengthToWorld(1.5f))
        {
            throw new InvalidOperationException(
                "Snap Point Tool did not resolve a precise point on a fill boundary: "
                + $"expected={boundary}, actual={resolvedBoundary}, error={boundaryError:0.###}.");
        }

        beginPointer.Invoke(
            form,
            [new MouseEventArgs(MouseButtons.Left, 1, endpointScreen.X, endpointScreen.Y, 0)]);
        completePointer.Invoke(form, null);
        if (drawingObject.SnapPoints.Count != 1
            || drawingObject.SnapPoints[0].Position != endpoint
            || stage.SnapPointOverlayPoints.Count != 1)
        {
            throw new InvalidOperationException("Left click did not create and present an endpoint snap point.");
        }

        beginPointer.Invoke(
            form,
            [new MouseEventArgs(MouseButtons.Left, 1, endpointScreen.X, endpointScreen.Y, 0)]);
        updatePointer.Invoke(form, [boundaryScreen]);
        completePointer.Invoke(form, null);
        if (drawingObject.SnapPoints[0].Position != resolvedBoundary)
        {
            throw new InvalidOperationException("An existing snap point could not be dragged to a fill boundary.");
        }
        if (undoLastEdit.Invoke(form, null) is not true
            || drawingObject.SnapPoints[0].Position != endpoint
            || stage.SnapPointOverlayPoints.Single().Point != endpoint)
        {
            throw new InvalidOperationException("Undo did not restore a dragged snap point and its overlay.");
        }

        projectDirtyField.SetValue(form, false);
        beginPointer.Invoke(
            form,
            [new MouseEventArgs(MouseButtons.Left, 1, endpointScreen.X, endpointScreen.Y, 0)]);
        updatePointer.Invoke(form, [boundaryScreen]);
        cancelPointer.Invoke(form, [true]);
        if (drawingObject.SnapPoints[0].Position != endpoint
            || projectDirtyField.GetValue(form) is not false
            || stage.SnapPointOverlayPoints.Single().Point != endpoint)
        {
            throw new InvalidOperationException(
                "Cancel did not restore the snap point, overlay, and pre-edit project dirty state.");
        }

        var blankScreen = Point.Round(stage.WorldToScreen(2_000, -1_000));
        beginPointer.Invoke(
            form,
            [new MouseEventArgs(MouseButtons.Left, 1, blankScreen.X, blankScreen.Y, 0)]);
        cancelPointer.Invoke(form, [true]);
        if (drawingObject.SnapPoints.Count != 1 || projectDirtyField.GetValue(form) is not false)
        {
            throw new InvalidOperationException(
                "Cancel did not remove a newly placed snap point or restore the clean project state.");
        }

        beginPointer.Invoke(
            form,
            [new MouseEventArgs(MouseButtons.Right, 1, endpointScreen.X, endpointScreen.Y, 0)]);
        if (drawingObject.SnapPoints.Count != 0 || stage.SnapPointOverlayPoints.Count != 0)
        {
            throw new InvalidOperationException("Right click did not remove a symbol snap point.");
        }
        if (undoLastEdit.Invoke(form, null) is not true
            || drawingObject.SnapPoints.Count != 1
            || stage.SnapPointOverlayPoints.Count != 1)
        {
            throw new InvalidOperationException("Undo did not restore a deleted snap point and its overlay.");
        }
        Console.WriteLine("snap_point_tool_regression=ok");
    }

    private static void RunSnapPointSceneRegression()
    {
        var projectField = RequireField(typeof(MainForm), "_project");
        var workspaceTabsField = RequireField(typeof(MainForm), "_workspaceTabs");
        var stageField = RequireField(typeof(MainForm), "_stage");
        var setSelection = RequireMethod(
            typeof(MainForm),
            "SetSceneInstanceSelection",
            [typeof(DrawingObjectInstanceDefinition), typeof(bool)]);
        var rebuildComposition = RequireMethod(typeof(MainForm), "RebuildSceneComposition");
        var beginSpatialEdit = RequireMethod(
            typeof(MainForm),
            "BeginSpatialTransformEdit",
            [typeof(bool)]);
        var cancelSpatialEdit = RequireMethod(typeof(MainForm), "CancelSpatialTransformEdit");
        var resolveDelta = RequireMethod(typeof(MainForm), "ResolveSceneSnapDelta");

        using var form = CreateOffscreenSnapPointForm();
        form.Show();
        Application.DoEvents();
        var project = (VectorProject)projectField.GetValue(form)!;
        var workspaceTabs = (WorkspaceTabs)workspaceTabsField.GetValue(form)!;
        var stage = (StageControl)stageField.GetValue(form)!;
        var scene = project.Scenes[0];
        var definition = project.DrawingObjects[0];
        definition.Scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 160),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        project.TryAddDrawingObjectSnapPoint(definition.Id, 0, 0, 0, out _);
        project.TryAddSceneInstance(scene.Id, definition.Id, PointF.Empty, 0, out var source);
        project.TryAddSceneInstance(scene.Id, definition.Id, new PointF(500, 0), 0, out _);
        project.TryAddSceneInstance(scene.Id, definition.Id, new PointF(0, 500), 0, out _);
        project.TryAddSceneInstance(scene.Id, definition.Id, new PointF(500, 500), 0, out _);
        if (source is null) throw new InvalidOperationException("Snap-point scene regression could not create its source instance.");

        workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
        Application.DoEvents();
        rebuildComposition.Invoke(form, [false, false]);
        setSelection.Invoke(form, [source, false]);
        beginSpatialEdit.Invoke(form, [true]);

        Vector3 Resolve(Vector3 raw, Func<Vector3, Vector3> constraint, bool requested = true)
        {
            return (Vector3)resolveDelta.Invoke(form, [raw, constraint, requested])!;
        }

        var raw2D = new Vector3(475, 0, 0);
        var unsnapped2D = Resolve(
            raw2D,
            correction => new Vector3(correction.X, correction.Y, 0),
            requested: false);
        var snapped2D = Resolve(raw2D, correction => new Vector3(correction.X, correction.Y, 0));
        if (unsnapped2D != raw2D || snapped2D != new Vector3(500, 0, 0))
        {
            var editSession = RequireField(typeof(MainForm), "_spatialTransformEditSession").GetValue(form);
            var composition = (SceneCompositionResult)RequireField(typeof(MainForm), "_sceneCompositionResult").GetValue(form)!;
            var ownerIds = composition.ObjectOwners.Values
                .Select(owner => string.IsNullOrWhiteSpace(owner.RootInstanceId)
                    ? owner.InstanceId
                    : owner.RootInstanceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var sourceWorld = DrawingObjectSnapPointMath.Transform(
                definition,
                source.EvaluateState(0) with { X = raw2D.X, Y = raw2D.Y, Z = raw2D.Z },
                definition.SnapPoints[0]);
            var sourceScreen = stage.WorldToScreen(sourceWorld.X, sourceWorld.Y);
            var targetDiagnostics = scene.Instances
                .Where(instance => instance.Id != source.Id)
                .Select(instance =>
                {
                    var targetWorld = DrawingObjectSnapPointMath.Transform(
                        definition,
                        instance.EvaluateState(0),
                        definition.SnapPoints[0]);
                    var targetScreen = stage.WorldToScreen(targetWorld.X, targetWorld.Y);
                    return $"{instance.Id[..6]}:{targetWorld}/{targetScreen}/d="
                        + $"{Vector2.Distance(new Vector2(sourceScreen.X, sourceScreen.Y), new Vector2(targetScreen.X, targetScreen.Y)):0.###}";
                });
            throw new InvalidOperationException(
                "Scene 2D Shift movement did not gate or align symbol snap points: "
                + $"raw={raw2D}, withoutShift={unsnapped2D}, withShift={snapped2D}, "
                + $"sessionActive={editSession is not null}, snapPoints={definition.SnapPoints.Count}, "
                + $"owners={ownerIds.Length}, source={sourceWorld}/{sourceScreen}, "
                + $"targets=[{string.Join(";", targetDiagnostics)}].");
        }

        stage.ConfigureReferenceView(scene, SceneDimension.TwoD);
        stage.SetReferenceViewDirection(ReferenceViewDirection.Right);
        var projected = Resolve(
            new Vector3(0, 490, 0),
            correction => new Vector3(0, correction.Y, correction.Z));
        if (projected != new Vector3(0, 500, 0))
        {
            throw new InvalidOperationException("Projected 2D Shift movement did not align visible snap points in its view plane.");
        }

        scene.Dimension = SceneDimension.ThreeD;
        scene.Camera.Projection = CameraProjection.Perspective;
        stage.ConfigureReferenceView(scene, SceneDimension.ThreeD);
        stage.SetReferenceCameraOrientation(0.58f, -0.34f);
        var axis = Resolve(new Vector3(495, 0, 0), correction => Vector3.UnitX * correction.X);
        if (axis != new Vector3(500, 0, 0))
        {
            throw new InvalidOperationException("3D axis movement broke its constraint while aligning snap points.");
        }
        var plane = Resolve(new Vector3(495, 495, 0), correction => new Vector3(correction.X, correction.Y, 0));
        if (plane != new Vector3(500, 500, 0)
            || stage.SceneSnapIndicatorSource is null
            || stage.SceneSnapIndicatorTarget is null)
        {
            throw new InvalidOperationException("3D plane movement did not align snap points or present its target indicator.");
        }

        cancelSpatialEdit.Invoke(form, null);
        RunSnapPointScenePerformanceRegression();
        Console.WriteLine("snap_point_scene_regression=ok");
    }

    private static void RunSnapPointScenePerformanceRegression()
    {
        const int snapPointCount = 64;
        const int instanceCount = 96;
        const int warmSamples = 96;
        const double warmAverageBudgetMilliseconds = 3;
        const long warmAllocationBudgetBytes = 8192;

        var projectField = RequireField(typeof(MainForm), "_project");
        var workspaceTabsField = RequireField(typeof(MainForm), "_workspaceTabs");
        var stageField = RequireField(typeof(MainForm), "_stage");
        var cacheField = RequireField(typeof(MainForm), "_sceneSnapQueryCache");
        var candidateBuildsField = RequireField(typeof(MainForm), "_sceneSnapCandidateCacheBuildCount");
        var projectionBuildsField = RequireField(typeof(MainForm), "_sceneSnapProjectionCacheBuildCount");
        var targetPointCountField = RequireField(typeof(MainForm), "_lastSceneSnapTargetPointCount");
        var candidateChecksField = RequireField(typeof(MainForm), "_lastSceneSnapCandidateCheckCount");
        var setSelection = RequireMethod(
            typeof(MainForm),
            "SetSceneInstanceSelection",
            [typeof(DrawingObjectInstanceDefinition), typeof(bool)]);
        var rebuildComposition = RequireMethod(typeof(MainForm), "RebuildSceneComposition");
        var beginSpatialEdit = RequireMethod(
            typeof(MainForm),
            "BeginSpatialTransformEdit",
            [typeof(bool)]);
        var cancelSpatialEdit = RequireMethod(typeof(MainForm), "CancelSpatialTransformEdit");
        var resolveDelta = RequireMethod(typeof(MainForm), "ResolveSceneSnapDelta")
            .CreateDelegate<Func<MainForm, Vector3, Func<Vector3, Vector3>, bool?, Vector3>>();
        Func<Vector3, Vector3> constrain2D = static correction =>
            new Vector3(correction.X, correction.Y, 0);

        using var form = CreateOffscreenSnapPointForm();
        form.Show();
        Application.DoEvents();
        var project = (VectorProject)projectField.GetValue(form)!;
        var workspaceTabs = (WorkspaceTabs)workspaceTabsField.GetValue(form)!;
        var stage = (StageControl)stageField.GetValue(form)!;
        var scene = project.Scenes[0];
        var definition = project.DrawingObjects[0];
        definition.Scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 160),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        for (var pointIndex = 0; pointIndex < snapPointCount; pointIndex++)
        {
            var x = pointIndex % 8 * 4000f;
            var y = pointIndex / 8 * 4000f;
            if (!project.TryAddDrawingObjectSnapPoint(definition.Id, x, y, 0, out _))
            {
                throw new InvalidOperationException("Dense snap-point regression could not create its point grid.");
            }
        }

        if (!project.TryAddSceneInstance(scene.Id, definition.Id, PointF.Empty, 0, out var source)
            || source is null
            || !project.TryAddSceneInstance(scene.Id, definition.Id, new PointF(500, 0), 0, out _))
        {
            throw new InvalidOperationException("Dense snap-point regression could not create its near instances.");
        }
        for (var instanceIndex = 2; instanceIndex < instanceCount; instanceIndex++)
        {
            var position = new PointF(
                100_000 + instanceIndex * 20_000,
                100_000 + instanceIndex % 7 * 20_000);
            if (!project.TryAddSceneInstance(scene.Id, definition.Id, position, 0, out _))
            {
                throw new InvalidOperationException("Dense snap-point regression could not create its far instances.");
            }
        }

        workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
        Application.DoEvents();
        rebuildComposition.Invoke(form, [false, false]);
        setSelection.Invoke(form, [source, false]);
        beginSpatialEdit.Invoke(form, [true]);

        var raw = new Vector3(475, 0, 0);
        var candidateBuildsBefore = (int)candidateBuildsField.GetValue(form)!;
        for (var sample = 0; sample < warmSamples; sample++)
        {
            if (resolveDelta(form, raw, constrain2D, false) != raw)
            {
                throw new InvalidOperationException("Dense snap-point no-Shift movement changed its delta.");
            }
        }
        if ((int)candidateBuildsField.GetValue(form)! != candidateBuildsBefore
            || cacheField.GetValue(form) is not null)
        {
            throw new InvalidOperationException("Dense snap-point no-Shift movement eagerly built a candidate cache.");
        }

        var snapped = resolveDelta(form, raw, constrain2D, true);
        var cache = cacheField.GetValue(form);
        var expectedTargetPoints = (instanceCount - 1) * snapPointCount;
        if (snapped != new Vector3(500, 0, 0)
            || cache is null
            || (int)candidateBuildsField.GetValue(form)! != candidateBuildsBefore + 1
            || (int)projectionBuildsField.GetValue(form)! != 1
            || (int)targetPointCountField.GetValue(form)! != expectedTargetPoints
            || (int)candidateChecksField.GetValue(form)! > snapPointCount * 2)
        {
            throw new InvalidOperationException(
                "Dense snap-point cold query did not build one bounded candidate index: "
                + $"delta={snapped}, targets={targetPointCountField.GetValue(form)}, "
                + $"checks={candidateChecksField.GetValue(form)}, "
                + $"candidate_builds={candidateBuildsField.GetValue(form)}, "
                + $"projection_builds={projectionBuildsField.GetValue(form)}.");
        }

        var maximumCandidateChecks = 0;
        for (var sample = 0; sample < warmSamples; sample++)
        {
            var sampleRaw = new Vector3(raw.X + sample % 5, raw.Y, raw.Z);
            if (resolveDelta(form, sampleRaw, constrain2D, true) != new Vector3(500, 0, 0))
            {
                throw new InvalidOperationException("Dense snap-point warm query changed its alignment.");
            }
            maximumCandidateChecks = Math.Max(
                maximumCandidateChecks,
                (int)candidateChecksField.GetValue(form)!);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var sample = 0; sample < warmSamples; sample++)
        {
            var sampleRaw = new Vector3(raw.X + sample % 5, raw.Y, raw.Z);
            if (resolveDelta(form, sampleRaw, constrain2D, true) != new Vector3(500, 0, 0))
            {
                throw new InvalidOperationException("Measured dense snap-point query changed its alignment.");
            }
        }
        var elapsedMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var averageMilliseconds = elapsedMilliseconds / warmSamples;
        if (!ReferenceEquals(cache, cacheField.GetValue(form))
            || (int)candidateBuildsField.GetValue(form)! != candidateBuildsBefore + 1
            || (int)projectionBuildsField.GetValue(form)! != 1
            || maximumCandidateChecks > snapPointCount * 2
            || allocatedBytes > warmAllocationBudgetBytes
            || averageMilliseconds > warmAverageBudgetMilliseconds)
        {
            throw new InvalidOperationException(
                "Dense snap-point warm queries rebuilt or scanned the Cartesian product: "
                + $"average_ms={averageMilliseconds:0.000}, allocated={allocatedBytes}, "
                + $"checks={maximumCandidateChecks}, "
                + $"candidate_builds={candidateBuildsField.GetValue(form)}, "
                + $"projection_builds={projectionBuildsField.GetValue(form)}.");
        }

        if (!stage.TryProjectScenePosition(Vector3.Zero, out var beforePan, out _))
        {
            throw new InvalidOperationException("Dense snap-point fixture could not project before camera movement.");
        }
        stage.PanReferenceCamera(12, 0);
        if (!stage.TryProjectScenePosition(Vector3.Zero, out var afterPan, out _)
            || beforePan == afterPan
            || resolveDelta(form, raw, constrain2D, true) != new Vector3(500, 0, 0)
            || !ReferenceEquals(cache, cacheField.GetValue(form))
            || (int)candidateBuildsField.GetValue(form)! != candidateBuildsBefore + 1
            || (int)projectionBuildsField.GetValue(form)! != 2)
        {
            throw new InvalidOperationException(
                "A 2D camera change did not rebuild only the snap-point projection index.");
        }

        for (var sample = 0; sample < warmSamples; sample++)
        {
            if (resolveDelta(form, raw, constrain2D, false) != raw)
            {
                throw new InvalidOperationException("Warm no-Shift movement changed its unsnapped delta.");
            }
        }
        if (!ReferenceEquals(cache, cacheField.GetValue(form))
            || (int)candidateBuildsField.GetValue(form)! != candidateBuildsBefore + 1
            || (int)projectionBuildsField.GetValue(form)! != 2
            || (int)candidateChecksField.GetValue(form)! != 0)
        {
            throw new InvalidOperationException("Warm no-Shift movement touched the snap-point candidate index.");
        }

        Console.WriteLine(
            "snap_point_scene_dense_query_metrics="
            + $"targets={expectedTargetPoints},checks={maximumCandidateChecks},"
            + $"warm_average_ms={averageMilliseconds:0.000},warm_allocated_bytes={allocatedBytes}");
        cancelSpatialEdit.Invoke(form, null);
        if (cacheField.GetValue(form) is not null)
        {
            throw new InvalidOperationException("Ending a dense snap-point session retained its candidate cache.");
        }
    }

    private static void RunSnapPointOverlayRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        using var stage = new StageControl(scene)
        {
            ClientSize = new Size(360, 240),
            BackColor = Color.Black,
            WorldGridOpacity = 0
        };
        var point = new SnapPointOverlayEntry("hovered", PointF.Empty);
        stage.SetSnapPointOverlay(
            [point],
            hoveredId: point.Id,
            candidate: new PointF(500, 0),
            candidateSnapped: true);
        stage.SetSceneSnapIndicator(new Vector3(-250, 250, 0), new Vector3(250, 250, 0));
        var drawOverlay = RequireMethod(typeof(StageControl), "DrawSnapPointOverlay");
        using var bitmap = new Bitmap(stage.ClientSize.Width, stage.ClientSize.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(stage.BackColor);
            drawOverlay.Invoke(stage, [graphics]);
        }
        var pointScreen = Point.Round(stage.WorldToScreen(0, 0));
        var candidateScreen = Point.Round(stage.WorldToScreen(500, 0));
        var targetScreen = Point.Round(stage.WorldToScreen(250, 250));
        if (!HasNonBackgroundPixel(bitmap, pointScreen, stage.BackColor, 8)
            || !HasNonBackgroundPixel(bitmap, candidateScreen, stage.BackColor, 8)
            || !HasNonBackgroundPixel(bitmap, targetScreen, stage.BackColor, 12))
        {
            throw new InvalidOperationException("GDI did not render the snap point, candidate, and scene target overlays.");
        }
        stage.ClearSnapPointOverlay();
        stage.ClearSceneSnapIndicator();
        if (stage.SnapPointOverlayPoints.Count != 0
            || stage.SnapPointOverlayCandidate is not null
            || stage.SceneSnapIndicatorTarget is not null)
        {
            throw new InvalidOperationException("Snap-point overlay cleanup retained stale Stage state.");
        }

        using var form = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            ClientSize = new Size(360, 240)
        };
        using var direct2DStage = new StageControl(scene) { Dock = DockStyle.Fill, WorldGridOpacity = 0 };
        direct2DStage.SetSnapPointOverlay([point], activeId: point.Id);
        direct2DStage.SetSceneSnapIndicator(Vector3.Zero, new Vector3(250, 0, 0));
        form.Controls.Add(direct2DStage);
        form.Show();
        Application.DoEvents();
        direct2DStage.Invalidate();
        direct2DStage.Update();
        Application.DoEvents();
        if (direct2DStage.GpuAccelerationActive && !direct2DStage.LastFrameUsedDirect2D)
        {
            throw new InvalidOperationException("An available Direct2D Stage fell back while drawing snap-point overlays.");
        }
        Console.WriteLine(direct2DStage.LastFrameUsedDirect2D
            ? "snap_point_overlay_direct2d=ok"
            : "snap_point_overlay_direct2d=skipped_gpu_unavailable");
        Console.WriteLine("snap_point_overlay_regression=ok");
    }

    private static MainForm CreateOffscreenSnapPointForm() => new()
    {
        ShowInTaskbar = false,
        StartPosition = FormStartPosition.Manual,
        Location = new Point(-30_000, -30_000),
        Size = new Size(1280, 800)
    };

    private static bool HasNonBackgroundPixel(
        Bitmap bitmap,
        Point center,
        Color background,
        int radius)
    {
        for (var y = Math.Max(0, center.Y - radius); y <= Math.Min(bitmap.Height - 1, center.Y + radius); y++)
        {
            for (var x = Math.Max(0, center.X - radius); x <= Math.Min(bitmap.Width - 1, center.X + radius); x++)
            {
                if (bitmap.GetPixel(x, y).ToArgb() != background.ToArgb()) return true;
            }
        }
        return false;
    }

    private static ProjectRestartSnapshot ReplaceFirstDrawingSnapPoints(
        ProjectRestartSnapshot source,
        DrawingObjectSnapPointRestartSnapshot[] snapPoints)
    {
        var drawings = source.DrawingObjects.ToArray();
        var drawing = drawings[0];
        drawings[0] = new DrawingObjectRestartSnapshot
        {
            Id = drawing.Id,
            Name = drawing.Name,
            Kind = drawing.Kind,
            Detail = drawing.Detail,
            AssetFolderId = drawing.AssetFolderId,
            AssetTagIds = drawing.AssetTagIds,
            AnchorX = drawing.AnchorX,
            AnchorY = drawing.AnchorY,
            SnapPoints = snapPoints,
            CreatedAt = drawing.CreatedAt,
            Scene = drawing.Scene,
            Instances = drawing.Instances
        };
        return new ProjectRestartSnapshot
        {
            Id = source.Id,
            Name = source.Name,
            PlaybackFps = source.PlaybackFps,
            LoopPlayback = source.LoopPlayback,
            PlaybackStartFrame = source.PlaybackStartFrame,
            PlaybackEndFrame = source.PlaybackEndFrame,
            AssetTags = source.AssetTags,
            AssetFolders = source.AssetFolders,
            ExternalSvgAssets = source.ExternalSvgAssets,
            DrawingObjects = drawings,
            Scenes = source.Scenes
        };
    }

    private static void ExpectInvalidSnapPointRestart(ProjectRestartSnapshot snapshot, string label)
    {
        try
        {
            VectorProject.RestoreRestartSnapshot(snapshot);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException($"Editor restart accepted {label}.");
    }

}
