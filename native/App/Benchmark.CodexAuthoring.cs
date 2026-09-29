using System.Text.Json;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunCodexAuthoringRegression()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("MCP authoring regression: " + message);
        }

        static JsonElement Arguments(object value)
            => JsonSerializer.SerializeToElement(value, CodexBridgeProtocol.JsonOptions);

        static JsonElement Json(string text)
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }

        static JsonElement Invoke(MainForm form, string name, object? arguments = null)
        {
            var json = arguments is JsonElement element
                ? element
                : Arguments(arguments ?? new { });
            var deadline = Environment.TickCount64 + 5000;
            while (true)
            {
                try
                {
                    var result = form.ExecuteCodexTool(name, json);
                    return JsonSerializer.SerializeToElement(result, CodexBridgeProtocol.JsonOptions);
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.StartsWith("The editor is busy", StringComparison.Ordinal)
                    && Environment.TickCount64 < deadline)
                {
                    // Real desktop input affects MouseButtons even for this off-screen form.
                    Application.DoEvents();
                    Thread.Sleep(25);
                }
            }
        }

        static JsonElement[] Items(JsonElement result, string property)
            => result.GetProperty(property).EnumerateArray().Select(item => item.Clone()).ToArray();

        static string Id(JsonElement item) => item.GetProperty("id").GetString()!;

        static JsonElement FindById(IEnumerable<JsonElement> items, string id, string description)
        {
            var item = items.FirstOrDefault(candidate => string.Equals(Id(candidate), id, StringComparison.Ordinal));
            if (item.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException($"MCP authoring regression: {description} '{id}' was not found.");
            return item;
        }

        static JsonElement FindByName(IEnumerable<JsonElement> items, string name, string description)
        {
            var item = items.FirstOrDefault(candidate => string.Equals(candidate.GetProperty("name").GetString(), name, StringComparison.Ordinal));
            if (item.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException($"MCP authoring regression: {description} '{name}' was not found.");
            return item;
        }

        static string Snapshot(JsonElement result, string property)
            => result.GetProperty(property).GetRawText();

        static void ExpectRejected(MainForm form, string name, JsonElement arguments, string message)
        {
            try
            {
                _ = Invoke(form, name, arguments);
            }
            catch (Exception ex) when ((ex is ArgumentException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
                && !ex.Message.StartsWith("The editor is busy", StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException("MCP authoring regression: accepted " + message + ".");
        }

        static void ExpectRejectedWithoutMutation(
            MainForm form,
            string name,
            JsonElement arguments,
            Func<string> snapshot,
            string message)
        {
            var before = snapshot();
            ExpectRejected(form, name, arguments, message);
            Check(before == snapshot(), message + " partially changed editor state");
        }

        static JsonElement Property(JsonElement item, string name)
            => item.GetProperty(name);

        static double Number(JsonElement item, string name)
            => Property(item, name).GetDouble();

        static void CheckNumber(JsonElement item, string name, double expected, string message)
            => Check(Math.Abs(Number(item, name) - expected) < 0.0001, message);

        var expectedLegacyTools = new[]
        {
            "editor_get_state", "project_get_info", "tools_get", "tool_set_active",
            "timeline_set_frame", "timeline_set_playback", "editor_undo", "settings_get",
            "settings_update", "project_open", "project_save", "project_new"
        };
        var expectedAuthoringTools = new[]
        {
            "workspace_set_active",
            "layers_get", "layer_create", "layer_update", "layer_reorder", "layer_remove", "layer_set_active",
            "symbols_get", "symbol_create", "symbol_duplicate", "symbol_rename", "symbol_remove", "symbol_set_active",
            "scene_create", "scene_set_active", "scene_rename", "scene_remove",
            "scene_instances_get", "scene_instance_create", "scene_instance_remove",
            "scene_set_dimension", "scene_lights_get", "scene_light_create", "scene_light_update", "scene_light_remove",
            "scene_instance_set_transform", "scene_camera_get", "scene_camera_update", "scene_view_get", "scene_view_update"
        };
        var toolNames = CodexBridgeProtocol.Tools.Select(tool => tool.Name).ToArray();
        Check(toolNames.Distinct(StringComparer.Ordinal).Count() == toolNames.Length, "tool names are not unique");
        foreach (var name in expectedLegacyTools)
            Check(toolNames.Contains(name, StringComparer.Ordinal), "legacy tool was removed: " + name);
        foreach (var name in expectedAuthoringTools)
            Check(toolNames.Contains(name, StringComparer.Ordinal), "authoring tool was not registered: " + name);

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var settingsField = typeof(MainForm).GetField("_applicationSettings", flags)
            ?? throw new InvalidOperationException("MCP authoring regression could not find application settings.");
        var dirtyField = typeof(MainForm).GetField("_projectDirty", flags)
            ?? throw new InvalidOperationException("MCP authoring regression could not find project dirty state.");
        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000)
        };
        var originalSettings = (ApplicationSettings)settingsField.GetValue(form)!;
        var originalLanguage = UiLocalization.CurrentLanguage;
        var timelineControl = (TimelineStrip)typeof(MainForm).GetField("_timeline", flags)!.GetValue(form)!;
        // The real UI persists this event to the user's settings. Isolate the test's Auto Key toggles.
        var autoKeyChangedField = typeof(TimelineStrip).GetField("AutoKeyframeChanged", flags)!;
        var autoKeyChangedHandlers = autoKeyChangedField.GetValue(timelineControl);
        autoKeyChangedField.SetValue(timelineControl, null);
        var projectDirectory = Path.Combine(Path.GetTempPath(), "Vector2D-MCP-Authoring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDirectory);

        try
        {
            form.Show();
            Application.DoEvents();
            settingsField.SetValue(form, originalSettings with
            {
                CodexIntegrationEnabled = true,
                CodexIntegrationAllowChanges = true,
                CodexIntegrationPort = 43522,
                CodexIntegrationAuthToken = ""
            });
            dirtyField.SetValue(form, false);

            Invoke(form, "project_new", new { discardUnsaved = true });
            Invoke(form, "workspace_set_active", new { workspace = "BasicDrawing" });

            var symbols = Items(Invoke(form, "symbols_get"), "symbols");
            Check(symbols.Length >= 1, "a new project did not expose its initial symbol");
            var originalSymbolId = Id(symbols[0]);
            Invoke(form, "symbol_set_active", new { symbolId = originalSymbolId });

            var layersResult = Invoke(form, "layers_get");
            var initialLayers = Items(layersResult, "layers");
            Check(initialLayers.Length == 1, "a new symbol did not start with one layer");
            var originalLayerId = Id(initialLayers[0]);
            var layerSnapshot = () => Snapshot(Invoke(form, "layers_get"), "layers");

            Invoke(form, "layer_create", new { name = "Authoring Decimal Layer" });
            var layersAfterCreate = Items(Invoke(form, "layers_get"), "layers");
            var decimalLayerItem = FindByName(layersAfterCreate, "Authoring Decimal Layer", "created layer");
            var decimalLayerId = Id(decimalLayerItem);
            Invoke(form, "layer_update", new
            {
                layerId = decimalLayerId,
                name = "Authoring Updated Layer",
                visible = false,
                locked = false,
                opacity = 0.5
            });
            var updatedLayer = FindById(Items(Invoke(form, "layers_get"), "layers"), decimalLayerId, "updated layer");
            Check(updatedLayer.GetProperty("name").GetString() == "Authoring Updated Layer"
                && !updatedLayer.GetProperty("visible").GetBoolean()
                && !updatedLayer.GetProperty("locked").GetBoolean(),
                "layer scalar fields were not updated");
            CheckNumber(updatedLayer, "opacity", 0.5, "layer opacity did not accept a finite decimal");

            Invoke(form, "layer_create", new { name = "Authoring Reorder Layer" });
            var layersBeforeReorder = Items(Invoke(form, "layers_get"), "layers");
            var reorderLayer = FindByName(layersBeforeReorder, "Authoring Reorder Layer", "reorder layer");
            Invoke(form, "layer_reorder", new { layerId = Id(reorderLayer), index = 0 });
            var reordered = Items(Invoke(form, "layers_get"), "layers");
            Check(Id(reordered[0]) == Id(reorderLayer) && reordered[0].GetProperty("index").GetInt32() == 0,
                "layer reorder did not move the requested layer to index zero");

            Invoke(form, "layer_set_active", new { layerId = originalLayerId });
            Invoke(form, "layer_update", new { layerId = decimalLayerId, locked = true });
            ExpectRejectedWithoutMutation(
                form,
                "layer_update",
                Json("{\"layerId\":\"" + decimalLayerId + "\",\"name\":\"Locked mutation\"}"),
                layerSnapshot,
                "locked layer update");
            Invoke(form, "layer_update", new { layerId = decimalLayerId, locked = false });

            ExpectRejectedWithoutMutation(
                form,
                "layer_update",
                Json("{\"layerId\":\"" + decimalLayerId + "\",\"opacity\":1.5}"),
                layerSnapshot,
                "out-of-range layer opacity");
            ExpectRejectedWithoutMutation(
                form,
                "layer_reorder",
                Json("{\"layerId\":\"" + decimalLayerId + "\",\"index\":0.5}"),
                layerSnapshot,
                "fractional layer index");
            ExpectRejectedWithoutMutation(
                form,
                "layer_reorder",
                Json("{\"layerId\":\"" + decimalLayerId + "\",\"index\":2147483648}"),
                layerSnapshot,
                "overflow layer index");
            ExpectRejectedWithoutMutation(
                form,
                "layer_update",
                Json("{\"layerId\":\"" + decimalLayerId + "\",\"unknown\":true}"),
                layerSnapshot,
                "unknown layer argument");
            ExpectRejectedWithoutMutation(
                form,
                "layer_update",
                Json("{\"layerId\":\"" + decimalLayerId + "\",\"visible\":\"false\"}"),
                layerSnapshot,
                "invalid layer argument type");
            ExpectRejectedWithoutMutation(
                form,
                "layer_update",
                Json("{\"layerId\":\"" + decimalLayerId + "\",\"name\":\"one\",\"name\":\"two\"}"),
                layerSnapshot,
                "duplicate layer argument");
            ExpectRejectedWithoutMutation(
                form,
                "layer_update",
                Arguments(new { layerId = "missing-layer", name = "should not apply" }),
                layerSnapshot,
                "unknown layer id");

            Invoke(form, "layer_remove", new { layerId = Id(reorderLayer) });
            Invoke(form, "layer_remove", new { layerId = decimalLayerId });
            var oneLayer = Items(Invoke(form, "layers_get"), "layers");
            Check(oneLayer.Length == 1 && Id(oneLayer[0]) == originalLayerId, "layer cleanup did not retain the original layer");
            ExpectRejectedWithoutMutation(
                form,
                "layer_remove",
                Arguments(new { layerId = originalLayerId }),
                layerSnapshot,
                "removing the last layer");

            Invoke(form, "symbol_create", new { name = "Authoring Temporary Symbol" });
            symbols = Items(Invoke(form, "symbols_get"), "symbols");
            var temporarySymbol = FindByName(symbols, "Authoring Temporary Symbol", "created symbol");
            var temporarySymbolId = Id(temporarySymbol);
            var symbolIdsBeforeDuplicate = symbols.Select(Id).ToHashSet(StringComparer.Ordinal);
            Invoke(form, "symbol_duplicate", new { symbolId = temporarySymbolId });
            symbols = Items(Invoke(form, "symbols_get"), "symbols");
            var duplicateSymbolId = symbols.Select(Id).First(id => !symbolIdsBeforeDuplicate.Contains(id));
            Invoke(form, "symbol_rename", new { symbolId = duplicateSymbolId, name = "Authoring Duplicate Symbol" });
            Check(FindById(Items(Invoke(form, "symbols_get"), "symbols"), duplicateSymbolId, "renamed duplicate symbol")
                .GetProperty("name").GetString() == "Authoring Duplicate Symbol",
                "symbol rename did not persist");
            Invoke(form, "symbol_remove", new { symbolId = duplicateSymbolId });

            Invoke(form, "workspace_set_active", new { workspace = "SceneEditor" });
            var projectInfo = Invoke(form, "project_get_info");
            var scenes = projectInfo.GetProperty("scenes").EnumerateArray().Select(item => item.Clone()).ToArray();
            Check(scenes.Length >= 1, "a new project did not expose its initial scene");
            var originalSceneId = scenes[0].GetProperty("id").GetString()!;
            Invoke(form, "scene_set_active", new { sceneId = originalSceneId });

            Invoke(form, "scene_create", new { name = "Authoring Temporary Scene" });
            projectInfo = Invoke(form, "project_get_info");
            var temporaryScene = projectInfo.GetProperty("scenes").EnumerateArray()
                .Select(item => item.Clone())
                .Single(item => item.GetProperty("name").GetString() == "Authoring Temporary Scene");
            var temporarySceneId = temporaryScene.GetProperty("id").GetString()!;
            Invoke(form, "scene_set_active", new { sceneId = temporarySceneId });
            Invoke(form, "scene_rename", new { sceneId = temporarySceneId, name = "Authoring Renamed Scene" });
            Check(Invoke(form, "project_get_info").GetProperty("scenes").EnumerateArray()
                .Any(item => item.GetProperty("id").GetString() == temporarySceneId
                    && item.GetProperty("name").GetString() == "Authoring Renamed Scene"),
                "scene rename did not persist");
            Invoke(form, "scene_set_active", new { sceneId = originalSceneId });

            var rootSceneLayerId = Id(Items(Invoke(form, "layers_get"), "layers")[0]);
            var extraSceneLayer = Invoke(form, "layer_create", new { name = "Named Scene Layer" });
            var extraSceneLayerId = extraSceneLayer.GetProperty("layerId").GetString()!;
            Check(FindById(Items(Invoke(form, "layers_get"), "layers"), extraSceneLayerId, "scene layer")
                .GetProperty("name").GetString() == "Named Scene Layer", "scene layer name was ignored");
            Invoke(form, "layer_update", new { layerId = extraSceneLayerId, visible = false, name = "Renamed Scene Layer" });
            Invoke(form, "layer_reorder", new { layerId = extraSceneLayerId, index = 0 });
            Invoke(form, "layer_set_active", new { layerId = rootSceneLayerId });
            Invoke(form, "layer_remove", new { layerId = extraSceneLayerId });
            Invoke(form, "editor_undo");
            Check(Items(Invoke(form, "layers_get"), "layers").Any(layer => Id(layer) == extraSceneLayerId),
                "scene layer removal was not undoable");
            Invoke(form, "layer_remove", new { layerId = extraSceneLayerId });

            var instancesSnapshot = () => Snapshot(Invoke(form, "scene_instances_get"), "instances");
            var lightsSnapshot = () => Snapshot(Invoke(form, "scene_lights_get"), "lights");
            var playbackSnapshot = () =>
            {
                var state = Invoke(form, "editor_get_state");
                return state.GetProperty("playing").GetRawText();
            };
            var initialInstances = Items(Invoke(form, "scene_instances_get"), "instances");
            Invoke(form, "scene_instance_create", new { symbolId = originalSymbolId, x = 12.5, y = -7.25 });
            var createdInstances = Items(Invoke(form, "scene_instances_get"), "instances");
            var createdInstance = createdInstances.Single(instance => !initialInstances.Any(previous => Id(previous) == Id(instance)));
            var createdInstanceId = Id(createdInstance);
            Check(createdInstance.GetProperty("symbolId").GetString() == originalSymbolId
                && Math.Abs(Number(createdInstance, "x") - 12.5) < 0.0001
                && Math.Abs(Number(createdInstance, "y") + 7.25) < 0.0001,
                "scene instance did not preserve symbol reference and initial coordinates");

            Invoke(form, "scene_instance_create", new { symbolId = originalSymbolId, x = 1.25, y = 2.5 });
            var instanceAfterSecondCreate = Items(Invoke(form, "scene_instances_get"), "instances");
            var removableInstance = instanceAfterSecondCreate.Single(instance =>
                Id(instance) != createdInstanceId
                && !initialInstances.Any(previous => Id(previous) == Id(instance)));
            var removableInstanceId = Id(removableInstance);
            Invoke(form, "scene_instance_remove", new { instanceId = removableInstanceId });
            Check(!Items(Invoke(form, "scene_instances_get"), "instances")
                .Any(instance => Id(instance) == removableInstanceId),
                "scene instance remove did not remove the requested instance");

            Invoke(form, "symbol_set_active", new { symbolId = temporarySymbolId });
            Invoke(form, "workspace_set_active", new { workspace = "SceneEditor" });
            Invoke(form, "scene_set_active", new { sceneId = originalSceneId });
            Invoke(form, "scene_instance_create", new { symbolId = temporarySymbolId, x = -18.5, y = 3.25 });
            var instancesWithTemporaryReference = Items(Invoke(form, "scene_instances_get"), "instances");
            Check(instancesWithTemporaryReference.Any(instance =>
                instance.GetProperty("symbolId").GetString() == temporarySymbolId),
                "temporary symbol reference was not created");
            Invoke(form, "symbol_remove", new { symbolId = temporarySymbolId });
            Invoke(form, "workspace_set_active", new { workspace = "SceneEditor" });
            Invoke(form, "scene_set_active", new { sceneId = originalSceneId });
            Check(!Items(Invoke(form, "scene_instances_get"), "instances")
                .Any(instance => instance.GetProperty("symbolId").GetString() == temporarySymbolId),
                "removing a referenced symbol left a dangling scene instance");

            Invoke(form, "workspace_set_active", new { workspace = "BasicDrawing" });
            Invoke(form, "symbol_set_active", new { symbolId = originalSymbolId });
            ExpectRejectedWithoutMutation(
                form,
                "symbol_remove",
                Arguments(new { symbolId = originalSymbolId }),
                () => Snapshot(Invoke(form, "symbols_get"), "symbols"),
                "removing the last symbol");
            Invoke(form, "workspace_set_active", new { workspace = "SceneEditor" });
            Invoke(form, "scene_set_active", new { sceneId = originalSceneId });

            ExpectRejectedWithoutMutation(
                form,
                "scene_instance_set_transform",
                Arguments(new { instanceId = "missing-instance", x = 10.0 }),
                instancesSnapshot,
                "unknown scene instance id");
            ExpectRejectedWithoutMutation(
                form,
                "scene_instance_set_transform",
                Json("{\"instanceId\":\"" + createdInstanceId + "\",\"x\":\"bad\"}"),
                instancesSnapshot,
                "invalid transform value");

            Invoke(form, "scene_set_dimension", new { dimension = "ThreeD" });
            ExpectRejectedWithoutMutation(
                form,
                "scene_instance_set_transform",
                Json("{\"instanceId\":\"" + createdInstanceId + "\",\"rotationX\":1e30}"),
                instancesSnapshot,
                "out-of-range transform rotation");
            var beforeTransform = Invoke(form, "scene_instance_set_transform", new { instanceId = createdInstanceId });
            var transformed = Invoke(form, "scene_instance_set_transform", new
            {
                instanceId = createdInstanceId,
                x = 120.5,
                y = 240.25,
                z = 30.75,
                rotationX = 10.5,
                rotationY = -20.25,
                rotationZ = 45.125,
                scaleX = 1.25,
                scaleY = 0.75,
                scaleZ = 2.0
            });
            CheckNumber(transformed, "x", 120.5, "3D transform x was not applied");
            CheckNumber(transformed, "y", 240.25, "3D transform y was not applied");
            CheckNumber(transformed, "z", 30.75, "3D transform z was not applied");
            CheckNumber(transformed, "rotationX", 10.5, "3D transform rotationX was not applied");
            CheckNumber(transformed, "scaleZ", 2.0, "3D transform scaleZ was not applied");
            Invoke(form, "editor_undo");
            var restoredTransform = Invoke(form, "scene_instance_set_transform", new { instanceId = createdInstanceId });
            foreach (var field in new[] { "x", "y", "z", "rotationX", "rotationY", "rotationZ", "scaleX", "scaleY", "scaleZ" })
                CheckNumber(restoredTransform, field, Number(beforeTransform, field), "editor_undo did not restore instance " + field);

            var testProject = (VectorProject)typeof(MainForm).GetField("_project", flags)!.GetValue(form)!;
            var keyedScene = testProject.Scenes.Single(scene => scene.Id == originalSceneId);
            var keyedInstance = keyedScene.Instances.Single(instance => instance.Id == createdInstanceId);
            keyedScene.Timeline.SetTrackDuration(keyedScene.Timeline.FindTrackByTargetId(keyedInstance.SceneLayerId)!.Id, 6);
            timelineControl.RefreshTimeline();
            var playbackControl = (PlaybackSettingsPanel)typeof(MainForm).GetField("_playbackSettings", flags)!.GetValue(form)!;
            playbackControl.EndFrame = 5;
            var originalAutoKey = timelineControl.AutoKeyframeEnabled;
            timelineControl.AutoKeyframeEnabled = true;
            Invoke(form, "timeline_set_frame", new { frame = 5 });
            Invoke(form, "scene_instance_set_transform", new { instanceId = createdInstanceId, z = 55.5 });
            Invoke(form, "timeline_set_frame", new { frame = 0 });
            CheckNumber(FindById(Items(Invoke(form, "scene_instances_get"), "instances"), createdInstanceId, "original frame"),
                "z", Number(beforeTransform, "z"), "Auto Key changed an earlier instance frame");
            Invoke(form, "timeline_set_frame", new { frame = 5 });
            CheckNumber(FindById(Items(Invoke(form, "scene_instances_get"), "instances"), createdInstanceId, "keyed frame"),
                "z", 55.5, "instance query did not evaluate the current frame");
            Invoke(form, "editor_undo");
            timelineControl.AutoKeyframeEnabled = originalAutoKey;
            Invoke(form, "timeline_set_frame", new { frame = 0 });

            Invoke(form, "scene_light_create", new { kind = "Point", name = "Authoring Point Light" });
            var lightsAfterCreate = Items(Invoke(form, "scene_lights_get"), "lights");
            var pointLight = FindByName(lightsAfterCreate, "Authoring Point Light", "created point light");
            var pointLightId = Id(pointLight);
            var lightBeforeUpdate = pointLight.Clone();
            Invoke(form, "scene_light_update", new
            {
                lightId = pointLightId,
                name = "Authoring Updated Light",
                intensity = 2.25,
                positionX = 40.5,
                positionY = -12.25,
                positionZ = 80.75
            });
            var updatedLight = FindById(Items(Invoke(form, "scene_lights_get"), "lights"), pointLightId, "updated light");
            Check(updatedLight.GetProperty("name").GetString() == "Authoring Updated Light"
                && updatedLight.GetProperty("kind").GetString() == "Point",
                "light identity fields were not updated");
            CheckNumber(updatedLight, "intensity", 2.25, "light intensity was not updated");
            Invoke(form, "editor_undo");
            var restoredLight = FindById(Items(Invoke(form, "scene_lights_get"), "lights"), pointLightId, "restored light");
            Check(restoredLight.GetProperty("name").GetString() == lightBeforeUpdate.GetProperty("name").GetString()
                && Math.Abs(Number(restoredLight, "intensity") - Number(lightBeforeUpdate, "intensity")) < 0.0001,
                "editor_undo did not restore the light update");

            Invoke(form, "scene_light_update", new
            {
                lightId = pointLightId,
                name = "Authoring Saved Light",
                intensity = 1.75,
                positionX = 5.5,
                positionY = 6.25,
                positionZ = 7.75
            });
            var invalidLightSnapshot = lightsSnapshot();
            ExpectRejectedWithoutMutation(
                form,
                "scene_light_update",
                Arguments(new { lightId = "missing-light", intensity = 2.0 }),
                lightsSnapshot,
                "unknown light id");
            ExpectRejectedWithoutMutation(
                form,
                "scene_light_update",
                Json("{\"lightId\":\"" + pointLightId + "\",\"intensity\":\"bad\"}"),
                lightsSnapshot,
                "invalid light value");
            ExpectRejectedWithoutMutation(
                form,
                "scene_light_update",
                Arguments(new { lightId = pointLightId, intensity = -1.0 }),
                lightsSnapshot,
                "out-of-range light intensity");
            Check(invalidLightSnapshot == lightsSnapshot(), "light validation changed state");

            var invalidLightCreateSnapshot = () =>
                lightsSnapshot()
                + "|dirty=" + dirtyField.GetValue(form)
                + "|playback=" + playbackSnapshot();
            Invoke(form, "timeline_set_playback", new { playing = true });
            foreach (var (arguments, description) in new (object Arguments, string Description)[]
            {
                (new { kind = "Point", name = "Invalid Intensity Light", intensity = -1.0 }, "invalid light intensity"),
                (new { kind = "Point", name = "Invalid Range Light", range = 0.0 }, "invalid light range"),
                (new { kind = "Area", name = "Invalid Area Width Light", areaWidth = 0.0, areaHeight = 2.0 }, "invalid area light width"),
                (new { kind = "Area", name = "Invalid Area Height Light", areaWidth = 2.0, areaHeight = 0.0 }, "invalid area light height"),
                (new { kind = "Directional", name = "Incompatible Range Light", range = 10.0 }, "incompatible directional light range"),
                (new { kind = "Point", name = "Incompatible Area Width Light", areaWidth = 2.0 }, "incompatible point light area width")
            })
            {
                ExpectRejectedWithoutMutation(
                    form,
                    "scene_light_create",
                    Arguments(arguments),
                    invalidLightCreateSnapshot,
                    description);
            }
            Invoke(form, "timeline_set_playback", new { playing = false });

            Invoke(form, "scene_light_create", new
            {
                kind = "Area",
                name = "Authoring Area Light",
                range = 150.0,
                areaWidth = 24.5,
                areaHeight = 13.25
            });
            var areaLight = FindByName(
                Items(Invoke(form, "scene_lights_get"), "lights"),
                "Authoring Area Light",
                "created area light");
            CheckNumber(areaLight, "areaWidth", 24.5, "area light width was not applied");
            CheckNumber(areaLight, "areaHeight", 13.25, "area light height was not applied");

            Invoke(form, "scene_light_create", new { kind = "Point", name = "Authoring Removed Light" });
            var removableLight = FindByName(Items(Invoke(form, "scene_lights_get"), "lights"), "Authoring Removed Light", "removable light");
            Invoke(form, "scene_light_remove", new { lightId = Id(removableLight) });
            Check(!Items(Invoke(form, "scene_lights_get"), "lights").Any(light => Id(light) == Id(removableLight)),
                "light remove did not remove the requested light");

            Invoke(form, "scene_remove", new { sceneId = temporarySceneId });
            Invoke(form, "scene_set_active", new { sceneId = originalSceneId });
            ExpectRejectedWithoutMutation(
                form,
                "scene_remove",
                Arguments(new { sceneId = originalSceneId }),
                () => Invoke(form, "project_get_info").GetProperty("scenes").GetRawText(),
                "removing the last scene");

            var cameraSnapshot = () => Invoke(form, "scene_camera_get").GetRawText();
            var cameraMutationSnapshot = () => cameraSnapshot() + "|dirty=" + dirtyField.GetValue(form);
            ExpectRejectedWithoutMutation(
                form,
                "scene_camera_update",
                Arguments(new { name = "Rejected Camera", x = 321.5, depth = 0.0 }),
                cameraMutationSnapshot,
                "invalid camera depth");
            Invoke(form, "scene_camera_update", new
            {
                x = 321.5,
                y = -222.25,
                z = 48.75,
                fieldOfViewDegrees = 72.5
            });
            var savedCamera = cameraSnapshot();

            var viewSnapshot = () => Invoke(form, "scene_view_get").GetRawText();
            var viewMutationSnapshot = () =>
                viewSnapshot()
                + "|camera=" + cameraSnapshot()
                + "|dirty=" + dirtyField.GetValue(form);
            ExpectRejectedWithoutMutation(
                form,
                "scene_view_update",
                Json("{\"viewDirection\":\"Front\",\"yaw\":0.5,\"pitch\":0.25}"),
                viewMutationSnapshot,
                "view direction and yaw/pitch preset conflict");
            ExpectRejectedWithoutMutation(
                form,
                "scene_view_update",
                Arguments(new { distance = 0.0 }),
                viewMutationSnapshot,
                "invalid view distance");
            ExpectRejectedWithoutMutation(
                form,
                "scene_view_update",
                Arguments(new { zoomScale = 0.0 }),
                viewMutationSnapshot,
                "invalid view zoom");
            ExpectRejectedWithoutMutation(
                form,
                "scene_view_update",
                Json("{\"yaw\":1e30}"),
                viewMutationSnapshot,
                "out-of-range view yaw");
            dirtyField.SetValue(form, false);
            var dirtyBeforeViewUpdate = (bool)dirtyField.GetValue(form)!;
            Invoke(form, "scene_view_update", new
            {
                yaw = 0.75,
                pitch = 0.3,
                distance = 24000.0,
                zoomScale = 1.2,
                targetX = 10.0,
                targetY = -20.0,
                targetZ = 30.0
            });
            Check((bool)dirtyField.GetValue(form)! == dirtyBeforeViewUpdate,
                "valid transient view update dirtied the project");

            settingsField.SetValue(form, originalSettings with
            {
                CodexIntegrationEnabled = true,
                CodexIntegrationAllowChanges = false,
                CodexIntegrationPort = 43522,
                CodexIntegrationAuthToken = ""
            });
            foreach (var (name, arguments) in new (string Name, object Arguments)[]
            {
                ("layer_create", new { name = "Read-only layer" }),
                ("symbol_create", new { name = "Read-only symbol" }),
                ("scene_create", new { name = "Read-only scene" }),
                ("scene_light_create", new { kind = "Point", name = "Read-only light" })
            })
            {
                ExpectRejected(form, name, Arguments(arguments), "read-only " + name);
            }
            settingsField.SetValue(form, originalSettings with
            {
                CodexIntegrationEnabled = true,
                CodexIntegrationAllowChanges = true,
                CodexIntegrationPort = 43522,
                CodexIntegrationAuthToken = ""
            });

            Invoke(form, "workspace_set_active", new { workspace = "BasicDrawing" });
            Invoke(form, "symbol_set_active", new { symbolId = originalSymbolId });
            var savedLayers = Snapshot(Invoke(form, "layers_get"), "layers");
            var savedSymbols = Snapshot(Invoke(form, "symbols_get"), "symbols");
            Invoke(form, "workspace_set_active", new { workspace = "SceneEditor" });
            Invoke(form, "scene_set_active", new { sceneId = originalSceneId });
            var savedScenes = Invoke(form, "project_get_info").GetProperty("scenes").GetRawText();
            var savedInstances = instancesSnapshot();
            var savedLights = lightsSnapshot();
            var manifest = Path.Combine(projectDirectory, "Authoring.v2dProject");
            Invoke(form, "project_save", new { path = manifest });
            Check(File.Exists(manifest), "authoring project was not saved to the temporary manifest");
            Invoke(form, "project_new", new { discardUnsaved = true });
            Invoke(form, "project_open", new { path = manifest });
            Invoke(form, "workspace_set_active", new { workspace = "BasicDrawing" });
            Invoke(form, "symbol_set_active", new { symbolId = originalSymbolId });
            Check(savedLayers == Snapshot(Invoke(form, "layers_get"), "layers"), "layer authoring did not survive Save/Load");
            Check(savedSymbols == Snapshot(Invoke(form, "symbols_get"), "symbols"), "symbol authoring did not survive Save/Load");
            Invoke(form, "workspace_set_active", new { workspace = "SceneEditor" });
            Invoke(form, "scene_set_active", new { sceneId = originalSceneId });
            Check(savedScenes == Invoke(form, "project_get_info").GetProperty("scenes").GetRawText(), "scene authoring did not survive Save/Load");
            Check(savedInstances == instancesSnapshot(), "scene instance authoring did not survive Save/Load");
            Check(savedLights == lightsSnapshot(), "light authoring did not survive Save/Load");
            Check(savedCamera == cameraSnapshot(), "scene camera authoring did not survive Save/Load");
            Check(Invoke(form, "scene_view_get").GetProperty("dimension").GetString() == "ThreeD",
                "scene dimension did not survive Save/Load");
        }
        finally
        {
            autoKeyChangedField.SetValue(timelineControl, autoKeyChangedHandlers);
            settingsField.SetValue(form, originalSettings);
            dirtyField.SetValue(form, false);
            UiLocalization.SetLanguage(originalLanguage);
            form.Dispose();
            if (Directory.Exists(projectDirectory)) Directory.Delete(projectDirectory, recursive: true);
        }

        Console.WriteLine("codex_authoring_regression=ok");
    }
}
