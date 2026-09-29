using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private readonly SceneLightingPanel _sceneLightingPanel = new();
    private readonly SpatialMaterialPanel _spatialMaterialPanel = new();
    private string _selectedSceneLightId = string.Empty;
    private SceneOpticsEditSnapshot? _sceneOpticsEditSnapshot;
    private SceneOpticsEditSnapshot? _sceneOpticsSavedSnapshot;
    private readonly HashSet<(string SceneId, string LightId, int EndpointFrame)>
        _pendingSceneLightTweenMaterializations = [];
    private bool _sceneOpticsEventsAttached;

    private sealed record SceneOpticsEditSnapshot(
        string SceneId,
        SceneLightDefinition[] Lights,
        AnimationTimelineSnapshot Timeline,
        Dictionary<string, SpatialOpticalMaterial?> InstanceMaterials,
        string SelectedLightId,
        bool ProjectDirty);

    // BuildInspectorPages hook: call after _sceneEditPage.Content has been created.
    private void BuildSceneOpticsInspectorPanels()
    {
        _sceneLightingPanel.Dock = DockStyle.Top;
        _sceneLightingPanel.Height = _sceneLightingPanel.PreferredPanelHeight;
        _sceneLightingPanel.Visible = false;
        _spatialMaterialPanel.Dock = DockStyle.Top;
        _spatialMaterialPanel.Height = _spatialMaterialPanel.PreferredPanelHeight;
        _spatialMaterialPanel.Visible = false;
        _sceneEditPage.Content.Controls.Add(_sceneLightingPanel);
        _sceneEditPage.Content.Controls.Add(_spatialMaterialPanel);
        ArrangeSceneOpticsInspectorPanels();
    }

    // HookEvents hook: safe to call more than once during workbench reconstruction.
    private void HookSceneOpticsEvents()
    {
        if (_sceneOpticsEventsAttached) return;
        _sceneOpticsEventsAttached = true;

        _sceneLightingPanel.AddRequested += (_, e) => AddSceneLight(e.Kind);
        _sceneLightingPanel.SelectionChanged += (_, e) => SelectSceneOpticsLight(e.LightId);
        _sceneLightingPanel.LightChanged += (_, e) => ApplySceneLightChange(e);
        _sceneLightingPanel.DuplicateRequested += (_, e) => DuplicateSceneLight(e.LightId);
        _sceneLightingPanel.RemoveRequested += (_, e) => RemoveSceneLight(e.LightId);
        _sceneLightingPanel.ResetRequested += (_, e) => ResetSceneLight(e.LightId);
        _sceneLightingPanel.InteractionStarted += (_, _) => BeginSceneOpticsEdit();
        _sceneLightingPanel.InteractionCompleted += (_, _) => CompleteSceneOpticsEdit();
        _sceneLightingPanel.InteractionCanceled += (_, _) => CancelSceneOpticsEdit();

        _spatialMaterialPanel.OverrideChanged += (_, e) => ApplySpatialMaterialOverride(e);
        _spatialMaterialPanel.MaterialChanged += (_, e) => ApplySpatialMaterialChange(e);
        _spatialMaterialPanel.InteractionStarted += (_, _) => BeginSceneOpticsEdit();
        _spatialMaterialPanel.InteractionCompleted += (_, _) => CompleteSceneOpticsEdit();
        _spatialMaterialPanel.InteractionCanceled += (_, _) => CancelSceneOpticsEdit();
    }

    // UpdateInspector, scene/frame selection, language, and hot-reload refresh hook.
    private void RefreshSceneOpticsInspector()
    {
        var sceneWorkspace = IsSceneWorkspaceSelected;
        var scene3D = sceneWorkspace && IsSceneCompositionContext() && IsScene3DView() && !IsSceneMaskEditing();
        var scene = scene3D ? ActiveScene() : null;
        if (!scene3D
            && !_sceneLightingPanel.Visible
            && !_spatialMaterialPanel.Visible
            && string.IsNullOrEmpty(_selectedSceneLightId))
        {
            // Most inspector refreshes happen in the 2D workspace. Once the
            // optics panels are already hidden there is no panel state to
            // rebuild; keep the gizmo cleanup path for workspace transitions.
            if (_stage.SceneLightGizmoVisible) RefreshSceneLightGizmo();
            return;
        }

        var lights = scene?.Lights.Select(light => ToEditorState(scene, light, _frame)).ToArray() ?? [];
        if (scene is null
            || !string.IsNullOrWhiteSpace(_selectedSceneLightId)
                && !lights.Any(light => string.Equals(light.Id, _selectedSceneLightId, StringComparison.Ordinal)))
        {
            _selectedSceneLightId = string.Empty;
        }

        _sceneLightingPanel.Visible = scene3D;
        _sceneLightingPanel.Enabled = scene3D && !_playing;
        _sceneLightingPanel.SetLights(lights, _selectedSceneLightId);
        RefreshSceneLightGizmo();

        var instances = scene3D ? SelectedSceneInstances().ToArray() : [];
        _spatialMaterialPanel.Visible = scene3D && instances.Length > 0;
        _spatialMaterialPanel.SetMaterials(
            instances.Select(instance => instance.OpticalMaterialOverride).ToArray(),
            enabled: scene3D && instances.Length > 0 && !_playing);
        ArrangeSceneOpticsInspectorPanels();
    }

    private void RefreshSceneOpticsText()
    {
        _sceneLightingPanel.RefreshText();
        _spatialMaterialPanel.RefreshText();
    }

    private void ArrangeSceneOpticsInspectorPanels()
    {
        ArrangeSceneInspectorSections();
    }

    private void SelectSceneOpticsLight(string? lightId)
    {
        var scene = ActiveScene();
        var next = scene?.Lights.FirstOrDefault(light =>
            string.Equals(light.Id, lightId, StringComparison.Ordinal));
        if (next is not null) ClearSelection();
        _selectedSceneLightId = next?.Id ?? string.Empty;
        _sceneLightingPanel.SetLights(
            scene?.Lights.Select(light => ToEditorState(scene, light, _frame)).ToArray() ?? [],
            _selectedSceneLightId);
        if (next is not null) _timeline.SelectSingleLayerTarget(next.Id, notifyActiveLayerChanged: false);
        RefreshSceneLightGizmo();
        UpdateSpatialTransformPresentation();
        _stage.Invalidate();
    }

    private void ClearSceneOpticsLightSelection()
    {
        if (string.IsNullOrWhiteSpace(_selectedSceneLightId)
            && _sceneLightGizmoPointerSession is null)
        {
            return;
        }

        if (_sceneLightGizmoPointerSession is not null) CancelSceneLightGizmoPointer();
        _selectedSceneLightId = string.Empty;
        var scene = ActiveScene();
        _sceneLightingPanel.SetLights(
            scene?.Lights.Select(light => ToEditorState(scene, light, _frame)).ToArray() ?? [],
            string.Empty);
        RefreshSceneLightGizmo();
    }

    private bool SynchronizeSceneLightSelectionFromTimeline()
    {
        if (!IsSceneCompositionContext()
            || ActiveScene() is not { } scene
            || string.IsNullOrWhiteSpace(_timeline.ActiveTrackId)
            || _timeline.Context.Timeline.FindTrack(_timeline.ActiveTrackId) is not { } track
            || scene.FindLight(track.TargetId) is null)
        {
            return false;
        }

        SelectSceneOpticsLight(track.TargetId);
        return true;
    }

    private void AddSceneLight(SceneLightKind kind, Vector3? position = null)
    {
        var scene = ActiveScene();
        if (scene is null || !Enum.IsDefined(kind)) return;
        var addedLightId = string.Empty;
        RunDiscreteSceneOpticsEdit(() =>
        {
            if (!_project.TryAddSceneLight(scene.Id, kind, out var light) || light is null) return false;
            if (position is { } requestedPosition
                && light.Settings.Position != requestedPosition
                && !_project.TryUpdateSceneLight(
                    scene.Id,
                    light.Id,
                    light.Name,
                    light.Settings with { Position = requestedPosition }))
            {
                _project.TryRemoveSceneLight(scene.Id, light.Id);
                return false;
            }
            _selectedSceneLightId = light.Id;
            addedLightId = light.Id;
            return true;
        });
        if (string.IsNullOrWhiteSpace(addedLightId)) return;
        _timeline.RefreshTimeline();
        SelectSceneOpticsLight(addedLightId);
    }

    private void DuplicateSceneLight(string lightId)
    {
        var scene = ActiveScene();
        var source = scene?.Lights.FirstOrDefault(light => string.Equals(light.Id, lightId, StringComparison.Ordinal));
        if (scene is null || source is null) return;
        var duplicatedLightId = string.Empty;
        RunDiscreteSceneOpticsEdit(() =>
        {
            if (!_project.TryAddSceneLight(scene.Id, source.Kind, out var copy) || copy is null) return false;
            var name = DuplicateLightName(source.Name);
            if (!_project.TryUpdateSceneLight(scene.Id, copy.Id, name, source.Settings))
            {
                _project.TryRemoveSceneLight(scene.Id, copy.Id);
                return false;
            }
            if (!scene.CopyLightTimeline(source.Id, copy.Id))
            {
                _project.TryRemoveSceneLight(scene.Id, copy.Id);
                return false;
            }
            _selectedSceneLightId = copy.Id;
            duplicatedLightId = copy.Id;
            return true;
        });
        if (string.IsNullOrWhiteSpace(duplicatedLightId)) return;
        _timeline.RefreshTimeline();
        SelectSceneOpticsLight(duplicatedLightId);
    }

    private void RemoveSceneLight(string lightId)
    {
        var scene = ActiveScene();
        if (scene is null) return;
        var index = scene.Lights.ToList().FindIndex(light => string.Equals(light.Id, lightId, StringComparison.Ordinal));
        if (index < 0) return;
        var removed = false;
        RunDiscreteSceneOpticsEdit(() =>
        {
            if (!_project.TryRemoveSceneLight(scene.Id, lightId)) return false;
            removed = true;
            _selectedSceneLightId = scene.Lights.Count == 0
                ? string.Empty
                : scene.Lights[Math.Clamp(index, 0, scene.Lights.Count - 1)].Id;
            return true;
        });
        if (!removed) return;
        _timeline.RefreshTimeline();
        if (string.IsNullOrWhiteSpace(_selectedSceneLightId)) ClearSceneOpticsLightSelection();
        else SelectSceneOpticsLight(_selectedSceneLightId);
    }

    private void ResetSceneLight(string lightId)
    {
        var scene = ActiveScene();
        var light = scene?.Lights.FirstOrDefault(candidate => string.Equals(candidate.Id, lightId, StringComparison.Ordinal));
        if (scene is null || light is null) return;
        var defaults = SceneLightDefinition.CreateDefault(light.Kind);
        if (!TryResolveSceneLightEdit(scene, light, out var editFrame, out var currentSettings)) return;
        RunDiscreteSceneOpticsEdit(() =>
            ApplyResolvedSceneLightEdit(
                scene,
                light,
                light.Name,
                defaults.Settings,
                editFrame,
                currentSettings));
    }

    private void ApplySceneLightChange(SceneLightChangedEventArgs change)
    {
        var scene = ActiveScene();
        var light = scene?.Lights.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, change.State.Id, StringComparison.Ordinal));
        if (scene is null || light is null) return;
        if (!TryResolveSceneLightEdit(scene, light, out var editFrame, out var currentSettings)) return;

        bool Apply()
        {
            var state = change.State;
            var name = NormalizeLightName(state.Name, light.Name);
            var range = light.Kind is SceneLightKind.Point or SceneLightKind.Area
                ? Math.Clamp(state.Range, 0.01f, SceneLightSettings.MaximumRange)
                : 0f;
            var area = light.Kind == SceneLightKind.Area
                ? new Vector2(Math.Max(0.01f, state.AreaWidth), Math.Max(0.01f, state.AreaHeight))
                : Vector2.Zero;
            var editedSettings = new SceneLightSettings(
                state.Enabled,
                OpaqueArgb(state.Color),
                Math.Clamp(state.Intensity, 0f, SceneLightSettings.MaximumIntensity),
                range,
                state.Position,
                state.Rotation,
                area,
                light.Kind != SceneLightKind.Ambient && state.CastShadows,
                Math.Clamp(state.ShadowStrength, 0f, 1f),
                Math.Clamp(state.ShadowSoftness, 0f, 1f));
            var settings = MergeSceneLightSettings(
                currentSettings,
                editedSettings,
                change.Fields);
            return ApplyResolvedSceneLightEdit(
                scene,
                light,
                name,
                settings,
                editFrame,
                currentSettings);
        }

        ApplySceneOpticsEdit(Apply);
    }

    private bool TryResolveSceneLightEdit(
        SceneDefinition scene,
        SceneLightDefinition light,
        out int editFrame,
        out SceneLightSettings currentSettings)
    {
        editFrame = Math.Max(0, _frame);
        currentSettings = default;
        var track = scene.Timeline.FindTrackByTargetId(light.Id);
        if (track is null) return false;

        var exposure = track.EvaluateExposure(editFrame);
        if (!_timeline.AutoKeyframeEnabled)
        {
            var tween = track.EvaluateTween(editFrame);
            if (tween is { } span && editFrame > span.StartFrame && editFrame < span.EndFrame)
            {
                editFrame = span.StartFrame;
            }
            else
            {
                if (!exposure.HasContent || exposure.SourceKeyframeFrame < 0) return false;
                editFrame = exposure.SourceKeyframeFrame;
            }
        }

        currentSettings = scene.TryEvaluateLightSettings(light.Id, editFrame, out var evaluated)
            ? evaluated
            : light.EvaluateSettings(editFrame);
        return true;
    }

    private bool ApplyResolvedSceneLightEdit(
        SceneDefinition scene,
        SceneLightDefinition light,
        string name,
        SceneLightSettings settings,
        int editFrame,
        SceneLightSettings currentSettings)
    {
        var nameChanged = !string.Equals(light.Name, name, StringComparison.Ordinal);
        var settingsChanged = currentSettings != settings;
        if (!nameChanged && !settingsChanged) return false;

        var changed = false;
        if (settingsChanged && _timeline.AutoKeyframeEnabled && editFrame == Math.Max(0, _frame))
        {
            changed |= scene.InsertLightTimelineKeyframe(light.Id, editFrame);
        }
        var deferTweenMaterialization = settingsChanged && _sceneOpticsEditSnapshot is not null;
        var lightChanged = deferTweenMaterialization
            ? _project.TryPreviewSceneLightAtFrame(
                scene.Id,
                light.Id,
                name,
                settings,
                editFrame)
            : _project.TryUpdateSceneLightAtFrame(
                scene.Id,
                light.Id,
                name,
                settings,
                editFrame);
        changed |= lightChanged;
        if (lightChanged && deferTweenMaterialization)
        {
            _pendingSceneLightTweenMaterializations.Add((scene.Id, light.Id, editFrame));
        }
        return changed;
    }

    private static SceneLightSettings MergeSceneLightSettings(
        SceneLightSettings current,
        SceneLightSettings edited,
        SceneLightChangedFields fields) => new(
        fields.HasFlag(SceneLightChangedFields.Enabled) ? edited.Enabled : current.Enabled,
        fields.HasFlag(SceneLightChangedFields.Color) ? edited.ColorArgb : current.ColorArgb,
        fields.HasFlag(SceneLightChangedFields.Intensity) ? edited.Intensity : current.Intensity,
        fields.HasFlag(SceneLightChangedFields.Range) ? edited.Range : current.Range,
        fields.HasFlag(SceneLightChangedFields.Position) ? edited.Position : current.Position,
        fields.HasFlag(SceneLightChangedFields.Rotation) ? edited.RotationDegrees : current.RotationDegrees,
        fields.HasFlag(SceneLightChangedFields.AreaSize) ? edited.AreaSize : current.AreaSize,
        fields.HasFlag(SceneLightChangedFields.CastShadows) ? edited.CastsShadows : current.CastsShadows,
        fields.HasFlag(SceneLightChangedFields.ShadowStrength) ? edited.ShadowStrength : current.ShadowStrength,
        fields.HasFlag(SceneLightChangedFields.ShadowSoftness) ? edited.ShadowSoftness : current.ShadowSoftness);

    private void ApplySpatialMaterialOverride(SpatialMaterialOverrideChangedEventArgs change)
    {
        var scene = ActiveScene();
        var instanceIds = SelectedSceneInstances().Select(instance => instance.Id).ToArray();
        if (scene is null || instanceIds.Length == 0) return;
        RunDiscreteSceneOpticsEdit(() =>
        {
            var changed = false;
            var material = change.UseOverride ? change.Material.Normalize() : (SpatialOpticalMaterial?)null;
            foreach (var instanceId in instanceIds)
            {
                changed |= _project.TrySetSceneInstanceOpticalMaterial(scene.Id, instanceId, material);
            }
            return changed;
        });
    }

    private void ApplySpatialMaterialChange(SpatialMaterialChangedEventArgs change)
    {
        var scene = ActiveScene();
        var instances = SelectedSceneInstances().ToArray();
        if (scene is null || instances.Length == 0) return;
        bool Apply()
        {
            var changed = false;
            foreach (var instance in instances)
            {
                var current = (instance.OpticalMaterialOverride ?? SpatialOpticalMaterial.Default).Normalize();
                var next = MergeMaterial(current, change.Material, change.Fields).Normalize();
                changed |= _project.TrySetSceneInstanceOpticalMaterial(scene.Id, instance.Id, next);
            }
            return changed;
        }
        ApplySceneOpticsEdit(Apply);
    }

    private void ApplySceneOpticsEdit(Func<bool> mutation)
    {
        if (_sceneOpticsEditSnapshot is null)
        {
            RunDiscreteSceneOpticsEdit(mutation);
            return;
        }
        if (!mutation()) return;
        RefreshSceneOpticsRendering(refreshInspector: false);
    }

    private void RunDiscreteSceneOpticsEdit(Func<bool> mutation)
    {
        var before = CaptureSceneOpticsSnapshot();
        if (before is null || !mutation()) return;
        RefreshSceneOpticsRendering(refreshInspector: true);
        var after = CaptureSceneOpticsSnapshot();
        if (after is not null && SceneOpticsChanged(before, after))
            RecordSceneOpticsUndo(before, after);
    }

    private void BeginSceneOpticsEdit()
    {
        if (_sceneOpticsEditSnapshot is not null) return;
        _sceneOpticsSavedSnapshot = null;
        _pendingSceneLightTweenMaterializations.Clear();
        _sceneOpticsEditSnapshot = CaptureSceneOpticsSnapshot();
    }

    private void CompleteSceneOpticsEdit()
    {
        var before = _sceneOpticsEditSnapshot;
        var saved = _sceneOpticsSavedSnapshot;
        _sceneOpticsEditSnapshot = null;
        _sceneOpticsSavedSnapshot = null;
        if (before is null)
        {
            _pendingSceneLightTweenMaterializations.Clear();
            return;
        }
        RefreshPendingSceneLightTweenMaterializations();
        var after = CaptureSceneOpticsSnapshot();
        if (after is not null && SceneOpticsChanged(before, after))
            RecordSceneOpticsUndo(before, after);
        if (saved is not null && after is not null)
            SetProjectDirty(SceneOpticsChanged(saved, after));
        RefreshSceneOpticsInspector();
    }

    private void CancelSceneOpticsEdit()
    {
        var snapshot = _sceneOpticsEditSnapshot;
        var saved = _sceneOpticsSavedSnapshot;
        _sceneOpticsEditSnapshot = null;
        _sceneOpticsSavedSnapshot = null;
        _pendingSceneLightTweenMaterializations.Clear();
        if (snapshot is null) return;
        RestoreSceneOpticsSnapshot(snapshot);
        if (saved is not null) SetProjectDirty(SceneOpticsChanged(saved, snapshot));
    }

    private void CaptureActiveSceneOpticsSaveCheckpoint()
    {
        _sceneOpticsSavedSnapshot = _sceneOpticsEditSnapshot is null
            ? null
            : CaptureSceneOpticsSnapshot();
    }

    private void RefreshPendingSceneLightTweenMaterializations()
    {
        foreach (var pending in _pendingSceneLightTweenMaterializations)
        {
            var scene = _scenes.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, pending.SceneId, StringComparison.Ordinal));
            scene?.RefreshLightTimelineTweenMaterializationsAtEndpoint(
                pending.LightId,
                pending.EndpointFrame);
        }
        _pendingSceneLightTweenMaterializations.Clear();
    }

    private SceneOpticsEditSnapshot? CaptureSceneOpticsSnapshot()
    {
        var scene = ActiveScene();
        if (scene is null) return null;
        return new SceneOpticsEditSnapshot(
            scene.Id,
            scene.Lights.Select(light => light.Clone()).ToArray(),
            scene.Timeline.CreateSnapshot(),
            scene.Instances.ToDictionary(
                instance => instance.Id,
                instance => instance.OpticalMaterialOverride,
                StringComparer.Ordinal),
            _selectedSceneLightId,
            _projectDirty);
    }

    private void RestoreSceneOpticsSnapshot(SceneOpticsEditSnapshot snapshot)
    {
        var scene = _scenes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, snapshot.SceneId, StringComparison.Ordinal));
        if (scene is null) return;
        scene.RestoreLights(snapshot.Lights, lightsWerePresent: true);
        scene.Timeline.RestoreSnapshot(snapshot.Timeline);
        scene.SynchronizeTimelineTracks();
        foreach (var instance in scene.Instances)
        {
            if (snapshot.InstanceMaterials.TryGetValue(instance.Id, out var material))
                instance.OpticalMaterialOverride = material;
        }
        _selectedSceneLightId = snapshot.SelectedLightId;
        SetProjectDirty(snapshot.ProjectDirty);
        InvalidateSceneCompositionCache();
        if (IsSceneCompositionContext()) RebuildSceneComposition(refreshScenePanels: false);
        RefreshSceneOpticsInspector();
        _stage.Invalidate();
    }

    private void RefreshSceneOpticsRendering(bool refreshInspector)
    {
        MarkProjectDirty();
        if (IsSceneCompositionContext()) RebuildSceneComposition(refreshScenePanels: false);
        if (refreshInspector) RefreshSceneOpticsInspector();
        else RefreshSceneLightGizmo();
        _stage.Invalidate();
    }

    private void RefreshSceneLightGizmo()
    {
        var scene3D = IsSceneWorkspaceSelected
            && IsSceneCompositionContext()
            && IsScene3DView()
            && !IsSceneMaskEditing();
        var entries = scene3D && ActiveScene() is { } scene
            ? scene.Lights
                .Select(light => scene.TryEvaluateLightSettings(light.Id, _frame, out var settings)
                    ? new SceneLightGizmoEntry?(
                        new SceneLightGizmoEntry(
                            light.Id,
                            light.Kind,
                            settings,
                            !_playing && string.Equals(
                                light.Id,
                                _selectedSceneLightId,
                                StringComparison.Ordinal)))
                    : null)
                .Where(entry => entry.HasValue)
                .Select(entry => entry!.Value)
                .ToArray()
            : [];
        _stage.SetSceneLightGizmos(entries);
    }

    private SceneLightEditorState ToEditorState(
        SceneDefinition scene,
        SceneLightDefinition light,
        int frame)
    {
        var hasContent = scene.TryEvaluateLightSettings(light.Id, frame, out var evaluated);
        var settings = hasContent ? evaluated : light.EvaluateSettings(frame);
        return new SceneLightEditorState(
            light.Id,
            light.Name,
            light.Kind,
            settings.Enabled,
            Color.FromArgb(settings.ColorArgb),
            settings.Intensity,
            settings.Range,
            settings.Position,
            settings.RotationDegrees,
            settings.AreaSize.X,
            settings.AreaSize.Y,
            settings.CastsShadows,
            settings.ShadowStrength,
            settings.ShadowSoftness,
            hasContent || _timeline.AutoKeyframeEnabled);
    }

    private static SpatialOpticalMaterial MergeMaterial(
        SpatialOpticalMaterial current,
        SpatialOpticalMaterial edited,
        SpatialMaterialChangedFields fields) => new(
        fields.HasFlag(SpatialMaterialChangedFields.Transmission) ? edited.Transmission : current.Transmission,
        fields.HasFlag(SpatialMaterialChangedFields.Reflectivity) ? edited.Reflectivity : current.Reflectivity,
        fields.HasFlag(SpatialMaterialChangedFields.Metallic) ? edited.Metallic : current.Metallic,
        fields.HasFlag(SpatialMaterialChangedFields.Roughness) ? edited.Roughness : current.Roughness,
        fields.HasFlag(SpatialMaterialChangedFields.IndexOfRefraction) ? edited.IndexOfRefraction : current.IndexOfRefraction,
        fields.HasFlag(SpatialMaterialChangedFields.CastsShadows) ? edited.CastsShadows : current.CastsShadows,
        fields.HasFlag(SpatialMaterialChangedFields.ReceivesShadows) ? edited.ReceivesShadows : current.ReceivesShadows);

    private static bool SceneOpticsChanged(SceneOpticsEditSnapshot before, SceneOpticsEditSnapshot after)
    {
        if (!string.Equals(before.SceneId, after.SceneId, StringComparison.Ordinal)
            || before.Lights.Length != after.Lights.Length
            || before.InstanceMaterials.Count != after.InstanceMaterials.Count)
        {
            return true;
        }
        for (var index = 0; index < before.Lights.Length; index++)
        {
            var left = before.Lights[index];
            var right = after.Lights[index];
            if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                || !string.Equals(left.Name, right.Name, StringComparison.Ordinal)
                || left.Kind != right.Kind
                || left.Settings != right.Settings
                || !left.StateKeyframes.SequenceEqual(right.StateKeyframes))
            {
                return true;
            }
        }
        return !SceneOpticsTimelineEquals(before.Timeline, after.Timeline)
            || before.InstanceMaterials.Any(pair =>
                !after.InstanceMaterials.TryGetValue(pair.Key, out var material) || material != pair.Value);
    }

    private static bool SceneOpticsTimelineEquals(
        AnimationTimelineSnapshot left,
        AnimationTimelineSnapshot right)
    {
        if (left.Tracks.Length != right.Tracks.Length) return false;
        for (var trackIndex = 0; trackIndex < left.Tracks.Length; trackIndex++)
        {
            var leftTrack = left.Tracks[trackIndex];
            var rightTrack = right.Tracks[trackIndex];
            if (!string.Equals(leftTrack.Id, rightTrack.Id, StringComparison.Ordinal)
                || !string.Equals(leftTrack.TargetId, rightTrack.TargetId, StringComparison.Ordinal)
                || leftTrack.Duration != rightTrack.Duration
                || !leftTrack.Keyframes.SequenceEqual(rightTrack.Keyframes)
                || leftTrack.Tweens.Length != rightTrack.Tweens.Length)
            {
                return false;
            }

            for (var tweenIndex = 0; tweenIndex < leftTrack.Tweens.Length; tweenIndex++)
            {
                var leftTween = leftTrack.Tweens[tweenIndex];
                var rightTween = rightTrack.Tweens[tweenIndex];
                if (leftTween.StartFrame != rightTween.StartFrame
                    || leftTween.EndFrame != rightTween.EndFrame
                    || leftTween.Kind != rightTween.Kind
                    || !leftTween.CurveAnchors.SequenceEqual(rightTween.CurveAnchors))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static string NormalizeLightName(string? requested, string fallback)
    {
        var value = requested?.Trim() ?? string.Empty;
        if (value.Length == 0) return fallback;
        return value.Length <= SceneLightDefinition.MaximumNameLength
            ? value
            : value[..SceneLightDefinition.MaximumNameLength];
    }

    private static string DuplicateLightName(string sourceName)
    {
        const string suffix = " Copy";
        var maximumBaseLength = SceneLightDefinition.MaximumNameLength - suffix.Length;
        var baseName = sourceName.Length <= maximumBaseLength ? sourceName : sourceName[..maximumBaseLength];
        return baseName + suffix;
    }

    private static int OpaqueArgb(Color color) => Color.FromArgb(255, color.R, color.G, color.B).ToArgb();

    partial void RecordSceneOpticsUndo(SceneOpticsEditSnapshot before, SceneOpticsEditSnapshot after);
}
