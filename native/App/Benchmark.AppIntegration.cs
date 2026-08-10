using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
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
        var rendering = HotReloadModuleResolver.Resolve([typeof(Direct2DStageRenderer)]);
        var worldGridRendering = HotReloadModuleResolver.Resolve([typeof(WorldGridLayout)]);
        var polarGridRendering = HotReloadModuleResolver.Resolve([typeof(PolarGridLayout)]);
        var referenceRendering = HotReloadModuleResolver.Resolve([
            typeof(ReferenceViewDirection),
            typeof(SpatialTransformMode),
            typeof(SceneCompositionMaskClip),
            typeof(Reference3DProjectedContour)]);
        var timeline = HotReloadModuleResolver.Resolve([typeof(TimelineStrip)]);
        var inspector = HotReloadModuleResolver.Resolve([typeof(MaterialEditorPanel)]);
        var spatialInspector = HotReloadModuleResolver.Resolve([
            typeof(SpatialTransformPanel),
            typeof(SpatialTransformValues)]);
        var instanceInspector = HotReloadModuleResolver.Resolve([typeof(DrawingObjectInstancePanel)]);
        var themedScroll = HotReloadModuleResolver.Resolve([typeof(ThemedScrollPanel)]);
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
        var engine = HotReloadModuleResolver.Resolve([typeof(VectorScene), typeof(DrawingObjectPlaybackMode)]);
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
            || harmonyWheel.Modules != HotReloadModule.Inspector
            || gradientPreset.Modules != HotReloadModule.Inspector
            || paletteIcon.Modules != HotReloadModule.Inspector
            || paletteStore.Modules != HotReloadModule.Inspector
            || settingsDialog.Modules != HotReloadModule.Shell
            || releaseNotes.Modules != HotReloadModule.Shell
            || referenceWorkspace.Modules != HotReloadModule.Workspace
            || engine.Modules != HotReloadModule.Engine
            || sceneMaskEngine.Modules != HotReloadModule.Engine
            || projectAssetFolder.Modules != HotReloadModule.Engine
            || projectAssetTags.Modules != HotReloadModule.Engine
            || unknown.Modules != HotReloadModule.All
            || merged.Modules != (HotReloadModule.Rendering | HotReloadModule.Engine)
            || merged.UpdatedTypes.Split(',', StringSplitOptions.RemoveEmptyEntries).Length != 3
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
                    $"Module hot reload routing was not scoped: rendering={rendering.Modules}/{rendering.RequiresWorkbenchRebuild}, worldGrid={worldGridRendering.Modules}/{worldGridRendering.RequiresWorkbenchRebuild}, polarGrid={polarGridRendering.Modules}/{polarGridRendering.RequiresWorkbenchRebuild}, referenceRendering={referenceRendering.Modules}/{referenceRendering.RequiresWorkbenchRebuild}, timeline={timeline.Modules}/{timeline.RequiresWorkbenchRebuild}, inspector={inspector.Modules}/{inspector.RequiresWorkbenchRebuild}, spatialInspector={spatialInspector.Modules}/{spatialInspector.RequiresWorkbenchRebuild}, instanceInspector={instanceInspector.Modules}/{instanceInspector.RequiresWorkbenchRebuild}, themedScroll={themedScroll.Modules}/{themedScroll.RequiresWorkbenchRebuild}, harmonyWheel={harmonyWheel.Modules}/{harmonyWheel.RequiresWorkbenchRebuild}, gradientPreset={gradientPreset.Modules}/{gradientPreset.RequiresWorkbenchRebuild}, paletteIcon={paletteIcon.Modules}/{paletteIcon.RequiresWorkbenchRebuild}, paletteStore={paletteStore.Modules}/{paletteStore.RequiresWorkbenchRebuild}, settingsDialog={settingsDialog.Modules}/{settingsDialog.RequiresWorkbenchRebuild}, releaseNotes={releaseNotes.Modules}/{releaseNotes.RequiresWorkbenchRebuild}, referenceWorkspace={referenceWorkspace.Modules}/{referenceWorkspace.RequiresWorkbenchRebuild}, engine={engine.Modules}/{engine.RequiresWorkbenchRebuild}, sceneMaskEngine={sceneMaskEngine.Modules}/{sceneMaskEngine.RequiresWorkbenchRebuild}, projectAssetFolder={projectAssetFolder.Modules}/{projectAssetFolder.RequiresWorkbenchRebuild}, projectAssetTags={projectAssetTags.Modules}/{projectAssetTags.RequiresWorkbenchRebuild}, unknown={unknown.Modules}, merged={merged.Modules}/{merged.UpdatedTypes}, coordinator={dispatchedBatch}.");
        }

        Console.WriteLine("module_reload_routing_regression=ok");
    }

}
