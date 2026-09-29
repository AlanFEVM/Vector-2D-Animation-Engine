using System.Diagnostics;
using System.Numerics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunProjectDocumentStructureRegression()
    {
        var project = new VectorProject();
        AssertTimeline(
            project.DrawingObjects.Count == 1
            && project.DrawingObjects[0].CanDraw
            && project.DrawingObjects[0].Scene.LayerCount == 1
            && project.DrawingObjects[0].FrameCount == 1
            && project.DrawingObjects[0].Timeline.Tracks.Count == 1
            && project.DrawingObjects[0].Timeline.Tracks[0].Duration == 1
            && project.Scenes.Count == 1
            && !project.Scenes[0].CanDraw
            && project.Scenes[0].Layers.Count == 1
            && project.Scenes[0].FrameCount == 1
            && project.Scenes[0].Camera.Projection == CameraProjection.Perspective
            && project.Scenes[0].Timeline.Tracks.Count == 1
            && project.Scenes[0].Timeline.Tracks[0].Duration == 1
            && project.DrawingObjects is not ICollection<DrawingObjectDefinition> { IsReadOnly: false }
            && project.Scenes is not ICollection<SceneDefinition> { IsReadOnly: false },
            "A new project did not create one layer, one frame, and the preferred perspective camera for its scene timeline.");

        var appendedSceneProject = VectorProject.CreateEmpty();
        var appendedScene = appendedSceneProject.AddScene("Second perspective scene");
        AssertTimeline(
            appendedScene.Camera.Projection == CameraProjection.Perspective,
            "A scene added to an existing project did not use the preferred perspective camera.");

        var scaleZDefaultsProject = VectorProject.CreateEmpty();
        var scaleZContainer = scaleZDefaultsProject.DrawingObjects[0];
        var scaleZChild = scaleZDefaultsProject.AddDrawingObject("Scale Z nested default");
        DrawingObjectInstanceDefinition? defaultNestedInstance = null;
        DrawingObjectInstanceDefinition? defaultSceneRoot = null;
        AssertTimeline(
            scaleZDefaultsProject.TryAddDrawingObjectInstance(
                scaleZContainer.Id,
                scaleZChild.Id,
                PointF.Empty,
                out defaultNestedInstance)
            && defaultNestedInstance is { ScaleZ: 1 }
            && scaleZDefaultsProject.TryAddSceneInstance(
                scaleZDefaultsProject.Scenes[0].Id,
                scaleZContainer.Id,
                PointF.Empty,
                0,
                out defaultSceneRoot)
            && defaultSceneRoot is { ScaleZ: 0 },
            "New scene roots did not start flat or changed the nested drawing-object Scale Z default.");
        defaultSceneRoot!.ScaleZ = 2.75f;
        var restoredScaleZDefaults = VectorProject.RestoreRestartSnapshot(
            scaleZDefaultsProject.CreateRestartSnapshot());
        AssertTimeline(
            restoredScaleZDefaults.Scenes[0].Instances.Single(instance =>
                string.Equals(instance.Id, defaultSceneRoot.Id, StringComparison.Ordinal)).ScaleZ == 2.75f,
            "Restoring a project snapshot replaced an existing scene-root Scale Z value with the new default.");

        var first = project.DrawingObjects[0];
        var second = project.AddDrawingObject("Nested B");
        var third = project.AddDrawingObject("Nested C");
        second.Scene.AddObject(0, PointF.Empty, new SizeF(80, 60), 0, 0, Color.Teal, 8, ShapeKind.Rectangle);
        third.Scene.AddObject(0, PointF.Empty, new SizeF(80, 60), 0, 0, Color.Coral, 8, ShapeKind.Rectangle);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(first.Id, second.Id, PointF.Empty, out var firstToSecond)
            && firstToSecond is not null
            && project.TryAddDrawingObjectInstance(second.Id, third.Id, PointF.Empty, out var secondToThird)
            && secondToThird is not null,
            "Valid drawing-object containment was rejected.");
        AssertTimeline(
            project.TryAddDrawingObjectInstance(first.Id, third.Id, PointF.Empty, out var firstToThird)
            && firstToThird is not null
            && firstToSecond!.SceneLayerId == first.Scene.LayerIds[0]
            && firstToThird.SceneLayerId == first.Scene.LayerIds[0]
            && first.InstancesInLayer(first.Scene.LayerIds[0]).Count == 2,
            "A drawing layer did not retain multiple nested drawing-object instances.");
        var initialNestedStackScene = new VectorScene();
        var initialNestedStack = SceneCompositionBuilder.BuildDrawingObjectChildren(
            initialNestedStackScene,
            first,
            project.DrawingObjects,
            0);
        var initialNestedRootIds = initialNestedStack.ObjectOwners.Values.Select(owner =>
                string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        static string FrontRootInstanceId(VectorScene compositionScene, SceneCompositionResult composition)
        {
            var hit = compositionScene.HitTestElement(PointF.Empty, 0, 0);
            if (!hit.IsValid || !composition.TryGetOwner(hit.Key.ObjectIndex, out var owner)) return "";
            return string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
        }
        AssertTimeline(
            initialNestedRootIds.SequenceEqual([firstToSecond!.Id, firstToThird!.Id])
            && FrontRootInstanceId(initialNestedStackScene, initialNestedStack) == firstToSecond.Id,
            $"Nested drawing-object composition did not expose its initial same-layer stack order: {string.Join(',', initialNestedRootIds)}.");
        AssertTimeline(
            !project.CanMoveDrawingObjectInstancesInLayer(first.Id, [firstToSecond.Id], 1)
            && !project.TryMoveDrawingObjectInstancesInLayer(first.Id, [firstToSecond.Id], 1)
            && !project.CanMoveDrawingObjectInstancesInLayer(first.Id, [firstToThird.Id], -1)
            && !project.TryMoveDrawingObjectInstancesInLayer(first.Id, [firstToThird.Id], -1),
            "Nested drawing-object stack boundaries accepted movement beyond the visual front or back.");
        AssertTimeline(
            project.CanMoveDrawingObjectInstancesInLayer(first.Id, [firstToThird.Id], 1)
            && project.TryMoveDrawingObjectInstancesInLayer(first.Id, [firstToThird.Id], 1),
            "Nested drawing-object stacking rejected a valid forward move.");
        var movedNestedStackScene = new VectorScene();
        var movedNestedStack = SceneCompositionBuilder.BuildDrawingObjectChildren(
            movedNestedStackScene,
            first,
            project.DrawingObjects,
            0);
        var movedNestedRootIds = movedNestedStack.ObjectOwners.Values.Select(owner =>
                string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        AssertTimeline(
            movedNestedRootIds.SequenceEqual([firstToThird.Id, firstToSecond.Id])
            && FrontRootInstanceId(movedNestedStackScene, movedNestedStack) == firstToThird.Id
            && project.CanMoveDrawingObjectInstancesInLayer(first.Id, [firstToThird.Id], -1)
            && project.TryMoveDrawingObjectInstancesInLayer(first.Id, [firstToThird.Id], -1),
            $"Nested drawing-object stacking did not change the flattened composition order: {string.Join(',', movedNestedRootIds)}.");
        var restoredNestedStackScene = new VectorScene();
        var restoredNestedStack = SceneCompositionBuilder.BuildDrawingObjectChildren(
            restoredNestedStackScene,
            first,
            project.DrawingObjects,
            0);
        AssertTimeline(
            first.InstancesInLayer(first.Scene.LayerIds[0]).SequenceEqual([firstToSecond, firstToThird])
            && FrontRootInstanceId(restoredNestedStackScene, restoredNestedStack) == firstToSecond.Id,
            "Sending a nested drawing object backward did not restore its visual stack order.");
        var nestedLayer = first.Scene.AddLayer("Nested Layer");
        AssertTimeline(
            project.TryAddDrawingObjectInstance(
                first.Id,
                third.Id,
                new PointF(240, 0),
                first.Scene.LayerIds[nestedLayer],
                out var layeredNested)
            && layeredNested is not null
            && layeredNested.SceneLayerId == first.Scene.LayerIds[nestedLayer]
            && first.InstancesInLayer(first.Scene.LayerIds[nestedLayer]).Count == 1,
            "A nested drawing-object instance did not retain its selected host layer.");
        AssertTimeline(
            project.CanMoveDrawingObjectInstancesInLayer(first.Id, [firstToSecond!.Id], -1)
            && project.TryMoveDrawingObjectInstancesInLayer(first.Id, [firstToSecond.Id], -1)
            && first.InstancesInLayer(first.Scene.LayerIds[0]).SequenceEqual([firstToThird!, firstToSecond])
            && first.InstancesInLayer(first.Scene.LayerIds[nestedLayer]).SequenceEqual([layeredNested!])
            && project.TryMoveDrawingObjectInstancesInLayer(first.Id, [firstToSecond.Id], 1)
            && first.InstancesInLayer(first.Scene.LayerIds[0]).SequenceEqual([firstToSecond, firstToThird!]),
            "Nested drawing-object stacking did not move one level within its host layer.");
        AssertTimeline(
            !project.CanContainDrawingObject(first.Id, first.Id)
            && !project.TryAddDrawingObjectInstance(first.Id, first.Id, PointF.Empty, out _),
            "A drawing object was allowed to contain itself.");
        AssertTimeline(
            !project.CanContainDrawingObject(third.Id, first.Id)
            && !project.TryAddDrawingObjectInstance(third.Id, first.Id, PointF.Empty, out _),
            "An indirect drawing-object containment cycle was allowed.");

        var validInstanceCount = first.Instances.Count;
        var rejectedDirectMutation = false;
        try
        {
            first.AddInstance(project, new DrawingObjectInstanceDefinition { DrawingObjectId = first.Id });
        }
        catch (InvalidOperationException)
        {
            rejectedDirectMutation = true;
        }

        var rejectedLayerIdCollision = false;
        var rejectedInstanceIdCollision = false;
        try
        {
            first.AddInstance(project, new DrawingObjectInstanceDefinition
            {
                Id = first.Scene.LayerIds[0],
                DrawingObjectId = third.Id
            });
        }
        catch (InvalidOperationException)
        {
            rejectedLayerIdCollision = true;
        }

        try
        {
            first.AddInstance(project, new DrawingObjectInstanceDefinition
            {
                Id = firstToSecond!.Id,
                DrawingObjectId = third.Id
            });
        }
        catch (InvalidOperationException)
        {
            rejectedInstanceIdCollision = true;
        }

        AssertTimeline(
            rejectedDirectMutation
            && rejectedLayerIdCollision
            && rejectedInstanceIdCollision
            && first.Instances.Count == validInstanceCount
            && first.Instances.All(instance => !string.Equals(instance.DrawingObjectId, first.Id, StringComparison.Ordinal))
            && first.Instances is ICollection<DrawingObjectInstanceDefinition> { IsReadOnly: true },
            "The drawing-object model exposed a mutation path that bypassed containment validation.");

        var firstTrack = first.Timeline.FindTrackByTargetId(first.Scene.LayerIds[0]);
        var nestedLayerTrack = first.Timeline.FindTrackByTargetId(first.Scene.LayerIds[nestedLayer]);
        AssertTimeline(
            firstTrack is not null
            && firstTrack.Keyframes.SequenceEqual(new[] { new TimelineKeyframe(0, TimelineKeyframeKind.Populated) })
            && nestedLayerTrack is not null
            && first.Timeline.FindTrackByTargetId(firstToSecond!.Id) is null
            && first.Timeline.FindTrackByTargetId(firstToThird!.Id) is null
            && ReferenceEquals(first.Timeline, first.Scene.Timeline)
            && first.TimelineTargetIds.SequenceEqual(first.Scene.LayerIds),
            "A drawing object exposed nested instances as generated timeline layers.");
        AssertTimeline(
            first.Scene.InsertTimelineBlankKeyframe(0, 3)
            && first.Scene.InsertTimelineBlankKeyframe(nestedLayer, 4),
            "The drawing-object timeline rejected a host-layer key edit.");
        first.SynchronizeTimelineTracks();
        AssertTimeline(
            first.Timeline.FindTrackByTargetId(first.Scene.LayerIds[0])?.EvaluateExposure(3).HasContent == false
            && first.Timeline.FindTrackByTargetId(first.Scene.LayerIds[nestedLayer])?.EvaluateExposure(3).HasContent == true
            && first.Timeline.FindTrackByTargetId(first.Scene.LayerIds[nestedLayer])?.EvaluateExposure(4).HasContent == false,
            "Nested-instance exposure did not follow its host drawing layer.");

        var nestedKeyframeProject = VectorProject.CreateEmpty();
        var nestedKeyframeRoot = nestedKeyframeProject.DrawingObjects[0];
        var nestedKeyframeChild = nestedKeyframeProject.AddDrawingObject("Nested keyframe child");
        nestedKeyframeChild.Scene.AddObject(
            0,
            new PointF(24, 18),
            new SizeF(80, 48),
            0,
            0,
            Color.CadetBlue,
            8,
            ShapeKind.Rectangle);
        AssertTimeline(
            nestedKeyframeProject.TryAddDrawingObjectInstance(
                nestedKeyframeRoot.Id,
                nestedKeyframeChild.Id,
                PointF.Empty,
                out var nestedKeyframeInstance)
            && nestedKeyframeInstance is not null,
            "Nested-keyframe regression could not create its child instance.");
        var nestedKeyframeTrack = nestedKeyframeRoot.Timeline.FindTrackByTargetId(
            nestedKeyframeRoot.Scene.LayerIds[0])
            ?? throw new InvalidOperationException("Nested-keyframe regression lost its host-layer track.");
        AssertTimeline(
            nestedKeyframeRoot.Scene.InsertTimelineKeyframe(0, 5)
            && nestedKeyframeTrack.EvaluateExposure(5) is
            {
                IsKeyframe: true,
                HasContent: true,
                SourceKind: TimelineKeyframeKind.Populated
            }
            && nestedKeyframeRoot.Instances.Contains(nestedKeyframeInstance!),
            "F6 converted a nested-instance-only drawing layer into a blank keyframe.");
        AssertTimeline(
            nestedKeyframeInstance!.SetPositionAtFrame(5, new PointF(120, 60))
            && PointsNear(nestedKeyframeInstance.EvaluatePosition(4), PointF.Empty)
            && PointsNear(nestedKeyframeInstance.EvaluatePosition(5), new PointF(120, 60)),
            "A nested instance did not retain a held position key on its host timeline.");
        nestedKeyframeInstance.InsertPositionFrames(4, 2);
        AssertTimeline(
            PointsNear(nestedKeyframeInstance.EvaluatePosition(6), PointF.Empty)
            && PointsNear(nestedKeyframeInstance.EvaluatePosition(7), new PointF(120, 60)),
            "Inserting timeline frames did not shift a nested instance position key.");
        nestedKeyframeInstance.RemovePositionFrames(4, 2);
        AssertTimeline(
            nestedKeyframeInstance.PositionKeyframes.Select(keyframe => keyframe.Frame).SequenceEqual([5])
            && PointsNear(nestedKeyframeInstance.EvaluatePosition(5), new PointF(120, 60)),
            "Removing timeline frames did not restore the nested instance position-key timing.");
        var leadingFrameRemovalInstance = new DrawingObjectInstanceDefinition();
        leadingFrameRemovalInstance.SetPositionAtFrame(2, new PointF(48, 24));
        leadingFrameRemovalInstance.RemovePositionFrames(0, 2);
        AssertTimeline(
            leadingFrameRemovalInstance.PositionKeyframes.Count == 0
            && PointsNear(leadingFrameRemovalInstance.EvaluatePosition(0), new PointF(48, 24)),
            "Removing leading frames did not fold a shifted position key into the base instance position.");
        var stateFrameEditInstance = new DrawingObjectInstanceDefinition();
        var shiftedState = stateFrameEditInstance.EvaluateState(3) with
        {
            RotationZ = 42,
            SkewX = 9,
            ScaleX = 1.8f,
            PlaybackFps = 20,
            PlaybackMode = DrawingObjectPlaybackMode.HoldFrame,
            HoldFrame = 2
        };
        stateFrameEditInstance.SetStateAtFrame(3, shiftedState);
        stateFrameEditInstance.InsertStateFrames(1, 2);
        AssertTimeline(
            stateFrameEditInstance.StateKeyframes.Select(keyframe => keyframe.Frame).SequenceEqual([5])
            && stateFrameEditInstance.EvaluateState(4).RotationZ == 0
            && stateFrameEditInstance.EvaluateState(5) == shiftedState,
            "Inserting frames did not move the complete drawing-object instance state.");
        stateFrameEditInstance.RemoveStateFrames(1, 2);
        AssertTimeline(
            stateFrameEditInstance.StateKeyframes.Select(keyframe => keyframe.Frame).SequenceEqual([3])
            && stateFrameEditInstance.EvaluateState(3) == shiftedState
            && stateFrameEditInstance.SetStateAtFrame(6, stateFrameEditInstance.EvaluateState(6))
            && stateFrameEditInstance.SetStateAtFrame(3, shiftedState with { RotationZ = 18 })
            && stateFrameEditInstance.EvaluateState(6) == shiftedState
            && stateFrameEditInstance.RemoveStateKeyframe(3)
            && stateFrameEditInstance.EvaluateState(3).RotationZ == 0
            && stateFrameEditInstance.EvaluateState(6) == shiftedState,
            "Removing frames or clearing a keyframe left partial drawing-object instance state behind.");
        var disabledNestedOnionSkinPreview = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectOnionSkin(
            disabledNestedOnionSkinPreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            4);
        AssertTimeline(
            !nestedKeyframeRoot.Scene.HasOnionSkinPreviewEnabled
            && disabledNestedOnionSkinPreview.ObjectCount == 0,
            "A disabled nested-instance onion skin still built a preview scene.");
        AssertTimeline(
            nestedKeyframeRoot.Scene.ToggleOnionSkin()
            && nestedKeyframeRoot.Scene.SetOnionSkinRange(1, 1),
            "Nested-instance onion-skin regression could not enable the drawing timeline.");
        var nestedOnionSkinPreview = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectOnionSkin(
            nestedOnionSkinPreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            4);
        AssertTimeline(
            nestedKeyframeRoot.Scene.HasOnionSkinPreviewEnabled
            && nestedOnionSkinPreview.ObjectCount == 1
            && NearlyEqual(nestedOnionSkinPreview.X[0], 144)
            && NearlyEqual(nestedOnionSkinPreview.Y[0], 78)
            && Color.FromArgb(nestedOnionSkinPreview.Argb[0]).A is > 0 and < 255,
            "Drawing-object onion skin did not show the nested instance at its neighboring keyed position.");
        AssertTimeline(
            nestedKeyframeChild.Scene.SetLayerLocked(0, true),
            "Nested-instance onion-skin regression could not lock the child layer.");
        var lockedChildOnionSkinPreview = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectOnionSkin(
            lockedChildOnionSkinPreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            4);
        AssertTimeline(
            lockedChildOnionSkinPreview.ObjectCount == 0
            && nestedKeyframeChild.Scene.SetLayerLocked(0, false)
            && nestedKeyframeRoot.Scene.SetLayerLocked(0, true),
            "A locked layer inside a nested drawing object still produced onion-skin content.");
        var lockedHostOnionSkinPreview = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectOnionSkin(
            lockedHostOnionSkinPreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            4);
        AssertTimeline(
            nestedKeyframeRoot.Scene.OnionSkinEnabled
            && lockedHostOnionSkinPreview.ObjectCount == 0
            && nestedKeyframeRoot.Scene.SetLayerLocked(0, false),
            "A locked nested-instance host layer still produced onion-skin content or disabled the global toggle.");
        var nestedKeyframePreview = new VectorScene();
        var nestedKeyframeComposition = SceneCompositionBuilder.BuildDrawingObjectChildren(
            nestedKeyframePreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            5);
        AssertTimeline(
            nestedKeyframePreview.ObjectCount == 1
            && NearlyEqual(nestedKeyframePreview.X[0], 144)
            && NearlyEqual(nestedKeyframePreview.Y[0], 78)
            && nestedKeyframeComposition.ObjectOwners.Values.Any(owner =>
                owner.RootInstanceId == nestedKeyframeInstance!.Id),
            "Creating a keyframe removed the nested object or lost its keyed position in composition.");
        var heldInstanceState = nestedKeyframeInstance!.EvaluateState(5);
        var keyedInstanceState = heldInstanceState with
        {
            Z = 7,
            RotationX = 11,
            RotationY = 13,
            RotationZ = 90,
            SkewX = 7,
            SkewY = 4,
            ScaleX = 2,
            ScaleY = 1.5f,
            ScaleZ = 3,
            PlaybackFps = 12,
            PlaybackMode = DrawingObjectPlaybackMode.Loop,
            HoldFrame = 3
        };
        AssertTimeline(
            nestedKeyframeRoot.Scene.InsertTimelineKeyframe(0, 6)
            && nestedKeyframeInstance.SetStateAtFrame(6, keyedInstanceState)
            && nestedKeyframeInstance.EvaluateState(5) == heldInstanceState
            && nestedKeyframeInstance.EvaluateState(6) == keyedInstanceState,
            "Changing a nested instance transform shared rotation or other state with the previous keyframe.");
        SceneCompositionBuilder.BuildDrawingObjectChildren(
            nestedKeyframePreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            6);
        var expectedKeyedCenter = System.Numerics.Vector2.Transform(
            new System.Numerics.Vector2(24, 18),
            System.Numerics.Matrix3x2.CreateScale(keyedInstanceState.ScaleX, keyedInstanceState.ScaleY)
            * System.Numerics.Matrix3x2.CreateSkew(
                keyedInstanceState.SkewX * MathF.PI / 180f,
                keyedInstanceState.SkewY * MathF.PI / 180f)
            * System.Numerics.Matrix3x2.CreateRotation(keyedInstanceState.RotationZ * MathF.PI / 180f)
            * System.Numerics.Matrix3x2.CreateTranslation(keyedInstanceState.X, keyedInstanceState.Y));
        var keyedBounds = nestedKeyframePreview.GetObjectWorldBounds(0);
        AssertTimeline(
            nestedKeyframePreview.ObjectCount == 1
            && Math.Abs(keyedBounds.Left + keyedBounds.Width * 0.5f - expectedKeyedCenter.X) <= 0.51f
            && Math.Abs(keyedBounds.Top + keyedBounds.Height * 0.5f - expectedKeyedCenter.Y) <= 0.51f,
            $"Nested-instance composition ignored the transform state stored at its current keyframe: " +
            $"expected={expectedKeyedCenter.X:0.###},{expectedKeyedCenter.Y:0.###}, " +
            $"actual={keyedBounds.Left + keyedBounds.Width * 0.5f:0.###},{keyedBounds.Top + keyedBounds.Height * 0.5f:0.###}, " +
            $"shape={nestedKeyframePreview.ShapeKind[0]}.");
        AssertTimeline(
            nestedKeyframeProject.TryDuplicateDrawingObject(nestedKeyframeRoot.Id, out var nestedKeyframeDuplicate)
            && nestedKeyframeDuplicate is not null
            && PointsNear(nestedKeyframeDuplicate.Instances.Single().EvaluatePosition(5), new PointF(120, 60))
            && nestedKeyframeDuplicate.Instances.Single().EvaluateState(6) == keyedInstanceState,
            "Duplicating a drawing object discarded its nested instance keyframe state.");
        AssertTimeline(
            nestedKeyframeRoot.Scene.InsertTimelineBlankKeyframe(0, 8)
            && nestedKeyframeTrack.EvaluateExposure(8).HasContent == false,
            "An explicit blank keyframe was repopulated by nested layer content.");
        SceneCompositionBuilder.BuildDrawingObjectChildren(
            nestedKeyframePreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            8);
        AssertTimeline(
            nestedKeyframePreview.ObjectCount == 0,
            "An explicit blank keyframe did not hide its nested drawing objects.");
        var nestedKeyframeProjectChanges = 0;
        nestedKeyframeProject.Changed += (_, _) => nestedKeyframeProjectChanges++;
        AssertTimeline(
            nestedKeyframeProject.TryRemoveDrawingObjectInstance(
                nestedKeyframeRoot.Id,
                nestedKeyframeInstance!.Id,
                out var removedNestedKeyframeInstance)
            && ReferenceEquals(removedNestedKeyframeInstance, nestedKeyframeInstance)
            && nestedKeyframeRoot.Instances.Count == 0
            && nestedKeyframeTrack.EvaluateExposure(0).HasContent == false
            && nestedKeyframeTrack.EvaluateExposure(5).HasContent == false
            && !nestedKeyframeProject.TryRemoveDrawingObjectInstance(
                nestedKeyframeRoot.Id,
                nestedKeyframeInstance.Id,
                out _)
            && nestedKeyframeProjectChanges == 1,
            "Deleting a nested instance did not update its owner and host-layer exposure exactly once.");
        SceneCompositionBuilder.BuildDrawingObjectChildren(
            nestedKeyframePreview,
            nestedKeyframeRoot,
            nestedKeyframeProject.DrawingObjects,
            5);
        AssertTimeline(
            nestedKeyframePreview.ObjectCount == 0,
            "A deleted nested instance remained in drawing-object composition.");

        first.Scene.Generate(2, 16, 128);
        first.SynchronizeTimelineTracks();
        AssertTimeline(
            first.Instances.Count == validInstanceCount
            && first.Timeline.Tracks.Select(track => track.TargetId).SequenceEqual(first.Scene.LayerIds)
            && first.Instances.All(instance => first.Scene.LayerIds.Contains(instance.SceneLayerId, StringComparer.Ordinal)),
            "Regenerating drawing geometry did not migrate nested instances onto valid drawing layers.");

        RunSceneInstanceStackRegression();
    }

    private static void RunSceneInstanceStackRegression()
    {
        var project = VectorProject.CreateEmpty();
        var scene = project.Scenes[0];
        scene.Dimension = SceneDimension.TwoD;
        var frontSource = project.DrawingObjects[0];
        frontSource.Name = "Scene stack front";
        var middleSource = project.AddDrawingObject("Scene stack middle");
        var backSource = project.AddDrawingObject("Scene stack back");
        var tailSource = project.AddDrawingObject("Scene stack tail");

        static void ConfigurePureFill(DrawingObjectDefinition drawingObject, Color fill)
        {
            drawingObject.Scene.CreateEmpty();
            drawingObject.Scene.AddObject(
                0,
                PointF.Empty,
                new SizeF(240, 160),
                0,
                0,
                fill,
                Color.Transparent,
                8,
                ShapeKind.Rectangle);
        }

        ConfigurePureFill(frontSource, Color.Teal);
        ConfigurePureFill(middleSource, Color.Coral);
        ConfigurePureFill(backSource, Color.Gold);
        ConfigurePureFill(tailSource, Color.MediumPurple);

        var stackLayerId = scene.Layers[0].Id;
        SceneLayerDefinition? isolatedLayer = null;
        DrawingObjectInstanceDefinition? front = null;
        DrawingObjectInstanceDefinition? middle = null;
        DrawingObjectInstanceDefinition? back = null;
        DrawingObjectInstanceDefinition? tail = null;
        DrawingObjectInstanceDefinition? isolatedFirst = null;
        DrawingObjectInstanceDefinition? isolatedSecond = null;
        AssertTimeline(
            project.TryAddSceneLayer(scene.Id, out isolatedLayer)
            && isolatedLayer is not null
            && project.TryAddSceneInstance(
                scene.Id,
                frontSource.Id,
                PointF.Empty,
                0,
                stackLayerId,
                out front)
            && front is not null
            && project.TryAddSceneInstance(
                scene.Id,
                middleSource.Id,
                PointF.Empty,
                0,
                stackLayerId,
                out middle)
            && middle is not null
            && project.TryAddSceneInstance(
                scene.Id,
                backSource.Id,
                PointF.Empty,
                0,
                stackLayerId,
                out back)
            && back is not null
            && project.TryAddSceneInstance(
                scene.Id,
                tailSource.Id,
                PointF.Empty,
                0,
                stackLayerId,
                out tail)
            && tail is not null
            && project.TryAddSceneInstance(
                scene.Id,
                frontSource.Id,
                new PointF(600, 0),
                0,
                isolatedLayer.Id,
                out isolatedFirst)
            && isolatedFirst is not null
            && project.TryAddSceneInstance(
                scene.Id,
                middleSource.Id,
                new PointF(800, 0),
                0,
                isolatedLayer.Id,
                out isolatedSecond)
            && isolatedSecond is not null,
            "Scene-instance stack regression could not create overlapping pure-fill instances and an isolated layer.");

        var frontId = front!.Id;
        var middleId = middle!.Id;
        var backId = back!.Id;
        var tailId = tail!.Id;
        var isolatedLayerId = isolatedLayer!.Id;
        var initialOrder = new[] { frontId, middleId, backId, tailId };
        var isolatedOrder = new[] { isolatedFirst!.Id, isolatedSecond!.Id };
        var initialStackView = scene.InstancesInLayer(stackLayerId);
        var initialIsolatedView = scene.InstancesInLayer(isolatedLayerId);

        string[] CurrentStackOrder() => scene.InstancesInLayer(stackLayerId)
            .Select(instance => instance.Id)
            .ToArray();
        bool IsolatedLayerUnchanged() => scene.InstancesInLayer(isolatedLayerId)
            .Select(instance => instance.Id)
            .SequenceEqual(isolatedOrder);
        string FrontFillOwnerId()
        {
            var compositionScene = new VectorScene();
            var composition = SceneCompositionBuilder.Build(
                compositionScene,
                scene,
                project.DrawingObjects,
                0);
            var hit = compositionScene.HitTestElement(PointF.Empty, 0, 0);
            if (!hit.IsValid
                || hit.Key.Kind != DrawingElementKind.Fill
                || !composition.TryGetOwner(hit.Key.ObjectIndex, out var owner))
            {
                return "";
            }

            return string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
        }

        var projectChanges = 0;
        project.Changed += (_, _) => projectChanges++;
        AssertTimeline(
            CurrentStackOrder().SequenceEqual(initialOrder)
            && initialIsolatedView.Select(instance => instance.Id).SequenceEqual(isolatedOrder)
            && FrontFillOwnerId() == frontId,
            "Overlapping pure-fill scene instances did not begin with the first same-layer instance at the actual hit-test front.");
        AssertTimeline(
            !project.CanMoveSceneInstancesInLayer(scene.Id, [frontId], 1)
            && !project.TryMoveSceneInstancesInLayer(scene.Id, [frontId], 1)
            && !project.CanMoveSceneInstancesInLayer(scene.Id, [tailId], -1)
            && !project.TryMoveSceneInstancesInLayer(scene.Id, [tailId], -1)
            && ReferenceEquals(initialStackView, scene.InstancesInLayer(stackLayerId))
            && CurrentStackOrder().SequenceEqual(initialOrder)
            && IsolatedLayerUnchanged()
            && projectChanges == 0,
            "Scene-instance stack boundaries consumed a move, invalidated the cached view, crossed layers, or raised Project.Changed.");

        AssertTimeline(
            project.CanMoveSceneInstancesInLayer(scene.Id, [middleId], 1)
            && projectChanges == 0
            && project.TryMoveSceneInstancesInLayer(scene.Id, [middleId], 1)
            && projectChanges == 1,
            "A valid +1 scene-instance stack move was rejected or raised an invalid Project.Changed count.");
        var forwardView = scene.InstancesInLayer(stackLayerId);
        AssertTimeline(
            !ReferenceEquals(initialStackView, forwardView)
            && initialStackView.Select(instance => instance.Id).SequenceEqual(initialOrder)
            && CurrentStackOrder().SequenceEqual([middleId, frontId, backId, tailId])
            && FrontFillOwnerId() == middleId
            && IsolatedLayerUnchanged(),
            "Moving a scene instance +1 did not invalidate the cached view or move the actual front owner by exactly one level.");

        AssertTimeline(
            project.CanMoveSceneInstancesInLayer(scene.Id, [middleId], -1)
            && project.TryMoveSceneInstancesInLayer(scene.Id, [middleId], -1)
            && projectChanges == 2,
            "A valid -1 scene-instance stack move was rejected or raised an invalid Project.Changed count.");
        var restoredView = scene.InstancesInLayer(stackLayerId);
        AssertTimeline(
            !ReferenceEquals(forwardView, restoredView)
            && CurrentStackOrder().SequenceEqual(initialOrder)
            && FrontFillOwnerId() == frontId
            && IsolatedLayerUnchanged(),
            "Moving a scene instance -1 did not restore the same-layer order and actual front owner.");

        AssertTimeline(
            project.CanMoveSceneInstancesInLayer(scene.Id, [tailId, backId], 1)
            && project.TryMoveSceneInstancesInLayer(scene.Id, [tailId, backId], 1)
            && projectChanges == 3,
            "A reversed-order multi-selection could not move forward in the scene-instance stack.");
        var multiForwardView = scene.InstancesInLayer(stackLayerId);
        AssertTimeline(
            !ReferenceEquals(restoredView, multiForwardView)
            && CurrentStackOrder().SequenceEqual([frontId, backId, tailId, middleId])
            && FrontFillOwnerId() == frontId
            && IsolatedLayerUnchanged(),
            "A scene-instance multi-selection did not retain stable relative order while moving forward one level.");
        AssertTimeline(
            project.CanMoveSceneInstancesInLayer(scene.Id, [tailId, backId], -1)
            && project.TryMoveSceneInstancesInLayer(scene.Id, [tailId, backId], -1)
            && projectChanges == 4,
            "A reversed-order multi-selection could not move backward in the scene-instance stack.");
        AssertTimeline(
            !ReferenceEquals(multiForwardView, scene.InstancesInLayer(stackLayerId))
            && CurrentStackOrder().SequenceEqual(initialOrder)
            && FrontFillOwnerId() == frontId
            && IsolatedLayerUnchanged()
            && projectChanges == 4,
            "Sending a stable scene-instance multi-selection backward did not restore order or preserve layer isolation.");

        Console.WriteLine("scene_instance_stack_model=ok");
    }

    private static void RunProjectAssetFolderRegression()
    {
        var project = new VectorProject();
        var rootObject = project.DrawingObjects[0];
        rootObject.Name = "Root Asset";
        rootObject.Scene.AddObject(0, PointF.Empty, new SizeF(80, 60), 0, 0, Color.Coral, 6, ShapeKind.Rectangle);
        var childObject = project.AddDrawingObject("Child Asset");
        childObject.Scene.AddObject(0, PointF.Empty, new SizeF(40, 30), 0, 0, Color.Teal, 4, ShapeKind.Ellipse);
        ProjectAssetFolder? rootFolder = null;
        ProjectAssetFolder? childFolder = null;
        AssertTimeline(
            project.TryAddAssetFolder("Characters", "", out rootFolder)
            && rootFolder is not null
            && project.TryAddAssetFolder("Heads", rootFolder.Id, out childFolder)
            && childFolder is not null
            && project.TryMoveDrawingObjectToAssetFolder(rootObject.Id, rootFolder.Id)
            && project.TryMoveDrawingObjectToAssetFolder(childObject.Id, childFolder.Id)
            && project.TryAddDrawingObjectInstance(rootObject.Id, childObject.Id, PointF.Empty, out var nested)
            && nested is not null,
            "The project asset folder model could not create a nested source tree.");

        var changeCount = 0;
        project.Changed += (_, _) => changeCount++;
        AssertTimeline(
            project.TryDuplicateAssetFolder(rootFolder!.Id, out var duplicateRoot)
            && duplicateRoot is not null
            && changeCount == 1,
            "Duplicating a project asset folder did not complete as one project mutation.");
        var duplicateChild = project.AssetFolders.SingleOrDefault(folder =>
            string.Equals(folder.ParentFolderId, duplicateRoot!.Id, StringComparison.Ordinal));
        var duplicateRootObject = project.DrawingObjects.SingleOrDefault(item =>
            string.Equals(item.AssetFolderId, duplicateRoot!.Id, StringComparison.Ordinal));
        var duplicateChildObject = duplicateChild is null
            ? null
            : project.DrawingObjects.SingleOrDefault(item =>
                string.Equals(item.AssetFolderId, duplicateChild.Id, StringComparison.Ordinal));
        AssertTimeline(
            duplicateRoot!.Name == "Characters Copy"
            && duplicateChild is { Name: "Heads" }
            && duplicateRootObject is not null
            && duplicateChildObject is not null
            && duplicateRootObject.Id != rootObject.Id
            && duplicateChildObject.Id != childObject.Id
            && duplicateRootObject.Scene.ObjectCount == rootObject.Scene.ObjectCount
            && duplicateChildObject.Scene.ObjectCount == childObject.Scene.ObjectCount
            && duplicateRootObject.Instances.Count == 1
            && duplicateRootObject.Instances[0].DrawingObjectId == duplicateChildObject.Id
            && rootObject.Instances[0].DrawingObjectId == childObject.Id,
            "Folder duplication did not preserve its subtree or remap internal drawing-object references.");
        AssertTimeline(
            !project.TryMoveAssetFolder(rootFolder.Id, childFolder!.Id)
            && project.TryRenameAssetFolder(duplicateRoot.Id, "Characters Variant")
            && duplicateRoot.Name == "Characters Variant"
            && project.TryMoveAssetFolder(childFolder.Id, "")
            && string.IsNullOrEmpty(childFolder.ParentFolderId)
            && project.TryMoveDrawingObjectToAssetFolder(rootObject.Id, "")
            && string.IsNullOrEmpty(rootObject.AssetFolderId)
            && project.AssetFolders is not ICollection<ProjectAssetFolder> { IsReadOnly: false },
            "Project asset folder rename, root move, cycle rejection, or read-only ownership failed.");
    }

    private static void RunLayeredInstanceIndexRegression()
    {
        const int layerCount = 16;
        const int instancesPerLayer = 16;
        const int queryIterations = 256;
        const double lookupBudgetMilliseconds = 25;

        var project = VectorProject.CreateEmpty();
        var scene = project.Scenes[0];
        var container = project.DrawingObjects[0];
        var child = project.AddDrawingObject("Indexed child");
        for (var layer = scene.Layers.Count; layer < layerCount; layer++)
        {
            AssertTimeline(
                project.TryAddSceneLayer(scene.Id, out _),
                "Layered instance index regression could not add a scene layer.");
        }
        container.Scene.CreateEmpty(layerCount);
        container.SynchronizeTimelineTracks();

        for (var index = 0; index < layerCount * instancesPerLayer; index++)
        {
            var sceneLayerId = scene.Layers[index % layerCount].Id;
            var drawingLayerId = container.Scene.LayerIds[index % layerCount];
            AssertTimeline(
                project.TryAddSceneInstance(
                    scene.Id,
                    child.Id,
                    new PointF(index, -index),
                    index,
                    sceneLayerId,
                    out _)
                && project.TryAddDrawingObjectInstance(
                    container.Id,
                    child.Id,
                    new PointF(-index, index),
                    drawingLayerId,
                    out _),
                "Layered instance index regression could not populate its scene and drawing layers.");
        }

        var sceneLayerIds = scene.Layers.Select(layer => layer.Id).ToArray();
        var drawingLayerIds = container.Scene.LayerIds.ToArray();
        foreach (var layerId in sceneLayerIds) _ = scene.InstancesInLayer(layerId).Count;
        foreach (var layerId in drawingLayerIds) _ = container.InstancesInLayer(layerId).Count;
        var cachedSceneLayer = scene.InstancesInLayer(sceneLayerIds[0]);
        var cachedDrawingLayer = container.InstancesInLayer(drawingLayerIds[0]);

        var queryWatch = new Stopwatch();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        queryWatch.Start();
        var queriedInstances = 0;
        for (var iteration = 0; iteration < queryIterations; iteration++)
        {
            foreach (var layerId in sceneLayerIds) queriedInstances += scene.InstancesInLayer(layerId).Count;
            foreach (var layerId in drawingLayerIds) queriedInstances += container.InstancesInLayer(layerId).Count;
        }
        queryWatch.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var lookupBudgetMet = queryWatch.Elapsed.TotalMilliseconds <= lookupBudgetMilliseconds
            && allocatedBytes <= 4096;
        AssertTimeline(
            queriedInstances == queryIterations * layerCount * instancesPerLayer * 2
            && ReferenceEquals(cachedSceneLayer, scene.InstancesInLayer(sceneLayerIds[0]))
            && ReferenceEquals(cachedDrawingLayer, container.InstancesInLayer(drawingLayerIds[0]))
            && lookupBudgetMet,
            "Warm layered instance lookup changed membership, rebuilt cached views, or allocated per query: "
            + $"elapsed={queryWatch.Elapsed.TotalMilliseconds:0.000} ms, allocated={allocatedBytes} bytes.");

        var sceneSnapshot = scene.CreateInstanceSnapshot();
        var removedSceneInstance = cachedSceneLayer[0];
        var sceneRemovalChanges = 0;
        project.Changed += (_, _) => sceneRemovalChanges++;
        AssertTimeline(
            project.TryRemoveSceneInstance(scene.Id, removedSceneInstance.Id, out var removedSceneResult)
            && ReferenceEquals(removedSceneResult, removedSceneInstance)
            && scene.InstancesInLayer(sceneLayerIds[0]).Count == instancesPerLayer - 1
            && cachedSceneLayer.Count == instancesPerLayer
            && sceneRemovalChanges == 1
            && !project.TryRemoveSceneInstance(scene.Id, removedSceneInstance.Id, out _)
            && sceneRemovalChanges == 1,
            "Project scene-instance removal did not return the removed instance, invalidate the layered index, "
            + "preserve its prior read-only view, or publish exactly one successful change.");
        scene.RestoreInstanceSnapshot(sceneSnapshot);
        AssertTimeline(
            scene.InstancesInLayer(sceneLayerIds[0]).Count == instancesPerLayer,
            "Restoring scene instances did not rebuild the layered index.");

        var drawingSnapshot = container.CreateInstanceSnapshot();
        var firstDrawingId = cachedDrawingLayer[0].Id;
        var secondDrawingId = cachedDrawingLayer[1].Id;
        var thirdDrawingId = cachedDrawingLayer[2].Id;
        var lastDrawingId = cachedDrawingLayer[^1].Id;
        var isolatedDrawingLayerIds = container.InstancesInLayer(drawingLayerIds[1])
            .Select(instance => instance.Id)
            .ToArray();
        AssertTimeline(
            !project.CanMoveDrawingObjectInstancesInLayer(container.Id, [firstDrawingId], 1)
            && !project.CanMoveDrawingObjectInstancesInLayer(container.Id, [lastDrawingId], -1)
            && project.TryMoveDrawingObjectInstancesInLayer(
                container.Id,
                [secondDrawingId, thirdDrawingId],
                1),
            "Layered instance index regression did not enforce stack boundaries or move a stable selection forward.");
        var reorderedDrawingLayer = container.InstancesInLayer(drawingLayerIds[0]);
        AssertTimeline(
            !ReferenceEquals(cachedDrawingLayer, reorderedDrawingLayer)
            && reorderedDrawingLayer[0].Id == secondDrawingId
            && reorderedDrawingLayer[1].Id == thirdDrawingId
            && reorderedDrawingLayer[2].Id == firstDrawingId
            && cachedDrawingLayer[0].Id == firstDrawingId
            && container.InstancesInLayer(drawingLayerIds[1])
                .Select(instance => instance.Id)
                .SequenceEqual(isolatedDrawingLayerIds),
            "Reordering nested instances did not invalidate the layered index or preserve stack order.");
        AssertTimeline(
            project.TryMoveDrawingObjectInstancesInLayer(
                container.Id,
                [secondDrawingId, thirdDrawingId],
                -1),
            "Layered instance index regression could not send a stable selection backward.");
        var restoredDrawingLayer = container.InstancesInLayer(drawingLayerIds[0]);
        AssertTimeline(
            !ReferenceEquals(reorderedDrawingLayer, restoredDrawingLayer)
            && restoredDrawingLayer[0].Id == firstDrawingId
            && restoredDrawingLayer[1].Id == secondDrawingId
            && restoredDrawingLayer[2].Id == thirdDrawingId
            && container.InstancesInLayer(drawingLayerIds[1])
                .Select(instance => instance.Id)
                .SequenceEqual(isolatedDrawingLayerIds),
            "Sending nested instances backward did not preserve their relative order or host-layer isolation.");
        AssertTimeline(
            project.TryRemoveDrawingObjectInstance(container.Id, secondDrawingId, out _)
            && container.InstancesInLayer(drawingLayerIds[0]).Count == instancesPerLayer - 1,
            "Removing a nested instance did not invalidate the layered index.");
        container.RestoreInstanceSnapshot(drawingSnapshot);
        AssertTimeline(
            container.InstancesInLayer(drawingLayerIds[0]).Count == instancesPerLayer
            && container.InstancesInLayer(drawingLayerIds[0])[0].Id == firstDrawingId,
            "Restoring nested instances did not recover layered membership and order.");

        Console.WriteLine($"layered_instance_lookup_ms={queryWatch.Elapsed.TotalMilliseconds:0.000}");
        Console.WriteLine($"layered_instance_lookup_allocated_bytes={allocatedBytes}");
        Console.WriteLine($"layered_instance_lookup_budget_ms={lookupBudgetMilliseconds:0.000}");
        Console.WriteLine($"layered_instance_lookup_budget_met={lookupBudgetMet.ToString().ToLowerInvariant()}");
    }

    private static void RunSceneCompositionRegression()
    {
        RunFillEdgeBezierCompositionRegression();
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Name = "Composition source";
        drawingObject.Scene.CreateEmpty();
        var sourceGradientObject = drawingObject.Scene.AddObject(
            0,
            new PointF(100, 50),
            new SizeF(80, 40),
            0,
            0,
            Color.Teal,
            12,
            ShapeKind.Rectangle);
        drawingObject.Scene.SetGradientPaint(
            sourceGradientObject,
            GradientKind.ShapeRadial,
            [
                new GradientStop(0, Color.Teal),
                new GradientStop(0.5f, Color.Gold),
                new GradientStop(1, Color.Coral)
            ],
            new PointF(60, 50),
            new PointF(140, 50));
        var sourceShapeGradientMapping = new[]
        {
            new[]
            {
                new PointF(60, 30),
                new PointF(140, 30),
                new PointF(140, 70),
                new PointF(60, 70)
            }
        };
        drawingObject.Scene.SetShapeGradientMapping(sourceGradientObject, sourceShapeGradientMapping);

        var childDrawingObject = project.AddDrawingObject("Nested source");
        childDrawingObject.Scene.CreateEmpty();
        childDrawingObject.Scene.AddObject(
            0,
            new PointF(10, 0),
            new SizeF(20, 20),
            0,
            0,
            Color.Coral,
            6,
            ShapeKind.Ellipse);
        var nestedHostLayer = drawingObject.Scene.AddLayer("Nested instances");
        AssertTimeline(
            project.TryAddDrawingObjectInstance(
                drawingObject.Id,
                childDrawingObject.Id,
                new PointF(20, 0),
                drawingObject.Scene.LayerIds[nestedHostLayer],
                out var nestedInstance)
            && nestedInstance is not null,
            "Validated nested drawing-object insertion was rejected during composition setup.");

        var scene = project.Scenes[0];
        scene.Name = "Composition scene";
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, new PointF(200, 300), 0, out var instance)
            && instance is not null,
            "Validated scene-instance insertion was rejected during composition setup.");
        instance!.Name = "Transformed source";
        instance.RotationZ = 90;
        instance.ScaleX = 2;
        instance.ScaleY = 1;

        var destination = new VectorScene();
        var composition = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        var compositionHasShapeMapping = destination.TryGetShapeGradientMappingWorldContours(0, out var compositionShapeMapping);
        AssertTimeline(
            destination.ObjectCount == 2
            && destination.LayerNames[0].StartsWith(instance.Name, StringComparison.Ordinal)
            && NearlyEqual(destination.X[0], 150)
            && NearlyEqual(destination.Y[0], 500)
            && NearlyEqual(destination.Width[0], 160)
            && NearlyEqual(destination.Height[0], 40)
            && NearlyEqual(destination.Angle[0], MathF.PI / 2f)
            && destination.GetGradientKind(0) == GradientKind.ShapeRadial
            && destination.GradientStartArgb[0] == Color.Teal.ToArgb()
            && destination.GradientEndArgb[0] == Color.Coral.ToArgb()
            && destination.GetGradientStops(0).Length == 3
            && destination.GetGradientStops(0)[1].Argb == Color.Gold.ToArgb()
            && PointsNear(destination.GetGradientStart(0), new PointF(150, 420))
            && PointsNear(destination.GetGradientEnd(0), new PointF(150, 580))
            && compositionHasShapeMapping
            && compositionShapeMapping.Length == 1
            && PointsNear(compositionShapeMapping[0][0], new PointF(170, 420))
            && NearlyEqual(destination.X[1], 200)
            && NearlyEqual(destination.Y[1], 360)
            && destination.GeometryRevision <= 2
            && destination.TileCount.Sum() > 0
            && destination.OverviewCount.Sum() > 0,
            "Scene composition did not resolve and transform the drawing-object instance.");
        AssertTimeline(
            composition.TryGetOwner(0, out var rootOwner)
            && rootOwner.InstanceId == instance.Id
            && rootOwner.DrawingObjectId == drawingObject.Id
            && rootOwner.RootInstanceId == instance.Id
            && composition.TryGetOwner(1, out var nestedOwner)
            && nestedOwner.InstanceId == nestedInstance!.Id
            && nestedOwner.DrawingObjectId == childDrawingObject.Id
            && nestedOwner.RootInstanceId == instance.Id,
            "Scene composition did not preserve drawing-object provenance for editor navigation.");

        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 1);
        AssertTimeline(
            destination.ObjectCount == 2
            && SceneCompositionBuilder.LastActiveObjectBucketCacheHits >= 2
            && SceneCompositionBuilder.LastActiveObjectBucketCacheMisses == 0,
            "Scene composition did not reuse source object buckets when switching within the same held keyframe exposure.");

        var sourceGradientStops = drawingObject.Scene.GetGradientStops(sourceGradientObject);
        var rootTint = Color.FromArgb(255, 128, 255, 128).ToArgb();
        var nestedTint = Color.FromArgb(255, 255, 128, 255).ToArgb();
        instance.Alpha = 0.5f;
        instance.TintArgb = rootTint;
        nestedInstance!.Alpha = 0.5f;
        nestedInstance.TintArgb = nestedTint;
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        var tintedStops = destination.GetGradientStops(0);
        AssertTimeline(
            destination.ObjectCount == 2
            && destination.Argb[0] == ApplyInstanceAppearanceForRegression(Color.Teal.ToArgb(), 0.5f, rootTint)
            && destination.Argb[1] == ApplyInstanceAppearanceForRegression(
                Color.Coral.ToArgb(),
                0.25f,
                Color.FromArgb(255, 128, 128, 128).ToArgb())
            && tintedStops.Length == sourceGradientStops.Length
            && tintedStops.Select(stop => stop.Argb).SequenceEqual(sourceGradientStops.Select(stop =>
                ApplyInstanceAppearanceForRegression(stop.Argb, 0.5f, rootTint)))
            && drawingObject.Scene.Argb[sourceGradientObject] == Color.Teal.ToArgb()
            && drawingObject.Scene.GetGradientStops(sourceGradientObject).SequenceEqual(sourceGradientStops),
            "Nested instance Alpha or multiply tint did not compose independently across fills and gradient stops.");
        instance.Alpha = 1f;
        instance.TintArgb = Color.White.ToArgb();
        nestedInstance.Alpha = 1f;
        nestedInstance.TintArgb = Color.White.ToArgb();

        var childPreview = new VectorScene();
        var childPreviewResult = SceneCompositionBuilder.BuildDrawingObjectChildren(
            childPreview,
            drawingObject,
            project.DrawingObjects,
            0);
        AssertTimeline(
            childPreview.ObjectCount == 1
            && childPreviewResult.TryGetOwner(0, out var previewOwner)
            && previewOwner.DrawingObjectId == childDrawingObject.Id
            && MainForm.TryResolveCompositionInstance(
                childPreview,
                childPreviewResult,
                drawingObject.Instances,
                new PointF(30, 0),
                0,
                2,
                out var resolvedNested)
            && ReferenceEquals(resolvedNested, nestedInstance),
            "Drawing-object editing preview did not expose its nested child instance.");
        nestedInstance!.X += 40;
        childPreviewResult = SceneCompositionBuilder.BuildDrawingObjectChildren(
            childPreview,
            drawingObject,
            project.DrawingObjects,
            0);
        AssertTimeline(
            MainForm.TryResolveCompositionInstance(
                childPreview,
                childPreviewResult,
                drawingObject.Instances,
                new PointF(70, 0),
                0,
                2,
                out var movedNested)
            && ReferenceEquals(movedNested, nestedInstance),
            "Moving a nested drawing-object instance did not move its selectable composition geometry.");
        nestedInstance.X -= 40;

        var dragPreview = new VectorScene();
        var dragPreviewResult = SceneCompositionBuilder.BuildDrawingObjectPreview(
            dragPreview,
            drawingObject,
            project.DrawingObjects,
            new PointF(320, 180),
            0);
        AssertTimeline(
            dragPreview.ObjectCount == 2
            && NearlyEqual(dragPreview.X[0], 420)
            && NearlyEqual(dragPreview.Y[0], 230)
            && NearlyEqual(dragPreview.X[1], 350)
            && NearlyEqual(dragPreview.Y[1], 180)
            && Color.FromArgb(dragPreview.Argb[0]).A is > 0 and < 255
            && dragPreviewResult.TryGetOwner(1, out var dragPreviewOwner)
            && dragPreviewOwner.DrawingObjectId == childDrawingObject.Id,
            "Drag preview did not preserve nested drawing-object geometry, placement, or opacity.");

        var track = scene.Timeline.FindTrackByTargetId(instance.SceneLayerId)
            ?? throw new InvalidOperationException("Scene composition regression lost its scene-layer track.");
        scene.Timeline.InsertBlankKeyframe(track.Id, 5);
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 5);
        var placeholderTrack = destination.Timeline.FindTrackByTargetId(destination.LayerIds[0]);
        AssertTimeline(
            destination.ObjectCount == 0
            && destination.LayerCount == 1
            && placeholderTrack is not null
            && !placeholderTrack.EvaluateExposure(0).HasContent,
            "Blank scene-layer exposure remained visible or populated the composition placeholder layer.");

        scene.Timeline.InsertKeyframe(track.Id, 8);
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 8);
        AssertTimeline(destination.ObjectCount == 2, "Populated scene-layer exposure did not resume composition rendering.");

        scene.Timeline.SetTrackDuration(track.Id, 10);
        var nestedTrack = drawingObject.Timeline.FindTrackByTargetId(drawingObject.Scene.LayerIds[nestedHostLayer])
            ?? throw new InvalidOperationException("Nested composition regression lost its host-layer track.");
        drawingObject.Timeline.InsertBlankKeyframe(nestedTrack.Id, 9);
        var frameNineComposition = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 9);
        AssertTimeline(
            destination.ObjectCount == 1
            && frameNineComposition.ObjectOwners.Values.All(owner => owner.DrawingObjectId != childDrawingObject.Id),
            "Blanking a nested instance host layer did not hide only that child branch.");

        AssertTimeline(
            project.TryAddDrawingObjectInstance(
                drawingObject.Id,
                childDrawingObject.Id,
                new PointF(120, 0),
                drawingObject.Scene.LayerIds[nestedHostLayer],
                out var secondNested)
            && secondNested is not null,
            "A second nested drawing-object instance was rejected from the same host layer.");
        var multiInstancePreview = SceneCompositionBuilder.BuildDrawingObjectChildren(
            childPreview,
            drawingObject,
            project.DrawingObjects,
            0);
        AssertTimeline(
            childPreview.ObjectCount == 2
            && multiInstancePreview.ObjectOwners.Values
                .Select(owner => owner.RootInstanceId)
                .Distinct(StringComparer.Ordinal)
                .Count() == 2,
            "Multiple nested drawing objects in one drawing layer were not composed independently.");
        var marqueeInstances = MainForm.FindCompositionInstancesInsideBounds(
            childPreview,
            multiInstancePreview,
            drawingObject.Instances,
            new RectangleF(-10_000, -10_000, 20_000, 20_000));
        var partialMarqueeInstances = MainForm.FindCompositionInstancesInsideBounds(
            childPreview,
            multiInstancePreview,
            drawingObject.Instances,
            new RectangleF(120, 0, 1, 1));
        AssertTimeline(
            marqueeInstances.Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals([nestedInstance!.Id, secondNested!.Id])
            && partialMarqueeInstances.Count == 0,
            "Drawing-object marquee selection did not require and return fully enclosed nested instances.");

        RunDrawingObjectInstancePlaybackRegression();
        RunConvertSelectionToSymbolRegression();
        RunDrawingObjectAnchorRegression();
        RunSparseLayerCompositionRegression();
        RunLayerEffectCompositionRegression();
        RunSceneSpatialPoseCompositionRegression();
        RunDegeneratePathCompositionRegression();
        RunChunkedCompositionRegression();
        RunSkewedSceneCompositionRegression();
    }

    private static void RunConvertSelectionToSymbolRegression()
    {
        var project = VectorProject.CreateEmpty();
        var container = project.DrawingObjects[0];
        container.Name = "Convert selection host";
        container.Scene.CreateEmpty(1, 8);
        container.Scene.AddObject(
            0,
            new PointF(120, 80),
            new SizeF(80, 60),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        container.Scene.AddObject(
            0,
            new PointF(280, 160),
            new SizeF(100, 100),
            0,
            6,
            Color.Gold,
            Color.Crimson,
            12,
            ShapeKind.Ellipse);
        AssertTimeline(
            container.Scene.InsertTimelineKeyframe(0, 4)
            && container.Scene.InsertTimelineBlankKeyframe(0, 7),
            "Convert-to-symbol regression could not create an isolated source Cel.");
        var targets = Enumerable.Range(0, container.Scene.ObjectCount)
            .Where(index => container.Scene.ObjectKeyframeFrame[index] == 4)
            .ToArray();
        var before = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectPreview(
            before,
            container,
            project.DrawingObjects,
            PointF.Empty,
            4,
            opacity: 1);

        AssertTimeline(targets.Length == 2, "Convert-to-symbol regression did not isolate two source objects.");
        AssertTimeline(
            project.TryConvertDrawingObjectsToSymbol(
                container.Id,
                targets,
                4,
                out var symbol,
                out var instance)
            && symbol is not null
            && instance is not null,
            "Selected drawing objects could not be converted to a nested symbol.");
        var convertedSymbol = symbol!;
        var convertedInstance = instance!;
        var after = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectPreview(
            after,
            container,
            project.DrawingObjects,
            PointF.Empty,
            4,
            opacity: 1);
        var blank = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectPreview(
            blank,
            container,
            project.DrawingObjects,
            PointF.Empty,
            7,
            opacity: 1);

        AssertTimeline(
            project.DrawingObjects.Count == 2
            && ReferenceEquals(project.DrawingObjects[0], container)
            && container.Instances.Count == 1
            && ReferenceEquals(container.Instances[0], convertedInstance)
            && convertedInstance.DrawingObjectId == convertedSymbol.Id
            && convertedInstance.SceneLayerId == container.Scene.LayerIds[0]
            && convertedInstance.EvaluateState(0).Visible == false
            && convertedInstance.EvaluateState(4).Visible
            && convertedInstance.EvaluateState(7).Visible == false
            && convertedSymbol.Scene.ObjectCount == 2
            && convertedSymbol.Scene.ObjectKeyframeFrame.Take(2).All(frame => frame == 0)
            && container.Scene.ObjectCount == 2
            && container.Scene.ObjectKeyframeFrame.Take(2).All(frame => frame == 0)
            && before.ObjectCount == after.ObjectCount
            && Enumerable.Range(0, before.ObjectCount).All(index =>
                before.ShapeKind[index] == after.ShapeKind[index]
                && before.Argb[index] == after.Argb[index]
                && PointsNear(new PointF(before.X[index], before.Y[index]), new PointF(after.X[index], after.Y[index])))
            && blank.ObjectCount == 0,
            "Convert to Symbol changed the rendered position, escaped the current symbol, or leaked outside the source Cel exposure.");

        var stackProject = VectorProject.CreateEmpty();
        var stackContainer = stackProject.DrawingObjects[0];
        stackContainer.Scene.CreateEmpty();
        var bottomRectangle = stackContainer.Scene.AddObject(
            0,
            new PointF(40, 40),
            new SizeF(40, 40),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            4,
            ShapeKind.Rectangle);
        var bottomEllipse = stackContainer.Scene.AddObject(
            0,
            new PointF(80, 40),
            new SizeF(40, 40),
            0,
            0,
            Color.Gold,
            Color.Transparent,
            4,
            ShapeKind.Ellipse);
        var topRectangle = stackContainer.Scene.AddObject(
            0,
            new PointF(60, 40),
            new SizeF(80, 40),
            0,
            0,
            Color.Crimson,
            Color.Transparent,
            4,
            ShapeKind.Rectangle);
        var existingChild = stackProject.AddDrawingObject("Existing child");
        existingChild.Scene.AddObject(
            0,
            new PointF(60, 40),
            new SizeF(120, 80),
            0,
            0,
            Color.Navy,
            Color.Transparent,
            4,
            ShapeKind.Rectangle);
        AssertTimeline(
            stackProject.TryAddDrawingObjectInstance(
                stackContainer.Id,
                existingChild.Id,
                PointF.Empty,
                stackContainer.Scene.LayerIds[0],
                out var existingInstance)
            && existingInstance is not null,
            "Convert-to-symbol stack regression could not create its existing nested instance.");
        AssertTimeline(
            !stackProject.TryConvertDrawingObjectsToSymbol(
                stackContainer.Id,
                [topRectangle],
                0,
                out _,
                out _)
            && stackProject.DrawingObjects.Count == 2
            && stackContainer.Scene.ObjectCount == 3
            && stackContainer.Instances.Count == 1,
            "Convert to Symbol accepted a local selection whose stack position cannot be represented by a nested instance.");

        var stackBefore = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectPreview(
            stackBefore,
            stackContainer,
            stackProject.DrawingObjects,
            PointF.Empty,
            0,
            opacity: 1);
        AssertTimeline(
            stackProject.TryConvertDrawingObjectsToSymbol(
                stackContainer.Id,
                [bottomRectangle, bottomEllipse],
                0,
                out var stackSymbol,
                out var stackInstance)
            && stackSymbol is not null
            && stackInstance is not null,
            "Convert to Symbol rejected a bottommost contiguous local selection.");
        var stackAfter = new VectorScene();
        SceneCompositionBuilder.BuildDrawingObjectPreview(
            stackAfter,
            stackContainer,
            stackProject.DrawingObjects,
            PointF.Empty,
            0,
            opacity: 1);
        var stackInstances = stackContainer.InstancesInLayer(stackContainer.Scene.LayerIds[0]);
        var tealObject = Enumerable.Range(0, stackAfter.ObjectCount)
            .Single(index => stackAfter.Argb[index] == Color.Teal.ToArgb());
        var goldObject = Enumerable.Range(0, stackAfter.ObjectCount)
            .Single(index => stackAfter.Argb[index] == Color.Gold.ToArgb());
        var crimsonObject = Enumerable.Range(0, stackAfter.ObjectCount)
            .Single(index => stackAfter.Argb[index] == Color.Crimson.ToArgb());
        var navyObject = Enumerable.Range(0, stackAfter.ObjectCount)
            .Single(index => stackAfter.Argb[index] == Color.Navy.ToArgb());
        AssertTimeline(
            stackBefore.ObjectCount == stackAfter.ObjectCount
            && stackInstances.Count == 2
            && ReferenceEquals(stackInstances[0], stackInstance)
            && ReferenceEquals(stackInstances[1], existingInstance)
            && stackAfter.ObjectLayer[navyObject] > stackAfter.ObjectLayer[tealObject]
            && stackAfter.ObjectLayer[tealObject] == stackAfter.ObjectLayer[goldObject]
            && stackAfter.ObjectLayer[tealObject] > stackAfter.ObjectLayer[crimsonObject]
            && PointsNear(new PointF(stackAfter.X[tealObject], stackAfter.Y[tealObject]), new PointF(40, 40))
            && PointsNear(new PointF(stackAfter.X[goldObject], stackAfter.Y[goldObject]), new PointF(80, 40))
            && PointsNear(new PointF(stackAfter.X[crimsonObject], stackAfter.Y[crimsonObject]), new PointF(60, 40))
            && PointsNear(new PointF(stackAfter.X[navyObject], stackAfter.Y[navyObject]), new PointF(60, 40)),
            "Convert to Symbol changed the selected group's order relative to local geometry or existing nested instances.");
    }

    private static void RunSceneSpatialPoseCompositionRegression()
    {
        RunSpatialPivotTransformRegression();
        var project = VectorProject.CreateEmpty();
        var container = project.DrawingObjects[0];
        container.Name = "Spatial pose container";
        container.Scene.CreateEmpty();
        container.Scene.AddObject(
            0,
            new PointF(24, 18),
            new SizeF(48, 36),
            0,
            0,
            Color.Teal,
            8,
            ShapeKind.Rectangle);
        container.SetAnchor(new PointF(8, -6));

        var planarChild = project.AddDrawingObject("Planar nested pose source");
        planarChild.Scene.CreateEmpty();
        planarChild.Scene.AddObject(
            0,
            new PointF(12, 10),
            new SizeF(24, 20),
            0,
            0,
            Color.Gold,
            6,
            ShapeKind.Ellipse);
        planarChild.SetAnchor(new PointF(3, 2));

        var spatialChild = project.AddDrawingObject("Spatial nested pose source");
        spatialChild.Scene.CreateEmpty();
        spatialChild.Scene.AddObject(
            0,
            new PointF(-16, 14),
            new SizeF(32, 28),
            0,
            0,
            Color.Coral,
            7,
            ShapeKind.Rectangle);
        spatialChild.SetAnchor(new PointF(-4, 5));

        DrawingObjectInstanceDefinition? planarNested = null;
        DrawingObjectInstanceDefinition? spatialNested = null;
        AssertTimeline(
            project.TryAddDrawingObjectInstance(
                container.Id,
                planarChild.Id,
                new PointF(74, -28),
                out planarNested)
            && planarNested is not null
            && project.TryAddDrawingObjectInstance(
                container.Id,
                spatialChild.Id,
                new PointF(-52, 46),
                out spatialNested)
            && spatialNested is not null,
            "Spatial-pose composition setup rejected valid nested instances.");
        planarNested!.RotationZ = -17;
        planarNested.ScaleX = 0.85f;
        planarNested.ScaleY = 1.2f;
        planarNested.RotationPivot = new Vector3(18, -11, 0);
        planarNested.ScalePivot = new Vector3(-9, 14, 0);
        spatialNested!.Z = -28;
        spatialNested.RotationX = 22;
        spatialNested.RotationY = -14;
        spatialNested.RotationZ = 9;
        spatialNested.ScaleX = 1.1f;
        spatialNested.ScaleY = 0.9f;
        spatialNested.ScaleZ = 1.35f;
        spatialNested.RotationPivot = new Vector3(-24, 16, 7);
        spatialNested.ScalePivot = new Vector3(12, -19, 5);

        var scene = project.Scenes[0];
        scene.Dimension = SceneDimension.TwoD;
        DrawingObjectInstanceDefinition? planarRoot = null;
        DrawingObjectInstanceDefinition? spatialRoot = null;
        DrawingObjectInstanceDefinition? zeroThicknessRoot = null;
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, container.Id, new PointF(180, 120), 0, out planarRoot)
            && planarRoot is not null
            && project.TryAddSceneInstance(scene.Id, container.Id, new PointF(420, 260), 96, out spatialRoot)
            && spatialRoot is not null
            && project.TryAddSceneInstance(scene.Id, container.Id, new PointF(660, 180), 0, out zeroThicknessRoot)
            && zeroThicknessRoot is { ScaleZ: 0 },
            "Spatial-pose composition setup rejected valid scene instances.");
        planarRoot!.ScaleZ = 1;
        planarRoot.RotationZ = 24;
        planarRoot.ScaleX = 1.25f;
        planarRoot.ScaleY = 0.8f;
        planarRoot.RotationPivot = new Vector3(36, -20, 0);
        planarRoot.ScalePivot = new Vector3(-18, 28, 0);
        spatialRoot!.RotationX = -18;
        spatialRoot.RotationY = 27;
        spatialRoot.RotationZ = 13;
        spatialRoot.ScaleX = 1.15f;
        spatialRoot.ScaleY = 0.95f;
        spatialRoot.ScaleZ = 1.6f;
        spatialRoot.RotationPivot = new Vector3(44, -31, 13);
        spatialRoot.ScalePivot = new Vector3(-27, 22, -9);
        zeroThicknessRoot!.RotationPivot = new Vector3(16, 12, 0);
        zeroThicknessRoot.ScalePivot = new Vector3(-8, 6, 0);

        var expectedMetadata = new Dictionary<
            (string RootInstanceId, string InstanceId),
            (string DrawingObjectId, Matrix4x4 Pose, Vector3 ExtrusionVector)>();
        AddExpectedBranch(planarRoot);
        AddExpectedBranch(spatialRoot);
        AddExpectedBranch(zeroThicknessRoot!);

        var twoDDestination = new VectorScene();
        var twoDResult = SceneCompositionBuilder.Build(twoDDestination, scene, project.DrawingObjects, 0);
        scene.Dimension = SceneDimension.ThreeD;
        var threeDDestination = new VectorScene();
        var threeDResult = SceneCompositionBuilder.Build(threeDDestination, scene, project.DrawingObjects, 0);

        AssertTimeline(
            planarRoot.Z == 0
            && planarRoot.RotationX == 0
            && planarRoot.RotationY == 0
            && planarRoot.ScaleZ == 1
            && zeroThicknessRoot!.ScaleZ == 0
            && twoDDestination.ObjectCount == expectedMetadata.Count
            && CompositionRegressionMetadataMatches(twoDDestination, twoDResult, expectedMetadata)
            && CompositionRegressionMetadataMatches(threeDDestination, threeDResult, expectedMetadata)
            && CompositionRegressionOutputsMatch(twoDDestination, twoDResult, threeDDestination, threeDResult)
            && expectedMetadata[(planarRoot.Id, planarRoot.Id)].Pose == Matrix4x4.Identity
            && expectedMetadata[(planarRoot.Id, planarNested.Id)].Pose == Matrix4x4.Identity
            && expectedMetadata[(zeroThicknessRoot.Id, zeroThicknessRoot.Id)].ExtrusionVector == Vector3.Zero
            && expectedMetadata[(zeroThicknessRoot.Id, spatialNested.Id)].ExtrusionVector == Vector3.Zero
            && CompositionRegressionVectorsNear(
                expectedMetadata[(spatialRoot.Id, spatialRoot.Id)].ExtrusionVector,
                Vector3.TransformNormal(
                    Vector3.UnitZ,
                    CompositionRegressionSpatialTransform(container, spatialRoot.EvaluateState(0))))
            && CompositionRegressionVectorsNear(
                expectedMetadata[(spatialRoot.Id, spatialNested.Id)].ExtrusionVector,
                expectedMetadata[(spatialRoot.Id, spatialRoot.Id)].ExtrusionVector)
            && !CompositionRegressionMatricesNear(
                expectedMetadata[(spatialRoot.Id, spatialRoot.Id)].Pose,
                Matrix4x4.Identity),
            "Switching a scene between 2D and 3D changed flattened content, lost Z=0 plane identity, or misaligned scalar poses.");

        void AddExpectedBranch(DrawingObjectInstanceDefinition root)
        {
            var rootState = root.EvaluateState(0);
            var rootFlat = CompositionRegressionFlatTransform(container, rootState);
            var rootSpatial = CompositionRegressionSpatialTransform(container, rootState);
            var rootIsPlanar = CompositionRegressionIsPlanar(rootState);
            var rootExtrusion = CompositionRegressionExtrusionVector(rootState, rootSpatial);
            expectedMetadata.Add(
                (root.Id, root.Id),
                (container.Id, CompositionRegressionPose(rootFlat, rootSpatial, rootIsPlanar), rootExtrusion));

            AddExpectedNested(planarChild, planarNested, root, rootFlat, rootSpatial, rootIsPlanar, rootExtrusion);
            AddExpectedNested(spatialChild, spatialNested, root, rootFlat, rootSpatial, rootIsPlanar, rootExtrusion);
        }

        void AddExpectedNested(
            DrawingObjectDefinition child,
            DrawingObjectInstanceDefinition nested,
            DrawingObjectInstanceDefinition root,
            Matrix3x2 rootFlat,
            Matrix4x4 rootSpatial,
            bool rootIsPlanar,
            Vector3 rootExtrusion)
        {
            var nestedState = nested.EvaluateState(0);
            var nestedFlat = CompositionRegressionFlatTransform(child, nestedState) * rootFlat;
            var nestedIsPlanar = rootIsPlanar && CompositionRegressionIsPlanar(nestedState);
            var nestedSpatial = nestedIsPlanar
                ? CompositionRegressionLift(nestedFlat)
                : CompositionRegressionSpatialTransform(child, nestedState) * rootSpatial;
            expectedMetadata.Add(
                (root.Id, nested.Id),
                (child.Id, CompositionRegressionPose(nestedFlat, nestedSpatial, nestedIsPlanar), rootExtrusion));
        }
    }

    private static void RunSpatialPivotTransformRegression()
    {
        var instance = new DrawingObjectInstanceDefinition
        {
            X = 320,
            Y = -180,
            Z = 75,
            RotationX = 21,
            RotationY = -34,
            RotationZ = 47,
            SkewX = 8,
            SkewY = -6,
            ScaleX = 1.4f,
            ScaleY = 0.65f,
            ScaleZ = 2.25f,
            RotationPivot = new Vector3(42, -26, 11),
            ScalePivot = new Vector3(-18, 33, -7)
        };
        var state = instance.EvaluateState(0);
        var skew = Matrix3x2.CreateSkew(
            state.SkewX * MathF.PI / 180f,
            state.SkewY * MathF.PI / 180f);
        var rotation = Matrix4x4.CreateRotationZ(state.RotationZ * MathF.PI / 180f)
            * Matrix4x4.CreateRotationX(state.RotationX * MathF.PI / 180f)
            * Matrix4x4.CreateRotationY(state.RotationY * MathF.PI / 180f);
        var expected = Matrix4x4.CreateTranslation(-state.ScalePivot)
            * Matrix4x4.CreateScale(state.ScaleX, state.ScaleY, state.ScaleZ)
            * Matrix4x4.CreateTranslation(state.ScalePivot)
            * CompositionRegressionLift(skew)
            * Matrix4x4.CreateTranslation(-state.RotationPivot)
            * rotation
            * Matrix4x4.CreateTranslation(state.RotationPivot)
            * Matrix4x4.CreateTranslation(state.X, state.Y, state.Z);
        var zeroPivot = state with
        {
            RotationPivot = Vector3.Zero,
            ScalePivot = Vector3.Zero
        };
        var legacySpatial = Matrix4x4.CreateScale(
                zeroPivot.ScaleX,
                zeroPivot.ScaleY,
                zeroPivot.ScaleZ)
            * CompositionRegressionLift(skew)
            * rotation
            * Matrix4x4.CreateTranslation(zeroPivot.X, zeroPivot.Y, zeroPivot.Z);
        var legacyPlanar = Matrix3x2.CreateScale(zeroPivot.ScaleX, zeroPivot.ScaleY)
            * skew
            * Matrix3x2.CreateRotation(zeroPivot.RotationZ * MathF.PI / 180f)
            * Matrix3x2.CreateTranslation(zeroPivot.X, zeroPivot.Y);
        var changedPivots = state with
        {
            RotationPivot = new Vector3(-55, 21, 9),
            ScalePivot = new Vector3(31, -17, 4)
        };
        var compensated = DrawingObjectInstanceDefinition.PreserveSpatialTransformForPivotChange(
            state,
            changedPivots);
        var expectedScalePivotPosition = Vector3.Transform(
            state.ScalePivot,
            CompositionRegressionLift(skew)
            * Matrix4x4.CreateTranslation(-state.RotationPivot)
            * rotation
            * Matrix4x4.CreateTranslation(state.RotationPivot)
            * Matrix4x4.CreateTranslation(state.X, state.Y, state.Z));

        AssertTimeline(
            CompositionRegressionMatricesNear(
                DrawingObjectInstanceDefinition.CreateSpatialTransform(state),
                expected)
            && DrawingObjectInstanceDefinition.CreateSpatialTransform(zeroPivot) == legacySpatial
            && DrawingObjectInstanceDefinition.CreatePlanarTransform(zeroPivot) == legacyPlanar
            && CompositionRegressionMatricesNear(
                DrawingObjectInstanceDefinition.CreateSpatialTransform(compensated),
                DrawingObjectInstanceDefinition.CreateSpatialTransform(state))
            && DrawingObjectInstanceDefinition.RotationPivotScenePosition(state)
                == state.RotationPivot + new Vector3(state.X, state.Y, state.Z)
            && Vector3.Distance(
                DrawingObjectInstanceDefinition.ScalePivotScenePosition(state),
                expectedScalePivotPosition) <= 0.001f,
            "Spatial pivot transforms changed the legacy zero-pivot matrix, transform order, or compensation contract.");
    }

    private static Matrix3x2 CompositionRegressionFlatTransform(
        DrawingObjectDefinition drawingObject,
        InstanceFrameState state)
    {
        return Matrix3x2.CreateTranslation(-drawingObject.Anchor.X, -drawingObject.Anchor.Y)
            * Matrix3x2.CreateTranslation(-state.ScalePivot.X, -state.ScalePivot.Y)
            * Matrix3x2.CreateScale(state.ScaleX, state.ScaleY)
            * Matrix3x2.CreateTranslation(state.ScalePivot.X, state.ScalePivot.Y)
            * Matrix3x2.CreateSkew(state.SkewX * MathF.PI / 180f, state.SkewY * MathF.PI / 180f)
            * Matrix3x2.CreateTranslation(-state.RotationPivot.X, -state.RotationPivot.Y)
            * Matrix3x2.CreateRotation(state.RotationZ * MathF.PI / 180f)
            * Matrix3x2.CreateTranslation(state.RotationPivot.X, state.RotationPivot.Y)
            * Matrix3x2.CreateTranslation(state.X, state.Y);
    }

    private static Matrix4x4 CompositionRegressionSpatialTransform(
        DrawingObjectDefinition drawingObject,
        InstanceFrameState state)
    {
        var flatTransform = CompositionRegressionFlatTransform(drawingObject, state);
        if (CompositionRegressionIsPlanar(state)) return CompositionRegressionLift(flatTransform);
        return Matrix4x4.CreateTranslation(-drawingObject.Anchor.X, -drawingObject.Anchor.Y, 0)
            * Matrix4x4.CreateTranslation(-state.ScalePivot)
            * Matrix4x4.CreateScale(state.ScaleX, state.ScaleY, state.ScaleZ)
            * Matrix4x4.CreateTranslation(state.ScalePivot)
            * CompositionRegressionLift(Matrix3x2.CreateSkew(
                state.SkewX * MathF.PI / 180f,
                state.SkewY * MathF.PI / 180f))
            * Matrix4x4.CreateTranslation(-state.RotationPivot)
            * Matrix4x4.CreateRotationZ(state.RotationZ * MathF.PI / 180f)
            * Matrix4x4.CreateRotationX(state.RotationX * MathF.PI / 180f)
            * Matrix4x4.CreateRotationY(state.RotationY * MathF.PI / 180f)
            * Matrix4x4.CreateTranslation(state.RotationPivot)
            * Matrix4x4.CreateTranslation(state.X, state.Y, state.Z);
    }

    private static Vector3 CompositionRegressionExtrusionVector(
        InstanceFrameState rootState,
        Matrix4x4 rootSpatialTransform)
    {
        return rootState.ScaleZ == 0
            ? Vector3.Zero
            : Vector3.TransformNormal(Vector3.UnitZ, rootSpatialTransform);
    }

    private static Matrix4x4 CompositionRegressionPose(
        Matrix3x2 flattenedTransform,
        Matrix4x4 spatialTransform,
        bool isPlanar)
    {
        if (isPlanar) return Matrix4x4.Identity;
        if (!Matrix3x2.Invert(flattenedTransform, out var inverse))
        {
            throw new InvalidOperationException("Spatial-pose regression produced a non-invertible flat transform.");
        }
        return CompositionRegressionLift(inverse) * spatialTransform;
    }

    private static Matrix4x4 CompositionRegressionLift(Matrix3x2 transform)
    {
        return new Matrix4x4(
            transform.M11, transform.M12, 0, 0,
            transform.M21, transform.M22, 0, 0,
            0, 0, 1, 0,
            transform.M31, transform.M32, 0, 1);
    }

    private static bool CompositionRegressionIsPlanar(InstanceFrameState state)
    {
        var effectiveZ = state.Z + state.ScalePivot.Z * (1f - state.ScaleZ);
        return effectiveZ == 0
            && state.RotationX == 0
            && state.RotationY == 0
            && (state.ScaleZ == 0 || state.ScaleZ == 1);
    }

    private static bool CompositionRegressionMatricesNear(
        Matrix4x4 actual,
        Matrix4x4 expected,
        float tolerance = 0.001f)
    {
        static bool ComponentNear(float actual, float expected, float tolerance)
        {
            return float.IsFinite(actual)
                && float.IsFinite(expected)
                && Math.Abs(actual - expected) <= tolerance;
        }

        return ComponentNear(actual.M11, expected.M11, tolerance)
            && ComponentNear(actual.M12, expected.M12, tolerance)
            && ComponentNear(actual.M13, expected.M13, tolerance)
            && ComponentNear(actual.M14, expected.M14, tolerance)
            && ComponentNear(actual.M21, expected.M21, tolerance)
            && ComponentNear(actual.M22, expected.M22, tolerance)
            && ComponentNear(actual.M23, expected.M23, tolerance)
            && ComponentNear(actual.M24, expected.M24, tolerance)
            && ComponentNear(actual.M31, expected.M31, tolerance)
            && ComponentNear(actual.M32, expected.M32, tolerance)
            && ComponentNear(actual.M33, expected.M33, tolerance)
            && ComponentNear(actual.M34, expected.M34, tolerance)
            && ComponentNear(actual.M41, expected.M41, tolerance)
            && ComponentNear(actual.M42, expected.M42, tolerance)
            && ComponentNear(actual.M43, expected.M43, tolerance)
            && ComponentNear(actual.M44, expected.M44, tolerance);
    }

    private static bool CompositionRegressionVectorsNear(
        Vector3 actual,
        Vector3 expected,
        float tolerance = 0.001f)
    {
        return float.IsFinite(actual.X)
            && float.IsFinite(actual.Y)
            && float.IsFinite(actual.Z)
            && float.IsFinite(expected.X)
            && float.IsFinite(expected.Y)
            && float.IsFinite(expected.Z)
            && Math.Abs(actual.X - expected.X) <= tolerance
            && Math.Abs(actual.Y - expected.Y) <= tolerance
            && Math.Abs(actual.Z - expected.Z) <= tolerance;
    }

    private static bool CompositionRegressionMetadataMatches(
        VectorScene destination,
        SceneCompositionResult result,
        IReadOnlyDictionary<
            (string RootInstanceId, string InstanceId),
            (string DrawingObjectId, Matrix4x4 Pose, Vector3 ExtrusionVector)> expectedMetadata)
    {
        if (destination.ObjectCount != result.ObjectOwners.Count
            || destination.ObjectCount != result.ObjectPoses.Count)
        {
            return false;
        }

        var seen = new HashSet<(string RootInstanceId, string InstanceId)>();
        for (var objectIndex = 0; objectIndex < destination.ObjectCount; objectIndex++)
        {
            if (!result.TryGetOwner(objectIndex, out var owner)
                || !result.TryGetPose(objectIndex, out var pose)
                || !expectedMetadata.TryGetValue((owner.RootInstanceId, owner.InstanceId), out var expected)
                || !string.Equals(owner.DrawingObjectId, expected.DrawingObjectId, StringComparison.Ordinal)
                || !CompositionRegressionMatricesNear(pose.FlatToScene, expected.Pose)
                || !CompositionRegressionVectorsNear(pose.ExtrusionVector, expected.ExtrusionVector))
            {
                return false;
            }
            seen.Add((owner.RootInstanceId, owner.InstanceId));
        }

        return seen.SetEquals(expectedMetadata.Keys);
    }

    private static bool CompositionRegressionOutputsMatch(
        VectorScene firstDestination,
        SceneCompositionResult firstResult,
        VectorScene secondDestination,
        SceneCompositionResult secondResult)
    {
        if (firstDestination.ObjectCount != secondDestination.ObjectCount
            || firstDestination.LayerCount != secondDestination.LayerCount)
        {
            return false;
        }

        for (var layer = 0; layer < firstDestination.LayerCount; layer++)
        {
            if (!string.Equals(firstDestination.LayerNames[layer], secondDestination.LayerNames[layer], StringComparison.Ordinal)
                || firstDestination.LayerKinds[layer] != secondDestination.LayerKinds[layer])
            {
                return false;
            }
        }

        for (var objectIndex = 0; objectIndex < firstDestination.ObjectCount; objectIndex++)
        {
            var firstBounds = firstDestination.GetObjectWorldBounds(objectIndex);
            var secondBounds = secondDestination.GetObjectWorldBounds(objectIndex);
            if (!firstResult.TryGetOwner(objectIndex, out var firstOwner)
                || !secondResult.TryGetOwner(objectIndex, out var secondOwner)
                || firstOwner != secondOwner
                || !firstResult.TryGetPose(objectIndex, out var firstPose)
                || !secondResult.TryGetPose(objectIndex, out var secondPose)
                || !CompositionRegressionMatricesNear(firstPose.FlatToScene, secondPose.FlatToScene)
                || !CompositionRegressionVectorsNear(firstPose.ExtrusionVector, secondPose.ExtrusionVector)
                || firstDestination.ShapeKind[objectIndex] != secondDestination.ShapeKind[objectIndex]
                || firstDestination.ObjectLayer[objectIndex] != secondDestination.ObjectLayer[objectIndex]
                || firstDestination.Argb[objectIndex] != secondDestination.Argb[objectIndex]
                || !NearlyEqual(firstBounds.X, secondBounds.X)
                || !NearlyEqual(firstBounds.Y, secondBounds.Y)
                || !NearlyEqual(firstBounds.Width, secondBounds.Width)
                || !NearlyEqual(firstBounds.Height, secondBounds.Height))
            {
                return false;
            }
        }

        return true;
    }

    private static void RunDrawingObjectAnchorRegression()
    {
        var project = VectorProject.CreateEmpty();
        var source = project.DrawingObjects[0];
        source.Scene.CreateEmpty();
        source.Scene.AddObject(
            0,
            new PointF(140, 60),
            new SizeF(80, 40),
            0,
            0,
            Color.Teal,
            8,
            ShapeKind.Rectangle);

        var container = project.AddDrawingObject("Anchor container");
        container.Scene.CreateEmpty(1, 8);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(container.Id, source.Id, new PointF(80, 40), out var nested)
            && nested is not null,
            "Anchor regression could not create its nested instance.");
        nested!.RotationZ = 22;
        nested.SkewX = 8;
        nested.ScaleX = 1.4f;
        nested.ScaleY = 0.7f;
        nested.SetStateAtFrame(6, nested.EvaluateState(0) with
        {
            X = 130,
            Y = 75,
            RotationZ = -18,
            SkewY = 11,
            ScaleX = 0.85f,
            ScaleY = 1.25f
        });

        var scene = project.Scenes[0];
        var sceneTrack = scene.Timeline.FindTrackByTargetId(scene.ActiveLayerId)
            ?? throw new InvalidOperationException("Anchor regression lost its scene track.");
        scene.Timeline.SetTrackDuration(sceneTrack.Id, 8);
        DrawingObjectInstanceDefinition? direct = null;
        DrawingObjectInstanceDefinition? containerInstance = null;
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, source.Id, new PointF(420, 180), 0, out direct)
            && direct is not null
            && project.TryAddSceneInstance(scene.Id, container.Id, new PointF(120, 240), 1, out containerInstance)
            && containerInstance is not null,
            "Anchor regression could not create its direct and container instances.");
        direct!.RotationZ = 35;
        direct.SkewX = 12;
        direct.ScaleX = 1.6f;
        direct.ScaleY = 0.75f;
        direct.SetStateAtFrame(6, direct.EvaluateState(0) with
        {
            X = 500,
            Y = 220,
            RotationZ = -20,
            SkewY = 9,
            ScaleX = 0.8f,
            ScaleY = 1.3f
        });
        containerInstance!.RotationZ = -12;
        containerInstance.ScaleX = 1.15f;
        containerInstance.ScaleY = 0.9f;

        var directBaseBefore = direct.EvaluateState(0);
        var directKeyBefore = direct.EvaluateState(6);
        var nestedBaseBefore = nested.EvaluateState(0);
        var nestedKeyBefore = nested.EvaluateState(6);
        var beforeFrameZero = CapturePositions(0);
        var beforeFrameSix = CapturePositions(6);
        var anchor = new PointF(35, -20);
        AssertTimeline(
            project.TrySetDrawingObjectAnchor(source.Id, anchor),
            "A valid drawing-object anchor change was rejected.");
        var afterFrameZero = CapturePositions(0);
        var afterFrameSix = CapturePositions(6);

        AssertTimeline(
            source.Anchor == anchor
            && beforeFrameZero.Count == 2
            && beforeFrameSix.Count == 2
            && beforeFrameZero.All(item => afterFrameZero.TryGetValue(item.Key, out var position)
                && PointsNear(position, item.Value))
            && beforeFrameSix.All(item => afterFrameSix.TryGetValue(item.Key, out var position)
                && PointsNear(position, item.Value))
            && direct.EvaluateState(0).Position != directBaseBefore.Position
            && direct.EvaluateState(6).Position != directKeyBefore.Position
            && nested.EvaluateState(0).Position != nestedBaseBefore.Position
            && nested.EvaluateState(6).Position != nestedKeyBefore.Position
            && NearlyEqual(direct.EvaluateState(0).RotationZ, directBaseBefore.RotationZ)
            && NearlyEqual(direct.EvaluateState(6).SkewY, directKeyBefore.SkewY)
            && NearlyEqual(nested.EvaluateState(0).ScaleX, nestedBaseBefore.ScaleX)
            && NearlyEqual(nested.EvaluateState(6).ScaleY, nestedKeyBefore.ScaleY),
            "Changing a shared drawing-object anchor moved rendered instances or lost keyframed transform state.");

        AssertTimeline(
            project.TryDuplicateDrawingObject(source.Id, out var duplicate)
            && duplicate is not null
            && duplicate.Anchor == anchor
            && !project.TrySetDrawingObjectAnchor(source.Id, new PointF(float.NaN, 0))
            && source.Anchor == anchor,
            "Drawing-object duplication or invalid-anchor rejection did not preserve the shared anchor.");

        Dictionary<string, PointF> CapturePositions(int frame)
        {
            var destination = new VectorScene();
            var composition = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, frame);
            var positions = new Dictionary<string, PointF>(StringComparer.Ordinal);
            for (var index = 0; index < destination.ObjectCount; index++)
            {
                if (!composition.TryGetOwner(index, out var owner)
                    || owner.DrawingObjectId != source.Id)
                {
                    continue;
                }

                positions[owner.RootInstanceId] = new PointF(destination.X[index], destination.Y[index]);
            }
            return positions;
        }
    }

    private static void RunEditorRestartSnapshotRegression()
    {
        var project = VectorProject.CreateEmpty();
        project.Name = "Restart Snapshot";
        project.Scenes[0].Camera.Projection = CameraProjection.Orthographic;
        AssertTimeline(
            project.TrySetPlaybackSettings(30m, loopPlayback: true, playbackStartFrame: 100, playbackEndFrame: 200)
            && project.TrySetPlaybackFps(29.97m)
            && project.TrySetLoopPlayback(false)
            && project.PlaybackStartFrame == 100
            && project.PlaybackEndFrame == 200
            && project.TrySetPlaybackRange(2, 18),
            "Project playback setting updates overwrote an unrelated saved setting.");
        var root = project.DrawingObjects[0];
        root.Scene.CreateEmpty();
        var rootLine = root.Scene.AddLineSegment(
            0,
            new PointF(-120, 0),
            new PointF(120, 0),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.Coral,
            6,
            LineEndpointStyle.Sharp,
            LineEndpointStyle.Round);
        var child = project.AddDrawingObject("Restart Child");
        child.Scene.AddObject(0, new PointF(24, 12), new SizeF(48, 36), 0, 0, Color.Teal, 6, ShapeKind.Rectangle);
        if (!project.TryAddAssetFolder("Restart Assets", "", out var restartFolder)
            || restartFolder is null
            || !project.TryAddAssetFolder("Restart Children", restartFolder.Id, out var restartChildFolder)
            || restartChildFolder is null
            || !project.TryMoveDrawingObjectToAssetFolder(root.Id, restartFolder.Id)
            || !project.TryMoveDrawingObjectToAssetFolder(child.Id, restartChildFolder.Id))
        {
            throw new InvalidOperationException("Editor restart snapshot regression could not create project asset folders.");
        }
        if (!project.TrySetDrawingObjectAnchor(root.Id, new PointF(18, -12))
            || !project.TrySetDrawingObjectAnchor(child.Id, new PointF(-9, 7)))
        {
            throw new InvalidOperationException("Editor restart snapshot regression could not set drawing-object anchors.");
        }
        if (!project.TryAddDrawingObjectInstance(root.Id, child.Id, new PointF(40, 20), out var nested)
            || nested is null
            || !project.TryAddSceneInstance(project.Scenes[0].Id, root.Id, new PointF(320, 180), 2, out var sceneInstance)
            || sceneInstance is null)
        {
            throw new InvalidOperationException("Editor restart snapshot regression could not create its source project graph.");
        }
        nested.RotationPivot = new Vector3(18, -12, 4);
        nested.ScalePivot = new Vector3(-9, 15, 3);
        sceneInstance.RotationPivot = new Vector3(42, -28, 11);
        sceneInstance.ScalePivot = new Vector3(-21, 34, -6);
        var nestedRestartState = nested.EvaluateState(5) with
        {
            X = 140,
            Y = 70,
            RotationZ = 35,
            ScaleX = 1.75f,
            RotationPivot = new Vector3(36, -24, 8),
            ScalePivot = new Vector3(-18, 30, 6),
            Alpha = 0.65f,
            TintArgb = Color.FromArgb(255, 180, 220, 96).ToArgb(),
            PlaybackFps = 18,
            PlaybackMode = DrawingObjectPlaybackMode.Loop,
            HoldFrame = 4
        };
        var sceneRestartState = sceneInstance.EvaluateState(6) with
        {
            X = 420,
            Y = 240,
            SkewY = 16,
            ScaleY = 0.8f,
            RotationPivot = new Vector3(84, -56, 22),
            ScalePivot = new Vector3(-42, 68, -12),
            Alpha = 0.4f,
            TintArgb = Color.FromArgb(255, 96, 160, 240).ToArgb(),
            PlaybackFps = 24,
            PlaybackMode = DrawingObjectPlaybackMode.HoldFrame,
            HoldFrame = 6
        };
        nested.SetStateAtFrame(5, nestedRestartState);
        sceneInstance.SetStateAtFrame(6, sceneRestartState);

        var restored = VectorProject.RestoreRestartSnapshot(
            EditorRestartStore.RoundTripProjectSnapshot(project.CreateRestartSnapshot()));
        var restoredRoot = restored.DrawingObjects.SingleOrDefault(item => item.Id == root.Id);
        var restoredChild = restored.DrawingObjects.SingleOrDefault(item => item.Id == child.Id);
        var restoredScene = restored.Scenes.SingleOrDefault(item => item.Id == project.Scenes[0].Id);
        if (restored.Name != project.Name
            || restored.Id != project.Id
            || restored.PlaybackFps != 29.97m
            || restored.LoopPlayback
            || restored.PlaybackStartFrame != 2
            || restored.PlaybackEndFrame != 18
            || restored.DrawingObjects.Count != 2
            || restored.AssetFolders.Count != 2
            || restored.Scenes.Count != 1
            || restoredRoot is null
            || restoredChild is null
            || restoredRoot.Anchor != root.Anchor
            || restoredChild.Anchor != child.Anchor
            || restoredRoot.AssetFolderId != restartFolder.Id
            || restoredChild.AssetFolderId != restartChildFolder.Id
            || restored.AssetFolders.Single(folder => folder.Id == restartChildFolder.Id).ParentFolderId != restartFolder.Id
            || restoredRoot.Instances.Count != 1
            || restoredRoot.Instances[0].Id != nested.Id
            || restoredRoot.Instances[0].DrawingObjectId != child.Id
            || restoredRoot.Instances[0].EvaluateState(0).RotationPivot != nested.RotationPivot
            || restoredRoot.Instances[0].EvaluateState(0).ScalePivot != nested.ScalePivot
            || restoredRoot.Instances[0].EvaluateState(5) != nestedRestartState
            || restoredRoot.Scene.ObjectCount != 1
            || restoredRoot.Scene.GetLineEndpointStyle(rootLine, startEndpoint: true) != LineEndpointStyle.Sharp
            || restoredRoot.Scene.GetLineEndpointStyle(rootLine, startEndpoint: false) != LineEndpointStyle.Round
            || restoredScene is null
            || restoredScene.Camera.Projection != CameraProjection.Orthographic
            || restoredScene.Instances.Count != 1
            || restoredScene.Instances[0].Id != sceneInstance.Id
            || restoredScene.Instances[0].DrawingObjectId != root.Id
            || restoredScene.Instances[0].EvaluateState(0).RotationPivot != sceneInstance.RotationPivot
            || restoredScene.Instances[0].EvaluateState(0).ScalePivot != sceneInstance.ScalePivot
            || restoredScene.Instances[0].EvaluateState(6) != sceneRestartState
            || !restoredScene.Timeline.EvaluateTargetExposure(restoredScene.ActiveLayerId, 0).HasContent)
        {
            throw new InvalidOperationException("Editor restart snapshot did not preserve project geometry, instance ownership, or timeline state.");
        }

        var editorState = new EditorRestartState
        {
            Project = project,
            ProjectManifestPath = @"C:\Projects\Restart Snapshot.v2dProject",
            ProjectDirty = true,
            ActiveSceneIndex = 0,
            ActiveDrawingObjectIndex = 0,
            Workspace = WorkspaceView.BasicDrawing,
            Frame = 5,
            SelectedObjects = [rootLine],
            StageView = new StageViewState(
                12,
                -8,
                1.25f,
                0.2f,
                -0.1f,
                2400,
                0.9f,
                3,
                4,
                5,
                0.2f,
                WorldGridType.Cartesian),
            WindowBounds = new Rectangle(40, 60, 1280, 800),
            WindowState = FormWindowState.Normal
        };
        EditorRestartStore.DeletePending();
        try
        {
            var pendingPaths = EditorRestartStore.GetPendingPathsForRegression();
            AssertTimeline(
                EditorRestartStore.TrySave(editorState)
                && File.Exists(pendingPaths.StatePath)
                && File.Exists(pendingPaths.TokenSidecarPath)
                && !EditorRestartStore.TryConsume(requestedToken: null, out _)
                && !File.Exists(pendingPaths.StatePath)
                && !File.Exists(pendingPaths.TokenSidecarPath),
                "A normal startup did not reject and remove an unrequested editor restart state.");

            AssertTimeline(
                EditorRestartStore.TrySave(editorState)
                && EditorRestartStore.TryGetPendingTokenForRegression(out var rejectedToken)
                && !EditorRestartStore.TryConsume(Guid.NewGuid().ToString("N"), out _)
                && !File.Exists(pendingPaths.StatePath)
                && !File.Exists(pendingPaths.TokenSidecarPath)
                && rejectedToken.Length == 32,
                "An incorrect editor restart token did not reject and remove the pending state.");

            var preparedToken = "";
            AssertTimeline(
                EditorRestartStore.TrySave(editorState)
                && EditorRestartStore.TryGetPendingTokenForRegression(out preparedToken)
                && EditorRestartStore.TryPrepareConsume(preparedToken, out var preparedState)
                && preparedState is not null
                && File.Exists(pendingPaths.StatePath)
                && File.Exists(pendingPaths.TokenSidecarPath),
                "A prepared editor restart handoff was deleted before the main window became ready.");
            EditorRestartStore.CompletePreparedConsume(preparedToken);
            AssertTimeline(
                !File.Exists(pendingPaths.StatePath)
                && !File.Exists(pendingPaths.TokenSidecarPath),
                "A prepared editor restart handoff was not deleted after the main window became ready.");

            AssertTimeline(
                EditorRestartStore.TrySave(editorState)
                && EditorRestartStore.TryGetPendingTokenForRegression(out var restartToken)
                && EditorRestartStore.GetRequestedToken(
                    ["--unrelated", EditorRestartStore.TokenArgumentPrefix + restartToken]) == restartToken
                && EditorRestartStore.TryConsume(restartToken, out var consumedState)
                && consumedState is not null
                && consumedState.Project.Id == project.Id
                && consumedState.Project.PlaybackFps == project.PlaybackFps
                && consumedState.ProjectManifestPath == editorState.ProjectManifestPath
                && consumedState.ProjectDirty
                && consumedState.Frame == editorState.Frame
                && !File.Exists(pendingPaths.StatePath)
                && !File.Exists(pendingPaths.TokenSidecarPath)
                && !EditorRestartStore.TryConsume(restartToken, out _),
                "The editor restart token did not authorize exactly one state restoration.");
        }
        finally
        {
            EditorRestartStore.DeletePending();
        }

        RunSingleInstanceRestartWaitRegression();
        RunRestartWindowVisibilityRegression();

        Console.WriteLine("editor_restart_snapshot_regression=ok");
    }

    private static void RunSingleInstanceRestartWaitRegression()
    {
        var mutexName = $"Local\\Vector2DAnimationEngine.Native.Regression.{Guid.NewGuid():N}";
        using var holderReady = new ManualResetEventSlim();
        using var releaseHolder = new ManualResetEventSlim();
        using var waiterStarted = new ManualResetEventSlim();
        var holderTask = Task.Run(() =>
        {
            if (!SingleInstanceLease.TryAcquireForRegression(mutexName, TimeSpan.Zero, out var holderLease)
                || holderLease is null)
            {
                return false;
            }

            using (holderLease)
            {
                holderReady.Set();
                return releaseHolder.Wait(TimeSpan.FromSeconds(5));
            }
        });

        if (!holderReady.Wait(TimeSpan.FromSeconds(2)))
        {
            releaseHolder.Set();
            throw new InvalidOperationException("The single-instance regression could not establish its holder lease.");
        }

        var duplicateAcquired = SingleInstanceLease.TryAcquireForRegression(
            mutexName,
            TimeSpan.Zero,
            out var duplicateLease);
        duplicateLease?.Dispose();
        var waiterTask = Task.Run(() =>
        {
            waiterStarted.Set();
            var wait = Stopwatch.StartNew();
            var acquired = SingleInstanceLease.TryAcquireForRegression(
                mutexName,
                TimeSpan.FromSeconds(2),
                out var waitingLease);
            wait.Stop();
            waitingLease?.Dispose();
            return (Acquired: acquired, Elapsed: wait.Elapsed);
        });
        waiterStarted.Wait(TimeSpan.FromSeconds(2));
        Thread.Sleep(80);
        var waitedForHolder = !waiterTask.IsCompleted;
        releaseHolder.Set();
        Task.WaitAll(holderTask, waiterTask);
        var waiterResult = waiterTask.Result;

        AssertTimeline(
            holderTask.Result
            && !duplicateAcquired
            && waitedForHolder
            && waiterResult.Acquired
            && waiterResult.Elapsed >= TimeSpan.FromMilliseconds(50)
            && waiterResult.Elapsed < TimeSpan.FromSeconds(2),
            $"The restart-aware single-instance lease did not wait for and acquire the released mutex: " +
            $"holder={holderTask.Result}, duplicate={duplicateAcquired}, waited={waitedForHolder}, " +
            $"acquired={waiterResult.Acquired}, elapsedMs={waiterResult.Elapsed.TotalMilliseconds:0}.");
    }

    private static void RunRestartWindowVisibilityRegression()
    {
        var primaryWorkingArea = new Rectangle(0, 0, 1920, 1040);
        var secondaryWorkingArea = new Rectangle(-1280, 0, 1280, 984);
        var offscreen = MainForm.NormalizeRestartWindowBounds(
            new Rectangle(5400, 3200, 1480, 920),
            [primaryWorkingArea, secondaryWorkingArea],
            new Size(1120, 720));
        var retainedSecondary = MainForm.NormalizeRestartWindowBounds(
            new Rectangle(-1200, 80, 1180, 800),
            [primaryWorkingArea, secondaryWorkingArea],
            new Size(1120, 720));

        AssertTimeline(
            primaryWorkingArea.Contains(offscreen)
            && secondaryWorkingArea.Contains(retainedSecondary)
            && MainForm.NormalizeRestartWindowState(FormWindowState.Minimized) == FormWindowState.Normal
            && MainForm.NormalizeRestartWindowState(FormWindowState.Normal) == FormWindowState.Normal
            && MainForm.NormalizeRestartWindowState(FormWindowState.Maximized) == FormWindowState.Maximized,
            $"Restart window normalization did not keep the restored editor visible: " +
            $"offscreen={offscreen}, secondary={retainedSecondary}.");
    }

    private static void RunProjectVaultPersistenceRegression()
    {
        var temporaryRoot = CreateTemporaryDirectory("project-vault-regression");
        var manifestPath = Path.Combine(temporaryRoot, "Library.v2dProject");
        try
        {
            var project = VectorProject.CreateEmpty();
            project.Name = "Library & 资产";
            project.Scenes[0].Camera.Projection = CameraProjection.Orthographic;
            var playbackChangeCount = 0;
            project.Changed += (_, _) => playbackChangeCount++;
            AssertTimeline(
                project.TrySetPlaybackSettings(23.976m, loopPlayback: false, playbackStartFrame: 3, playbackEndFrame: 9)
                && project.PlaybackFps == 23.976m
                && !project.LoopPlayback
                && project.PlaybackStartFrame == 3
                && project.PlaybackEndFrame == 9
                && playbackChangeCount == 1
                && !project.TrySetPlaybackSettings(23.976m, loopPlayback: false, playbackStartFrame: 3, playbackEndFrame: 9)
                && playbackChangeCount == 1,
                "Project playback settings did not normalize or raise one change notification.");
            var root = project.DrawingObjects[0];
            root.Scene.CreateEmpty(2, 12);
            root.Scene.ActiveLayer = 0;
            var nestedLayerId = root.Scene.LayerIds[1];
            var line = root.Scene.AddLineSegment(
                0,
                new PointF(-120, -30),
                new PointF(180, 90),
                VectorUnits.StrokePointsToUnits(2.5f),
                Color.Transparent,
                Color.FromArgb(210, 24, 170, 220),
                18,
                LineEndpointStyle.Round,
                LineEndpointStyle.Sharp);
            var path = root.Scene.AddPathObject(
                1,
                [new PointF(-40, -30), new PointF(70, -20), new PointF(55, 80), new PointF(-65, 60)],
                0,
                Color.Teal,
                Color.Coral,
                12);
            root.Scene.SetLinearGradient(
                path,
                Color.FromArgb(180, Color.Gold),
                Color.FromArgb(220, Color.RoyalBlue),
                new PointF(-65, -30),
                new PointF(70, 80));
            root.Scene.SetGradientStops(path, [
                new GradientStop(0, Color.FromArgb(180, Color.Gold)),
                new GradientStop(0.45f, Color.FromArgb(210, Color.Coral)),
                new GradientStop(1, Color.FromArgb(220, Color.RoyalBlue))]);
            var pathBezierPrepared = root.Scene.TryConvertFillToBezierPath(path)
                && root.Scene.TryGetPathBezierSegment(path, 0, out var pathSegment)
                && root.Scene.SetPathBezierSegment(
                    path,
                    pathSegment.PartIndex,
                    pathSegment.Start,
                    new PointF(pathSegment.Control1.X, pathSegment.Control1.Y - 70),
                    new PointF(pathSegment.Control2.X, pathSegment.Control2.Y - 110),
                    pathSegment.End);
            var hasSavedPathBezier = root.Scene.TryGetPathBezierSegment(path, 0, out var savedPathBezier);
            AssertTimeline(
                pathBezierPrepared && hasSavedPathBezier,
                "Project Vault regression could not prepare exact fill-edge Bezier geometry.");
            const string importedSvgSource = """
                <svg xmlns="http://www.w3.org/2000/svg" width="120" height="80" viewBox="0 0 120 80">
                  <defs><linearGradient id="g"><stop stop-color="#ff3355"/><stop offset="1" stop-color="#2277ee" stop-opacity=".55"/></linearGradient></defs>
                  <rect width="120" height="80" rx="12" fill="url(#g)"/>
                </svg>
                """;
            var importedSvg = root.Scene.AddImportedSvgObject(
                1,
                new PointF(160, 40),
                new SizeF(3000, 2000),
                0.25f,
                importedSvgSource,
                "Vault Logo");
            var text = root.Scene.AddTextObject(
                1,
                new PointF(-320, 180),
                new TextObjectData(
                    "Vault \u6587\u672c\nEditable",
                    TextGeometry.FallbackFontFamilyName,
                    28,
                    TextFontStyle.BoldItalic,
                    TextHorizontalAlignment.Right,
                    new SizeF(4_000, 2_000)),
                Color.FromArgb(220, Color.MediumPurple));
            AssertTimeline(
                root.Scene.InsertTimelineBlankKeyframe(0, 4),
                "Project Vault regression could not create a blank drawing cel.");

            var child = project.AddDrawingObject("Nested <Child>");
            child.Scene.AddObject(0, new PointF(24, 18), new SizeF(90, 60), 0.2f, 0, Color.LimeGreen, 8, ShapeKind.Ellipse);
            DrawingObjectInstanceDefinition? nested = null;
            DrawingObjectInstanceDefinition? sceneInstance = null;
            AssertTimeline(
                project.TryAddAssetFolder("Characters", "", out var folder)
                && folder is not null
                && project.TryMoveDrawingObjectToAssetFolder(child.Id, folder.Id)
                && project.TryAddDrawingObjectInstance(
                    root.Id,
                    child.Id,
                    new PointF(48, 36),
                    nestedLayerId,
                    out nested)
                && nested is not null
                && project.TryAddSceneInstance(project.Scenes[0].Id, root.Id, new PointF(320, 180), 2, out sceneInstance)
                && sceneInstance is not null,
                "Project Vault regression could not create its asset graph.");
            nested!.Alpha = 0.75f;
            nested.TintArgb = Color.FromArgb(255, 220, 180, 120).ToArgb();
            nested.RotationPivot = new Vector3(14, -22, 5);
            nested.ScalePivot = new Vector3(-8, 17, 3);
            sceneInstance!.Alpha = 0.6f;
            sceneInstance.TintArgb = Color.FromArgb(255, 120, 200, 240).ToArgb();
            sceneInstance.ScaleZ = 2.25f;
            sceneInstance.RotationPivot = new Vector3(48, -36, 12);
            sceneInstance.ScalePivot = new Vector3(-24, 30, -9);
            nested!.SetStateAtFrame(6, nested.EvaluateState(6) with
            {
                X = 140,
                RotationZ = 32,
                RotationPivot = new Vector3(28, -44, 10),
                ScalePivot = new Vector3(-16, 34, 6),
                Alpha = 0.35f,
                TintArgb = Color.FromArgb(255, 96, 220, 160).ToArgb(),
                PlaybackFps = 23.976m,
                PlaybackMode = DrawingObjectPlaybackMode.Loop
            });
            sceneInstance!.SetStateAtFrame(7, sceneInstance.EvaluateState(7) with
            {
                Y = 260,
                ScaleX = 1.4f,
                RotationPivot = new Vector3(96, -72, 24),
                ScalePivot = new Vector3(-48, 60, -18),
                Alpha = 0.45f,
                TintArgb = Color.FromArgb(255, 180, 96, 240).ToArgb(),
                PlaybackFps = 24m,
                PlaybackMode = DrawingObjectPlaybackMode.HoldFrame,
                HoldFrame = 3
            });

            var drawingTrackId = root.Scene.Timeline.Tracks[0].Id;
            var savedManifest = ProjectVaultStore.Save(project, manifestPath);
            var svgPath = Path.Combine(temporaryRoot, ".Vault", $"{root.Id}.svg");
            var drawingTimelinePath = Path.Combine(temporaryRoot, ".TimeLine", "Drawings", $"{root.Id}.json.br");
            var sceneTimelinePath = Path.Combine(temporaryRoot, ".TimeLine", "Scenes", $"{project.Scenes[0].Id}.json.br");
            var svg = File.ReadAllText(svgPath);
            AssertTimeline(
                savedManifest == Path.GetFullPath(manifestPath)
                && File.Exists(manifestPath)
                && File.Exists(svgPath)
                && File.Exists(drawingTimelinePath)
                && File.Exists(sceneTimelinePath)
                && svg.Contains("http://www.w3.org/2000/svg", StringComparison.Ordinal)
                && svg.Contains("v2d-metadata", StringComparison.Ordinal)
                && svg.Contains("<path", StringComparison.Ordinal)
                && svg.Contains("<image", StringComparison.Ordinal)
                && svg.Contains("data:image/svg+xml;base64,", StringComparison.Ordinal),
                "Project Vault did not write the manifest, SVG assets, timeline library, or standard SVG preview.");

            var restored = ProjectVaultStore.Load(manifestPath);
            var restoredRoot = restored.DrawingObjects.Single(item => item.Id == root.Id);
            var restoredChild = restored.DrawingObjects.Single(item => item.Id == child.Id);
            var restoredScene = restored.Scenes.Single(item => item.Id == project.Scenes[0].Id);
            var restoredRootSnapshot = restoredRoot.Scene.CreateSnapshot();
            AssertTimeline(
                restored.Id == project.Id
                && restored.Name == project.Name
                && restored.PlaybackFps == 23.976m
                && !restored.LoopPlayback
                && restored.PlaybackStartFrame == 3
                && restored.PlaybackEndFrame == 9
                && restoredRoot.Scene.ObjectCount == root.Scene.ObjectCount
                && restoredRoot.Scene.ShapeKind[line] == ShapeKind.Line
                && restoredRoot.Scene.GetLineEndpointStyle(line, startEndpoint: true) == LineEndpointStyle.Round
                && restoredRoot.Scene.GetLineEndpointStyle(line, startEndpoint: false) == LineEndpointStyle.Sharp
                && restoredRoot.Scene.ShapeKind[importedSvg] == ShapeKind.ImportedSvg
                && restoredRoot.Scene.TryGetImportedSvgSource(importedSvg, out var restoredImportedSvgSource)
                && restoredImportedSvgSource == importedSvgSource
                && restoredRoot.Scene.TryGetImportedSvgName(importedSvg, out var restoredImportedSvgName)
                && restoredImportedSvgName == "Vault Logo"
                && restoredRoot.Scene.ShapeKind[text] == ShapeKind.Text
                && restoredRoot.Scene.TryGetTextObjectData(text, out var restoredText)
                && restoredText.Content == "Vault \u6587\u672c\nEditable"
                && restoredText.FontStyle == TextFontStyle.BoldItalic
                && restoredText.Alignment == TextHorizontalAlignment.Right
                && restoredRootSnapshot.GradientStops.TryGetValue(path, out var restoredStops)
                && restoredStops.Length == 3
                && restoredRoot.Scene.TryGetPathBezierSegment(path, 0, out var restoredPathBezier)
                && PointsNear(restoredPathBezier.Control1, savedPathBezier.Control1)
                && PointsNear(restoredPathBezier.Control2, savedPathBezier.Control2)
                && restoredRoot.Scene.Timeline.Tracks[0].Id == drawingTrackId
                && restoredRoot.Scene.Timeline.Tracks[0].Keyframes.Any(key =>
                    key.Frame == 4 && key.Kind == TimelineKeyframeKind.Blank)
                && restoredRoot.Instances.Single().SceneLayerId == nestedLayerId
                && NearlyEqual(restoredRoot.Instances.Single().EvaluateState(0).Alpha, 0.75f)
                && restoredRoot.Instances.Single().EvaluateState(0).RotationPivot == nested.RotationPivot
                && restoredRoot.Instances.Single().EvaluateState(0).ScalePivot == nested.ScalePivot
                && restoredRoot.Instances.Single().EvaluateState(0).TintArgb == Color.FromArgb(255, 220, 180, 120).ToArgb()
                && NearlyEqual(restoredRoot.Instances.Single().EvaluateState(6).Alpha, 0.35f)
                && restoredRoot.Instances.Single().EvaluateState(6).RotationPivot == new Vector3(28, -44, 10)
                && restoredRoot.Instances.Single().EvaluateState(6).ScalePivot == new Vector3(-16, 34, 6)
                && restoredRoot.Instances.Single().EvaluateState(6).TintArgb == Color.FromArgb(255, 96, 220, 160).ToArgb()
                && restoredRoot.Instances.Single().EvaluateState(6).PlaybackFps == 23.976m
                && restoredChild.AssetFolderId == folder!.Id
                && restoredScene.Camera.Projection == CameraProjection.Orthographic
                && NearlyEqual(restoredScene.Instances.Single().EvaluateState(0).Alpha, 0.6f)
                && restoredScene.Instances.Single().EvaluateState(0).RotationPivot == sceneInstance.RotationPivot
                && restoredScene.Instances.Single().EvaluateState(0).ScalePivot == sceneInstance.ScalePivot
                && NearlyEqual(restoredScene.Instances.Single().EvaluateState(0).ScaleZ, 2.25f)
                && restoredScene.Instances.Single().EvaluateState(0).TintArgb == Color.FromArgb(255, 120, 200, 240).ToArgb()
                && NearlyEqual(restoredScene.Instances.Single().EvaluateState(7).Alpha, 0.45f)
                && restoredScene.Instances.Single().EvaluateState(7).RotationPivot == new Vector3(96, -72, 24)
                && restoredScene.Instances.Single().EvaluateState(7).ScalePivot == new Vector3(-48, 60, -18)
                && NearlyEqual(restoredScene.Instances.Single().EvaluateState(7).ScaleZ, 2.25f)
                && restoredScene.Instances.Single().EvaluateState(7).TintArgb == Color.FromArgb(255, 180, 96, 240).ToArgb()
                && restoredScene.Instances.Single().EvaluateState(7).HoldFrame == 3,
                "Project Vault load did not restore project identity, SVG geometry, layers, frames, or animated instances.");

            var unrelatedPath = Path.Combine(temporaryRoot, "notes.txt");
            File.WriteAllText(unrelatedPath, "preserve me");
            var stalePath = Path.Combine(temporaryRoot, ".Vault", "stale.svg");
            File.WriteAllText(stalePath, "stale");
            ProjectVaultStore.Save(project, manifestPath);
            AssertTimeline(
                File.Exists(unrelatedPath) && !File.Exists(stalePath),
                "Project Vault overwrite removed an unrelated root file or retained a stale managed asset.");

            File.AppendAllText(svgPath, "<!-- tampered -->");
            var checksumRejected = false;
            try
            {
                _ = ProjectVaultStore.Load(manifestPath);
            }
            catch (InvalidDataException)
            {
                checksumRejected = true;
            }
            AssertTimeline(checksumRejected, "Project Vault accepted a drawing SVG whose checksum no longer matched the manifest.");

            ProjectVaultStore.Save(project, manifestPath);
            var secondManifestRejected = false;
            try
            {
                _ = ProjectVaultStore.Save(project, Path.Combine(temporaryRoot, "Second.v2dProject"));
            }
            catch (InvalidOperationException)
            {
                secondManifestRejected = true;
            }
            AssertTimeline(secondManifestRejected, "One asset-library folder accepted two project manifests that share managed directories.");

            var unmanagedRoot = Path.Combine(temporaryRoot, "unmanaged-target");
            var unmanagedVault = Path.Combine(unmanagedRoot, ".Vault");
            var unmanagedTimeline = Path.Combine(unmanagedRoot, ".TimeLine");
            Directory.CreateDirectory(unmanagedVault);
            Directory.CreateDirectory(unmanagedTimeline);
            var unmanagedVaultSentinel = Path.Combine(unmanagedVault, "keep.bin");
            var unmanagedTimelineSentinel = Path.Combine(unmanagedTimeline, "keep.json");
            File.WriteAllBytes(unmanagedVaultSentinel, [1, 2, 3, 4]);
            File.WriteAllText(unmanagedTimelineSentinel, "preserve me");
            var unmanagedTargetRejected = false;
            try
            {
                _ = ProjectVaultStore.Save(project, Path.Combine(unmanagedRoot, "Library.v2dProject"));
            }
            catch (InvalidOperationException)
            {
                unmanagedTargetRejected = true;
            }
            AssertTimeline(
                unmanagedTargetRejected
                && File.ReadAllBytes(unmanagedVaultSentinel).SequenceEqual(new byte[] { 1, 2, 3, 4 })
                && File.ReadAllText(unmanagedTimelineSentinel) == "preserve me",
                "Project Vault replaced unmanaged .Vault or .TimeLine data during a first save.");

            var foreignRoot = Path.Combine(temporaryRoot, "foreign-project");
            var foreignManifest = Path.Combine(foreignRoot, "Library.v2dProject");
            var foreignProject = VectorProject.CreateEmpty();
            ProjectVaultStore.Save(foreignProject, foreignManifest);
            var foreignProjectRejected = false;
            try
            {
                _ = ProjectVaultStore.Save(project, foreignManifest);
            }
            catch (InvalidOperationException)
            {
                foreignProjectRejected = true;
            }
            var preservedForeignProject = ProjectVaultStore.Load(foreignManifest);
            AssertTimeline(
                foreignProjectRejected && preservedForeignProject.Id == foreignProject.Id,
                "Project Vault overwrote a same-name manifest that belongs to a different project.");

            var recoveryRoot = Path.Combine(temporaryRoot, "interrupted-save");
            var recoveryManifest = Path.Combine(recoveryRoot, "Recovery.v2dProject");
            var recoveryProject = VectorProject.CreateEmpty();
            recoveryProject.Name = "Before interruption";
            ProjectVaultStore.Save(recoveryProject, recoveryManifest);

            var preparedOperationId = Guid.NewGuid().ToString("N");
            var preparedPaths = ProjectVaultStore.GetSaveRecoveryPathsForRegression(
                recoveryManifest,
                preparedOperationId);
            Directory.CreateDirectory(preparedPaths.BackupRoot);
            Directory.Move(
                Path.Combine(recoveryRoot, ".Vault"),
                Path.Combine(preparedPaths.BackupRoot, ".Vault"));
            Directory.Move(
                Path.Combine(recoveryRoot, ".TimeLine"),
                Path.Combine(preparedPaths.BackupRoot, ".TimeLine"));
            File.Move(
                recoveryManifest,
                Path.Combine(preparedPaths.BackupRoot, Path.GetFileName(recoveryManifest)));
            Directory.CreateDirectory(preparedPaths.StagingRoot);
            Directory.CreateDirectory(Path.Combine(recoveryRoot, ".Vault"));
            File.WriteAllText(Path.Combine(recoveryRoot, ".Vault", "partial.svg"), "partial");
            ProjectVaultStore.WriteSaveJournalForRegression(
                recoveryManifest,
                preparedOperationId,
                recoveryProject.Id,
                hadManagedProject: true,
                installed: false);

            var rolledBackProject = ProjectVaultStore.Load(recoveryManifest);
            AssertTimeline(
                rolledBackProject.Id == recoveryProject.Id
                && rolledBackProject.Name == recoveryProject.Name
                && File.Exists(recoveryManifest)
                && Directory.Exists(Path.Combine(recoveryRoot, ".Vault"))
                && Directory.Exists(Path.Combine(recoveryRoot, ".TimeLine"))
                && !File.Exists(Path.Combine(recoveryRoot, ".Vault", "partial.svg"))
                && !Directory.Exists(preparedPaths.StagingRoot)
                && !Directory.Exists(preparedPaths.BackupRoot)
                && !File.Exists(preparedPaths.JournalPath),
                "Prepared project-save recovery did not restore the previous complete managed file set.");

            var damagedOperationId = Guid.NewGuid().ToString("N");
            var damagedPaths = ProjectVaultStore.GetSaveRecoveryPathsForRegression(
                recoveryManifest,
                damagedOperationId);
            Directory.CreateDirectory(damagedPaths.BackupRoot);
            Directory.Move(
                Path.Combine(recoveryRoot, ".Vault"),
                Path.Combine(damagedPaths.BackupRoot, ".Vault"));
            Directory.Move(
                Path.Combine(recoveryRoot, ".TimeLine"),
                Path.Combine(damagedPaths.BackupRoot, ".TimeLine"));
            File.Move(
                recoveryManifest,
                Path.Combine(damagedPaths.BackupRoot, Path.GetFileName(recoveryManifest)));

            rolledBackProject.Name = "After interruption";
            ProjectVaultStore.Save(rolledBackProject, recoveryManifest);
            var damagedSvgPath = Directory.GetFiles(Path.Combine(recoveryRoot, ".Vault"), "*.svg").Single();
            File.AppendAllText(damagedSvgPath, "<!-- incomplete installed file -->");
            ProjectVaultStore.WriteSaveJournalForRegression(
                recoveryManifest,
                damagedOperationId,
                recoveryProject.Id,
                hadManagedProject: true,
                installed: true);

            var recoveredDamagedInstall = ProjectVaultStore.Load(recoveryManifest);
            AssertTimeline(
                recoveredDamagedInstall.Id == recoveryProject.Id
                && recoveredDamagedInstall.Name == recoveryProject.Name
                && !File.ReadAllText(
                    Directory.GetFiles(Path.Combine(recoveryRoot, ".Vault"), "*.svg").Single())
                    .Contains("incomplete installed file", StringComparison.Ordinal)
                && !Directory.Exists(damagedPaths.StagingRoot)
                && !Directory.Exists(damagedPaths.BackupRoot)
                && !File.Exists(damagedPaths.JournalPath),
                "Installed project-save recovery deleted the valid backup instead of rolling back damaged files.");

            var missingTargetOperationId = Guid.NewGuid().ToString("N");
            var missingTargetPaths = ProjectVaultStore.GetSaveRecoveryPathsForRegression(
                recoveryManifest,
                missingTargetOperationId);
            Directory.CreateDirectory(missingTargetPaths.BackupRoot);
            Directory.Move(
                Path.Combine(recoveryRoot, ".Vault"),
                Path.Combine(missingTargetPaths.BackupRoot, ".Vault"));
            Directory.Move(
                Path.Combine(recoveryRoot, ".TimeLine"),
                Path.Combine(missingTargetPaths.BackupRoot, ".TimeLine"));
            File.Move(
                recoveryManifest,
                Path.Combine(missingTargetPaths.BackupRoot, Path.GetFileName(recoveryManifest)));

            recoveredDamagedInstall.Name = "Installed with missing target";
            ProjectVaultStore.Save(recoveredDamagedInstall, recoveryManifest);
            Directory.Delete(Path.Combine(recoveryRoot, ".TimeLine"), recursive: true);
            ProjectVaultStore.WriteSaveJournalForRegression(
                recoveryManifest,
                missingTargetOperationId,
                recoveryProject.Id,
                hadManagedProject: true,
                installed: true);

            var recoveredMissingTarget = ProjectVaultStore.Load(recoveryManifest);
            AssertTimeline(
                recoveredMissingTarget.Id == recoveryProject.Id
                && recoveredMissingTarget.Name == recoveryProject.Name
                && Directory.Exists(Path.Combine(recoveryRoot, ".Vault"))
                && Directory.Exists(Path.Combine(recoveryRoot, ".TimeLine"))
                && File.Exists(recoveryManifest)
                && !Directory.Exists(missingTargetPaths.StagingRoot)
                && !Directory.Exists(missingTargetPaths.BackupRoot)
                && !File.Exists(missingTargetPaths.JournalPath),
                "Installed project-save recovery did not restore a valid backup when a target was missing.");

            var installedOperationId = Guid.NewGuid().ToString("N");
            var installedPaths = ProjectVaultStore.GetSaveRecoveryPathsForRegression(
                recoveryManifest,
                installedOperationId);
            Directory.CreateDirectory(installedPaths.StagingRoot);
            Directory.CreateDirectory(installedPaths.BackupRoot);
            File.WriteAllText(Path.Combine(installedPaths.StagingRoot, "stale.tmp"), "stale");
            File.WriteAllText(Path.Combine(installedPaths.BackupRoot, "stale.tmp"), "stale");
            ProjectVaultStore.WriteSaveJournalForRegression(
                recoveryManifest,
                installedOperationId,
                recoveryProject.Id,
                hadManagedProject: true,
                installed: true);

            var installedProject = ProjectVaultStore.Load(recoveryManifest);
            AssertTimeline(
                installedProject.Id == recoveryProject.Id
                && installedProject.Name == recoveryProject.Name
                && !Directory.Exists(installedPaths.StagingRoot)
                && !Directory.Exists(installedPaths.BackupRoot)
                && !File.Exists(installedPaths.JournalPath),
                "Installed project-save recovery did not preserve the complete new file set and clean recovery files.");
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }

        Console.WriteLine("project_vault_persistence_regression=ok");
    }

    private static void RunDrawingObjectInstancePlaybackRegression()
    {
        var project = VectorProject.CreateEmpty();
        var root = project.DrawingObjects[0];
        root.Scene.CreateEmpty(1, 20);
        var animated = project.AddDrawingObject("Animated child");
        animated.Scene.CreateEmpty(1, 4);
        animated.Scene.EditFrame = 0;
        animated.Scene.AddObject(0, PointF.Empty, new SizeF(24, 24), 0, 0, Color.Coral, 4, ShapeKind.Rectangle);
        AssertTimeline(
            animated.Scene.InsertTimelineBlankKeyframe(0, 1),
            "Instance playback regression could not create its second source cel.");
        animated.Scene.EditFrame = 1;
        animated.Scene.AddObject(0, PointF.Empty, new SizeF(24, 24), 0, 0, Color.CornflowerBlue, 4, ShapeKind.Rectangle);
        AssertTimeline(
            animated.Scene.InsertTimelineBlankKeyframe(0, 2),
            "Instance playback regression could not terminate its second source cel.");
        animated.Scene.EditFrame = 0;

        AssertTimeline(
            project.TryAddDrawingObjectInstance(root.Id, animated.Id, PointF.Empty, out var nested)
            && nested is not null,
            "Instance playback regression could not create its nested instance.");
        nested!.PlaybackMode = DrawingObjectPlaybackMode.HoldFrame;
        nested.HoldFrame = 1;

        var scene = project.Scenes[0];
        var sceneTrack = scene.Timeline.FindTrackByTargetId(scene.ActiveLayerId)
            ?? throw new InvalidOperationException("Instance playback regression lost its scene track.");
        scene.Timeline.SetTrackDuration(sceneTrack.Id, 20);
        DrawingObjectInstanceDefinition? rootInstance = null;
        DrawingObjectInstanceDefinition? direct = null;
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, root.Id, new PointF(100, 0), 0, out rootInstance)
            && rootInstance is not null
            && project.TryAddSceneInstance(scene.Id, animated.Id, new PointF(200, 0), 1, out direct)
            && direct is not null,
            "Instance playback regression could not create its scene instances.");
        direct!.PlaybackMode = DrawingObjectPlaybackMode.HoldFrame;
        direct.HoldFrame = 0;
        var defaultAppearanceState = direct.EvaluateState(0);
        var legacyStateNode = System.Text.Json.JsonSerializer.SerializeToNode(defaultAppearanceState)!.AsObject();
        legacyStateNode.Remove(nameof(InstanceFrameState.Alpha));
        legacyStateNode.Remove(nameof(InstanceFrameState.TintArgb));
        var legacyAppearanceState = System.Text.Json.JsonSerializer.Deserialize<InstanceFrameState>(
            legacyStateNode.ToJsonString());
        AssertTimeline(
            NearlyEqual(defaultAppearanceState.Alpha, 1f)
            && defaultAppearanceState.TintArgb == Color.White.ToArgb()
            && NearlyEqual(legacyAppearanceState.Alpha, 1f)
            && legacyAppearanceState.TintArgb == Color.White.ToArgb(),
            "Instance appearance defaults or legacy state migration did not preserve the original rendering.");

        var destination = new VectorScene();
        var result = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 10, parentFps: 30);
        AssertTimeline(
            destination.ObjectCount == 2
            && destination.Argb[0] == Color.CornflowerBlue.ToArgb()
            && destination.Argb[1] == Color.Coral.ToArgb()
            && NearlyEqual(destination.X[0], 100)
            && NearlyEqual(destination.X[1], 200)
            && result.TryGetOwner(0, out var nestedOwner)
            && nestedOwner.RootInstanceId == rootInstance!.Id
            && result.TryGetOwner(1, out var directOwner)
            && directOwner.RootInstanceId == direct.Id,
            "Composition did not evaluate two instances of one source at independent held frames.");

        direct.PlaybackMode = DrawingObjectPlaybackMode.PlayOnce;
        direct.PlaybackFps = 24m;
        AssertTimeline(
            direct.ResolvePlaybackFrame(999, 23.976m, 2000) == 1000,
            "Fractional parent FPS was truncated while resolving an instance playback frame.");

        direct.PlaybackMode = DrawingObjectPlaybackMode.Loop;
        direct.PlaybackFps = 15;
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 10, parentFps: 30);
        var loopFrame = direct.ResolvePlaybackFrame(10, 30, animated.FrameCount);
        AssertTimeline(
            destination.ObjectCount == 2
            && destination.Argb.Take(destination.ObjectCount).All(argb => argb == Color.CornflowerBlue.ToArgb())
            && loopFrame == 1,
            $"Looping instance playback did not wrap its FPS-scaled local frame: frame={loopFrame}, " +
            $"frameCount={animated.FrameCount}, objects={destination.ObjectCount}, " +
            $"colors={string.Join(',', destination.Argb.Take(destination.ObjectCount).Select(argb => argb.ToString("X8")))}.");

        direct.PlaybackMode = DrawingObjectPlaybackMode.PlayOnce;
        direct.PlaybackFps = 30;
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 10, parentFps: 30);
        AssertTimeline(
            destination.ObjectCount == 1
            && direct.ResolvePlaybackFrame(10, 30, animated.FrameCount) == 3,
            "Play-once instance playback did not stop on its terminal local frame.");

        var previousFrameState = direct.EvaluateState(9);
        var resizedState = direct.EvaluateState(10) with
        {
            ScaleX = 2.5f,
            ScaleY = 0.4f,
            RotationZ = 25f,
            SkewX = 12f,
            Alpha = 0.5f,
            TintArgb = Color.FromArgb(255, 128, 200, 64).ToArgb(),
            PlaybackFps = 15,
            PlaybackMode = DrawingObjectPlaybackMode.Loop,
            HoldFrame = 1
        };
        direct.SetStateAtFrame(10, resizedState);
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 9, parentFps: 30);
        var previousFrameObjectCount = destination.ObjectCount;
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 10, parentFps: 30);
        AssertTimeline(
            previousFrameState.PlaybackFps == 30
            && previousFrameState.PlaybackMode == DrawingObjectPlaybackMode.PlayOnce
            && previousFrameObjectCount == 1
            && destination.ObjectCount == 2
            && destination.Argb[0] == Color.CornflowerBlue.ToArgb()
            && destination.Argb[1] == ApplyInstanceAppearanceForRegression(
                Color.CornflowerBlue.ToArgb(),
                0.5f,
                Color.FromArgb(255, 128, 200, 64).ToArgb())
            && direct.EvaluateState(10).PlaybackFps == 15
            && direct.EvaluateState(10).PlaybackMode == DrawingObjectPlaybackMode.Loop
            && direct.EvaluateState(10).HoldFrame == 1
            && MainForm.RestoreDrawingObjectOriginalSize(direct, 10)
            && NearlyEqual(direct.EvaluateState(10).ScaleX, 1)
            && NearlyEqual(direct.EvaluateState(10).ScaleY, 1)
            && NearlyEqual(direct.EvaluateState(10).RotationZ, 0)
            && NearlyEqual(direct.EvaluateState(10).SkewX, 0)
            && NearlyEqual(direct.EvaluateState(10).Alpha, 0.5f)
            && direct.EvaluateState(10).TintArgb == Color.FromArgb(255, 128, 200, 64).ToArgb()
            && direct.EvaluateState(10).PlaybackFps == 15
            && direct.EvaluateState(10).PlaybackMode == DrawingObjectPlaybackMode.Loop
            && direct.EvaluateState(10).HoldFrame == 1
            && direct.EvaluateState(9) == previousFrameState
            && !MainForm.CanRestoreDrawingObjectOriginalSize(direct.EvaluateState(10))
            && !MainForm.RestoreDrawingObjectOriginalSize(direct, 10),
            "Playback controls or restoring original size changed another keyframe or unrelated instance state.");
    }

    private static void RunSkewedSceneCompositionRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Scene.CreateEmpty();
        drawingObject.Scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(120, 80),
            0,
            6,
            Color.Teal,
            Color.White,
            6,
            ShapeKind.Rectangle);
        var scene = project.Scenes[0];
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, new PointF(180, 120), 0, out var instance)
            && instance is not null,
            "Validated skewed scene-instance insertion was rejected.");
        instance!.SkewX = 28;

        var destination = new VectorScene();
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        AssertTimeline(
            destination.ObjectCount == 1
            && destination.ShapeKind[0] == ShapeKind.Path
            && destination.TryGetPathWorldContours(0, out var contours)
            && contours.Length == 1
            && contours[0].Length >= 4,
            "Scene composition did not preserve a skewed drawing-object instance as real path geometry.");
    }

    private static void RunLayerEffectCompositionRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Scene.CreateEmpty();
        drawingObject.Scene.EditFrame = 0;
        drawingObject.Scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(120, 80),
            0,
            0,
            Color.Coral,
            8,
            ShapeKind.Rectangle);
        drawingObject.Scene.AddFolderLayer("Mask Group");
        var sourceMask = drawingObject.Scene.AddMaskLayer("Mask Shape");
        drawingObject.Scene.AddObject(
            sourceMask,
            PointF.Empty,
            new SizeF(60, 40),
            0,
            0,
            Color.White,
            8,
            ShapeKind.Rectangle);
        var scene = project.Scenes[0];
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, PointF.Empty, 0, out _),
            "Layer-effect composition setup rejected a valid drawing-object instance.");

        var sourceContentLayer = drawingObject.Scene.ObjectLayer[0];
        drawingObject.Scene.SetLayerColor(sourceContentLayer, Color.DeepSkyBlue);
        drawingObject.Scene.SetLayerOutline(sourceContentLayer, true);
        var destination = new VectorScene();
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        var folder = Array.FindIndex(destination.LayerKinds, kind => kind == DrawingLayerKind.Folder);
        var mask = Array.FindIndex(destination.LayerKinds, kind => kind == DrawingLayerKind.Mask);
        var content = Array.FindIndex(destination.LayerKinds, kind => kind == DrawingLayerKind.Drawing);
        AssertTimeline(
            destination.ObjectCount == 2
            && folder >= 0
            && mask >= 0
            && content >= 0
            && destination.GetLayerParentIndex(mask) == folder
            && destination.GetLayerParentIndex(content) == folder
            && destination.TryGetMaskLayerIndex(content, out var linkedMask)
            && linkedMask == mask
            && destination.LayerOutline[content]
            && destination.GetLayerColor(content).ToArgb() == Color.DeepSkyBlue.ToArgb()
            && destination.HasLayerEffects,
            $"Flattened composition lost folder, mask, or Outline relationships: objects={destination.ObjectCount}, folder={folder}, mask={mask}, content={content}, parentMask={destination.GetLayerParentIndex(mask)}, parentContent={destination.GetLayerParentIndex(content)}, outlined={(content >= 0 && destination.LayerOutline[content])}, color={(content >= 0 ? destination.GetLayerColor(content).ToArgb() : 0):X8}.");

        var sceneLayer = scene.Layers[0];
        scene.SetLayerColor(sceneLayer.Id, Color.Magenta);
        scene.SetLayerOutline(sceneLayer.Id, true);
        SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        AssertTimeline(
            destination.LayerOutline.All(outlined => outlined)
            && destination.LayerColorArgb.All(argb => argb == Color.Magenta.ToArgb()),
            "Scene-layer Outline did not override every nested instance layer with the scene layer color.");
    }

    private static void RunSparseLayerCompositionRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Scene.CreateEmpty(3);
        var sourceLine = drawingObject.Scene.AddCurveSegment(
            1,
            new PointF(10.4f, 20.6f),
            new PointF(43.7f, -18.2f),
            new PointF(98.3f, 61.9f),
            VectorUnits.StrokePointsToUnits(2),
            Color.Transparent,
            Color.White,
            8);
        var scene = project.Scenes[0];
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, PointF.Empty, 0, out _),
            "Sparse-layer composition setup rejected a valid drawing-object instance.");

        var destination = new VectorScene();
        var result = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        var tracks = destination.LayerIds
            .Select(destination.Timeline.FindTrackByTargetId)
            .ToArray();
        drawingObject.Scene.TryGetLineEndpoint(sourceLine, startEndpoint: true, out var sourceStart);
        drawingObject.Scene.TryGetLineEndpoint(sourceLine, startEndpoint: false, out var sourceEnd);
        var reference = new VectorScene();
        reference.CreateEmpty(3);
        var referenceLine = reference.AddCubicCurveSegment(
            1,
            VectorUnits.Quantize(sourceStart),
            VectorUnits.Quantize(new PointF(
                drawingObject.Scene.CurveControlX[sourceLine],
                drawingObject.Scene.CurveControlY[sourceLine])),
            VectorUnits.Quantize(new PointF(
                drawingObject.Scene.CurveControl2X[sourceLine],
                drawingObject.Scene.CurveControl2Y[sourceLine])),
            VectorUnits.Quantize(sourceEnd),
            drawingObject.Scene.Stroke[sourceLine],
            Color.FromArgb(drawingObject.Scene.Argb[sourceLine]),
            Color.FromArgb(drawingObject.Scene.StrokeArgb[sourceLine]),
            drawingObject.Scene.AtomCount[sourceLine]);
        AssertTimeline(
            destination.LayerCount == 3
            && destination.ObjectCount == 1
            && result.ObjectOwners.Count == 1
            && tracks[0] is not null
            && !tracks[0]!.EvaluateExposure(0).HasContent
            && tracks[1] is not null
            && tracks[1]!.EvaluateExposure(0).HasContent
            && tracks[2] is not null
            && !tracks[2]!.EvaluateExposure(0).HasContent
            && NearlyEqual(destination.X[0], reference.X[referenceLine])
            && NearlyEqual(destination.Y[0], reference.Y[referenceLine])
            && NearlyEqual(destination.Width[0], reference.Width[referenceLine])
            && NearlyEqual(destination.Height[0], reference.Height[referenceLine])
            && NearlyEqual(destination.Angle[0], reference.Angle[referenceLine])
            && NearlyEqual(destination.CurveControlX[0], reference.CurveControlX[referenceLine])
            && NearlyEqual(destination.CurveControlY[0], reference.CurveControlY[referenceLine])
            && NearlyEqual(destination.CurveControl2X[0], reference.CurveControl2X[referenceLine])
            && NearlyEqual(destination.CurveControl2Y[0], reference.CurveControl2Y[referenceLine]),
            "Sparse-layer composition populated an empty layer or changed identity-transform line quantization.");
    }

    private static void RunChunkedCompositionRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Scene.Generate(8, 8192, 8_192_000);
        var nestedDrawingObject = project.AddDrawingObject("Chunked nested pose source");
        nestedDrawingObject.Scene.CreateEmpty();
        nestedDrawingObject.Scene.AddObject(
            0,
            new PointF(18, -12),
            new SizeF(36, 24),
            0,
            0,
            Color.Gold,
            6,
            ShapeKind.Ellipse);
        AssertTimeline(
            project.TryAddDrawingObjectInstance(
                drawingObject.Id,
                nestedDrawingObject.Id,
                new PointF(64, -36),
                out var nestedInstance)
            && nestedInstance is not null,
            "Chunked composition setup rejected a valid nested drawing-object instance.");
        nestedInstance!.Z = -24;
        nestedInstance.RotationX = 16;
        nestedInstance.RotationY = -11;
        nestedInstance.RotationZ = 12;
        nestedInstance.ScaleX = 0.8f;
        nestedInstance.ScaleY = 0.8f;
        nestedInstance.ScaleZ = 1.25f;
        var scene = project.Scenes[0];
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, PointF.Empty, 0, out var instance)
            && instance is not null,
            "Chunked composition setup rejected a valid drawing-object instance.");
        var sceneTrack = scene.Timeline.FindTrackByTargetId(scene.Layers[0].Id)
            ?? throw new InvalidOperationException("Chunked composition regression lost its scene-layer track.");
        scene.Timeline.SetTrackDuration(sceneTrack.Id, 21);
        instance!.Alpha = 0.5f;
        instance.TintArgb = Color.FromArgb(255, 128, 192, 224).ToArgb();
        instance.Z = 72;
        instance.RotationX = -19;
        instance.RotationY = 23;
        instance.ScaleZ = 1.5f;
        var rootState = instance.EvaluateState(20);
        var rootFlat = CompositionRegressionFlatTransform(drawingObject, rootState);
        var rootSpatial = CompositionRegressionSpatialTransform(drawingObject, rootState);
        var rootIsPlanar = CompositionRegressionIsPlanar(rootState);
        var rootExtrusion = CompositionRegressionExtrusionVector(rootState, rootSpatial);
        var nestedState = nestedInstance.EvaluateState(20);
        var nestedFlat = CompositionRegressionFlatTransform(nestedDrawingObject, nestedState) * rootFlat;
        var nestedIsPlanar = rootIsPlanar && CompositionRegressionIsPlanar(nestedState);
        var nestedSpatial = nestedIsPlanar
            ? CompositionRegressionLift(nestedFlat)
            : CompositionRegressionSpatialTransform(nestedDrawingObject, nestedState) * rootSpatial;
        var expectedMetadata = new Dictionary<
            (string RootInstanceId, string InstanceId),
            (string DrawingObjectId, Matrix4x4 Pose, Vector3 ExtrusionVector)>
        {
            [(instance.Id, instance.Id)] =
                (drawingObject.Id, CompositionRegressionPose(rootFlat, rootSpatial, rootIsPlanar), rootExtrusion),
            [(instance.Id, nestedInstance.Id)] =
                (nestedDrawingObject.Id, CompositionRegressionPose(nestedFlat, nestedSpatial, nestedIsPlanar), rootExtrusion)
        };
        var sourceArgb = drawingObject.Scene.Argb.Take(drawingObject.Scene.ObjectCount).ToArray();
        var sourceFirstObject = Enumerable.Range(0, drawingObject.Scene.ObjectCount)
            .OrderBy(index => drawingObject.Scene.ObjectLayer[index])
            .ThenBy(index => drawingObject.Scene.ObjectOrder[index])
            .ThenBy(index => drawingObject.Scene.ObjectSubOrder[index])
            .ThenBy(index => index)
            .First();
        var destination = new VectorScene();
        var packedResult = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 20);
        var evaluatedAppearance = instance.EvaluateState(20);
        var expectedFirstArgb = ApplyInstanceAppearanceForRegression(
            sourceArgb[sourceFirstObject],
            evaluatedAppearance.Alpha,
            evaluatedAppearance.TintArgb);
        var sourceUnchanged = drawingObject.Scene.Argb
            .Take(drawingObject.Scene.ObjectCount)
            .SequenceEqual(sourceArgb);
        var expectedPackedObjectCount = drawingObject.Scene.ObjectCount + nestedDrawingObject.Scene.ObjectCount;
        AssertTimeline(
            destination.ObjectCount == expectedPackedObjectCount
            && CompositionRegressionMetadataMatches(destination, packedResult, expectedMetadata)
            && destination.Argb[0] == expectedFirstArgb
            && sourceUnchanged,
            "Packed instance composition did not apply Alpha and multiply tint without mutating its source: "
            + $"objects={destination.ObjectCount}/{expectedPackedObjectCount}, owners={packedResult.ObjectOwners.Count}, "
            + $"poses={packedResult.ObjectPoses.Count}, "
            + $"source={sourceFirstObject}, actual=0x{(destination.ObjectCount > 0 ? destination.Argb[0] : 0):X8}, expected=0x{expectedFirstArgb:X8}, "
            + $"alpha={evaluatedAppearance.Alpha:R}, tint=0x{evaluatedAppearance.TintArgb:X8}, sourceUnchanged={sourceUnchanged}.");

        drawingObject.Scene.EditFrame = 20;
        drawingObject.Scene.AddPathObject(
            0,
            [new PointF(-80, -40), new PointF(80, -40), new PointF(60, 70), new PointF(-70, 65)],
            0,
            Color.Coral,
            Color.Transparent,
            12);
        var result = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 20);
        var destinationPaths = Enumerable.Range(0, destination.ObjectCount)
            .Where(index => destination.ShapeKind[index] == ShapeKind.Path)
            .ToArray();
        var expectedChunkedObjectCount = drawingObject.Scene.ObjectCount + nestedDrawingObject.Scene.ObjectCount;
        AssertTimeline(
            destination.ObjectCount == expectedChunkedObjectCount
            && CompositionRegressionMetadataMatches(destination, result, expectedMetadata)
            && destinationPaths.Length == 1
            && destination.TryGetPathWorldContours(destinationPaths[0], out var contours)
            && contours.Length == 1
            && contours[0].Length == 4,
            "Chunked mixed-geometry composition lost objects, owners, or path geometry.");
    }

    private static void RunDegeneratePathCompositionRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Scene.CreateEmpty();
        drawingObject.Scene.AddPathObject(
            0,
            [new PointF(-40, -40), new PointF(40, -40), new PointF(40, 40), new PointF(-40, 40)],
            0,
            Color.Teal,
            Color.Transparent,
            8);
        var scene = project.Scenes[0];
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, PointF.Empty, 0, out var instance)
            && instance is not null,
            "Degenerate-path composition setup rejected a valid drawing-object instance.");
        instance!.ScaleX = 0;
        instance.ScaleY = 0;

        var destination = new VectorScene();
        var result = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        var track = destination.Timeline.FindTrackByTargetId(destination.LayerIds[0]);
        AssertTimeline(
            destination.ObjectCount == 0
            && result.ObjectOwners.Count == 0
            && track is not null
            && !track.EvaluateExposure(0).HasContent,
            "A path discarded by a degenerate transform left a populated composition keyframe.");
    }

    private static void RunFillEdgeBezierCompositionRegression()
    {
        var project = VectorProject.CreateEmpty();
        var drawingObject = project.DrawingObjects[0];
        drawingObject.Scene.CreateEmpty();
        var fill = drawingObject.Scene.AddPathObject(
            0,
            [
                new PointF(-120, -80),
                new PointF(140, -60),
                new PointF(160, 100),
                new PointF(-100, 120)
            ],
            0,
            Color.Coral,
            Color.Transparent,
            8);
        var prepared = drawingObject.Scene.TryConvertFillToBezierPath(fill)
            && drawingObject.Scene.TryGetPathBezierSegment(fill, 0, out var segment)
            && drawingObject.Scene.SetPathBezierSegment(
                fill,
                segment.PartIndex,
                segment.Start,
                new PointF(segment.Control1.X, segment.Control1.Y - 90),
                new PointF(segment.Control2.X, segment.Control2.Y - 140),
                segment.End);
        var hasSourceContours = drawingObject.Scene.TryGetPathBezierWorldContours(fill, out var sourceContours);
        AssertTimeline(
            prepared && hasSourceContours,
            "Fill-edge Bezier composition setup did not retain exact source curves.");

        var scene = project.Scenes[0];
        AssertTimeline(
            project.TryAddSceneInstance(scene.Id, drawingObject.Id, new PointF(400, 300), 0, out var instance)
            && instance is not null,
            "Fill-edge Bezier composition setup rejected a valid instance.");
        instance!.RotationZ = 90;

        var destination = new VectorScene();
        var result = SceneCompositionBuilder.Build(destination, scene, project.DrawingObjects, 0);
        var hasDestinationContours = destination.TryGetPathBezierWorldContours(0, out var destinationContours);
        AssertTimeline(
            destination.ObjectCount == 1
            && result.TryGetOwner(0, out var owner)
            && owner.DrawingObjectId == drawingObject.Id
            && hasDestinationContours
            && destinationContours.Length == sourceContours.Length
            && destinationContours[0].Length == sourceContours[0].Length,
            "Scene composition flattened or dropped exact fill-edge Bezier geometry.");

        static PointF RotateTranslate90(PointF point) => new(400 - point.Y, 300 + point.X);
        for (var contourIndex = 0; contourIndex < sourceContours.Length; contourIndex++)
        {
            for (var nodeIndex = 0; nodeIndex < sourceContours[contourIndex].Length; nodeIndex++)
            {
                var source = sourceContours[contourIndex][nodeIndex];
                var composed = destinationContours[contourIndex][nodeIndex];
                AssertTimeline(
                    PointsNear(composed.Anchor, RotateTranslate90(source.Anchor))
                    && PointsNear(composed.IncomingControl, RotateTranslate90(source.IncomingControl))
                    && PointsNear(composed.OutgoingControl, RotateTranslate90(source.OutgoingControl)),
                    "Scene composition did not apply the instance transform equally to fill-edge anchors and controls.");
            }
        }
    }

}
