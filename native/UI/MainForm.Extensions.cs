namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    // Implemented by engine-owned code only; no external code or files are loaded here.
    partial void AddNativeExtensionCommands(Action<string, Action> addCommand);
    partial void CreateNativeExtensionInspector(ref Control? panel);
    partial void RefreshNativeExtensionInspector();
    partial void HandleNativeExtensionStageClick(MouseEventArgs e, ref bool handled);
    partial void HandleNativeExtensionStageDoubleClick(MouseEventArgs e, ref bool handled);
    partial void OpenNativeExtensionChunk(SceneDefinition scene, SceneBlockChunk chunk);
    partial void CloseNativeExtensionChunk(bool restoreTimeline, ref bool handled);

    private void OpenVaultBlockChunk(SceneDefinition scene, SceneBlockChunk chunk) => OpenNativeExtensionChunk(scene, chunk);

    private bool CloseNativeExtensionChunk(bool restoreTimeline)
    {
        var handled = false;
        CloseNativeExtensionChunk(restoreTimeline, ref handled);
        return handled;
    }

    private void ShowExtensionsEmptyState() => ModernMessageDialog.Show(
        this, "No extensions are available in this build.", UiLocalization.T("Extensions"),
        MessageBoxButtons.OK, MessageBoxIcon.Information);
}
