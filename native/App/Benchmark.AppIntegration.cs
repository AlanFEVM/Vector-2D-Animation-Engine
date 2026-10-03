using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunUiRefreshStabilityRegression()
    {
        RunUiDiscoverabilityRegression();
        using (var form = new MainForm())
        {
            var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
            var tabs = (FlowLayoutPanel)RequireField(typeof(MainForm), "_drawingObjectTabs").GetValue(form)!;
            var refresh = RequireMethod(typeof(MainForm), "BuildDrawingObjectTabs");
            var activeIndex = RequireField(typeof(MainForm), "_activeDrawingObjectIndex");
            activeIndex.SetValue(form, 0);
            refresh.Invoke(form, null);
            var tab = (Button)tabs.Controls[0];
            var add = tabs.Controls[1];
            var tabHandle = tab.Handle;
            var addHandle = add.Handle;
            var additions = 0;
            var removals = 0;
            tabs.ControlAdded += (_, _) => additions++;
            tabs.ControlRemoved += (_, _) => removals++;
            for (var iteration = 0; iteration < 100; iteration++) refresh.Invoke(form, null);
            AssertTimeline(additions == 0 && removals == 0 && tabs.Controls.Count == 2
                && ReferenceEquals(tabs.Controls[0], tab) && ReferenceEquals(tabs.Controls[1], add)
                && tab.Handle == tabHandle && add.Handle == addHandle && !tab.IsDisposed,
                "Repeated symbol presentation refresh recreated controls or native handles.");

            var second = project.AddDrawingObject("Long renamed symbol");
            activeIndex.SetValue(form, project.DrawingObjects.Count - 1);
            refresh.Invoke(form, null);
            AssertTimeline(ReferenceEquals(tabs.Controls[0], tab) && Equals(tab.Tag, second.Id)
                && tab.Text == second.Name && tab.AccessibleName == second.Name && tab.Width == 132,
                "Reused symbol tab did not follow the active object or its presentation.");
            AssertTimeline(project.TryRemoveDrawingObject(project.DrawingObjects[0].Id, out _),
                "UI refresh fixture could not remove the preceding symbol.");
            activeIndex.SetValue(form, 0);
            refresh.Invoke(form, null);
            RequireMethod(typeof(Control), "OnClick").Invoke(tab, [EventArgs.Empty]);
            AssertTimeline((int)activeIndex.GetValue(form)! == 0 && Equals(tab.Tag, second.Id)
                && additions == 0 && removals == 0,
                "Reused symbol tab retained a stale click index or accumulated controls.");
        }

        using (var panel = new SceneLightingPanel())
        {
            var states = Enumerable.Range(0, 20).Select(index => default(SceneLightEditorState) with
            {
                Id = "light-" + index,
                Name = "Light " + index,
                Kind = SceneLightKind.Point,
                Enabled = true,
                Color = Color.White,
                Intensity = 1,
                Range = 100,
                AreaWidth = 1,
                AreaHeight = 1,
                ParametersEditable = true
            }).ToArray();
            panel.SetLights(states, states[10].Id);
            var list = (ListBox)RequireField(typeof(SceneLightingPanel), "_lights").GetValue(panel)!;
            _ = list.Handle;
            list.TopIndex = 8;
            var top = list.TopIndex;
            var rows = list.Items.Cast<object>().ToArray();
            var selectionChanges = 0;
            var lightChanges = 0;
            panel.SelectionChanged += (_, _) => selectionChanges++;
            panel.LightChanged += (_, _) => lightChanges++;
            for (var iteration = 0; iteration < 100; iteration++)
            {
                states[10] = states[10] with { Intensity = 1 + iteration };
                panel.SetLights(states, states[10].Id);
            }
            AssertTimeline(list.Items.Count == rows.Length && list.SelectedIndex == 10
                && list.TopIndex == top && selectionChanges == 0 && lightChanges == 0
                && rows.Where((row, index) => !ReferenceEquals(row, list.Items[index])).Count() == 0,
                "Light parameter refresh rebuilt rows, moved scrolling or emitted edit/selection events.");
            states[10] = states[10] with { Name = "Renamed light", Enabled = false };
            panel.SetLights(states, states[10].Id);
            AssertTimeline(list.GetItemText(list.Items[10])?.Contains("Renamed light", StringComparison.Ordinal) == true
                && ReferenceEquals(rows[10], list.Items[10]),
                "Incremental light refresh retained stale display text.");
            panel.SetLights(states.Skip(1).Reverse().ToArray(), states[10].Id);
            AssertTimeline(list.Items.Count == 19 && list.SelectedIndex == 9
                && panel.SelectedLightId == states[10].Id && selectionChanges == 0 && lightChanges == 0,
                "Light removal/reordering lost stable-ID selection or generated editor commands.");
            panel.SetLights([], null);
            AssertTimeline(list.Items.Count == 0 && list.SelectedIndex == -1,
                "Incremental light refresh retained removed rows.");
        }
        Console.WriteLine("ui_refresh_stability=ok,symbol_refreshes=100,light_refreshes=100");
    }

    private static void RunUiDiscoverabilityRegression()
    {
        var language = UiLocalization.CurrentLanguage;
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "ui-learning");
        Directory.CreateDirectory(output);
        try
        {
            var settings = new DrawSettings { SnapToGrid = true };
            var settingsChanges = 0;
            settings.Changed += (_, _) => settingsChanges++;
            using var snapping = new DrawSnappingStrip(settings);
            using var workspaces = new WorkspaceTabs();
            UiLocalization.Watch(snapping);
            UiLocalization.Watch(workspaces);
            var grid = (Control)RequireField(typeof(DrawSnappingStrip), "_snapGrid").GetValue(snapping)!;
            var snapHelp = (ToolTip)RequireField(typeof(DrawSnappingStrip), "_toolTip").GetValue(snapping)!;
            var buttons = (Dictionary<WorkspaceView, Button>)RequireField(typeof(WorkspaceTabs), "_buttons").GetValue(workspaces)!;
            var tips = (ToolTip)RequireField(typeof(WorkspaceTabs), "_toolTip").GetValue(workspaces)!;
            var originalButtons = buttons.Values.ToArray();
            foreach (var currentLanguage in new[] { UiLanguage.English, UiLanguage.SimplifiedChinese })
            {
                UiLocalization.SetLanguage(currentLanguage);
                settings.SnapEnabled = false;
                settings.NotifyChanged();
                AssertTimeline((snapHelp.GetToolTip(grid) ?? string.Empty).Contains(UiLocalization.T("Turn on Snap first to use this option."), StringComparison.Ordinal)
                    && grid.AccessibleDescription == snapHelp.GetToolTip(grid),
                    "Snap guidance omitted its prerequisite or accessible equivalent.");
                Capture(snapping, 320, 36, "snap-" + currentLanguage);
                settings.SnapEnabled = true;
                settings.NotifyChanged();
                AssertTimeline(!(snapHelp.GetToolTip(grid) ?? string.Empty).Contains(UiLocalization.T("Turn on Snap first to use this option."), StringComparison.Ordinal)
                    && settings.SnapToGrid,
                    "Snap guidance did not update or changed an individual setting.");
                foreach (var width in new[] { 360, 800 })
                {
                    Capture(workspaces, width, 38, "workspaces-" + currentLanguage + "-" + width);
                    var index = 1;
                    foreach (var button in buttons.Values)
                    {
                        AssertTimeline((tips.GetToolTip(button) ?? string.Empty).Contains("Ctrl+" + index++, StringComparison.Ordinal)
                            && tips.GetToolTip(button) == button.AccessibleDescription,
                            "Workspace help omitted its registered shortcut or accessible description.");
                    }
                }
                AssertTimeline((tips.GetToolTip(buttons[WorkspaceView.BasicDrawing]) ?? string.Empty).Contains(
                    UiLocalization.T("Start here: draw and edit a reusable symbol."), StringComparison.Ordinal),
                    "Workspace beginner guidance was not localized.");
            }
            AssertTimeline(settingsChanges == 4 && buttons.Values.SequenceEqual(originalButtons),
                "Help refresh changed settings or rebuilt workspace controls.");
        }
        finally { UiLocalization.SetLanguage(language); }
        Console.WriteLine("ui_discoverability=ok,languages=2,workspace_widths=360/800");

        void Capture(Control control, int width, int height, string name)
        {
            using var host = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-30000, -30000),
                ClientSize = new Size(width, height)
            };
            host.Controls.Add(control);
            try
            {
                control.Size = new Size(width, height);
                host.Show();
                Application.DoEvents();
                control.PerformLayout();
                using var bitmap = new Bitmap(width, height);
                control.DrawToBitmap(bitmap, control.ClientRectangle);
                bitmap.Save(Path.Combine(output, name + ".png"));
            }
            finally { host.Controls.Remove(control); }
        }
    }

    private static void RunCodexBridgeRegression()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("MCP regression: " + message);
        }
        static System.Text.Json.JsonElement Json(string text)
        {
            using var document = System.Text.Json.JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        static System.Text.Json.JsonElement Data(object value)
            => System.Text.Json.JsonSerializer.SerializeToElement(value, CodexBridgeProtocol.JsonOptions);

        var legacy = System.Text.Json.JsonSerializer.Deserialize<ApplicationSettings>("{}")!;
        Check(!legacy.CodexIntegrationEnabled && !legacy.CodexIntegrationAllowChanges, "legacy settings must keep integration opt-in.");
        var patched = MainForm.PatchCodexSettings(legacy with { CodexIntegrationAuthToken = "test-secret" },
            Json("""{"language":"SimplifiedChinese","timelineFrameWidth":20,"colorTheme":"White"}"""));
        Check(patched.Language == UiLanguage.SimplifiedChinese && patched.TimelineFrameWidth == 20
            && patched.ColorTheme == ApplicationColorTheme.White && patched.CodexIntegrationAuthToken == "test-secret",
            "partial settings updates must preserve unrelated settings.");
        foreach (var invalid in new[]
        {
            """{"timelineFrameWidth":33}""", """{"language":0}""", """{"colorTheme":"Unknown"}""",
            """{"codexIntegrationAllowChanges":true}""", """{"language":"English","language":"SimplifiedChinese"}"""
        })
        {
            var rejected = false;
            try { MainForm.PatchCodexSettings(legacy, Json(invalid)); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "invalid or privileged settings patch was accepted.");
        }

        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var settings = legacy with { CodexIntegrationEnabled = true, CodexIntegrationPort = port, CodexIntegrationAuthToken = "regression-token" };
        var executed = 0;
        using var server = new CodexBridgeServer((name, args, cancellation) =>
        {
            Interlocked.Increment(ref executed);
            return Task.FromResult<object>(new { name, ok = true });
        });
        server.Apply(settings);
        Check(server.IsRunning, "loopback listener failed to start: " + server.Status);
        using var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        System.Net.Http.HttpResponseMessage Send(string body, bool authorized = true, string? origin = null, bool chunked = false)
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, CodexBridgeServer.EndpointFor(port));
            request.Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            if (authorized) request.Headers.TryAddWithoutValidation("Authorization", "Bearer regression-token");
            if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
            if (chunked) request.Headers.TransferEncodingChunked = true;
            return client.SendAsync(request).GetAwaiter().GetResult();
        }
        System.Text.Json.JsonElement Response(System.Net.Http.HttpResponseMessage response)
        {
            using (response)
            {
                Check(response.IsSuccessStatusCode, "unexpected HTTP status: " + response.StatusCode);
                return Json(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
        }
        var initialized = Response(Send("""{"jsonrpc":"2.0","id":"hello","method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"regression","version":"1"}}}"""));
        Check(initialized.GetProperty("id").GetString() == "hello"
            && initialized.GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-11-25", "initialize negotiation or string id failed.");
        var tools = Response(Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));
        Check(tools.GetProperty("result").GetProperty("tools").GetArrayLength() == CodexBridgeProtocol.Tools.Length, "tool discovery is incomplete.");
        using (var denied = Send("""{"jsonrpc":"2.0","id":3,"method":"ping"}""", authorized: false))
            Check(denied.StatusCode == System.Net.HttpStatusCode.Unauthorized, "missing bearer token was accepted.");
        using (var origin = Send("""{"jsonrpc":"2.0","id":3,"method":"ping"}""", origin: "https://example.com"))
            Check(origin.StatusCode == System.Net.HttpStatusCode.Forbidden, "browser origin was accepted.");
        using (var notification = Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""))
            Check(notification.StatusCode == System.Net.HttpStatusCode.Accepted && notification.Content.ReadAsStringAsync().Result.Length == 0, "notification did not return empty 202.");
        using (var notification = Send("""{"jsonrpc":"2.0","method":"tools/call","params":{"name":"project_new"}}"""))
            Check(notification.StatusCode == System.Net.HttpStatusCode.Accepted && executed == 0, "notification executed an editor command.");
        var malformed = Response(Send("{broken"));
        Check(malformed.GetProperty("error").GetProperty("code").GetInt32() == -32700, "malformed JSON did not produce a parse error.");
        var missing = Response(Send("""{"jsonrpc":"2.0","id":4,"method":"unknown"}"""));
        Check(missing.GetProperty("error").GetProperty("code").GetInt32() == -32601, "unknown method did not produce a protocol error.");
        var invalidArguments = Response(Send("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"timeline_set_frame","arguments":{"frame":"bad"}}}"""));
        Check(invalidArguments.GetProperty("result").GetProperty("isError").GetBoolean() && executed == 0, "invalid arguments reached the editor.");
        var result = Response(Send("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"editor_get_state","arguments":{}}}"""));
        Check(!result.GetProperty("result").GetProperty("isError").GetBoolean() && executed == 1
            && result.GetProperty("result").GetProperty("structuredContent").GetProperty("ok").GetBoolean(), "tool result is not structured.");
        var resource = Response(Send("""{"jsonrpc":"2.0","id":7,"method":"resources/read","params":{"uri":"vector2d://editor/state"}}"""));
        Check(resource.GetProperty("result").GetProperty("contents").GetArrayLength() == 1 && executed == 2, "resource read failed.");
        using (var oversized = Send(new string(' ', CodexBridgeServer.MaximumRequestBytes + 1), chunked: true))
            Check(oversized.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge, "chunked request size limit was bypassed.");
        using (var conflict = new CodexBridgeServer((_, _, _) => Task.FromResult<object>(new { })))
        {
            conflict.Apply(settings);
            Check(!conflict.IsRunning && conflict.Status != "Listening", "port conflict falsely reported a running listener.");
        }
        server.Stop();
        Check(!server.IsRunning, "stop left the listener running.");
        server.Apply(settings);
        Check(server.IsRunning, "listener could not restart on its released port.");
        server.Stop();

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var settingsField = typeof(MainForm).GetField("_applicationSettings", flags)!;
        var dirtyField = typeof(MainForm).GetField("_projectDirty", flags)!;
        using var form = new MainForm { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
        var originalSettings = (ApplicationSettings)settingsField.GetValue(form)!;
        var originalLanguage = UiLocalization.CurrentLanguage;
        var projectDirectory = Path.Combine(Path.GetTempPath(), "Vector2D-MCP-" + Guid.NewGuid().ToString("N"));
        try
        {
            form.Show();
            Application.DoEvents();
            settingsField.SetValue(form, originalSettings with { CodexIntegrationEnabled = true, CodexIntegrationAllowChanges = false });
            Check(!Data(form.ExecuteCodexTool("editor_get_state", CodexBridgeProtocol.EmptyArguments)).GetProperty("writeAccess").GetBoolean(), "read-only state reported write access.");
            var rejected = false;
            try { form.ExecuteCodexTool("tool_set_active", Json("""{"tool":"Rectangle"}""")); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "read-only editor accepted a mutation.");
            settingsField.SetValue(form, originalSettings with { CodexIntegrationEnabled = true, CodexIntegrationAllowChanges = true });
            form.ExecuteCodexTool("tool_set_active", Json("""{"tool":"Rectangle"}"""));
            Check(Data(form.ExecuteCodexTool("editor_get_state", CodexBridgeProtocol.EmptyArguments)).GetProperty("activeTool").GetString() == "Rectangle", "tool activation did not reach the real editor.");
            form.ExecuteCodexTool("timeline_set_frame", Json("""{"frame":0}"""));
            Check(Data(form.ExecuteCodexTool("editor_get_state", CodexBridgeProtocol.EmptyArguments)).GetProperty("frame").GetInt32() == 0, "frame command did not reach the real editor.");
            rejected = false;
            try { form.ExecuteCodexTool("timeline_set_frame", Json("""{"frame":2147483647}""")); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "a frame outside the playback range was accepted.");
            dirtyField.SetValue(form, true);
            rejected = false;
            try { form.ExecuteCodexTool("project_new", CodexBridgeProtocol.EmptyArguments); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && (bool)dirtyField.GetValue(form)!, "new project discarded unsaved changes.");
            var manifest = Path.Combine(projectDirectory, "McpTest.v2dProject");
            form.ExecuteCodexTool("project_save", Data(new { path = manifest }));
            Check(File.Exists(manifest) && !(bool)dirtyField.GetValue(form)!, "project save did not persist and clear dirty state.");
            form.ExecuteCodexTool("project_new", CodexBridgeProtocol.EmptyArguments);
            form.ExecuteCodexTool("project_open", Data(new { path = manifest }));
            Check(Data(form.ExecuteCodexTool("project_get_info", CodexBridgeProtocol.EmptyArguments)).GetProperty("path").GetString() == manifest, "saved project could not be opened.");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { form.ExecuteCodexToolAsync("tool_set_active", Json("""{"tool":"Ellipse"}"""), cancellation.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            Check(Data(form.ExecuteCodexTool("editor_get_state", CodexBridgeProtocol.EmptyArguments)).GetProperty("activeTool").GetString() != "Ellipse", "cancelled request mutated the editor.");
            form.Enabled = false;
            rejected = false;
            try { form.ExecuteCodexTool("tool_set_active", Json("""{"tool":"Ellipse"}""")); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "a dialog-disabled editor accepted a mutation.");
        }
        finally
        {
            settingsField.SetValue(form, originalSettings);
            dirtyField.SetValue(form, false);
            UiLocalization.SetLanguage(originalLanguage);
            form.Dispose();
            if (Directory.Exists(projectDirectory)) Directory.Delete(projectDirectory, recursive: true);
        }
        RunCodexAuthoringRegression();
        Console.WriteLine("codex_bridge_regression=ok");
    }

    private static void RunReleaseNotesRegression()
    {
        var entries = ReleaseNotesCatalog.Entries;
        var allEntries = ReleaseNotesCatalog.AllEntries;
        if (allEntries.Count == 0
            || allEntries is not IList<ReleaseNoteEntry> readOnlyAllEntries
            || !readOnlyAllEntries.IsReadOnly
            || entries is not IList<ReleaseNoteEntry> readOnlyVisibleEntries
            || !readOnlyVisibleEntries.IsReadOnly
            || allEntries.Select(entry => entry.Version).Distinct().Count() != allEntries.Count
            || !allEntries.Select(entry => entry.Version).SequenceEqual(
                allEntries.Select(entry => entry.Version).OrderByDescending(version => version))
            || entries.Any(entry => !entry.ShowInApplication)
            || !entries.SequenceEqual(allEntries.Where(entry =>
                entry.ShowInApplication && entry.Version <= ReleaseNotesCatalog.CurrentVersion)))
        {
            throw new InvalidOperationException(
                "The release-notes catalog was empty, mutable, duplicated, unsorted, or did not filter application visibility.");
        }

        var informationalVersion = typeof(Benchmark).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .SingleOrDefault()
            ?.InformationalVersion
            ?.Split('+', 2)[0];
        if (!Version.TryParse(informationalVersion, out var assemblyVersion)
            || ReleaseNotesCatalog.CurrentVersion != assemblyVersion
            || ReleaseNotesCatalog.Current.Version != assemblyVersion)
        {
            throw new InvalidOperationException(
                $"The current release note version {ReleaseNotesCatalog.CurrentVersion} did not match assembly version {informationalVersion ?? "missing"}.");
        }

        foreach (var entry in allEntries)
        {
            if (entry.English.Sections.Count != entry.SimplifiedChinese.Sections.Count
                || entry.English.Sections is not IList<ReleaseNoteSection> englishSections
                || entry.SimplifiedChinese.Sections is not IList<ReleaseNoteSection> chineseSections
                || !englishSections.IsReadOnly
                || !chineseSections.IsReadOnly
                || entry.English.Sections
                    .Zip(entry.SimplifiedChinese.Sections)
                    .Any(pair => pair.First.Items.Count != pair.Second.Items.Count
                        || pair.First.Items.Count == 0
                        || pair.First.Items is not IList<string> englishItems
                        || pair.Second.Items is not IList<string> chineseItems
                        || !englishItems.IsReadOnly
                        || !chineseItems.IsReadOnly))
            {
                throw new InvalidOperationException(
                    $"Release note {entry.Version} did not preserve immutable, structurally aligned English and Chinese content.");
            }
        }

        static IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }

        var originalLanguage = UiLocalization.CurrentLanguage;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.SimplifiedChinese })
            {
                UiLocalization.SetLanguage(language);
                using var panel = new ReleaseNotesPanel();
                panel.CreateControl();
                panel.PerformLayout();

                var controls = Descendants(panel).ToArray();
                var hasExpectedContent = entries.Count == 0
                    ? controls.OfType<Label>().Any(label =>
                        label.Text == UiLocalization.T("No release notes are enabled for this build.", language))
                    : controls.OfType<Label>().Any(label =>
                        label.Text == string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            UiLocalization.T("Version {0}", language),
                            entries[0].Version));
                if (panel.AccessibleRole != AccessibleRole.Document
                    || panel.AccessibleName != UiLocalization.T("Release Notes", language)
                    || !controls.OfType<ThemedScrollPanel>().Any(scroll =>
                        scroll.TabStop && scroll.AccessibleRole == AccessibleRole.Document)
                    || !hasExpectedContent
                    || !controls.OfType<Label>().Any(label => !string.IsNullOrWhiteSpace(label.Text)))
                {
                    throw new InvalidOperationException(
                        $"The {language} release-notes panel did not expose its current version, content, keyboard scrolling, and document accessibility.");
                }

                using var emptyPanel = new ReleaseNotesPanel([], language);
                emptyPanel.CreateControl();
                emptyPanel.PerformLayout();
                if (!Descendants(emptyPanel).OfType<Label>().Any(label =>
                        label.Text == UiLocalization.T("No release notes are enabled for this build.", language)))
                {
                    throw new InvalidOperationException(
                        $"The {language} release-notes panel did not expose the zero-visible-entry state.");
                }
            }
        }
        finally
        {
            UiLocalization.SetLanguage(originalLanguage);
        }

        Console.WriteLine("release_notes_regression=ok");
    }

    private static void RunHotReloadModuleRoutingRegression()
    {
        var pickerSupport = HotReloadModuleResolver.Resolve([typeof(ColorPickerSupport)]);
        var drawingHelpers = HotReloadModuleResolver.Resolve([typeof(UiDrawingHelpers)]);
        var instanceMaterialization = HotReloadModuleResolver.Resolve([typeof(InstanceTimelineMaterialization)]);
        AssertTimeline(pickerSupport.Modules == HotReloadModule.Shell && pickerSupport.RequiresProcessRestart
            && drawingHelpers.Modules == HotReloadModule.Inspector && drawingHelpers.RequiresProcessRestart
            && instanceMaterialization.Modules == HotReloadModule.Engine && instanceMaterialization.RequiresProcessRestart,
            "Extracted helpers lost their owning module or restart policy.");
        var rendering = HotReloadModuleResolver.Resolve([typeof(Direct2DStageRenderer)]);
        var worldGridRendering = HotReloadModuleResolver.Resolve([typeof(WorldGridLayout)]);
        var polarGridRendering = HotReloadModuleResolver.Resolve([typeof(PolarGridLayout)]);
        var referenceRendering = HotReloadModuleResolver.Resolve([
            typeof(ReferenceViewDirection),
            typeof(SpatialTransformMode),
            typeof(SpatialTransformSpace),
            typeof(SpatialGizmoBasis),
            typeof(SceneCompositionMaskClip),
            typeof(Reference3DProjectedContour)]);
        var timeline = HotReloadModuleResolver.Resolve([typeof(TimelineStrip)]);
        var inspector = HotReloadModuleResolver.Resolve([typeof(MaterialEditorPanel)]);
        var spatialInspector = HotReloadModuleResolver.Resolve([
            typeof(SpatialTransformPanel),
            typeof(SpatialTransformValues),
            typeof(SpatialTransformValueGroup),
            typeof(SpatialPivotKind),
            typeof(SpatialPivotKindChangedEventArgs)]);
        var instanceInspector = HotReloadModuleResolver.Resolve([typeof(DrawingObjectInstancePanel)]);
        var themedScroll = HotReloadModuleResolver.Resolve([typeof(ThemedScrollPanel)]);
        var traditionalPicker = HotReloadModuleResolver.Resolve([
            typeof(TraditionalColorPlane),
            typeof(VerticalColorComponentSlider)]);
        var harmonyWheel = HotReloadModuleResolver.Resolve([typeof(HarmonyColorWheel)]);
        var gradientPreset = HotReloadModuleResolver.Resolve([typeof(GradientPresetGrid)]);
        var paletteIcon = HotReloadModuleResolver.Resolve([typeof(SvgIconButton), typeof(SvgIcons)]);
        var paletteStore = HotReloadModuleResolver.Resolve([typeof(MaterialPaletteStore)]);
        var settingsDialog = HotReloadModuleResolver.Resolve([
            typeof(SettingsDialog),
            typeof(ShortcutProfileEditorPanel),
            typeof(ModernDialogForm),
            typeof(ModernMessageDialog),
            typeof(Theme),
            typeof(ApplicationColorTheme),
            typeof(ShortcutProfiles),
            typeof(ShortcutProfileRecord),
            typeof(ToolShortcutMap),
            typeof(UiLocalization)]);
        var releaseNotes = HotReloadModuleResolver.Resolve([
            typeof(ReleaseNotesCatalog),
            typeof(ReleaseNotesDialog),
            typeof(ReleaseNotesPanel)]);
        var referenceWorkspace = HotReloadModuleResolver.Resolve([
            typeof(ReferenceViewPad),
            typeof(ReferenceViewRequestedEventArgs)]);
        var shotDirectorWorkspace = HotReloadModuleResolver.Resolve([
            typeof(ShotDirectorPanel),
            typeof(ShotDirectorState)]);
        var sceneShotEngine = HotReloadModuleResolver.Resolve([
            typeof(SceneShotDefinition),
            typeof(SceneShotRange),
            typeof(SceneShotSnapshot)]);
        var engine = HotReloadModuleResolver.Resolve([
            typeof(VectorScene),
            typeof(DrawingObjectPlaybackMode),
            typeof(InstanceFrameState)]);
        var sceneMaskEngine = HotReloadModuleResolver.Resolve([typeof(SceneLayerKind)]);
        var projectAssetFolder = HotReloadModuleResolver.Resolve([typeof(ProjectAssetFolder)]);
        var projectAssetTags = HotReloadModuleResolver.Resolve([typeof(ProjectAssetTag), typeof(ProjectAssetTagData)]);
        var unknown = HotReloadModuleResolver.Resolve([typeof(Benchmark)]);
        var merged = rendering.Merge(engine).Merge(rendering);
        HotReloadBatch? dispatchedBatch = null;
        var unavailableTargetChecks = 0;
        using var dispatchForm = new Form
        {
            ShowInTaskbar = false,
            Location = new Point(-30_000, -30_000),
            ClientSize = new Size(80, 60)
        };
        dispatchForm.Show();
        Application.DoEvents();
        using (var coordinator = new HotReloadCoordinator(
                   () => dispatchForm,
                   batch => dispatchedBatch = batch,
                   coalesceMilliseconds: 10))
        {
            coordinator.Enqueue(rendering);
            coordinator.Enqueue(engine);
            var timeout = Stopwatch.StartNew();
            while (dispatchedBatch is null && timeout.Elapsed < TimeSpan.FromSeconds(2))
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
        }
        dispatchForm.Close();
        using (var unavailableCoordinator = new HotReloadCoordinator(
                   () =>
                   {
                       Interlocked.Increment(ref unavailableTargetChecks);
                       return null;
                   },
                   _ => throw new InvalidOperationException("An unavailable hot-reload target unexpectedly dispatched."),
                   coalesceMilliseconds: 2))
        {
            unavailableCoordinator.Enqueue(rendering);
            var unavailableTimeout = Stopwatch.StartNew();
            while (Volatile.Read(ref unavailableTargetChecks) < 16
                && unavailableTimeout.Elapsed < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(10);
            }
            var settledChecks = Volatile.Read(ref unavailableTargetChecks);
            Thread.Sleep(80);
            if (settledChecks != 16
                || Volatile.Read(ref unavailableTargetChecks) != settledChecks)
            {
                throw new InvalidOperationException(
                    $"An unavailable hot-reload target did not stop its bounded retry loop: checks={unavailableTargetChecks}.");
            }
        }
        var coordinatorMergedBatch = dispatchedBatch is { Generation: 2 } batch
            && batch.Plan.Modules == (HotReloadModule.Rendering | HotReloadModule.Engine);
        if (rendering.Modules != HotReloadModule.Rendering
            || worldGridRendering.Modules != HotReloadModule.Rendering
            || polarGridRendering.Modules != HotReloadModule.Rendering
            || referenceRendering.Modules != HotReloadModule.Rendering
            || timeline.Modules != HotReloadModule.Timeline
            || inspector.Modules != HotReloadModule.Inspector
            || spatialInspector.Modules != HotReloadModule.Inspector
            || instanceInspector.Modules != HotReloadModule.Inspector
            || themedScroll.Modules != HotReloadModule.Inspector
            || traditionalPicker.Modules != HotReloadModule.Inspector
            || harmonyWheel.Modules != HotReloadModule.Inspector
            || gradientPreset.Modules != HotReloadModule.Inspector
            || paletteIcon.Modules != HotReloadModule.Inspector
            || paletteStore.Modules != HotReloadModule.Inspector
            || settingsDialog.Modules != HotReloadModule.Shell
            || releaseNotes.Modules != HotReloadModule.Shell
            || referenceWorkspace.Modules != HotReloadModule.Workspace
            || shotDirectorWorkspace.Modules != HotReloadModule.Workspace
            || sceneShotEngine.Modules != HotReloadModule.Engine
            || !shotDirectorWorkspace.RequiresProcessRestart
            || !sceneShotEngine.RequiresProcessRestart
            || shotDirectorWorkspace.RequiresWorkbenchRebuild
            || engine.Modules != HotReloadModule.Engine
            || sceneMaskEngine.Modules != HotReloadModule.Engine
            || projectAssetFolder.Modules != HotReloadModule.Engine
            || projectAssetTags.Modules != HotReloadModule.Engine
            || unknown.Modules != HotReloadModule.All
            || merged.Modules != (HotReloadModule.Rendering | HotReloadModule.Engine)
            || merged.UpdatedTypes.Split(',', StringSplitOptions.RemoveEmptyEntries).Length != 4
            || !coordinatorMergedBatch
            || !rendering.RequiresProcessRestart
            || !timeline.RequiresProcessRestart
            || !inspector.RequiresProcessRestart
            || !spatialInspector.RequiresProcessRestart
            || !instanceInspector.RequiresProcessRestart
            || !referenceWorkspace.RequiresProcessRestart
            || !engine.RequiresProcessRestart
            || !sceneMaskEngine.RequiresProcessRestart
            || !projectAssetFolder.RequiresProcessRestart
            || !projectAssetTags.RequiresProcessRestart
            || !unknown.RequiresProcessRestart
            || worldGridRendering.RequiresProcessRestart
            || polarGridRendering.RequiresProcessRestart
            || referenceRendering.RequiresProcessRestart
            || rendering.RequiresWorkbenchRebuild
            || worldGridRendering.RequiresWorkbenchRebuild
            || polarGridRendering.RequiresWorkbenchRebuild
            || timeline.RequiresWorkbenchRebuild
            || inspector.RequiresWorkbenchRebuild
            || spatialInspector.RequiresWorkbenchRebuild
            || instanceInspector.RequiresWorkbenchRebuild
            || themedScroll.RequiresWorkbenchRebuild
            || traditionalPicker.RequiresWorkbenchRebuild
            || harmonyWheel.RequiresWorkbenchRebuild
            || gradientPreset.RequiresWorkbenchRebuild
            || paletteIcon.RequiresWorkbenchRebuild
            || paletteStore.RequiresWorkbenchRebuild
            || settingsDialog.RequiresWorkbenchRebuild
            || releaseNotes.RequiresWorkbenchRebuild
            || referenceWorkspace.RequiresWorkbenchRebuild
            || engine.RequiresWorkbenchRebuild
            || sceneMaskEngine.RequiresWorkbenchRebuild
            || projectAssetFolder.RequiresWorkbenchRebuild
            || projectAssetTags.RequiresWorkbenchRebuild)
        {
            throw new InvalidOperationException(
                    $"Module hot reload routing was not scoped: rendering={rendering.Modules}/{rendering.RequiresWorkbenchRebuild}, worldGrid={worldGridRendering.Modules}/{worldGridRendering.RequiresWorkbenchRebuild}, polarGrid={polarGridRendering.Modules}/{polarGridRendering.RequiresWorkbenchRebuild}, referenceRendering={referenceRendering.Modules}/{referenceRendering.RequiresWorkbenchRebuild}, timeline={timeline.Modules}/{timeline.RequiresWorkbenchRebuild}, inspector={inspector.Modules}/{inspector.RequiresWorkbenchRebuild}, spatialInspector={spatialInspector.Modules}/{spatialInspector.RequiresWorkbenchRebuild}, instanceInspector={instanceInspector.Modules}/{instanceInspector.RequiresWorkbenchRebuild}, themedScroll={themedScroll.Modules}/{themedScroll.RequiresWorkbenchRebuild}, traditionalPicker={traditionalPicker.Modules}/{traditionalPicker.RequiresWorkbenchRebuild}, harmonyWheel={harmonyWheel.Modules}/{harmonyWheel.RequiresWorkbenchRebuild}, gradientPreset={gradientPreset.Modules}/{gradientPreset.RequiresWorkbenchRebuild}, paletteIcon={paletteIcon.Modules}/{paletteIcon.RequiresWorkbenchRebuild}, paletteStore={paletteStore.Modules}/{paletteStore.RequiresWorkbenchRebuild}, settingsDialog={settingsDialog.Modules}/{settingsDialog.RequiresWorkbenchRebuild}, releaseNotes={releaseNotes.Modules}/{releaseNotes.RequiresWorkbenchRebuild}, referenceWorkspace={referenceWorkspace.Modules}/{referenceWorkspace.RequiresWorkbenchRebuild}, shotDirector={shotDirectorWorkspace.Modules}/{shotDirectorWorkspace.RequiresWorkbenchRebuild}/{shotDirectorWorkspace.RequiresProcessRestart}, sceneShotEngine={sceneShotEngine.Modules}/{sceneShotEngine.RequiresProcessRestart}, engine={engine.Modules}/{engine.RequiresWorkbenchRebuild}, sceneMaskEngine={sceneMaskEngine.Modules}/{sceneMaskEngine.RequiresWorkbenchRebuild}, projectAssetFolder={projectAssetFolder.Modules}/{projectAssetFolder.RequiresWorkbenchRebuild}, projectAssetTags={projectAssetTags.Modules}/{projectAssetTags.RequiresWorkbenchRebuild}, unknown={unknown.Modules}, merged={merged.Modules}/{merged.UpdatedTypes}, coordinator={dispatchedBatch}.");
        }

        Console.WriteLine("module_reload_routing_regression=ok");
    }

    /// <summary>
    /// Crash capture must survive the situations that make a crash hard to diagnose: nested
    /// exceptions, aggregate exceptions, non-Exception payloads, and repeated captures.
    /// </summary>
    private static void RunCrashDiagnosticsRegression()
    {
        var inner = new InvalidOperationException("inner cause");
        var middle = new ArgumentException("middle cause", inner);
        var outer = new ApplicationException("outer cause", middle);
        var description = AppLog.DescribeException(outer);
        AssertTimeline(
            description.Contains("outer cause", StringComparison.Ordinal)
            && description.Contains("middle cause", StringComparison.Ordinal)
            && description.Contains("inner cause", StringComparison.Ordinal)
            && description.Contains(nameof(InvalidOperationException), StringComparison.Ordinal),
            $"The exception description dropped part of the cause chain: {description}");

        var aggregate = new AggregateException(
            "aggregate cause",
            new InvalidOperationException("first"),
            new TaskCanceledException("second"));
        var aggregateDescription = AppLog.DescribeException(aggregate);
        AssertTimeline(
            aggregateDescription.Contains("first", StringComparison.Ordinal)
            && aggregateDescription.Contains("second", StringComparison.Ordinal),
            $"The exception description dropped aggregate members: {aggregateDescription}");

        // A crash must produce a report on disk that can be read after the process is gone.
        var reportPath = CrashReporter.Capture("Regression crash capture", outer, isTerminating: false);
        AssertTimeline(
            !string.IsNullOrEmpty(reportPath) && File.Exists(reportPath),
            "The crash reporter did not write a report file.");
        var report = File.ReadAllText(reportPath);
        AssertTimeline(
            report.Contains("Regression crash capture", StringComparison.Ordinal)
            && report.Contains("inner cause", StringComparison.Ordinal)
            && report.Contains("-- Recent user actions", StringComparison.Ordinal)
            && report.Contains("-- Modules --", StringComparison.Ordinal)
            && report.Contains(Environment.ProcessId.ToString(), StringComparison.Ordinal),
            "The crash report is missing forensic context.");

        // Repeated captures must stay safe and must not corrupt or lose the first report.
        var secondPath = CrashReporter.Capture("Secondary crash capture", new InvalidOperationException("secondary"), isTerminating: true);
        AssertTimeline(
            string.Equals(secondPath, reportPath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(reportPath),
            "A repeated crash capture replaced or lost the authoritative report.");

        // Breadcrumbs are what make a report actionable, so the ring must stay bounded and ordered.
        SessionBreadcrumbs.Clear();
        AssertTimeline(SessionBreadcrumbs.Count == 0, "Session breadcrumbs did not clear.");
        for (var index = 0; index < 200; index++) SessionBreadcrumbs.Record("Regression", $"action {index}");
        AssertTimeline(
            SessionBreadcrumbs.Count is > 0 and <= 120,
            $"Session breadcrumbs were not bounded: {SessionBreadcrumbs.Count}.");
        var formatted = SessionBreadcrumbs.Format();
        AssertTimeline(
            formatted.Contains("action 199", StringComparison.Ordinal)
            && !formatted.Contains("action 0\n", StringComparison.Ordinal),
            "Session breadcrumbs did not retain the most recent actions.");
        SessionBreadcrumbs.Clear();

        Console.WriteLine($"crash_diagnostics_regression=ok report_bytes={new FileInfo(reportPath).Length}");
    }

}
