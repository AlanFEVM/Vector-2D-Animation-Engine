namespace VectorAnimationEngine;

internal enum HierarchyNodeKind
{
    Scene,
    LayersRoot,
    Layer,
    ObjectsRoot,
    Object
}

internal sealed class HierarchySelectionChangedEventArgs : EventArgs
{
    public HierarchySelectionChangedEventArgs(HierarchyNodeKind kind, int index, string text)
    {
        Kind = kind;
        Index = index;
        Text = text;
    }

    public HierarchyNodeKind Kind { get; }
    public int Index { get; }
    public string Text { get; }
}

internal sealed class HierarchyPanel : UserControl
{
    private const int MaxLayerNodes = 256;
    private const int MaxObjectNodes = 512;

    private readonly Label _title = new();
    private readonly Label _summary = new();
    private readonly TreeView _tree = new();
    private VectorScene? _scene;
    private TreeNode? _sceneNode;
    private TreeNode? _layersRoot;
    private TreeNode? _objectsRoot;
    private bool _presentationSnapshotValid;
    private int _presentedLayerCount;
    private int _presentedObjectCount;
    private string[] _presentedLayerNames = [];
    private bool[] _presentedLayerVisible = [];
    private float[] _presentedLayerOpacity = [];
    private int[] _presentedLayerColors = [];
    private ushort[] _presentedObjectLayers = [];

    public HierarchyPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        MinimumSize = new Size(220, 220);
        Padding = new Padding(10);

        _title.Text = "Hierarchy";
        _title.Dock = DockStyle.Top;
        _title.Height = 28;
        _title.ForeColor = Theme.Text;
        _title.BackColor = Theme.Panel;
        _title.Font = Theme.UiFont(10, FontStyle.Bold);
        _title.TextAlign = ContentAlignment.MiddleLeft;
        Controls.Add(_title);

        _summary.Dock = DockStyle.Top;
        _summary.Height = 24;
        _summary.ForeColor = Theme.Muted;
        _summary.BackColor = Theme.Panel;
        _summary.Font = Theme.UiFont();
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.AutoEllipsis = true;
        Controls.Add(_summary);

        _tree.Dock = DockStyle.Fill;
        Theme.StyleTreeView(_tree);
        _tree.AfterSelect += (_, e) => RaiseSelection(e.Node);
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) RaiseFocusRequest(e.Node);
        };
        Controls.Add(_tree);

        _tree.BringToFront();
        RebuildPlaceholder();
    }

    public event EventHandler<HierarchySelectionChangedEventArgs>? HierarchySelectionChanged;
    public event EventHandler<HierarchySelectionChangedEventArgs>? HierarchyFocusRequested;

    public TreeNode? SelectedNode => _tree.SelectedNode;

    public void BindScene(VectorScene scene)
    {
        if (ReferenceEquals(_scene, scene))
        {
            RefreshScene();
            return;
        }

        _scene = scene;
        InvalidatePresentationSnapshot();
        Rebuild();
    }

    public void ClearScene()
    {
        _scene = null;
        RebuildPlaceholder();
    }

    public void RefreshScene(bool force = false)
    {
        if (_scene is null
            || _sceneNode is null
            || _layersRoot is null
            || _objectsRoot is null
            || _tree.Nodes.Count != 1
            || !ReferenceEquals(_tree.Nodes[0], _sceneNode)
            || _sceneNode.Nodes.Count != 2
            || !ReferenceEquals(_sceneNode.Nodes[0], _layersRoot)
            || !ReferenceEquals(_sceneNode.Nodes[1], _objectsRoot))
        {
            Rebuild();
            return;
        }

        if (!force && PresentationSnapshotMatches()) return;

        _tree.BeginUpdate();
        try
        {
            RefreshBranch(
                _layersRoot,
                _scene.LayerCount,
                MaxLayerNodes,
                index =>
                {
                    var visibility = _scene.LayerVisible[index] ? "Visible" : "Hidden";
                    return $"{_scene.LayerNames[index]} - {visibility}, {_scene.LayerOpacity[index]:P0}";
                },
                HierarchyNodeKind.Layer,
                "layers",
                styleAt: (node, index) => node.BackColor = LayerNodeBackground(index));
            RefreshBranch(
                _objectsRoot,
                _scene.ObjectCount,
                MaxObjectNodes,
                index =>
                {
                    var layer = _scene.ObjectLayer.Length > index ? _scene.ObjectLayer[index] : 0;
                    return $"Object {index:000000} - Layer {layer}";
                },
                HierarchyNodeKind.Object,
                "objects",
                index => _scene.ObjectLayer.Length > index ? _scene.ObjectLayer[index] : 0);
            SetTextIfChanged(_layersRoot, $"Layers ({_scene.LayerCount})");
            SetTextIfChanged(_objectsRoot, $"Objects ({_scene.ObjectCount})");
            SetTextIfChanged(_summary, $"{_scene.LayerCount} layers, {_scene.ObjectCount} objects");
        }
        finally
        {
            _tree.EndUpdate();
        }

        CapturePresentationSnapshot();
    }

    public void SelectLayer(int layerIndex)
    {
        SelectFirstNode(node => node.Tag is HierarchyNodeTag tag && tag.Kind == HierarchyNodeKind.Layer && tag.Index == layerIndex);
    }

    public void SelectObject(int objectIndex)
    {
        SelectFirstNode(node => node.Tag is HierarchyNodeTag tag && tag.Kind == HierarchyNodeKind.Object && tag.Index == objectIndex);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private void Rebuild()
    {
        if (_scene is null)
        {
            RebuildPlaceholder();
            return;
        }

        _tree.BeginUpdate();
        _tree.Nodes.Clear();

        var sceneNode = TaggedNode("Scene", HierarchyNodeKind.Scene, -1);
        var layersRoot = TaggedNode($"Layers ({_scene.LayerCount})", HierarchyNodeKind.LayersRoot, -1);
        var layerLimit = Math.Min(_scene.LayerCount, MaxLayerNodes);
        for (var i = 0; i < layerLimit; i++)
        {
            var visibility = _scene.LayerVisible[i] ? "Visible" : "Hidden";
            var opacity = _scene.LayerOpacity[i];
            var node = TaggedNode($"{_scene.LayerNames[i]} - {visibility}, {opacity:P0}", HierarchyNodeKind.Layer, i);
            node.BackColor = LayerNodeBackground(i);
            layersRoot.Nodes.Add(node);
        }

        if (_scene.LayerCount > layerLimit)
        {
            layersRoot.Nodes.Add(PlainNode($"+ {_scene.LayerCount - layerLimit} more layers"));
        }

        var objectsRoot = TaggedNode($"Objects ({_scene.ObjectCount})", HierarchyNodeKind.ObjectsRoot, -1);
        var objectLimit = Math.Min(_scene.ObjectCount, MaxObjectNodes);
        for (var i = 0; i < objectLimit; i++)
        {
            var layer = _scene.ObjectLayer.Length > i ? _scene.ObjectLayer[i] : 0;
            objectsRoot.Nodes.Add(TaggedNode($"Object {i:000000} - Layer {layer}", HierarchyNodeKind.Object, i, layer));
        }

        if (_scene.ObjectCount > objectLimit)
        {
            objectsRoot.Nodes.Add(PlainNode($"+ {_scene.ObjectCount - objectLimit} more objects"));
        }

        sceneNode.Nodes.Add(layersRoot);
        sceneNode.Nodes.Add(objectsRoot);
        _tree.Nodes.Add(sceneNode);
        sceneNode.Expand();
        layersRoot.Expand();
        objectsRoot.Expand();
        _sceneNode = sceneNode;
        _layersRoot = layersRoot;
        _objectsRoot = objectsRoot;
        _tree.EndUpdate();

        _summary.Text = $"{_scene.LayerCount} {UiLocalization.T("layers")}, {_scene.ObjectCount} {UiLocalization.T("objects")}";
        CapturePresentationSnapshot();
    }

    private void RebuildPlaceholder()
    {
        InvalidatePresentationSnapshot();
        _sceneNode = null;
        _layersRoot = null;
        _objectsRoot = null;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var sceneNode = TaggedNode("Scene", HierarchyNodeKind.Scene, -1);
        var layersRoot = TaggedNode("Layers", HierarchyNodeKind.LayersRoot, -1);
        layersRoot.Nodes.Add(TaggedNode("Layer 0000", HierarchyNodeKind.Layer, 0));
        var objectsRoot = TaggedNode("Objects", HierarchyNodeKind.ObjectsRoot, -1);
        objectsRoot.Nodes.Add(TaggedNode("Object placeholder", HierarchyNodeKind.Object, -1));
        sceneNode.Nodes.Add(layersRoot);
        sceneNode.Nodes.Add(objectsRoot);
        _tree.Nodes.Add(sceneNode);
        sceneNode.ExpandAll();
        _tree.EndUpdate();
        _summary.Text = "Scene, layers and objects";
    }

    private bool PresentationSnapshotMatches()
    {
        if (!_presentationSnapshotValid || _scene is null
            || _presentedLayerCount != _scene.LayerCount
            || _presentedObjectCount != _scene.ObjectCount)
        {
            return false;
        }

        var layerCount = Math.Min(_scene.LayerCount, MaxLayerNodes);
        if (_presentedLayerNames.Length != layerCount
            || _presentedLayerVisible.Length != layerCount
            || _presentedLayerOpacity.Length != layerCount
            || _presentedLayerColors.Length != layerCount)
        {
            return false;
        }

        for (var index = 0; index < layerCount; index++)
        {
            if (!string.Equals(_presentedLayerNames[index], _scene.LayerNames[index], StringComparison.Ordinal)
                || _presentedLayerVisible[index] != _scene.LayerVisible[index]
                || _presentedLayerOpacity[index] != _scene.LayerOpacity[index]
                || _presentedLayerColors[index] != _scene.GetLayerColor(index).ToArgb())
            {
                return false;
            }
        }

        var objectCount = Math.Min(_scene.ObjectCount, MaxObjectNodes);
        if (_presentedObjectLayers.Length != objectCount) return false;
        for (var index = 0; index < objectCount; index++)
        {
            var layer = _scene.ObjectLayer.Length > index ? _scene.ObjectLayer[index] : (ushort)0;
            if (_presentedObjectLayers[index] != layer) return false;
        }

        return true;
    }

    private void CapturePresentationSnapshot()
    {
        if (_scene is null)
        {
            InvalidatePresentationSnapshot();
            return;
        }

        _presentedLayerCount = _scene.LayerCount;
        _presentedObjectCount = _scene.ObjectCount;
        var layerCount = Math.Min(_scene.LayerCount, MaxLayerNodes);
        Array.Resize(ref _presentedLayerNames, layerCount);
        Array.Resize(ref _presentedLayerVisible, layerCount);
        Array.Resize(ref _presentedLayerOpacity, layerCount);
        Array.Resize(ref _presentedLayerColors, layerCount);
        for (var index = 0; index < layerCount; index++)
        {
            _presentedLayerNames[index] = _scene.LayerNames[index];
            _presentedLayerVisible[index] = _scene.LayerVisible[index];
            _presentedLayerOpacity[index] = _scene.LayerOpacity[index];
            _presentedLayerColors[index] = _scene.GetLayerColor(index).ToArgb();
        }

        var objectCount = Math.Min(_scene.ObjectCount, MaxObjectNodes);
        Array.Resize(ref _presentedObjectLayers, objectCount);
        for (var index = 0; index < objectCount; index++)
        {
            _presentedObjectLayers[index] = _scene.ObjectLayer.Length > index
                ? _scene.ObjectLayer[index]
                : (ushort)0;
        }
        _presentationSnapshotValid = true;
    }

    private void InvalidatePresentationSnapshot()
    {
        _presentationSnapshotValid = false;
    }

    private static void RefreshBranch(
        TreeNode root,
        int totalCount,
        int limit,
        Func<int, string> textAt,
        HierarchyNodeKind kind,
        string overflowLabel,
        Func<int, int>? detailAt = null,
        Action<TreeNode, int>? styleAt = null)
    {
        var visibleCount = Math.Min(totalCount, limit);
        while (root.Nodes.Count > 0 && root.Nodes[root.Nodes.Count - 1].Tag is not HierarchyNodeTag)
        {
            root.Nodes.RemoveAt(root.Nodes.Count - 1);
        }

        while (root.Nodes.Count > visibleCount)
        {
            root.Nodes.RemoveAt(root.Nodes.Count - 1);
        }

        for (var index = 0; index < root.Nodes.Count; index++)
        {
            var node = root.Nodes[index];
            var detail = detailAt?.Invoke(index) ?? -1;
            if (node.Tag is not HierarchyNodeTag tag
                || tag.Kind != kind
                || tag.Index != index
                || tag.Detail != detail)
            {
                SetTextIfChanged(node, textAt(index));
                node.Tag = new HierarchyNodeTag(kind, index, detail);
            }
            else if (detailAt is null)
            {
                SetTextIfChanged(node, textAt(index));
            }
            styleAt?.Invoke(node, index);
        }

        for (var index = root.Nodes.Count; index < visibleCount; index++)
        {
            var node = TaggedNode(textAt(index), kind, index, detailAt?.Invoke(index) ?? -1);
            styleAt?.Invoke(node, index);
            root.Nodes.Add(node);
        }

        if (totalCount > visibleCount)
        {
            root.Nodes.Add(PlainNode($"+ {totalCount - visibleCount} more {overflowLabel}"));
        }
    }

    private static void SetTextIfChanged(TreeNode node, string text)
    {
        if (!string.Equals(node.Text, text, StringComparison.Ordinal)) node.Text = text;
    }

    private Color LayerNodeBackground(int layerIndex)
    {
        if (_scene is null || layerIndex < 0 || layerIndex >= _scene.LayerCount) return Theme.Panel;
        return Theme.Mix(Theme.Panel, _scene.GetLayerColor(layerIndex), Theme.IsLight ? 0.18f : 0.26f);
    }

    private static void SetTextIfChanged(Label label, string text)
    {
        if (!string.Equals(label.Text, text, StringComparison.Ordinal)) label.Text = text;
    }

    private void RaiseSelection(TreeNode? node)
    {
        if (node?.Tag is not HierarchyNodeTag tag) return;
        HierarchySelectionChanged?.Invoke(this, new HierarchySelectionChangedEventArgs(tag.Kind, tag.Index, node.Text));
    }

    private void RaiseFocusRequest(TreeNode? node)
    {
        if (_scene is null
            || node?.Tag is not HierarchyNodeTag tag
            || tag.Index < 0
            || tag.Kind is not (HierarchyNodeKind.Layer or HierarchyNodeKind.Object)
            || tag.Kind == HierarchyNodeKind.Layer && tag.Index >= _scene.LayerCount
            || tag.Kind == HierarchyNodeKind.Object && tag.Index >= _scene.ObjectCount)
        {
            return;
        }

        HierarchyFocusRequested?.Invoke(
            this,
            new HierarchySelectionChangedEventArgs(tag.Kind, tag.Index, node.Text));
    }

    private void SelectFirstNode(Predicate<TreeNode> match)
    {
        foreach (TreeNode root in _tree.Nodes)
        {
            var found = FindNode(root, match);
            if (found is null) continue;
            _tree.SelectedNode = found;
            found.EnsureVisible();
            return;
        }
    }

    private static TreeNode? FindNode(TreeNode node, Predicate<TreeNode> match)
    {
        if (match(node)) return node;
        foreach (TreeNode child in node.Nodes)
        {
            var found = FindNode(child, match);
            if (found is not null) return found;
        }

        return null;
    }

    private static TreeNode TaggedNode(string text, HierarchyNodeKind kind, int index, int detail = -1)
    {
        return new TreeNode(text) { Tag = new HierarchyNodeTag(kind, index, detail) };
    }

    private static TreeNode PlainNode(string text) => new(text) { ForeColor = Theme.Muted };

    private readonly record struct HierarchyNodeTag(HierarchyNodeKind Kind, int Index, int Detail);
}
