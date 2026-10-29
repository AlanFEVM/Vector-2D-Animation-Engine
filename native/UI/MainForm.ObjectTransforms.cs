namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool InspectorUsesBakedGeometry(int index) => _scene.ShapeKind[index]
        is ShapeKind.Path or ShapeKind.Freeform or ShapeKind.BrushStroke or ShapeKind.MixingStroke;

    private bool TryGetInspectorObjectGeometry(int[] selection, out RectangleF geometry, out float rotation)
    {
        geometry = RectangleF.Empty;
        rotation = 0;
        var instances = SelectedSceneInstances();
        if (instances.Count > 0)
        {
            if (!TryGetSelectedSceneInstanceBounds(out var bounds)) return false;
            geometry = new RectangleF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, bounds.Width, bounds.Height);
            if (instances.Count == 1 && TryGetSelectedSceneInstanceFrame(out var frame))
            {
                geometry = new RectangleF(frame.Center.X, frame.Center.Y,
                    MathF.Sqrt(frame.AxisX.X * frame.AxisX.X + frame.AxisX.Y * frame.AxisX.Y),
                    MathF.Sqrt(frame.AxisY.X * frame.AxisY.X + frame.AxisY.Y * frame.AxisY.Y));
                rotation = instances[0].EvaluateState(_frame).RotationZ;
            }
        }
        else if (selection.Length == 1)
        {
            var index = selection[0];
            geometry = new RectangleF(_scene.X[index], _scene.Y[index], _scene.Width[index], _scene.Height[index]);
            rotation = InspectorUsesBakedGeometry(index) ? 0 : _scene.Angle[index] * (180f / MathF.PI);
        }
        else if (selection.Length > 1)
        {
            var bounds = _scene.GetObjectWorldBounds(selection[0]);
            foreach (var index in selection.Skip(1)) bounds = RectangleF.Union(bounds, _scene.GetObjectWorldBounds(index));
            geometry = new RectangleF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, bounds.Width, bounds.Height);
        }
        else return false;
        return float.IsFinite(geometry.X) && float.IsFinite(geometry.Y)
            && float.IsFinite(geometry.Width) && float.IsFinite(geometry.Height) && float.IsFinite(rotation);
    }

    private bool CanEditInspectorObjectTransform(int[] selection)
    {
        if (_playing || IsTextEditActive() || IsScene3DView() || DrawingToolsBlocked()) return false;
        var instances = SelectedSceneInstances();
        if (instances.Count > 0)
        {
            if (selection.Length > 0) return false;
            var source = IsSceneCompositionContext() ? ActiveScene()?.DrawingScene : ActiveDrawingObject()?.Scene;
            return source is not null && MotionTransformInstances().All(instance =>
            {
                var layer = Array.IndexOf(source.LayerIds, instance.SceneLayerId);
                return layer >= 0 && !source.IsLayerEffectivelyLocked(layer);
            });
        }
        return selection.Length > 0 && _scene.ExpandMotionTransformObjects(selection, _frame)
            .All(index => _scene.IsObjectActive(index, _frame) && !_scene.IsLayerEffectivelyLocked(_scene.ObjectLayer[index]));
    }

    private void ApplyInspectorObjectTransform(ObjectTransformField field, float value, bool keepRatio)
    {
        var selection = SelectedActiveDrawingObjectIndices();
        if (!Enum.IsDefined(field) || !float.IsFinite(value) || !CanEditInspectorObjectTransform(selection)
            || !TryGetInspectorObjectGeometry(selection, out var geometry, out var rotation))
        {
            UpdateInspector();
            return;
        }
        var instances = SelectedSceneInstances();
        var group = instances.Count > 1 || instances.Count == 0 && selection.Length > 1;
        var current = field switch
        {
            ObjectTransformField.X => geometry.X,
            ObjectTransformField.Y => geometry.Y,
            ObjectTransformField.Width => geometry.Width,
            ObjectTransformField.Height => geometry.Height,
            _ => rotation
        };
        if (Math.Abs(current - value) < 0.0001f) return;
        var moveX = field == ObjectTransformField.X ? value - geometry.X : 0;
        var moveY = field == ObjectTransformField.Y ? value - geometry.Y : 0;
        var scaleX = field == ObjectTransformField.Width ? value / Math.Max(0.0001f, geometry.Width) : 1;
        var scaleY = field == ObjectTransformField.Height ? value / Math.Max(0.0001f, geometry.Height) : 1;
        if (value < 1 && field is ObjectTransformField.Width or ObjectTransformField.Height) { UpdateInspector(); return; }
        if (instances.Count == 0 && selection.Length == 1)
        {
            var index = selection[0];
            if (field == ObjectTransformField.Height && _scene.ShapeKind[index] == ShapeKind.Line)
            {
                UpdateInspector();
                return;
            }
            if (field is ObjectTransformField.Width or ObjectTransformField.Height
                && _scene.ShapeKind[index] is ShapeKind.Freeform or ShapeKind.BrushStroke)
            {
                var stroke = _scene.Stroke[index];
                if (value <= stroke || current <= stroke) { UpdateInspector(); return; }
                if (field == ObjectTransformField.Width) scaleX = (value - stroke) / (current - stroke);
                else scaleY = (value - stroke) / (current - stroke);
            }
        }
        if (keepRatio || group)
        {
            if (field == ObjectTransformField.Width) scaleY = scaleX;
            if (field == ObjectTransformField.Height) scaleX = scaleY;
        }
        var deltaDegrees = field == ObjectTransformField.Rotation ? NormalizeDegrees(value - rotation) : 0;
        if (field == ObjectTransformField.Rotation && Math.Abs(deltaDegrees) < 0.0001f)
        {
            UpdateInspector();
            return;
        }
        var pivot = new PointF(geometry.X, geometry.Y);
        if (instances.Count > 0)
        {
            ApplyInspectorInstanceTransform(pivot, moveX, moveY, scaleX, scaleY, deltaDegrees);
            return;
        }
        var previousAngle = rotation * (MathF.PI / 180f);
        var nextAngle = previousAngle + deltaDegrees * (MathF.PI / 180f);
        var previousCos = MathF.Cos(previousAngle);
        var previousSin = MathF.Sin(previousAngle);
        var nextCos = MathF.Cos(nextAngle);
        var nextSin = MathF.Sin(nextAngle);
        PointF Transform(PointF point)
        {
            if (scaleX == 1 && scaleY == 1 && deltaDegrees == 0)
                return new PointF(point.X + moveX, point.Y + moveY);
            var offsetX = point.X - pivot.X;
            var offsetY = point.Y - pivot.Y;
            var localX = (offsetX * previousCos + offsetY * previousSin) * scaleX;
            var localY = (-offsetX * previousSin + offsetY * previousCos) * scaleY;
            return new PointF(pivot.X + moveX + localX * nextCos - localY * nextSin,
                pivot.Y + moveY + localX * nextSin + localY * nextCos);
        }
        var targets = _scene.ExpandMotionTransformObjects(selection, _frame);
        var snapshot = CreateCanvasMutationSnapshot(targets);
        try { _scene.TransformObjects(targets, Transform); }
        catch
        {
            RestoreCanvasMutationSnapshot(snapshot);
            throw;
        }
        PushUndoSnapshot(snapshot);
        _scene.RefreshTimelineTweenMaterializationsAtEndpointFrame(_frame);
        InvalidatePointerTopologyQueries();
        ClearSceneSnapQueryCache();
        RebuildDrawingObjectUnderlay();
        SyncSelectionToStage();
        _hierarchyPanel.RefreshScene();
        _timeline.RefreshTimeline();
        UpdateTransformOverlay();
        UpdateInspector();
        _stage.Invalidate();
        MarkProjectDirty();
    }

    private void ApplyInspectorInstanceTransform(PointF pivot, float moveX, float moveY,
        float scaleX, float scaleY, float deltaDegrees)
    {
        var scene = IsSceneCompositionContext() ? ActiveScene() : null;
        var container = scene is null ? ActiveDrawingObject() : null;
        if (scene is null && container is null) return;
        var instances = MotionTransformInstances();
        var selected = SelectedSceneInstances();
        var primary = selected.FirstOrDefault(instance => instance.Id == _selectedSceneInstanceId);
        var instanceSnapshot = scene is not null ? scene.CreateInstanceSnapshot() : container!.CreateInstanceSnapshot();
        var timelineSnapshot = scene?.Timeline.CreateSnapshot();
        var drawingSnapshot = scene is null ? _scene.CreateSnapshot() : null;
        var timelineSelection = _timeline.CaptureSelectionSnapshot();
        ClearInstanceTimelineEditTracking();
        foreach (var instance in instances)
        {
            var editFrame = PrepareInstanceStateTimelineEdit(instance);
            var state = instance.EvaluateState(editFrame);
            var next = state with
            {
                ScaleX = ClampScaleMagnitude(state.ScaleX * scaleX, 0.01f, 1000f),
                ScaleY = ClampScaleMagnitude(state.ScaleY * scaleY, 0.01f, 1000f),
                RotationZ = NormalizeDegrees(state.RotationZ + deltaDegrees)
            };
            if (selected.Count == 1 && instances.Count == 1) next = KeepWorldPointFixed(state, next, pivot);
            else
            {
                next = MoveScalePivotWithGroup(state, next, pivot, scaleX, scaleY);
                var operationPivot = DrawingObjectInstanceDefinition.RotationPivotScenePosition(next);
                var rotated = RotatePointAround(new PointF(operationPivot.X, operationPivot.Y), pivot, deltaDegrees * (MathF.PI / 180f));
                next = next with { X = next.X + rotated.X - operationPivot.X, Y = next.Y + rotated.Y - operationPivot.Y };
            }
            next = next with { X = VectorUnits.Quantize(next.X + moveX), Y = VectorUnits.Quantize(next.Y + moveY) };
            instance.SetStateAtFrame(editFrame, next);
        }
        RefreshEditedInstanceTimelineTweens();
        ClearInstanceTimelineEditTracking();
        _sceneInstanceTimelineDirty = false;
        if (scene is not null)
            PushSceneTimelineUndo(scene, timelineSnapshot!, instanceSnapshot: instanceSnapshot,
                playheadFrame: _frame, timelineSelection: timelineSelection);
        else PushUndoSnapshot(drawingSnapshot!, container, instanceSnapshot, playheadFrame: _frame, timelineSelection: timelineSelection);
        _timeline.RefreshTimeline();
        RebuildEditableInstanceComposition();
        SetSceneInstanceSelection(selected, primary);
        _hierarchyPanel.RefreshScene();
        UpdateInspector();
        _stage.Invalidate();
        MarkProjectDirty();
    }
}
