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

    // Existing WinForms controls do not rerun their constructors after CLR metadata updates.
    // Recreate the workbench for modules that own a control tree or its layout.
    public bool RequiresWorkbenchRebuild => (Modules & (HotReloadModule.Shell
        | HotReloadModule.Inspector
        | HotReloadModule.Workspace
        | HotReloadModule.Timeline)) != 0;
}

internal static class HotReloadModuleResolver
{
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

    private static HotReloadModule ModuleFor(string typeName)
    {
        if (typeName is nameof(StageControl) or nameof(Direct2DStageRenderer) or nameof(SceneRenderOrderBuffer))
        {
            return HotReloadModule.Rendering;
        }

        if (typeName is nameof(TimelineStrip) or nameof(OnionSkinRangeDialog) or nameof(PlaybackSettingsPanel) or nameof(AnimationTimeline))
        {
            return HotReloadModule.Timeline;
        }

        if (typeName is nameof(LibraryVaultPanel) or nameof(HierarchyPanel) or nameof(SceneEditorPanel) or nameof(WorkspaceTabs))
        {
            return HotReloadModule.Workspace;
        }

        if (typeName is nameof(MaterialEditorPanel)
            or nameof(ThemedScrollPanel)
            or nameof(ColorComponentSlider)
            or nameof(HsvColorPlane)
            or nameof(HarmonyColorWheel)
            or nameof(ColorTargetButton)
            or nameof(ColorPaletteGrid)
            or nameof(GradientPreset)
            or nameof(GradientPresetGrid)
            or nameof(GradientPreviewRenderer)
            or nameof(GradientStopStrip)
            or nameof(BrushTipPanel)
            or nameof(DrawSettingsPanel)
            or nameof(DrawSettings)
            or nameof(ModernSlider)
            or nameof(ModernNumericUpDown)
            or nameof(ModernToggleSwitch))
        {
            return HotReloadModule.Inspector;
        }

        if (typeName is nameof(AnimatedContextMenuStrip))
        {
            return HotReloadModule.Shell;
        }

        if (typeName is nameof(VectorScene)
            or nameof(VectorProject)
            or nameof(VectorSceneSnapshot)
            or nameof(LineEndpointStyle)
            or nameof(LineJoinGeometry)
            or nameof(SceneCompositionBuilder)
            or nameof(FreehandStrokeProcessor)
            or nameof(BrushShape)
            or nameof(VectorUnits)
            or nameof(DrawingTopologyRules)
            or nameof(DrawingObjectDefinition)
            or nameof(SceneDefinition)
            or nameof(SceneLayerDefinition)
            or nameof(SceneLayerSnapshot)
            or nameof(SceneLayerSnapshotItem)
            or nameof(SceneObjectInstanceDefinition)
            or nameof(ICompositionDefinition))
        {
            return HotReloadModule.Engine;
        }

        return HotReloadModule.Shell;
    }
}
