namespace VectorAnimationEngine;

[Flags]
internal enum HotReloadModule
{
    None = 0,
    Shell = 1 << 0,
    Inspector = 1 << 1,
    Workspace = 1 << 2,
    Timeline = 1 << 3,
    Rendering = 1 << 4,
    Engine = 1 << 5,
    All = Shell | Inspector | Workspace | Timeline | Rendering | Engine
}

internal readonly record struct HotReloadPlan(HotReloadModule Modules, string UpdatedTypes)
{
    public bool Includes(HotReloadModule module) => (Modules & module) != 0;

    public HotReloadPlan Merge(HotReloadPlan other)
    {
        var names = UpdatedTypes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(other.UpdatedTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .Take(24);
        return new HotReloadPlan(Modules | other.Modules, string.Join(", ", names));
    }

    public bool RequiresProcessRestart => Modules == HotReloadModule.All
        || (Modules & (HotReloadModule.Shell
            | HotReloadModule.Inspector
            | HotReloadModule.Workspace
            | HotReloadModule.Timeline)) != 0
        || HotReloadModuleResolver.RequiresProcessRestart(UpdatedTypes);

    // Existing WinForms controls do not rerun their constructors after CLR metadata updates.
    // Recreate the workbench for modules that own a control tree or its layout.
    public bool RequiresWorkbenchRebuild => !RequiresProcessRestart && (Modules & (HotReloadModule.Shell
        | HotReloadModule.Inspector
        | HotReloadModule.Workspace
        | HotReloadModule.Timeline)) != 0;
}

internal static class HotReloadModuleResolver
{
    private static readonly HashSet<string> ProcessRestartTypes = new(StringComparer.Ordinal)
    {
        nameof(MainForm),
        nameof(StageControl),
        nameof(Direct2DStageRenderer),
        nameof(LayerBlendCompositor),
        nameof(TimelineStrip),
        nameof(TweenCurveEditorPanel),
        nameof(TweenCurveEditor),
        nameof(LayerColorDialog),
        nameof(LayerBlendModePanel),
        nameof(DrawingObjectInstancePanel),
        nameof(TextSettingsPanel),
        nameof(MaterialEditorPanel),
        nameof(SceneEditorPanel),
        nameof(LibraryVaultPanel),
        nameof(HierarchyPanel),
        nameof(WorkspaceTabs),
        nameof(VectorScene),
        nameof(VectorSceneSnapshot),
        nameof(TextObjectData),
        nameof(TextGeometry),
        nameof(TextFontStyle),
        nameof(TextHorizontalAlignment),
        nameof(VectorProject),
        nameof(ProjectAssetFolder),
        nameof(ProjectAssetTag),
        nameof(ProjectAssetTagData),
        nameof(DrawingObjectDefinition),
        nameof(DrawingObjectInstanceDefinition),
        nameof(SceneObjectInstanceDefinition),
        nameof(SceneDefinition),
        nameof(SceneLayerDefinition),
        nameof(SceneLayerSnapshot),
        nameof(SceneLayerSnapshotItem),
        nameof(LayerBlendMode),
        nameof(DrawingObjectPlaybackMode),
        nameof(LineEndpointStyle)
    };

    public static HotReloadPlan Resolve(Type[]? updatedTypes)
    {
        if (updatedTypes is not { Length: > 0 }) return new HotReloadPlan(HotReloadModule.All, "unknown");

        var modules = HotReloadModule.None;
        var names = new List<string>(updatedTypes.Length);
        foreach (var updatedType in updatedTypes)
        {
            var name = RootTypeName(updatedType);
            names.Add(name);
            modules |= ModuleFor(name);
        }

        return new HotReloadPlan(
            modules == HotReloadModule.None ? HotReloadModule.Shell : modules,
            string.Join(", ", names.Distinct(StringComparer.Ordinal).Take(12)));
    }

    private static string RootTypeName(Type type)
    {
        while (type.DeclaringType is not null) type = type.DeclaringType;
        return type.Name;
    }

    internal static bool RequiresProcessRestart(string updatedTypes)
    {
        if (string.IsNullOrWhiteSpace(updatedTypes)
            || string.Equals(updatedTypes, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return updatedTypes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(ProcessRestartTypes.Contains);
    }

    private static HotReloadModule ModuleFor(string typeName)
    {
        if (typeName is nameof(StageControl)
            or nameof(Direct2DStageRenderer)
            or nameof(LayerBlendCompositor)
            or nameof(SceneRenderOrderBuffer)
            or nameof(WorldGridLayout)
            or nameof(PolarGridLayout))
        {
            return HotReloadModule.Rendering;
        }

        if (typeName is nameof(TimelineStrip) or nameof(LayerColorDialog) or nameof(PlaybackSettingsPanel) or nameof(AnimationTimeline))
        {
            return HotReloadModule.Timeline;
        }

        if (typeName is nameof(LibraryVaultPanel) or nameof(HierarchyPanel) or nameof(SceneEditorPanel) or nameof(WorkspaceTabs))
        {
            return HotReloadModule.Workspace;
        }

        if (typeName is nameof(MaterialEditorPanel)
            or nameof(TweenCurveEditorPanel)
            or nameof(TweenCurveEditor)
            or nameof(TextSettingsPanel)
            or nameof(SvgIcons)
            or nameof(SvgIconButton)
            or nameof(ThemedScrollPanel)
            or nameof(ColorComponentSlider)
            or nameof(HsvColorPlane)
            or nameof(HarmonyColorWheel)
            or nameof(ColorTargetButton)
            or nameof(ColorPaletteGrid)
            or nameof(GradientPreset)
            or nameof(GradientPresetGrid)
            or nameof(MaterialPaletteStore)
            or nameof(GradientPreviewRenderer)
            or nameof(GradientStopStrip)
            or nameof(BrushTipPanel)
            or nameof(MixingBrushSettingsPanel)
            or nameof(DrawSettingsPanel)
            or nameof(ShapeSettingsPanel)
            or nameof(LayerBlendModePanel)
            or nameof(DrawingObjectInstancePanel)
            or nameof(DrawSettings)
            or nameof(ModernSlider)
            or nameof(ModernNumericUpDown)
            or nameof(ModernToggleSwitch))
        {
            return HotReloadModule.Inspector;
        }

        if (typeName is nameof(AnimatedContextMenuStrip)
            or nameof(ModernDialogForm)
            or nameof(ModernMessageDialog)
            or nameof(ReleaseNotesCatalog)
            or nameof(ReleaseNotesDialog)
            or nameof(ReleaseNotesPanel)
            or nameof(SettingsDialog)
            or nameof(ShortcutProfileEditorPanel)
            or nameof(UiLocalization)
            or nameof(Theme)
            or nameof(ApplicationColorTheme)
            or nameof(ApplicationSettingsStore)
            or nameof(ShortcutProfiles)
            or nameof(ShortcutProfileRecord)
            or nameof(ToolShortcutMap))
        {
            return HotReloadModule.Shell;
        }

        if (typeName is nameof(VectorScene)
            or nameof(VectorProject)
            or nameof(ProjectAssetFolder)
            or nameof(ProjectAssetTag)
            or nameof(ProjectAssetTagData)
            or nameof(VectorSceneSnapshot)
            or nameof(TextObjectData)
            or nameof(TextGeometry)
            or nameof(TextFontStyle)
            or nameof(TextHorizontalAlignment)
            or nameof(LineEndpointStyle)
            or nameof(LayerBlendMode)
            or nameof(LineJoinGeometry)
            or nameof(SceneCompositionBuilder)
            or nameof(FreehandStrokeProcessor)
            or nameof(BrushShape)
            or nameof(BrushMixingMode)
            or nameof(MixingBrushSettings)
            or nameof(MixingBrushColorSample)
            or nameof(MixingBrushTrajectorySample)
            or nameof(MixingBrushRuntime)
            or nameof(PaintColorMixer)
            or nameof(MixingBrushProcessor)
            or nameof(MixingBrushPaintSampler)
            or nameof(VectorUnits)
            or nameof(DrawingTopologyRules)
            or nameof(DrawingObjectDefinition)
            or nameof(DrawingObjectInstanceDefinition)
            or nameof(DrawingObjectPlaybackMode)
            or nameof(SceneDefinition)
            or nameof(SceneLayerDefinition)
            or nameof(SceneLayerSnapshot)
            or nameof(SceneLayerSnapshotItem)
            or nameof(SceneObjectInstanceDefinition)
            or nameof(ICompositionDefinition))
        {
            return HotReloadModule.Engine;
        }

        // Unknown roots are treated conservatively. A full workbench refresh is
        // preferable to silently retaining stale controls or engine-derived state.
        return HotReloadModule.All;
    }
}

internal readonly record struct HotReloadBatch(long Generation, HotReloadPlan Plan, DateTime QueuedUtc);

internal enum HotReloadUiState
{
    Applying,
    Applied,
    Recovering,
    Failed
}
