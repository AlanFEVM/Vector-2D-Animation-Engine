using System.Text.Json;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private bool TryExecuteCodexAnimationTool(string name, JsonElement arguments, out object result)
    {
        result = null!;
        if (name != "animation_import_bundle") return false;
        RequireCodexProjectReplacement(arguments);
        // Complete file validation and model construction before touching the live editor.
        var imported = AnimationBundleImporter.Load(arguments.GetProperty("path").GetString()!);
        StopPlayback();
        FinishPointerInteractionForFrameChange();
        LoadProjectDocument(imported.Project, string.Empty);
        _workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
        ApplyProjectPlaybackSettingsToCurrentContext();
        SetFrame(0);
        var aspect = _stage.Width / (float)Math.Max(1, _stage.Height);
        var visibleWidth = Math.Max(imported.Width, imported.Height * aspect) * 1.04f;
        _stage.SetVisibleWorldWidth(visibleWidth);
        // Scene Building uses its orthographic reference camera even for 2D instances.
        var referenceScale = Math.Clamp(_stage.Width / (visibleWidth * 0.035f), 0.25f, 8f);
        _stage.RestoreViewState(_stage.CaptureViewState() with
        {
            ReferenceZoomScale = referenceScale,
            ReferenceTargetX = 0,
            ReferenceTargetY = -_stage.Height * 0.08f / (referenceScale * 0.035f),
            ReferenceTargetZ = 0
        });
        MarkProjectDirty();
        result = new
        {
            project = GetCodexProjectInfo(),
            state = GetCodexEditorState(),
            imported.Fps,
            imported.FrameCount,
            imported.Width,
            imported.Height,
            symbols = imported.AliasToId,
            imported.NestedInstanceCount
        };
        return true;
    }
}
