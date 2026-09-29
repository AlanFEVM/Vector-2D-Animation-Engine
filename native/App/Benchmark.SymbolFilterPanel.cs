namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunSymbolFiltersPanelRegression()
    {
        using var panel = new SymbolFiltersPanel();
        var blur = (ModernToggleSwitch)RequireField(typeof(SymbolFiltersPanel), "_blur").GetValue(panel)!;
        var glow = (ModernToggleSwitch)RequireField(typeof(SymbolFiltersPanel), "_glow").GetValue(panel)!;
        var shadow = (ModernToggleSwitch)RequireField(typeof(SymbolFiltersPanel), "_shadow").GetValue(panel)!;
        var glowStrength = panel.Controls.OfType<ModernNumericUpDown>().Single(control =>
            control.AccessibleName == UiLocalization.T("Glow intensity"));
        var shadowDistance = panel.Controls.OfType<ModernNumericUpDown>().Single(control =>
            control.AccessibleName == UiLocalization.T("Shadow distance"));
        var begin = RequireMethod(typeof(ModernNumericUpDown), "BeginStepperInteraction");
        var step = RequireMethod(typeof(ModernNumericUpDown), "StepValue", [typeof(int), typeof(decimal?)]);
        var end = RequireMethod(typeof(ModernNumericUpDown), "EndStepperInteraction", [typeof(bool)]);
        SymbolFilters[] selected = [default];
        SymbolFilters[] snapshot = [];
        var events = new List<string>();
        Func<SymbolFilters, SymbolFilters>? lastPatch = null;
        panel.InteractionStarted += (_, _) =>
        {
            snapshot = selected.ToArray();
            events.Add("start");
        };
        panel.FiltersChanged += (_, change) =>
        {
            lastPatch = change.Update;
            selected = selected.Select(change.Update).ToArray();
            panel.SetFilters(selected[0], selected.Any(item => item != selected[0]));
            events.Add("change");
        };
        panel.InteractionCompleted += (_, _) => events.Add("complete");
        panel.InteractionCanceled += (_, _) =>
        {
            selected = snapshot.ToArray();
            panel.SetFilters(selected[0], selected.Any(item => item != selected[0]));
            events.Add("cancel");
        };

        panel.SetFilters(default);
        AssertTimeline(events.Count == 0
            && panel.Controls.OfType<ModernNumericUpDown>().All(control => !control.Enabled)
            && panel.Controls.OfType<ColorTargetButton>().All(control => !control.Enabled),
            "Binding disabled symbol filters emitted edits or left their parameter controls enabled.");
        blur.Checked = true;
        glow.Checked = true;
        shadow.Checked = true;
        AssertTimeline(selected[0] == new SymbolFilters
        {
            Blur = SymbolBlurFilter.Default,
            Glow = SymbolGlowFilter.Default,
            Shadow = SymbolShadowFilter.Default
        } && events.SequenceEqual(["start", "change", "complete",
            "start", "change", "complete", "start", "change", "complete"]),
            "The symbol filter toggles did not coexist or initialize their independent defaults.");

        shadowDistance.Value = 43.5m;
        var enabledState = selected[0];
        shadow.Checked = false;
        AssertTimeline(selected[0] == enabledState with { Shadow = enabledState.Shadow with { Enabled = false } }
            && !shadowDistance.Enabled && glowStrength.Enabled,
            "Disabling drop shadow altered its parameters or another enabled effect.");
        shadow.Checked = true;
        AssertTimeline(selected[0] == enabledState && shadowDistance.Value == 43.5m,
            "Re-enabling drop shadow reset previously edited parameters.");

        var first = enabledState with
        {
            Blur = enabledState.Blur with { BlurX = 11 },
            Glow = enabledState.Glow with { BlurY = 17, Strength = 1.4f, ColorArgb = Color.CornflowerBlue.ToArgb() }
        };
        var second = enabledState with
        {
            Blur = enabledState.Blur with { Enabled = false, BlurY = 29 },
            Glow = enabledState.Glow with { BlurX = 31, Strength = 6.2f, Opacity = 0.35f, ColorArgb = Color.OrangeRed.ToArgb() },
            Shadow = enabledState.Shadow with { Distance = 88, Angle = -120 }
        };
        selected = [first, second];
        events.Clear();
        panel.SetFilters(first, mixed: true);
        AssertTimeline(events.Count == 0, "Refreshing a mixed filter selection caused a model edit.");
        glowStrength.Value = 3.2m;
        AssertTimeline(selected[0] == first with { Glow = first.Glow with { Strength = 3.2f } }
            && selected[1] == second with { Glow = second.Glow with { Strength = 3.2f } }
            && events.SequenceEqual(["start", "change", "complete"]),
            "A mixed selection numeric edit replaced unrelated filter parameters or failed to complete one transaction.");
        var capturedPatch = lastPatch
            ?? throw new InvalidOperationException("Symbol filter numeric editing emitted no patch.");
        panel.SetFilters(first);
        AssertTimeline(capturedPatch(second) == second with { Glow = second.Glow with { Strength = 3.2f } },
            "A symbol filter patch captured live control state instead of the edited value.");
        panel.SetFilters(selected[0], mixed: true);

        events.Clear();
        var continuousInitial = selected.ToArray();
        begin.Invoke(glowStrength, null);
        step.Invoke(glowStrength, [1, null]);
        step.Invoke(glowStrength, [1, null]);
        end.Invoke(glowStrength, [false]);
        AssertTimeline(events.SequenceEqual(["start", "change", "change", "complete"])
            && selected[0] == continuousInitial[0] with { Glow = continuousInitial[0].Glow with { Strength = 3.4f } }
            && selected[1] == continuousInitial[1] with { Glow = continuousInitial[1].Glow with { Strength = 3.4f } },
            "A continuous symbol filter edit emitted multiple completed transactions or lost mixed-selection isolation.");

        events.Clear();
        var cancelInitial = selected.ToArray();
        begin.Invoke(glowStrength, null);
        step.Invoke(glowStrength, [1, null]);
        step.Invoke(glowStrength, [1, null]);
        end.Invoke(glowStrength, [true]);
        AssertTimeline(selected.SequenceEqual(cancelInitial) && glowStrength.Value == 3.4m
            && events.Count(item => item == "start") == 1
            && events.Count(item => item == "cancel") == 1 && !events.Contains("complete"),
            "Canceling a symbol filter gesture committed the transaction or failed to restore every selected value.");

        events.Clear();
        begin.Invoke(glowStrength, null);
        step.Invoke(glowStrength, [1, null]);
        panel.Enabled = false;
        AssertTimeline(selected.SequenceEqual(cancelInitial)
            && events.Count(item => item == "cancel") == 1 && !events.Contains("complete"),
            "Disabling the symbol filter panel left a numeric session active or committed its preview.");
        panel.Enabled = true;
        events.Clear();

        var selector = (ComboBox)RequireField(typeof(SymbolFiltersPanel), "_selector").GetValue(panel)!;
        var add = (Button)RequireField(typeof(SymbolFiltersPanel), "_add").GetValue(panel)!;
        var remove = (Button)RequireField(typeof(SymbolFiltersPanel), "_remove").GetValue(panel)!;
        var click = typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        for (int kind = 3; kind < 6; kind++)
        {
            selector.SelectedIndex = kind;
            var before = selected.ToArray();
            click.Invoke(add, [EventArgs.Empty]);
            var added = selected[0];
            AssertTimeline(added.HasEdgeEffects && added.IsValid, "Adding an edge effect failed.");
            click.Invoke(remove, [EventArgs.Empty]);
            AssertTimeline(!selected[0].HasEdgeEffects, "Removing an edge effect left it enabled.");
            click.Invoke(add, [EventArgs.Empty]);
            AssertTimeline(selected[0] == added, "Re-adding an edge effect reset its parameters.");
            selected = before;
            panel.SetFilters(selected[0], true);
        }
        selector.SelectedIndex = 0;
        events.Clear();
        foreach (var kind in Enumerable.Range(0, 6))
        foreach (var width in new[] { 248, 296 })
        {
            selector.SelectedIndex = kind;
            panel.Size = new Size(width, panel.PreferredPanelHeight);
            panel.PerformLayout();
            var children = panel.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
            AssertTimeline(children.All(control => control.Width > 0 && control.Height > 0
                && panel.ClientRectangle.Contains(control.Bounds)),
                $"The symbol filter inspector overflowed its {width}px layout.");
            for (var i = 0; i < children.Length; i++)
            for (var j = i + 1; j < children.Length; j++)
                AssertTimeline(!children[i].Bounds.IntersectsWith(children[j].Bounds),
                    $"The symbol filter inspector overlapped controls at {width}px width.");
            var editors = children.Where(control => control is ModernNumericUpDown or ModernToggleSwitch or ColorTargetButton).ToArray();
            AssertTimeline(editors.Length > 0 && editors.All(control => !string.IsNullOrWhiteSpace(control.AccessibleName)),
                "Symbol filter editors were missing controls or accessible names.");
        }
        AssertTimeline(events.Count == 0, "Resizing the symbol filter inspector emitted model edits.");

        begin.Invoke(glowStrength, null);
        step.Invoke(glowStrength, [1, null]);
        panel.Dispose();
        AssertTimeline(selected.SequenceEqual(cancelInitial)
            && events.Count(item => item == "cancel") == 1 && !events.Contains("complete"),
            "Disposing the symbol filter panel did not cancel an active transaction.");
        Console.WriteLine("symbol_filters_panel=ok");
        RunSymbolFiltersInspectorUndoRegression();
    }

    private static void RunSymbolFiltersInspectorUndoRegression()
    {
        using var form = new MainForm
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000), Size = new Size(1280, 800)
        };
        var project = (VectorProject)RequireField(typeof(MainForm), "_project").GetValue(form)!;
        var tabs = (WorkspaceTabs)RequireField(typeof(MainForm), "_workspaceTabs").GetValue(form)!;
        var timeline = (TimelineStrip)RequireField(typeof(MainForm), "_timeline").GetValue(form)!;
        var inspector = (DrawingObjectInstancePanel)RequireField(typeof(MainForm), "_drawingObjectInstancePanel").GetValue(form)!;
        var undoStack = RequireField(typeof(MainForm), "_sceneTimelineUndoStack").GetValue(form)!;
        var undo = RequireMethod(typeof(MainForm), "UndoLastEdit");
        var select = RequireMethod(typeof(MainForm), "SetSceneInstanceSelection",
            [typeof(DrawingObjectInstanceDefinition), typeof(bool)]);
        var updateInspector = RequireMethod(typeof(MainForm), "UpdateInspector");
        var begin = RequireMethod(typeof(ModernNumericUpDown), "BeginStepperInteraction");
        var step = RequireMethod(typeof(ModernNumericUpDown), "StepValue", [typeof(int), typeof(decimal?)]);
        var end = RequireMethod(typeof(ModernNumericUpDown), "EndStepperInteraction", [typeof(bool)]);
        var originalAutoKey = timeline.AutoKeyframeEnabled;
        try
        {
            var source = project.AddDrawingObject("Filter inspector regression");
            source.Scene.AddObject(0, PointF.Empty, new SizeF(32, 32), 0, 0, Color.Teal, 0, ShapeKind.Rectangle);
            var scene = project.Scenes[0];
            var layerId = scene.Layers[0].Id;
            AssertTimeline(project.TryAddSceneInstance(scene.Id, source.Id, PointF.Empty, 0, layerId, out var created)
                && created is not null, "Filter inspector undo regression could not create a symbol instance.");
            var instanceId = created!.Id;
            var originalState = created.EvaluateState(0) with
            {
                Filters = new SymbolFilters { Glow = SymbolGlowFilter.Default }
            };
            created.SetStateAtFrame(0, originalState);
            var trackId = scene.Timeline.FindTrackByTargetId(layerId)?.Id
                ?? throw new InvalidOperationException("Filter inspector undo regression lost its timeline track.");
            scene.Timeline.SetTrackDuration(trackId, 12);
            form.Show();
            tabs.SelectedView = WorkspaceView.SceneEditor;
            Application.DoEvents();
            timeline.RefreshTimeline();
            var strength = inspector.FiltersPanel.Controls.OfType<ModernNumericUpDown>().Single(control =>
                control.AccessibleName == UiLocalization.T("Glow intensity"));
            DrawingObjectInstanceDefinition Instance() => scene.Instances.Single(item => item.Id == instanceId);
            int UndoCount() => (int)RequireProperty(undoStack.GetType(), "Count",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public).GetValue(undoStack)!;
            void SelectAtHeldFrame()
            {
                timeline.SelectSingleFrame(trackId, 4);
                select.Invoke(form, [Instance(), false]);
                updateInspector.Invoke(form, [true]);
            }
            void EditTwice(bool canceled)
            {
                begin.Invoke(strength, null);
                step.Invoke(strength, [1, null]);
                step.Invoke(strength, [1, null]);
                end.Invoke(strength, [canceled]);
            }

            timeline.AutoKeyframeEnabled = false;
            SelectAtHeldFrame();
            AssertTimeline(ReferenceEquals(timeline.Context, scene) && strength.Enabled,
                "Filter inspector undo regression did not bind the selected scene symbol.");
            var initialUndoCount = UndoCount();
            EditTwice(canceled: false);
            var expected = originalState with
            {
                Filters = originalState.Filters with { Glow = originalState.Filters.Glow with { Strength = 1.2f } }
            };
            AssertTimeline(Instance().EvaluateState(0) == expected && Instance().EvaluateState(4) == expected
                && !scene.Timeline.EvaluateExposure(trackId, 4).IsKeyframe && UndoCount() == initialUndoCount + 1,
                "Two continuous filter edits failed to modify the held source Cel with exactly one undo entry.");
            AssertTimeline(undo.Invoke(form, null) is true && UndoCount() == initialUndoCount
                && Instance().EvaluateState(0) == originalState && Instance().EvaluateState(4) == originalState,
                "One Undo failed to restore the filter inspector gesture across the held exposure.");

            SelectAtHeldFrame();
            EditTwice(canceled: true);
            AssertTimeline(UndoCount() == initialUndoCount && Instance().EvaluateState(0) == originalState
                && Instance().EvaluateState(4) == originalState,
                "Canceling the held-exposure filter edit changed the model or added an undo entry.");

            timeline.AutoKeyframeEnabled = true;
            SelectAtHeldFrame();
            EditTwice(canceled: false);
            AssertTimeline(Instance().EvaluateState(0) == originalState && Instance().EvaluateState(4) == expected
                && scene.Timeline.EvaluateExposure(trackId, 4).IsKeyframe && UndoCount() == initialUndoCount + 1,
                "Auto Key filter editing did not isolate its new keyframe from the previous held exposure.");
            AssertTimeline(undo.Invoke(form, null) is true && UndoCount() == initialUndoCount
                && Instance().EvaluateState(4) == originalState && !scene.Timeline.EvaluateExposure(trackId, 4).IsKeyframe,
                "Undo did not remove the filter Auto Key and restore the original exposure.");

            SelectAtHeldFrame();
            EditTwice(canceled: true);
            AssertTimeline(UndoCount() == initialUndoCount && Instance().EvaluateState(4) == originalState
                && !scene.Timeline.EvaluateExposure(trackId, 4).IsKeyframe,
                "Canceling an Auto Key filter gesture retained its provisional keyframe or an undo entry.");
            Console.WriteLine("symbol_filters_inspector_undo=ok");
            var restoreSize = (Button)RequireField(typeof(DrawingObjectInstancePanel), "_restoreSize").GetValue(inspector)!;
            foreach (var autoKey in new[] { false, true })
            foreach (var transform in new[]
            {
                originalState with { ScaleX = 2.5f, ScaleY = 0.4f, ScaleZ = 1.7f, RotationX = 15, RotationY = -20, RotationZ = 25, SkewX = 12, SkewY = -8 },
                originalState with { RotationX = 15 },
                originalState with { RotationY = -20 },
                originalState with { RotationZ = 25 },
                originalState with { SkewX = 12 },
                originalState with { SkewY = -8 }
            })
            {
                timeline.AutoKeyframeEnabled = autoKey;
                var resized = transform;
                Instance().SetStateAtFrame(0, resized);
                SelectAtHeldFrame();
                AssertTimeline(restoreSize.Enabled, "Restore original size was disabled for a resized symbol.");
                var beforeRestore = UndoCount();
                restoreSize.PerformClick();
                var restored = resized with
                {
                    ScaleX = 1, ScaleY = 1, ScaleZ = 1,
                    RotationX = 0, RotationY = 0, RotationZ = 0, SkewX = 0, SkewY = 0
                };
                AssertTimeline(Instance().EvaluateState(4) == restored,
                    $"Restore original size click failed at a held frame (Auto Key={autoKey}).");
                AssertTimeline(Instance().EvaluateState(0) == (autoKey ? resized : restored),
                    "Restore original size changed the wrong exposure.");
                AssertTimeline(UndoCount() == beforeRestore + 1, "Restore original size did not create exactly one undo entry.");
                AssertTimeline(!restoreSize.Enabled, "Restored symbol still advertised a scale reset.");
                RequireMethod(typeof(MainForm), "RestoreSelectedDrawingObjectOriginalSize").Invoke(form, null);
                AssertTimeline(UndoCount() == beforeRestore + 1, "An unchanged size reset created an undo entry.");
                AssertTimeline(undo.Invoke(form, null) is true && Instance().EvaluateState(4) == resized
                    && !scene.Timeline.EvaluateExposure(trackId, 4).IsKeyframe,
                    "Undo failed to restore symbol scale and remove a provisional Auto Key.");
            }
            Console.WriteLine("symbol_original_size_button_held_autokey_undo=ok");

            Instance().SetStateAtFrame(0, originalState);
            scene.Timeline.InsertKeyframe(trackId, 8);
            Instance().SetStateAtFrame(8, originalState with { ScaleX = 3, ScaleY = 2 });
            AssertTimeline(scene.TryCreateTimelineTween(layerId, 0, 8, TimelineTweenKind.Classic, out var tweenError),
                $"Restore size tween setup failed: {tweenError}");
            timeline.AutoKeyframeEnabled = false;
            SelectAtHeldFrame();
            AssertTimeline(MainForm.CanRestoreDrawingObjectOriginalSize(Instance().EvaluateState(4))
                && restoreSize.Enabled, "Restore size was disabled at a materialized tween keyframe.");
            timeline.AutoKeyframeEnabled = true;
            AssertTimeline(restoreSize.Enabled, "Enabling Auto Key did not refresh restore size availability.");
            var tweenMiddle = Instance().EvaluateState(4);
            var beforeTweenRestore = UndoCount();
            restoreSize.PerformClick();
            AssertTimeline(Instance().EvaluateState(4) == tweenMiddle with { ScaleX = 1, ScaleY = 1, ScaleZ = 1 }
                && UndoCount() == beforeTweenRestore + 1, "Restore size failed to reset a tween frame using Auto Key.");
            AssertTimeline(undo.Invoke(form, null) is true && Instance().EvaluateState(4) == tweenMiddle,
                "Undo did not restore the original tween scale.");
            timeline.AutoKeyframeEnabled = false;
            AssertTimeline(restoreSize.Enabled, "Disabling Auto Key disabled restore size at a materialized keyframe.");
            Console.WriteLine("symbol_original_size_tween_availability=ok");
        }
        finally
        {
            timeline.AutoKeyframeEnabled = originalAutoKey;
        }
    }
}
