namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private void HandleHierarchyFocusRequested(HierarchySelectionChangedEventArgs e)
    {
        if (_spatialTransformKeyboardActive) CancelSpatialTransformKeyboard();
        if (!CommitTextEdit() || !IsSceneCompositionContext()) return;

        var objectIndices = ResolveHierarchyFocusObjectIndices(_scene, _frame, e.Kind, e.Index);
        if (objectIndices.Length == 0) return;

        _stage.SetSpatialTransformHover(SpatialTransformHandleHit.None);
        if (_stage.FocusSceneObjects(objectIndices, ReferenceCameraMotion.Animated)) UpdateStatusBar();
    }

    internal static int[] ResolveHierarchyFocusObjectIndices(
        VectorScene scene,
        int frame,
        HierarchyNodeKind kind,
        int index)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (kind == HierarchyNodeKind.Object)
        {
            return IsHierarchyFocusObject(scene, frame, index) ? [index] : [];
        }

        if (kind != HierarchyNodeKind.Layer
            || (uint)index >= scene.LayerCount
            || !scene.IsLayerEffectivelyVisible(index))
        {
            return [];
        }

        return Enumerable.Range(0, scene.ObjectCount)
            .Where(objectIndex =>
            {
                var layer = (int)scene.ObjectLayer[objectIndex];
                return (layer == index || scene.IsLayerDescendantOf(layer, index))
                    && IsHierarchyFocusObject(scene, frame, objectIndex);
            })
            .ToArray();
    }

    private static bool IsHierarchyFocusObject(VectorScene scene, int frame, int objectIndex)
    {
        if ((uint)objectIndex >= scene.ObjectCount) return false;
        var layer = (int)scene.ObjectLayer[objectIndex];
        return scene.IsObjectActive(objectIndex, frame)
            && scene.ShouldRenderLayerContent(layer);
    }
}
