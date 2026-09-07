using System.Numerics;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private readonly AnimatedContextMenuStrip _scene3DContextMenu = new();
    private readonly ToolStripMenuItem _addSceneLightContextMenuItem = new("Add light");
    private readonly Dictionary<SceneLightKind, Bitmap> _scene3DLightMenuIcons = [];
    private Bitmap? _scene3DAddLightMenuIcon;
    private Vector3? _scene3DContextLightPosition;

    private void BuildScene3DContextMenu()
    {
        _scene3DAddLightMenuIcon = SvgIcons.CreateBitmap(
            SvgIconKind.Light,
            new Size(18, 18),
            Theme.Text,
            DeviceDpi);
        _addSceneLightContextMenuItem.Image = _scene3DAddLightMenuIcon;
        _addSceneLightContextMenuItem.AccessibleName = "Add light";

        foreach (var kind in new[]
                 {
                     SceneLightKind.Directional,
                     SceneLightKind.Point,
                     SceneLightKind.Area,
                     SceneLightKind.Ambient
                 })
        {
            var icon = SvgIcons.CreateBitmap(SceneLightContextIcon(kind), new Size(18, 18), Theme.Text, DeviceDpi);
            _scene3DLightMenuIcons.Add(kind, icon);
            var item = new ToolStripMenuItem(SceneLightContextLabel(kind), icon, (_, _) =>
            {
                var position = kind == SceneLightKind.Ambient ? null : _scene3DContextLightPosition;
                AddSceneLight(kind, position);
            })
            {
                Tag = kind,
                AccessibleName = SceneLightContextLabel(kind)
            };
            _addSceneLightContextMenuItem.DropDownItems.Add(item);
        }

        _scene3DContextMenu.Items.Add(_addSceneLightContextMenuItem);
        _scene3DContextMenu.Opening += (_, e) =>
        {
            var scene = ActiveScene();
            var available = !_playing
                && IsScene3DView()
                && IsSceneCompositionContext()
                && !IsSceneMaskEditing()
                && scene is { Lights.Count: < SceneDefinition.MaximumLights };
            _addSceneLightContextMenuItem.Enabled = available;
            e.Cancel = !IsScene3DView() || !IsSceneCompositionContext() || IsSceneMaskEditing();
        };
    }

    private void PrepareScene3DContextMenu(Point screen)
    {
        _scene3DContextLightPosition = TryGetSceneBasePlanePoint(screen, out var world)
            ? new Vector3(world.X, world.Y, 0f)
            : Vector3.Zero;
    }

    private void DisposeScene3DContextMenu()
    {
        _addSceneLightContextMenuItem.Image = null;
        foreach (ToolStripItem item in _addSceneLightContextMenuItem.DropDownItems) item.Image = null;
        _scene3DContextMenu.Dispose();
        _scene3DAddLightMenuIcon?.Dispose();
        _scene3DAddLightMenuIcon = null;
        foreach (var icon in _scene3DLightMenuIcons.Values) icon.Dispose();
        _scene3DLightMenuIcons.Clear();
    }

    private static SvgIconKind SceneLightContextIcon(SceneLightKind kind) => kind switch
    {
        SceneLightKind.Directional => SvgIconKind.DirectionalLight,
        SceneLightKind.Point => SvgIconKind.PointLight,
        SceneLightKind.Area => SvgIconKind.AreaLight,
        _ => SvgIconKind.AmbientLight
    };

    private static string SceneLightContextLabel(SceneLightKind kind) => kind switch
    {
        SceneLightKind.Directional => "Directional Light",
        SceneLightKind.Point => "Point Light",
        SceneLightKind.Area => "Area Light",
        _ => "Ambient Light"
    };
}
