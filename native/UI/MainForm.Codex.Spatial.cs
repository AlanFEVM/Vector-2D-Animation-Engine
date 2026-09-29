using System.Numerics;
using System.Text.Json;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool TryExecuteCodexSpatialTool(
        string name,
        JsonElement arguments,
        out object result)
    {
        result = new { };
        switch (name)
        {
            case "scene_lights_get":
                result = CodexSpatialGetLights(arguments);
                return true;
            case "scene_light_create":
                result = CodexSpatialCreateLight(arguments);
                return true;
            case "scene_light_update":
                result = CodexSpatialUpdateLight(arguments);
                return true;
            case "scene_light_remove":
                result = CodexSpatialRemoveLight(arguments);
                return true;
            case "scene_instance_set_transform":
                result = CodexSpatialSetInstanceTransform(arguments);
                return true;
            case "scene_camera_get":
                result = CodexSpatialGetDocumentCamera(arguments);
                return true;
            case "scene_camera_update":
                result = CodexSpatialUpdateDocumentCamera(arguments);
                return true;
            case "scene_view_get":
                result = CodexSpatialGetEditorView(arguments);
                return true;
            case "scene_view_update":
                result = CodexSpatialUpdateEditorView(arguments);
                return true;
            case "scene_set_dimension":
                result = CodexSpatialSetSceneDimension(arguments);
                return true;
            default:
                return false;
        }
    }

    private SceneDefinition CodexSpatialRequireActiveScene(JsonElement arguments)
    {
        var scene = ActiveScene();
        if (scene is null
            || !IsSceneCompositionContext()
            || !ReferenceEquals(_timeline.Context, scene))
        {
            throw new InvalidOperationException(
                "Spatial tools require the active scene composition context and cannot edit a mask or drawing object.");
        }

        if (arguments.TryGetProperty("sceneId", out var sceneId)
            && (sceneId.ValueKind != JsonValueKind.String
                || !string.Equals(scene.Id, sceneId.GetString(), StringComparison.Ordinal)))
        {
            throw new ArgumentException("sceneId must identify the active scene.");
        }

        return scene;
    }

    private static bool CodexSpatialHas(JsonElement arguments, string property) =>
        arguments.TryGetProperty(property, out _);

    private static string CodexSpatialRequiredString(JsonElement arguments, string property)
    {
        if (!arguments.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"{property} is required.");
        }

        return value.GetString()!.Trim();
    }

    private static string? CodexSpatialOptionalString(JsonElement arguments, string property)
    {
        if (!arguments.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"{property} must be a string.");
        return value.GetString()?.Trim();
    }

    private static float CodexSpatialNumber(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number)
            || number < -float.MaxValue
            || number > float.MaxValue)
        {
            throw new ArgumentException($"{property} must be a finite number.");
        }

        return (float)number;
    }

    private static bool CodexSpatialBoolean(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
            throw new ArgumentException($"{property} must be a boolean.");
        return value.GetBoolean();
    }

    private static T CodexSpatialEnum<T>(JsonElement value, string property)
        where T : struct, Enum
    {
        if (value.ValueKind != JsonValueKind.String
            || !Enum.TryParse<T>(value.GetString(), ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            throw new ArgumentException($"{property} is not a valid {typeof(T).Name} value.");
        }

        return parsed;
    }

    private static float CodexSpatialBounded(
        JsonElement value,
        string property,
        float minimum,
        float maximum)
    {
        var number = CodexSpatialNumber(value, property);
        if (number < minimum || number > maximum)
            throw new ArgumentOutOfRangeException(property, number, $"{property} must be between {minimum} and {maximum}.");
        return number;
    }

    private static int CodexSpatialArgb(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var argb))
            throw new ArgumentException($"{property} must be a signed 32-bit ARGB integer.");
        return argb | unchecked((int)0xff000000);
    }

    private static float CodexSpatialNormalizeDegrees(float degrees)
    {
        degrees %= 360f;
        if (degrees > 180f) degrees -= 360f;
        if (degrees <= -180f) degrees += 360f;
        return degrees;
    }

    private static bool CodexSpatialHasLightSettings(JsonElement arguments) =>
        new[]
        {
            "enabled", "colorArgb", "intensity", "range",
            "positionX", "positionY", "positionZ",
            "rotationX", "rotationY", "rotationZ",
            "areaWidth", "areaHeight", "castsShadows",
            "shadowStrength", "shadowSoftness"
        }.Any(property => arguments.TryGetProperty(property, out _));

    private static SceneLightSettings CodexSpatialPatchLightSettings(
        SceneLightKind kind,
        SceneLightSettings current,
        JsonElement arguments)
    {
        var enabled = current.Enabled;
        if (arguments.TryGetProperty("enabled", out var enabledValue))
            enabled = CodexSpatialBoolean(enabledValue, "enabled");

        var colorArgb = current.ColorArgb;
        if (arguments.TryGetProperty("colorArgb", out var colorValue))
            colorArgb = CodexSpatialArgb(colorValue, "colorArgb");

        var intensity = current.Intensity;
        if (arguments.TryGetProperty("intensity", out var intensityValue))
            intensity = CodexSpatialBounded(intensityValue, "intensity", 0f, SceneLightSettings.MaximumIntensity);

        var range = current.Range;
        if (arguments.TryGetProperty("range", out var rangeValue))
        {
            if (kind is not (SceneLightKind.Point or SceneLightKind.Area))
                throw new ArgumentException("range is only valid for point and area lights.");
            range = CodexSpatialBounded(rangeValue, "range", 0.01f, SceneLightSettings.MaximumRange);
        }

        var position = current.Position;
        if (arguments.TryGetProperty("positionX", out var positionX))
            position.X = CodexSpatialBounded(positionX, "positionX", -5_000_000f, 5_000_000f);
        if (arguments.TryGetProperty("positionY", out var positionY))
            position.Y = CodexSpatialBounded(positionY, "positionY", -5_000_000f, 5_000_000f);
        if (arguments.TryGetProperty("positionZ", out var positionZ))
            position.Z = CodexSpatialBounded(positionZ, "positionZ", -5_000_000f, 5_000_000f);

        var rotation = current.RotationDegrees;
        if (arguments.TryGetProperty("rotationX", out var rotationX))
            rotation.X = CodexSpatialNormalizeDegrees(CodexSpatialBounded(rotationX, "rotationX", -360_000f, 360_000f));
        if (arguments.TryGetProperty("rotationY", out var rotationY))
            rotation.Y = CodexSpatialNormalizeDegrees(CodexSpatialBounded(rotationY, "rotationY", -360_000f, 360_000f));
        if (arguments.TryGetProperty("rotationZ", out var rotationZ))
            rotation.Z = CodexSpatialNormalizeDegrees(CodexSpatialBounded(rotationZ, "rotationZ", -360_000f, 360_000f));

        var areaSize = current.AreaSize;
        var hasAreaWidth = arguments.TryGetProperty("areaWidth", out var areaWidth);
        var hasAreaHeight = arguments.TryGetProperty("areaHeight", out var areaHeight);
        if (hasAreaWidth || hasAreaHeight)
        {
            if (kind != SceneLightKind.Area)
                throw new ArgumentException("areaWidth and areaHeight are only valid for area lights.");
            if (hasAreaWidth)
                areaSize.X = CodexSpatialBounded(areaWidth, "areaWidth", 0.01f, SceneLightSettings.MaximumRange);
            if (hasAreaHeight)
                areaSize.Y = CodexSpatialBounded(areaHeight, "areaHeight", 0.01f, SceneLightSettings.MaximumRange);
        }

        var castsShadows = current.CastsShadows;
        if (arguments.TryGetProperty("castsShadows", out var castsShadowsValue))
            castsShadows = CodexSpatialBoolean(castsShadowsValue, "castsShadows");
        if (kind == SceneLightKind.Ambient && castsShadows)
            throw new ArgumentException("Ambient lights cannot cast shadows.");

        var shadowStrength = current.ShadowStrength;
        if (arguments.TryGetProperty("shadowStrength", out var shadowStrengthValue))
            shadowStrength = CodexSpatialBounded(shadowStrengthValue, "shadowStrength", 0f, 1f);

        var shadowSoftness = current.ShadowSoftness;
        if (arguments.TryGetProperty("shadowSoftness", out var shadowSoftnessValue))
            shadowSoftness = CodexSpatialBounded(shadowSoftnessValue, "shadowSoftness", 0f, 1f);

        if (kind is not (SceneLightKind.Point or SceneLightKind.Area)) range = 0;
        if (kind != SceneLightKind.Area) areaSize = Vector2.Zero;
        if (kind == SceneLightKind.Ambient)
        {
            castsShadows = false;
            shadowStrength = 0;
            shadowSoftness = 0;
        }

        var settings = new SceneLightSettings(
            enabled,
            colorArgb,
            intensity,
            range,
            position,
            rotation,
            areaSize,
            castsShadows,
            shadowStrength,
            shadowSoftness);
        if (!settings.IsValid(kind)) throw new ArgumentException("Invalid light settings for this kind.");
        return settings;
    }

    private object CodexSpatialGetLights(JsonElement arguments)
    {
        var scene = CodexSpatialRequireActiveScene(arguments);
        var frame = Math.Max(0, _frame);
        return new
        {
            sceneId = scene.Id,
            frame,
            lightingInitialized = scene.LightingInitialized,
            lights = scene.Lights.Select(light => CodexSpatialLightResult(scene, light, frame)).ToArray()
        };
    }

    // Validate first; retain the existing optics history and roll back unexpected model failures.
    private void CodexSpatialRunLightEdit(Func<bool> mutation)
    {
        var before = CaptureSceneOpticsSnapshot()
            ?? throw new InvalidOperationException("No active scene optics context.");
        var scene = ActiveScene()!;
        var initialized = scene.LightingInitialized;
        StopPlayback();
        try
        {
            RunDiscreteSceneOpticsEdit(mutation);
        }
        catch
        {
            RestoreSceneOpticsSnapshot(before);
            scene.RestoreLights(before.Lights, lightsWerePresent: initialized);
            throw;
        }
    }

    private object CodexSpatialCreateLight(JsonElement arguments)
    {
        var scene = CodexSpatialRequireActiveScene(arguments);
        var kind = CodexSpatialEnum<SceneLightKind>(arguments.GetProperty("kind"), "kind");
        if (scene.Lights.Count >= SceneDefinition.MaximumLights)
            throw new InvalidOperationException("The scene has reached its light limit.");
        var defaults = SceneLightDefinition.CreateDefault(kind);
        var requestedName = CodexSpatialOptionalString(arguments, "name") ?? defaults.Name;
        if (!SceneLightDefinition.TryNormalizeName(requestedName, out var normalizedName))
            throw new ArgumentException("name must contain 1 to 80 non-whitespace characters.");
        var settings = CodexSpatialPatchLightSettings(kind, defaults.EvaluateSettings(0), arguments);
        SceneLightDefinition? createdLight = null;
        CodexSpatialRunLightEdit(() =>
        {
            if (!_project.TryAddSceneLight(scene.Id, kind, out var light) || light is null)
                throw new InvalidOperationException("The active scene could not create this light.");
            // New lights start at frame zero, matching the editor's Add Light command.
            _project.TryUpdateSceneLightAtFrame(scene.Id, light.Id, normalizedName, settings, 0);
            createdLight = light;
            _selectedSceneLightId = light.Id;
            return true;
        });
        _timeline.RefreshTimeline();
        return CodexSpatialLightResult(scene, createdLight!, Math.Max(0, _frame));
    }

    private object CodexSpatialUpdateLight(JsonElement arguments)
    {
        var scene = CodexSpatialRequireActiveScene(arguments);
        var lightId = CodexSpatialRequiredString(arguments, "lightId");
        var light = scene.FindLight(lightId)
            ?? throw new ArgumentException("lightId does not identify a light in the active scene.");
        var requestedName = CodexSpatialOptionalString(arguments, "name");
        if (!SceneLightDefinition.TryNormalizeName(requestedName ?? light.Name, out var name))
            throw new ArgumentException("name must contain 1 to 80 non-whitespace characters.");
        if (!TryResolveSceneLightEdit(scene, light, out var editFrame, out var currentSettings))
            throw new InvalidOperationException("The light has no editable timeline exposure at the current frame.");

        var settings = CodexSpatialHasLightSettings(arguments)
            ? CodexSpatialPatchLightSettings(light.Kind, currentSettings, arguments)
            : currentSettings;
        if (settings == currentSettings && string.Equals(name, light.Name, StringComparison.Ordinal))
            return CodexSpatialLightResult(scene, light, Math.Max(0, _frame));
        var changed = false;
        CodexSpatialRunLightEdit(() =>
        {
            changed = ApplyResolvedSceneLightEdit(
                scene,
                light,
                name,
                settings,
                editFrame,
                currentSettings);
            if (changed) _selectedSceneLightId = light.Id;
            return changed;
        });
        _timeline.RefreshTimeline();
        return CodexSpatialLightResult(scene, light, Math.Max(0, _frame));
    }

    private object CodexSpatialRemoveLight(JsonElement arguments)
    {
        var scene = CodexSpatialRequireActiveScene(arguments);
        var lightId = CodexSpatialRequiredString(arguments, "lightId");
        if (scene.FindLight(lightId) is null)
            throw new ArgumentException("lightId does not identify a light in the active scene.");

        var removed = false;
        CodexSpatialRunLightEdit(() =>
        {
            removed = _project.TryRemoveSceneLight(scene.Id, lightId);
            if (removed && string.Equals(_selectedSceneLightId, lightId, StringComparison.Ordinal))
                _selectedSceneLightId = scene.Lights.FirstOrDefault()?.Id ?? string.Empty;
            return removed;
        });
        if (!removed) throw new InvalidOperationException("The light could not be removed.");
        _timeline.RefreshTimeline();
        return new { sceneId = scene.Id, lightId, removed = true };
    }

    private static object CodexSpatialLightResult(
        SceneDefinition scene,
        SceneLightDefinition light,
        int frame)
    {
        var hasContent = scene.TryEvaluateLightSettings(light.Id, frame, out var evaluated);
        var settings = hasContent ? evaluated : light.EvaluateSettings(frame);
        return new
        {
            id = light.Id,
            name = light.Name,
            kind = light.Kind.ToString(),
            frame,
            hasContent,
            visible = hasContent && settings.Enabled,
            enabled = settings.Enabled,
            colorArgb = settings.ColorArgb,
            intensity = settings.Intensity,
            range = settings.Range,
            positionX = settings.Position.X,
            positionY = settings.Position.Y,
            positionZ = settings.Position.Z,
            rotationX = settings.RotationDegrees.X,
            rotationY = settings.RotationDegrees.Y,
            rotationZ = settings.RotationDegrees.Z,
            areaWidth = settings.AreaSize.X,
            areaHeight = settings.AreaSize.Y,
            castsShadows = settings.CastsShadows,
            shadowStrength = settings.ShadowStrength,
            shadowSoftness = settings.ShadowSoftness,
            keyframes = light.StateKeyframes.Select(keyframe => new
            {
                frame = keyframe.Frame,
                enabled = keyframe.Settings.Enabled,
                colorArgb = keyframe.Settings.ColorArgb,
                intensity = keyframe.Settings.Intensity,
                range = keyframe.Settings.Range,
                positionX = keyframe.Settings.Position.X,
                positionY = keyframe.Settings.Position.Y,
                positionZ = keyframe.Settings.Position.Z,
                rotationX = keyframe.Settings.RotationDegrees.X,
                rotationY = keyframe.Settings.RotationDegrees.Y,
                rotationZ = keyframe.Settings.RotationDegrees.Z
            }).ToArray()
        };
    }

    private object CodexSpatialSetInstanceTransform(JsonElement arguments)
    {
        var scene = CodexSpatialRequireActiveScene(arguments);
        var instanceId = CodexSpatialRequiredString(arguments, "instanceId");
        var instance = scene.Instances.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, instanceId, StringComparison.Ordinal))
            ?? throw new ArgumentException("instanceId does not identify an instance in the active scene.");

        var exposure = scene.Timeline.EvaluateTargetExposure(instance.SceneLayerId, Math.Max(0, _frame));
        if (!exposure.HasContent && !_timeline.AutoKeyframeEnabled)
            throw new InvalidOperationException("The instance has no editable exposure at the current frame; enable Auto Key to create one.");
        var editFrame = ResolveInstanceStateEditFrameForCurrentContext(instance);
        var current = instance.EvaluateState(editFrame);
        var next = current;
        if (arguments.TryGetProperty("x", out var x)) next = next with
        {
            X = CodexSpatialBounded(x, "x", -5_000_000f, 5_000_000f)
        };
        if (arguments.TryGetProperty("y", out var y)) next = next with
        {
            Y = CodexSpatialBounded(y, "y", -5_000_000f, 5_000_000f)
        };
        if (arguments.TryGetProperty("z", out var z)) next = next with
        {
            Z = CodexSpatialBounded(z, "z", -5_000_000f, 5_000_000f)
        };
        if (arguments.TryGetProperty("rotationX", out var rotationX)) next = next with
        {
            RotationX = CodexSpatialNormalizeDegrees(CodexSpatialBounded(rotationX, "rotationX", -360_000f, 360_000f))
        };
        if (arguments.TryGetProperty("rotationY", out var rotationY)) next = next with
        {
            RotationY = CodexSpatialNormalizeDegrees(CodexSpatialBounded(rotationY, "rotationY", -360_000f, 360_000f))
        };
        if (arguments.TryGetProperty("rotationZ", out var rotationZ)) next = next with
        {
            RotationZ = CodexSpatialNormalizeDegrees(CodexSpatialBounded(rotationZ, "rotationZ", -360_000f, 360_000f))
        };
        if (arguments.TryGetProperty("scaleX", out var scaleX)) next = next with
        {
            ScaleX = CodexSpatialBounded(scaleX, "scaleX", 0.01f, 1000f)
        };
        if (arguments.TryGetProperty("scaleY", out var scaleY)) next = next with
        {
            ScaleY = CodexSpatialBounded(scaleY, "scaleY", 0.01f, 1000f)
        };
        if (arguments.TryGetProperty("scaleZ", out var scaleZ)) next = next with
        {
            ScaleZ = CodexSpatialBounded(scaleZ, "scaleZ", 0f, SpatialThicknessLimit)
        };

        if (next.Equals(current))
            return CodexSpatialInstanceResult(scene, instance, editFrame);

        StopPlayback();
        var wasDirty = _projectDirty;
        var timelineSnapshot = scene.Timeline.CreateSnapshot();
        var instanceSnapshot = scene.CreateInstanceSnapshot();
        var timelineSelection = _timeline.CaptureSelectionSnapshot();
        var selectedInstanceIds = _selectedSceneInstanceIds.ToArray();
        var primaryInstanceId = _selectedSceneInstanceId;
        ClearInstanceTimelineEditTracking();
        _sceneInstanceTimelineDirty = false;

        var changed = false;
        try
        {
            editFrame = PrepareInstanceStateTimelineEdit(instance);
            changed = instance.SetStateAtFrame(editFrame, next);
            if (changed)
            {
                _sceneInstanceTimelineDirty = true;
                RefreshEditedInstanceTimelineTweens();
            }
        }
        catch
        {
            scene.RestoreInstanceSnapshot(instanceSnapshot);
            scene.Timeline.RestoreSnapshot(timelineSnapshot);
            scene.SynchronizeTimelineTracks();
            SetProjectDirty(wasDirty);
            InvalidateSceneCompositionCache();
            RebuildSceneComposition();
            throw;
        }
        finally
        {
            _sceneInstanceTimelineDirty = false;
            ClearInstanceTimelineEditTracking();
        }

        if (!changed)
        {
            scene.RestoreInstanceSnapshot(instanceSnapshot);
            scene.Timeline.RestoreSnapshot(timelineSnapshot);
            scene.SynchronizeTimelineTracks();
            throw new InvalidOperationException("The instance transform was not changed.");
        }

        PushSceneTimelineUndo(
            scene,
            timelineSnapshot,
            instanceSnapshot: instanceSnapshot,
            playheadFrame: _frame,
            timelineSelection: timelineSelection);
        _timeline.RefreshTimeline();
        InvalidateSceneCompositionCache();
        RebuildSceneComposition();
        RestoreTimelineInstanceSelection(selectedInstanceIds, primaryInstanceId);
        UpdateInspector();
        _sceneEditorPanel.BindProject(_scenes, _activeSceneIndex, _drawingObjects);
        return CodexSpatialInstanceResult(scene, instance, editFrame);
    }

    private static object CodexSpatialInstanceResult(
        SceneDefinition scene,
        DrawingObjectInstanceDefinition instance,
        int frame)
    {
        var state = instance.EvaluateState(frame);
        return new
        {
            sceneId = scene.Id,
            instanceId = instance.Id,
            frame,
            x = state.X,
            y = state.Y,
            z = state.Z,
            rotationX = state.RotationX,
            rotationY = state.RotationY,
            rotationZ = state.RotationZ,
            scaleX = state.ScaleX,
            scaleY = state.ScaleY,
            scaleZ = state.ScaleZ
        };
    }

    private object CodexSpatialGetDocumentCamera(JsonElement arguments)
    {
        var scene = CodexSpatialRequireActiveScene(arguments);
        return CodexSpatialDocumentCameraResult(scene);
    }

    private object CodexSpatialUpdateDocumentCamera(JsonElement arguments)
    {
        var scene = CodexSpatialRequireActiveScene(arguments);
        var current = scene.Camera;
        var camera = new SceneCameraDefinition
        {
            Name = current.Name, Projection = current.Projection,
            X = current.X, Y = current.Y, Z = current.Z, Depth = current.Depth,
            OrthographicSize = current.OrthographicSize, FieldOfViewDegrees = current.FieldOfViewDegrees
        };
        var changed = false;
        if (arguments.TryGetProperty("name", out var nameValue))
        {
            var name = CodexSpatialOptionalString(arguments, "name");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
                throw new ArgumentException("name must contain 1 to 80 non-whitespace characters.");
            if (!string.Equals(camera.Name, name, StringComparison.Ordinal))
            {
                camera.Name = name;
                changed = true;
            }
        }
        if (arguments.TryGetProperty("projection", out var projectionValue))
        {
            var projection = CodexSpatialEnum<CameraProjection>(projectionValue, "projection");
            if (camera.Projection != projection)
            {
                camera.Projection = projection;
                changed = true;
            }
        }
        if (arguments.TryGetProperty("x", out var x))
        {
            var value = CodexSpatialBounded(x, "x", -5_000_000f, 5_000_000f);
            if (camera.X != value) { camera.X = value; changed = true; }
        }
        if (arguments.TryGetProperty("y", out var y))
        {
            var value = CodexSpatialBounded(y, "y", -5_000_000f, 5_000_000f);
            if (camera.Y != value) { camera.Y = value; changed = true; }
        }
        if (arguments.TryGetProperty("z", out var z))
        {
            var value = CodexSpatialBounded(z, "z", -5_000_000f, 5_000_000f);
            if (camera.Z != value) { camera.Z = value; changed = true; }
        }
        if (arguments.TryGetProperty("depth", out var depth))
        {
            var value = CodexSpatialBounded(depth, "depth", 0.01f, 5_000_000f);
            if (camera.Depth != value) { camera.Depth = value; changed = true; }
        }
        if (arguments.TryGetProperty("orthographicSize", out var orthographicSize))
        {
            var value = CodexSpatialBounded(orthographicSize, "orthographicSize", 0.01f, 5_000_000f);
            if (camera.OrthographicSize != value) { camera.OrthographicSize = value; changed = true; }
        }
        if (arguments.TryGetProperty("fieldOfViewDegrees", out var fieldOfView))
        {
            var value = CodexSpatialBounded(fieldOfView, "fieldOfViewDegrees", 1f, 179f);
            if (camera.FieldOfViewDegrees != value) { camera.FieldOfViewDegrees = value; changed = true; }
        }

        if (changed)
        {
            StopPlayback();
            scene.Camera = camera;
            MarkProjectDirty();
            _stage.ConfigureReferenceView(scene, ActiveSceneViewDimension(), ReferenceCameraMotion.Immediate);
            UpdateSceneDimensionButton();
            RefreshSceneOpticsInspector();
            _stage.Invalidate();
        }
        return CodexSpatialDocumentCameraResult(scene);
    }

    private static object CodexSpatialDocumentCameraResult(SceneDefinition scene) =>
        new
        {
            sceneId = scene.Id,
            name = scene.Camera.Name,
            projection = scene.Camera.Projection.ToString(),
            x = scene.Camera.X,
            y = scene.Camera.Y,
            z = scene.Camera.Z,
            depth = scene.Camera.Depth,
            orthographicSize = scene.Camera.OrthographicSize,
            fieldOfViewDegrees = scene.Camera.FieldOfViewDegrees
        };

    private object CodexSpatialGetEditorView(JsonElement arguments)
    {
        var scene = CodexSpatialRequireActiveScene(arguments);
        return CodexSpatialEditorViewResult(scene);
    }

    private object CodexSpatialUpdateEditorView(JsonElement arguments)
    {
        var scene = CodexSpatialRequireActiveScene(arguments);
        ReferenceViewDirection? direction = arguments.TryGetProperty("viewDirection", out var directionValue)
            ? CodexSpatialEnum<ReferenceViewDirection>(directionValue, "viewDirection")
            : null;
        if ((CodexSpatialHas(arguments, "yaw") || CodexSpatialHas(arguments, "pitch"))
            && ActiveSceneViewDimension() != SceneDimension.ThreeD)
            throw new InvalidOperationException("Free yaw and pitch require a 3D scene view.");
        if (direction.HasValue && (CodexSpatialHas(arguments, "yaw") || CodexSpatialHas(arguments, "pitch")))
            throw new ArgumentException("Use either viewDirection or yaw/pitch in one view update.");

        var current = _stage.CaptureViewState();
        var next = current;
        var changed = false;
        if (arguments.TryGetProperty("yaw", out var yaw))
        {
            next = next with { ReferenceYaw = CodexSpatialBounded(yaw, "yaw", -314f, 314f) };
            changed = true;
        }
        if (arguments.TryGetProperty("pitch", out var pitch))
        {
            next = next with { ReferencePitch = CodexSpatialBounded(pitch, "pitch", -1.5f, 1.5f) };
            changed = true;
        }
        if (arguments.TryGetProperty("distance", out var distance))
        {
            next = next with { ReferenceDistance = CodexSpatialBounded(distance, "distance", 2000f, 80000f) };
            changed = true;
        }
        if (arguments.TryGetProperty("zoomScale", out var zoomScale))
        {
            next = next with { ReferenceZoomScale = CodexSpatialBounded(zoomScale, "zoomScale", 0.25f, 8f) };
            changed = true;
        }
        if (arguments.TryGetProperty("targetX", out var targetX))
        {
            next = next with { ReferenceTargetX = CodexSpatialBounded(targetX, "targetX", -5_000_000f, 5_000_000f) };
            changed = true;
        }
        if (arguments.TryGetProperty("targetY", out var targetY))
        {
            next = next with { ReferenceTargetY = CodexSpatialBounded(targetY, "targetY", -5_000_000f, 5_000_000f) };
            changed = true;
        }
        if (arguments.TryGetProperty("targetZ", out var targetZ))
        {
            next = next with { ReferenceTargetZ = CodexSpatialBounded(targetZ, "targetZ", -5_000_000f, 5_000_000f) };
            changed = true;
        }
        // Apply the preset only after every supplied parameter has passed validation.
        if (direction.HasValue)
        {
            _stage.SetReferenceViewDirection(direction.Value, ReferenceCameraMotion.Immediate);
            var preset = _stage.CaptureViewState();
            next = next with { ReferenceYaw = preset.ReferenceYaw, ReferencePitch = preset.ReferencePitch };
        }
        if (changed) _stage.RestoreViewState(next);
        return CodexSpatialEditorViewResult(scene);
    }

    private object CodexSpatialSetSceneDimension(JsonElement arguments)
    {
        var scene = CodexSpatialRequireActiveScene(arguments);
        var dimension = CodexSpatialEnum<SceneDimension>(
            arguments.GetProperty("dimension"),
            "dimension");
        CodexSpatialSetSceneDimension(scene, dimension);
        return new
        {
            sceneId = scene.Id,
            dimension = ActiveSceneViewDimension().ToString(),
            documentDimension = scene.Dimension.ToString()
        };
    }

    private bool CodexSpatialSetSceneDimension(SceneDefinition scene, SceneDimension dimension)
    {
        var viewChanged = ActiveSceneViewDimension() != dimension;
        var documentChanged = scene.Dimension != dimension;
        if (!viewChanged && !documentChanged) return false;

        FinishPointerInteractionForContextChange();
        StopPlayback();
        _sceneViewDimensions[scene.Id] = dimension;
        if (documentChanged) _project.TrySetSceneDimension(scene.Id, dimension);
        _stage.ConfigureReferenceView(scene, dimension, ReferenceCameraMotion.Immediate);
        if (dimension == SceneDimension.ThreeD && _tool is ToolMode.Transform or ToolMode.Distort)
        {
            _tool = ToolMode.Transform3D;
        }
        else if (dimension == SceneDimension.TwoD && _tool == ToolMode.Transform3D)
        {
            _tool = ToolMode.Transform;
        }
        if (_selectionToolGroup.Contains(_tool)) _selectionToolGroup.ActiveTool = _tool;
        UpdateSceneDimensionButton();
        UpdateSpatialTransformPanelState();
        RefreshSceneOpticsInspector();
        RefreshToolButtons();
        ApplyToolCursor();
        UpdateTransformOverlay();
        _stage.Invalidate();
        return true;
    }

    private object CodexSpatialEditorViewResult(SceneDefinition scene) =>
        new
        {
            sceneId = scene.Id,
            dimension = ActiveSceneViewDimension().ToString(),
            projection = _stage.ReferenceProjection.ToString(),
            effectiveProjection = _stage.EffectiveReferenceProjection.ToString(),
            viewDirection = ActiveSceneViewDimension() == SceneDimension.TwoD
                ? _stage.Reference2DViewDirection.ToString() : null,
            usesReferenceProjection = _stage.UsesReferenceProjection,
            yaw = _stage.ReferenceYaw,
            pitch = _stage.ReferencePitch,
            distance = _stage.ReferenceDistance,
            zoomScale = _stage.ReferenceZoomScale,
            targetX = _stage.ReferenceTargetX,
            targetY = _stage.ReferenceTargetY,
            targetZ = _stage.ReferenceTargetZ,
            cameraX = _stage.CameraX,
            cameraY = _stage.CameraY,
            zoom = _stage.Zoom
        };
}
