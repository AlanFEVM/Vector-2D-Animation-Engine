using System.Reflection;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunHierarchyFocusRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(2);
        var primary = scene.AddObject(
            0,
            new PointF(24_000, -8_000),
            new SizeF(42_000, 18_000),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            200,
            ShapeKind.Rectangle);
        var descendant = scene.AddObject(
            1,
            new PointF(-18_000, 12_000),
            new SizeF(8_000, 6_000),
            0,
            0,
            Color.Coral,
            Color.Transparent,
            100,
            ShapeKind.Rectangle);
        scene.ActiveLayer = 1;
        var folder = scene.AddFolderLayer("Focus folder");
        var descendantLayer = (int)scene.ObjectLayer[descendant];
        var emptyLayer = scene.AddLayer("Empty focus layer");

        var objectTargets = MainForm.ResolveHierarchyFocusObjectIndices(
            scene,
            0,
            HierarchyNodeKind.Object,
            primary);
        var folderTargets = MainForm.ResolveHierarchyFocusObjectIndices(
            scene,
            0,
            HierarchyNodeKind.Layer,
            folder);
        if (!objectTargets.SequenceEqual([primary])
            || !folderTargets.SequenceEqual([descendant])
            || MainForm.ResolveHierarchyFocusObjectIndices(
                scene,
                0,
                HierarchyNodeKind.Layer,
                emptyLayer).Length != 0
            || MainForm.ResolveHierarchyFocusObjectIndices(
                scene,
                0,
                HierarchyNodeKind.ObjectsRoot,
                -1).Length != 0)
        {
            throw new InvalidOperationException("Hierarchy focus did not resolve object, folder, empty, and root targets correctly.");
        }

        scene.LayerVisible[descendantLayer] = false;
        if (MainForm.ResolveHierarchyFocusObjectIndices(
                scene,
                0,
                HierarchyNodeKind.Layer,
                folder).Length != 0)
        {
            throw new InvalidOperationException("Hierarchy focus included content from a hidden descendant layer.");
        }
        scene.LayerVisible[descendantLayer] = true;

        RunHierarchyFocusEventRegression(scene, primary, folder);
        RunHierarchyFocusCameraRegression(scene, primary, descendantLayer);
        Console.WriteLine("hierarchy_double_click_focus=ok");
    }

    private static void RunHierarchyFocusEventRegression(VectorScene scene, int objectIndex, int layerIndex)
    {
        using var hierarchy = new HierarchyPanel();
        hierarchy.BindScene(scene);
        var tree = hierarchy.Controls.OfType<TreeView>().SingleOrDefault()
            ?? throw new InvalidOperationException("Hierarchy focus regression did not find the TreeView.");
        var raiseDoubleClick = typeof(TreeView).GetMethod(
                "OnNodeMouseDoubleClick",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Hierarchy focus regression could not raise a node double-click.");
        var requests = new List<HierarchySelectionChangedEventArgs>();
        hierarchy.HierarchyFocusRequested += (_, e) => requests.Add(e);

        void DoubleClick(TreeNode node, MouseButtons button)
        {
            raiseDoubleClick.Invoke(
                tree,
                [new TreeNodeMouseClickEventArgs(node, button, 2, node.Bounds.X + 2, node.Bounds.Y + 2)]);
        }

        hierarchy.SelectObject(objectIndex);
        var objectNode = hierarchy.SelectedNode
            ?? throw new InvalidOperationException("Hierarchy focus regression did not select the object node.");
        DoubleClick(objectNode, MouseButtons.Left);
        DoubleClick(objectNode, MouseButtons.Left);
        DoubleClick(objectNode, MouseButtons.Right);
        if (requests.Count != 2
            || requests.Any(request => request.Kind != HierarchyNodeKind.Object || request.Index != objectIndex))
        {
            throw new InvalidOperationException("Hierarchy object double-click did not emit one left-button focus request per activation.");
        }

        hierarchy.SelectLayer(layerIndex);
        var layerNode = hierarchy.SelectedNode
            ?? throw new InvalidOperationException("Hierarchy focus regression did not select the layer node.");
        DoubleClick(layerNode, MouseButtons.Left);
        var sceneNode = tree.Nodes[0];
        DoubleClick(sceneNode, MouseButtons.Left);
        DoubleClick(sceneNode.Nodes[0], MouseButtons.Left);
        DoubleClick(sceneNode.Nodes[1], MouseButtons.Left);
        if (requests.Count != 3
            || requests[^1].Kind != HierarchyNodeKind.Layer
            || requests[^1].Index != layerIndex)
        {
            throw new InvalidOperationException("Hierarchy layer/root double-click focus routing was not selective.");
        }

        using var placeholder = new HierarchyPanel();
        var placeholderTree = placeholder.Controls.OfType<TreeView>().Single();
        var placeholderRequests = 0;
        placeholder.HierarchyFocusRequested += (_, _) => placeholderRequests++;
        var placeholderLayer = placeholderTree.Nodes[0].Nodes[0].Nodes[0];
        var placeholderObject = placeholderTree.Nodes[0].Nodes[1].Nodes[0];
        raiseDoubleClick.Invoke(
            placeholderTree,
            [new TreeNodeMouseClickEventArgs(placeholderLayer, MouseButtons.Left, 2, 0, 0)]);
        raiseDoubleClick.Invoke(
            placeholderTree,
            [new TreeNodeMouseClickEventArgs(placeholderObject, MouseButtons.Left, 2, 0, 0)]);
        if (placeholderRequests != 0)
        {
            throw new InvalidOperationException("Hierarchy placeholder double-click emitted a focus request.");
        }
    }

    private static void RunHierarchyFocusCameraRegression(
        VectorScene scene,
        int objectIndex,
        int hiddenLayer)
    {
        using var stage = new StageControl(scene) { Size = new Size(800, 600) };
        stage.ConfigureReferenceView(null, SceneDimension.TwoD, ReferenceCameraMotion.Immediate);
        var bounds = scene.GetObjectWorldBounds(objectIndex);
        var geometryRevision = scene.GeometryRevision;
        if (!stage.FocusSceneObjects([objectIndex], ReferenceCameraMotion.Immediate))
        {
            throw new InvalidOperationException("Hierarchy focus could not frame a planar 2D object.");
        }

        var visible = stage.VisibleWorldBounds();
        var expectedCenterX = bounds.Left + bounds.Width * 0.5f;
        var expectedCenterY = bounds.Top + bounds.Height * 0.5f;
        if (Math.Abs(stage.CameraX - expectedCenterX) > 0.01f
            || Math.Abs(stage.CameraY - expectedCenterY) > 0.01f
            || stage.Zoom > 1.0001f
            || visible.Left > bounds.Left
            || visible.Top > bounds.Top
            || visible.Right < bounds.Right
            || visible.Bottom < bounds.Bottom
            || scene.GeometryRevision != geometryRevision)
        {
            throw new InvalidOperationException("Hierarchy focus did not fit planar bounds without mutating scene geometry.");
        }

        var constrainedBounds = new RectangleF(4_000_000, -30_000_000, 40_000_000, 40_000_000);
        if (!stage.FocusWorldBounds(constrainedBounds)
            || Math.Abs(stage.CameraX - 24_000_000f) > 0.01f
            || Math.Abs(stage.CameraY + 10_000_000f) > 0.01f
            || Math.Abs(stage.Zoom - 0.02f) > 0.0001f)
        {
            throw new InvalidOperationException(
                "Hierarchy focus did not center oversized planar bounds at the minimum zoom.");
        }

        var unchanged = stage.CaptureViewState();
        scene.LayerVisible[hiddenLayer] = false;
        var hiddenObject = Enumerable.Range(0, scene.ObjectCount)
            .First(index => scene.ObjectLayer[index] == hiddenLayer);
        if (stage.FocusSceneObjects([hiddenObject], ReferenceCameraMotion.Immediate)
            || stage.CaptureViewState() != unchanged)
        {
            throw new InvalidOperationException("Hierarchy focus moved the 2D camera for hidden content.");
        }
        scene.LayerVisible[hiddenLayer] = true;

        var definition = new SceneDefinition
        {
            Dimension = SceneDimension.TwoD,
            Camera = { Projection = CameraProjection.Perspective }
        };
        stage.ConfigureReferenceView(definition, SceneDimension.TwoD, ReferenceCameraMotion.Immediate);
        stage.SetReferenceViewDirection(ReferenceViewDirection.Right, ReferenceCameraMotion.Immediate);
        if (!stage.UsesReferenceProjection
            || !stage.TryGetReferenceFocusPoints([objectIndex], out var projectedPoints)
            || !stage.FocusSceneObjects([objectIndex], ReferenceCameraMotion.Immediate))
        {
            throw new InvalidOperationException("Hierarchy focus could not frame a projected 2D object.");
        }
        AssertReferenceCameraFocusVisible(stage, projectedPoints, "hierarchy projected 2D focus");

        definition.Dimension = SceneDimension.ThreeD;
        stage.ConfigureReferenceView(definition, SceneDimension.ThreeD, ReferenceCameraMotion.Immediate);
        stage.SetReferenceCameraOrientation(0.48f, -0.27f, ReferenceCameraMotion.Immediate);
        if (!stage.TryGetReference3DFocusPoints([objectIndex], out var scenePoints)
            || !stage.FocusSceneObjects([objectIndex], ReferenceCameraMotion.Animated))
        {
            throw new InvalidOperationException("Hierarchy focus could not frame a 3D object.");
        }
        stage.CompleteReferenceCameraTransition();
        AssertReferenceCameraFocusVisible(stage, scenePoints, "hierarchy 3D focus");
        if (scene.GeometryRevision != geometryRevision)
        {
            throw new InvalidOperationException("Hierarchy camera focus mutated scene geometry.");
        }

        stage.SetReferenceViewDirection(ReferenceViewDirection.Front, ReferenceCameraMotion.Immediate);
        stage.ConfigureReferenceView(
            definition,
            SceneDimension.TwoD,
            ReferenceCameraMotion.Animated);
        var handoffTarget = stage.CaptureReferenceCameraFrameForPersistence();
        if (UiMotion.AnimationsEnabled
            && (!stage.ReferenceCameraTransitionActive
                || !stage.AdvanceReferenceCameraTransition(110)))
        {
            throw new InvalidOperationException(
                "Hierarchy focus regression could not enter a 3D-to-2D camera handoff.");
        }
        if (stage.UsesReferenceProjection
            || !stage.FocusSceneObjects([objectIndex], ReferenceCameraMotion.Immediate)
            || stage.ReferenceCameraTransitionActive
            || Math.Abs(stage.CameraX - expectedCenterX) > 0.01f
            || Math.Abs(stage.CameraY - expectedCenterY) > 0.01f)
        {
            throw new InvalidOperationException(
                "Hierarchy planar focus did not complete an active reference-camera handoff cleanly.");
        }
        AssertReferenceCameraFrameNear(
            stage.CaptureReferenceCameraFrameForPersistence(),
            handoffTarget,
            "hierarchy planar focus handoff");
    }

    private static void RunHierarchyFocusMainFormRegression(
        MainForm form,
        StageControl stage,
        HierarchyPanel hierarchy,
        VectorScene scene,
        Func<int> undoCount)
    {
        if (!form.IsHandleCreated || !stage.IsHandleCreated)
        {
            throw new InvalidOperationException("Hierarchy focus regression requires a real MainForm and Stage HWND.");
        }

        hierarchy.BindScene(scene);
        var tree = hierarchy.Controls.OfType<TreeView>().SingleOrDefault()
            ?? throw new InvalidOperationException("Hierarchy focus regression did not find the MainForm TreeView.");
        _ = tree.Handle;
        var raiseDoubleClick = typeof(TreeView).GetMethod(
                "OnNodeMouseDoubleClick",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Hierarchy focus regression could not raise a MainForm node double-click.");
        var objectIndex = Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.IsObjectActive(index, stage.Frame))
            .Select(index => (int?)index)
            .FirstOrDefault();
        if (objectIndex is not { } focusedObject
            || !stage.TryGetReference3DFocusPoints([focusedObject], out var objectPoints))
        {
            throw new InvalidOperationException("Hierarchy focus regression did not find visible composed Scene content.");
        }

        var revision = scene.GeometryRevision;
        var undoStart = undoCount();
        stage.ResetReferenceCameraView(ReferenceCameraMotion.Immediate);
        hierarchy.SelectObject(focusedObject);
        Application.DoEvents();
        var objectNode = hierarchy.SelectedNode
            ?? throw new InvalidOperationException("Hierarchy focus regression did not select a MainForm object node.");
        raiseDoubleClick.Invoke(
            tree,
            [new TreeNodeMouseClickEventArgs(
                objectNode,
                MouseButtons.Left,
                2,
                objectNode.Bounds.X + 2,
                objectNode.Bounds.Y + 2)]);
        Application.DoEvents();
        stage.CompleteReferenceCameraTransition();
        AssertReferenceCameraFocusVisible(stage, objectPoints, "MainForm hierarchy object focus");

        var layer = (int)scene.ObjectLayer[focusedObject];
        var layerTargets = MainForm.ResolveHierarchyFocusObjectIndices(
            scene,
            stage.Frame,
            HierarchyNodeKind.Layer,
            layer);
        if (layerTargets.Length == 0
            || !stage.TryGetReference3DFocusPoints(layerTargets, out var layerPoints))
        {
            throw new InvalidOperationException("Hierarchy focus regression could not resolve the MainForm layer target.");
        }
        stage.ResetReferenceCameraView(ReferenceCameraMotion.Immediate);
        hierarchy.SelectLayer(layer);
        Application.DoEvents();
        var layerNode = hierarchy.SelectedNode
            ?? throw new InvalidOperationException("Hierarchy focus regression did not select a MainForm layer node.");
        raiseDoubleClick.Invoke(
            tree,
            [new TreeNodeMouseClickEventArgs(
                layerNode,
                MouseButtons.Left,
                2,
                layerNode.Bounds.X + 2,
                layerNode.Bounds.Y + 2)]);
        Application.DoEvents();
        stage.CompleteReferenceCameraTransition();
        AssertReferenceCameraFocusVisible(stage, layerPoints, "MainForm hierarchy layer focus");
        if (!tree.IsHandleCreated
            || scene.GeometryRevision != revision
            || undoCount() != undoStart)
        {
            throw new InvalidOperationException("MainForm hierarchy focus changed model or undo state.");
        }
    }
}
