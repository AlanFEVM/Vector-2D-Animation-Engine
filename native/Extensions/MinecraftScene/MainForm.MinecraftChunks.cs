namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private SceneBlockChunkTimeline? _minecraftChunkEditor;
    private string? _minecraftOuterTrackId;

    partial void HandleNativeExtensionStageDoubleClick(MouseEventArgs e, ref bool handled)
    {
        if (e.Button != MouseButtons.Left || _minecraftChunkEditor is not null
            || _tool is not (ToolMode.Select or ToolMode.ObjectSelect)
            || !TryHitMinecraftChunk(e.Location, out var scene, out var chunk)) return;
        OpenNativeExtensionChunk(scene, chunk);
        handled = true;
    }

    private bool TrySelectMinecraftChunk(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _tool is not (ToolMode.Select or ToolMode.ObjectSelect or ToolMode.Transform or ToolMode.Distort)
            || IsSceneMaskEditing() || _playing) return false;
        if (!TryHitMinecraftChunk(e.Location, out var scene, out var chunk))
        {
            _stage.SetBlockChunkOutline(null);
            return false;
        }

        ClearSelection();
        _stage.SetBlockChunkOutline(chunk.Bounds(scene.BlockGrid!));
        return true;
    }

    private bool TryHitMinecraftChunk(Point location, out SceneDefinition scene, out SceneBlockChunk chunk)
    {
        scene = null!;
        chunk = default;
        if (!IsScene2DFrontView() || IsShotDirectorContext() || IsSceneMaskEditing()
            || ActiveScene() is not { BlockGrid: { } grid } active
            || !TryGetMinecraftCell(location, grid, out var x, out var y, out var world)) return false;

        // Leave a foreground character or drawing to the normal selection routing.
        var objectIndex = -1;
        if (active.Instances.Count > 0 && IsSceneDrawingContext()
            && TryResolveSceneInstanceAt(world, out _)) return false;
        if (_stage.RendersReferenceProjection)
            _stage.TryHitTestProjectedObject(location, 0, out objectIndex);
        else
        {
            var hit = _scene.HitTestElement(world, _frame, 0);
            if (hit.IsValid) objectIndex = hit.Key.ObjectIndex;
        }
        var hitDepth = 0;
        if ((uint)objectIndex < _scene.ObjectCount)
        {
            if (!_scene.TryGetBitmapObjectData(objectIndex, out var bitmap) || bitmap.BlockGridKey is not { } key)
                return false;
            hitDepth = grid.Decode(key < 0 ? ~key : key).Depth;
        }
        if (objectIndex < 0 && !grid.Layers.Values.Any(id =>
        {
            var layer = Array.IndexOf(active.DrawingScene.LayerIds, id);
            return layer >= 0 && active.DrawingScene.IsLayerEffectivelyVisible(layer)
                && !active.DrawingScene.IsLayerEffectivelyLocked(layer);
        })) return false;

        if (grid.SectionDepth > 0
            && active.Timeline.FindTrack(_timeline.ActiveTrackId ?? "")?.TargetId is { } layerId)
        {
            for (var depth = 0; depth < grid.SectionDepth; depth += SceneBlockChunk.CellCount)
            {
                if (grid.Layers[depth] != layerId) continue;
                var layer = Array.IndexOf(active.DrawingScene.LayerIds, layerId);
                if (layer >= 0 && active.DrawingScene.IsLayerEffectivelyVisible(layer)
                    && !active.DrawingScene.IsLayerEffectivelyLocked(layer)) hitDepth = depth;
                break;
            }
        }
        scene = active;
        chunk = SceneBlockChunk.FromCell(x, y, grid.SectionDepth > 0 ? hitDepth : 0);
        return true;
    }

    private bool TryGetMinecraftCell(Point location, SceneBlockGrid grid, out int x, out int y, out PointF world)
    {
        x = y = -1;
        world = default;
        if (_stage.UsesReferenceProjection)
        {
            if (!_stage.TryGetReferenceRay(location, out var ray) || Math.Abs(ray.Direction.Z) < 0.999f) return false;
            var point = ray.Origin - ray.Direction * (ray.Origin.Z / ray.Direction.Z);
            world = new(point.X, point.Y);
        }
        else world = _stage.ScreenToWorld(location);
        x = (int)MathF.Floor(world.X / grid.CellSize + grid.Columns * 0.5f);
        y = (int)MathF.Floor(world.Y / grid.CellSize + grid.Rows * 0.5f);
        return grid.Contains(x, y);
    }

    partial void OpenNativeExtensionChunk(SceneDefinition scene, SceneBlockChunk chunk)
    {
        if (scene.BlockGrid is not { } grid || !chunk.ExistsIn(grid) || !_scenes.Contains(scene)
            || !CommitTextEdit()) return;
        if (IsSymbolEditing && !ExitSceneSymbolEditing()) return;

        CloseNativeExtensionChunk(restoreTimeline: true);
        FinishPointerInteractionForContextChange();
        StopPlayback();
        if (!IsSceneCompositionContext() || !ReferenceEquals(ActiveScene(), scene))
        {
            _workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
            SelectScenePreservingHistory(_scenes.ToList().IndexOf(scene));
        }
        if (!ReferenceEquals(ActiveScene(), scene) || !IsScene2DFrontView() || IsSceneMaskEditing()) return;

        var editor = new SceneBlockChunkTimeline(scene, grid, chunk);
        if (editor.TimelineTargetIds.Count == 0) return;
        _minecraftOuterTrackId = _timeline.ActiveTrackId;
        _minecraftChunkEditor = editor;
        ClearSelection();
        ActivateTool(ToolMode.Select);
        _timeline.BindContext(editor);
        var firstDepth = chunk.Z * SceneBlockChunk.CellCount;
        if (editor.Timeline.FindTrackByTargetId(SceneBlockChunkTimeline.TargetId(firstDepth)) is { } first)
            _timeline.ActiveTrackIndex = TrackIndex(editor.Timeline, first.Id);
        SyncTimelineFrameRange();
        _timeline.CurrentFrame = _frame;
        _stage.SetBlockChunkOutline(chunk.Bounds(grid), editing: true);
        _stage.SetBlockChunkFocus(grid.SectionDepth > 0 ? chunk : null);
        UpdateSymbolEditingBreadcrumb();
        _drawingObjectTabs.ScrollControlIntoView(_symbolEditingReturnButton!);
        LayoutToolPalette();
        OpenMinecraftSceneExtension();
        RefreshNativeExtensionInspector();
        RefreshStageLayerContentHighlight();
        _stage.Focus();
    }

    partial void CloseNativeExtensionChunk(bool restoreTimeline, ref bool handled)
    {
        _stage.SetBlockChunkFocus(null);
        _stage.SetBlockChunkOutline(null);
        if (_minecraftChunkEditor is not { } editor) return;
        var outerTrack = _minecraftOuterTrackId;
        _minecraftChunkEditor = null;
        _minecraftOuterTrackId = null;
        UpdateSymbolEditingBreadcrumb();
        LayoutToolPalette();
        handled = true;
        if (_extensionInspectorPage is MinecraftSceneExtensionPanel panel) panel.EndChunkEditing();
        if (!restoreTimeline || !ReferenceEquals(ActiveScene(), editor.Scene)
            || !ReferenceEquals(_timeline.Context, editor)) return;
        _timeline.BindSceneDefinition(editor.Scene);
        SyncTimelineFrameRange();
        _timeline.CurrentFrame = _frame;
        if (outerTrack is not null)
        {
            var trackIndex = TrackIndex(editor.Scene.Timeline, outerTrack);
            if (trackIndex >= 0) _timeline.ActiveTrackIndex = trackIndex;
        }
        if (IsScene2DFrontView()) _stage.SetBlockChunkOutline(editor.Chunk.Bounds(editor.Grid));
        RefreshNativeExtensionInspector();
        RefreshStageLayerContentHighlight();
        _stage.Focus();
    }
}
