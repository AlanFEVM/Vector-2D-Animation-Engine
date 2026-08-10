using System.Diagnostics;
using System.Text.Json;

namespace VectorAnimationEngine;

internal sealed class VaultItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "Item";
    public string Name { get; set; } = "Untitled";
    public string Detail { get; set; } = "";
    public string Payload { get; set; } = "";
    public string ReferenceKind { get; set; } = "";
    public string ReferenceId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

internal sealed class ProjectAssetFolderRequestedEventArgs(string folderId) : EventArgs
{
    public string FolderId { get; } = folderId;
}

internal sealed class ProjectAssetFolderCreateRequestedEventArgs(string parentFolderId) : EventArgs
{
    public string ParentFolderId { get; } = parentFolderId;
}

internal sealed class ProjectAssetMoveRequestedEventArgs(string assetId, string targetFolderId) : EventArgs
{
    public string AssetId { get; } = assetId;
    public string TargetFolderId { get; } = targetFolderId;
}

internal sealed class DrawingObjectAssetTagsRequestedEventArgs(string drawingObjectId) : EventArgs
{
    public string DrawingObjectId { get; } = drawingObjectId;
}

internal sealed class DrawingObjectAssetTagAssignmentRequestedEventArgs(
    string drawingObjectId,
    string tagId,
    bool assigned) : EventArgs
{
    public string DrawingObjectId { get; } = drawingObjectId;
    public string TagId { get; } = tagId;
    public bool Assigned { get; } = assigned;
}

internal sealed record ProjectAssetFolderDragData(string ProjectId, string FolderId);

internal sealed partial class LibraryVaultPanel : UserControl
{
    private readonly ListView _library = new();
    private readonly TreeView _projectObjects = new AssetTreeView();
    private readonly ListView _vault = new();
    private readonly AnimatedContextMenuStrip _projectObjectMenu = new();
    private readonly Panel _contentHost = new();
    private readonly Label _vaultSummary = new();
    private readonly ToolTip _toolTip = new();
    private readonly List<VaultItem> _vaultItems = [];
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 220 };
    private readonly SvgIconButton _newFolderButton = new(SvgIconKind.FolderPlus)
    {
        AccessibleName = "New Folder"
    };
    private Button? _openButton;
    private VectorProject? _project;
    private VectorScene? _scene;
    private Func<int>? _selectedObjectProvider;
    private Func<int>? _frameProvider;
    private VaultPreviewForm? _preview;
    private ListView? _hoverList;
    private int _hoverIndex = -1;
    private TreeNode? _hoverProjectNode;
    private bool _syncingSelection;
    private bool _projectRowsDirty = true;
    private ProjectRowsFingerprint _projectRowsFingerprint;
    private string _activeDrawingObjectId = "";
    private string _selectedProjectDrawingObjectId = "";
    private string _selectedAssetFolderId = "";
    private VaultSource _activeSource = VaultSource.Project;

    private sealed class AssetTreeView : TreeView
    {
        private const int TvsNoHorizontalScroll = 0x8000;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Style |= TvsNoHorizontalScroll;
                return parameters;
            }
        }
    }

    private sealed record VaultRow(
        VaultItem Item,
        DrawingObjectDefinition? DrawingObject,
        bool IsProjectObject,
        ProjectAssetFolder? Folder = null) : ITreeNodeTrailingColorSource, ITreeNodeLeadingIconSource
    {
        public IReadOnlyList<Color> TrailingColors { get; set; } = [];
        public SvgIconKind LeadingIcon => Folder is not null ? SvgIconKind.Folder : SvgIconKind.Objects;
    }
    private readonly record struct ProjectRowsFingerprint(int ObjectCount, int FolderCount, int Hash);

    private enum VaultSource
    {
        Library,
        Project,
        Stored
    }

    public event EventHandler<DrawingObjectOpenRequestedEventArgs>? DrawingObjectOpenRequested;
    public event EventHandler<DrawingObjectAssetRequestedEventArgs>? DrawingObjectRenameRequested;
    public event EventHandler<DrawingObjectAssetRequestedEventArgs>? DrawingObjectDuplicateRequested;
    public event EventHandler<DrawingObjectAssetRequestedEventArgs>? DrawingObjectDeleteRequested;
    public event EventHandler<ProjectAssetFolderCreateRequestedEventArgs>? AssetFolderCreateRequested;
    public event EventHandler<ProjectAssetFolderRequestedEventArgs>? AssetFolderRenameRequested;
    public event EventHandler<ProjectAssetFolderRequestedEventArgs>? AssetFolderDuplicateRequested;
    public event EventHandler<ProjectAssetMoveRequestedEventArgs>? DrawingObjectMoveRequested;
    public event EventHandler<ProjectAssetMoveRequestedEventArgs>? AssetFolderMoveRequested;
    public event EventHandler<DrawingObjectAssetTagsRequestedEventArgs>? DrawingObjectAssetTagsRequested;
    public event EventHandler<DrawingObjectAssetTagAssignmentRequestedEventArgs>? DrawingObjectAssetTagAssignmentRequested;

    internal static int ResolvePreviewFrame(
        int initialFrame,
        int frameCount,
        decimal playbackFps,
        TimeSpan elapsed)
    {
        var count = Math.Max(1, frameCount);
        if (count == 1) return 0;

        var start = Math.Clamp(initialFrame, 0, count - 1);
        var fps = Math.Clamp(playbackFps, 1m, 120m);
        var elapsedTicks = Math.Max(0, elapsed.Ticks);
        var advancedFrames = decimal.Floor(elapsedTicks * fps / TimeSpan.TicksPerSecond);
        var offset = (int)(advancedFrames % count);
        return (int)(((long)start + offset) % count);
    }

    internal static int PreviewTimerIntervalMilliseconds(decimal playbackFps)
    {
        var fps = Math.Clamp(playbackFps, 1m, 120m);
        var halfFrameMilliseconds = decimal.Floor(500m / fps);
        return Math.Clamp((int)halfFrameMilliseconds, 8, 50);
    }

    internal static Rectangle ExpandPreviewAnchor(Rectangle sourceScreenBounds, Rectangle rowScreenBounds)
    {
        return new Rectangle(
            sourceScreenBounds.Left,
            rowScreenBounds.Top,
            Math.Max(1, sourceScreenBounds.Width),
            Math.Max(1, rowScreenBounds.Height));
    }

    public LibraryVaultPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        Padding = new Padding(8);

        Theme.StyleToolTip(_toolTip);
        BuildVaultWorkspace();
        _hoverTimer.Tick += (_, _) => ShowPendingPreview();
        VisibleChanged += (_, _) =>
        {
            if (!Visible) HidePreview(clearContent: true);
        };
        UiLocalization.Watch(this);
    }

    public void BindScene(VectorScene scene, Func<int> selectedObjectProvider)
    {
        _scene = scene;
        _selectedObjectProvider = selectedObjectProvider;
    }

    public void BindProject(VectorProject project, Func<int>? frameProvider = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!ReferenceEquals(_project, project))
        {
            if (_project is not null) _project.Changed -= ProjectChanged;
            HidePreview(clearContent: true);
            _project = project;
            _project.Changed += ProjectChanged;
            _activeDrawingObjectId = "";
            _selectedProjectDrawingObjectId = "";
            _selectedAssetFolderId = "";
            _projectRowsDirty = true;
        }

        _frameProvider = frameProvider;
        RefreshProjectObjects();
    }

    public void SetActiveDrawingObject(string? drawingObjectId)
    {
        var next = drawingObjectId ?? "";
        if (string.Equals(_activeDrawingObjectId, next, StringComparison.Ordinal)) return;
        _activeDrawingObjectId = next;
        _selectedProjectDrawingObjectId = next;
        _selectedAssetFolderId = "";
        HidePreview(clearContent: true);
        SynchronizeProjectObjectSelection();
        UpdateActionButtons();
    }

    public void SelectAssetFolder(string? folderId)
    {
        _selectedAssetFolderId = folderId ?? "";
        _selectedProjectDrawingObjectId = "";
        SynchronizeProjectObjectSelection();
    }

    public void RefreshProjectObjects()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(RefreshProjectObjects);
            return;
        }

        var fingerprint = CreateProjectRowsFingerprint();
        if (_projectRowsDirty || fingerprint != _projectRowsFingerprint)
        {
            HidePreview(clearContent: true);
            RebuildProjectObjectRows(fingerprint);
        }
        else
        {
            SynchronizeProjectObjectSelection();
        }
        UpdateVaultSummary();
        UpdateActionButtons();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_project is not null) _project.Changed -= ProjectChanged;
            _hoverTimer.Dispose();
            _assetSearchTimer.Dispose();
            _projectObjectMenu.Dispose();
            _toolTip.Dispose();
            _preview?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ProjectChanged(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ProjectChanged(sender, e));
            return;
        }

        if (!Visible) return;
        RefreshProjectObjects();
    }

    private void BuildVaultWorkspace()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        header.Controls.Add(new Label
        {
            Text = "Project Assets",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(2, 0, 0, 0)
        }, 0, 0);
        _vaultSummary.Dock = DockStyle.Fill;
        _vaultSummary.ForeColor = Theme.Muted;
        _vaultSummary.BackColor = Theme.Panel;
        _vaultSummary.Font = Theme.UiFont(8.5f);
        _vaultSummary.TextAlign = ContentAlignment.MiddleRight;
        _vaultSummary.AutoEllipsis = true;
        _vaultSummary.Margin = new Padding(0, 0, 4, 0);
        _vaultSummary.AccessibleName = "Project asset count";
        header.Controls.Add(_vaultSummary, 1, 0);
        _newFolderButton.Dock = DockStyle.Fill;
        _newFolderButton.Margin = new Padding(1, 3, 1, 3);
        _newFolderButton.Click += (_, _) => RequestNewAssetFolder();
        header.Controls.Add(_newFolderButton, 2, 0);
        Theme.StyleToolbarButton(_newFolderButton);
        _toolTip.SetToolTip(_newFolderButton, "New Folder");

        _openButton = new SvgIconButton(SvgIconKind.Open)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(1, 3, 0, 3),
            AccessibleName = "Open selected symbol"
        };
        _openButton.Click += (_, _) => OpenSelectedVaultItem();
        header.Controls.Add(_openButton, 3, 0);
        Theme.StyleToolbarButton(_openButton);
        _toolTip.SetToolTip(_openButton, "Open selected symbol");
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(BuildAssetFilterBar(), 0, 1);

        _contentHost.Dock = DockStyle.Fill;
        _contentHost.BackColor = Theme.Border;
        _contentHost.Margin = Padding.Empty;
        _contentHost.Padding = new Padding(0, 1, 0, 0);
        layout.Controls.Add(_contentHost, 0, 2);
        ConfigureContentLists();

        ActivateSource(VaultSource.Project, refresh: false);
    }

    private void ConfigureContentLists()
    {
        ConfigureProjectTree();
        ConfigureProjectObjectMenu();

        _projectObjects.Dock = DockStyle.Fill;
        _projectObjects.Margin = Padding.Empty;
        _contentHost.Controls.Add(_projectObjects);
    }

    private void ActivateSource(VaultSource source, bool refresh = true)
    {
        _activeSource = source;
        HidePreview(clearContent: true);
        if (refresh && source == VaultSource.Project) RefreshProjectObjects();

        _library.Visible = source == VaultSource.Library;
        _projectObjects.Visible = source == VaultSource.Project;
        _vault.Visible = source == VaultSource.Stored;
        Control activeList = source switch
        {
            VaultSource.Library => _library,
            VaultSource.Project => _projectObjects,
            _ => _vault
        };
        activeList.BringToFront();

        UpdateActionButtons();
    }

    private static void ConfigureList(ListView list)
    {
        Theme.StyleListView(list);
        list.ShowItemToolTips = false;
    }

    private static void ConfigureResponsiveColumns(ListView list, int preferredFirstColumnWidth)
    {
        void ResizeColumns() => ResizeResponsiveColumns(list, preferredFirstColumnWidth);

        list.Resize += (_, _) => ResizeColumns();
        list.ClientSizeChanged += (_, _) => ResizeColumns();
        ResizeColumns();
    }

    private static void ResizeResponsiveColumns(ListView list, int preferredFirstColumnWidth)
    {
        if (list.Columns.Count < 2 || list.ClientSize.Width <= 0) return;
        var available = Math.Max(144, list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
        var first = Math.Clamp(preferredFirstColumnWidth, 80, Math.Max(80, available - 64));
        list.Columns[0].Width = first;
        list.Columns[1].Width = Math.Max(64, available - first);
    }

    private void ConfigureVaultList(ListView list)
    {
        list.ItemDrag += (_, _) => BeginItemDrag(list);
        list.DoubleClick += (_, _) => OpenSelectedVaultItem();
        list.SelectedIndexChanged += (_, _) => VaultSelectionChanged(list);
        list.MouseMove += (_, e) => UpdateHoverTarget(list, e.Location);
        list.MouseLeave += (_, _) => HidePreview();
        list.MouseWheel += (_, _) => HidePreview();
        list.KeyDown += (_, _) => HidePreview();
    }

    private void ConfigureProjectObjectMenu()
    {
        var newFolder = new ToolStripMenuItem("New Folder");
        newFolder.Click += (_, _) => RequestNewAssetFolder();
        var rename = new ToolStripMenuItem("Rename");
        rename.Click += (_, _) => RaiseSelectedProjectRenameRequest();
        var duplicate = new ToolStripMenuItem("Duplicate");
        duplicate.Click += (_, _) => RaiseSelectedProjectDuplicateRequest();
        var setTags = new ToolStripMenuItem("Set Tags")
        {
            AccessibleName = "Set tags from existing project tags"
        };
        setTags.DropDown.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
        };
        var manageTags = new ToolStripMenuItem("Manage Tags...");
        manageTags.Click += (_, _) => RaiseSelectedAssetTagsRequest();
        var delete = new ToolStripMenuItem("Delete");
        delete.Click += (_, _) => RaiseProjectObjectAssetRequest(DrawingObjectDeleteRequested);
        _projectObjectMenu.Items.AddRange(new ToolStripItem[]
        {
            newFolder,
            new ToolStripSeparator(),
            rename,
            duplicate,
            setTags,
            manageTags,
            new ToolStripSeparator(),
            delete
        });
        _projectObjectMenu.Opening += (_, e) =>
        {
            HidePreview();
            var selected = SelectedProjectRow();
            var drawingObjectSelected = selected?.DrawingObject is not null;
            var folderSelected = selected?.Folder is not null;
            rename.Enabled = drawingObjectSelected || folderSelected;
            duplicate.Enabled = rename.Enabled;
            setTags.Visible = drawingObjectSelected;
            setTags.Enabled = drawingObjectSelected && (_project?.AssetTags.Count ?? 0) > 0;
            PopulateAssetTagAssignmentMenu(setTags, selected?.DrawingObject);
            manageTags.Visible = drawingObjectSelected;
            manageTags.Enabled = drawingObjectSelected;
            delete.Visible = drawingObjectSelected;
            delete.Enabled = drawingObjectSelected && (_project?.DrawingObjects.Count ?? 0) > 1;
        };
        _projectObjects.ContextMenuStrip = _projectObjectMenu;
        _projectObjects.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            HidePreview();
            _projectObjects.SelectedNode = _projectObjects.GetNodeAt(e.Location);
        };
    }

    private void ConfigureProjectTree()
    {
        Theme.StyleTreeView(
            _projectObjects,
            useCustomExpandButtons: true,
            animateRows: true,
            useSolidFocusCue: true);
        _projectObjects.AllowDrop = true;
        _projectObjects.Dock = DockStyle.Fill;
        _projectObjects.Margin = Padding.Empty;
        _projectObjects.AfterSelect += (_, _) => ProjectTreeSelectionChanged();
        _projectObjects.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node.Tag is VaultRow { Folder: not null })
            {
                if (Theme.IsTreeExpandGlyphHit(_projectObjects, e.Node, e.Location)) return;
                if (e.Node.IsExpanded) e.Node.Collapse(ignoreChildren: true);
                else e.Node.Expand();
                return;
            }
            OpenSelectedVaultItem();
        };
        _projectObjects.ItemDrag += (_, e) => BeginProjectItemDrag(e.Item as TreeNode);
        _projectObjects.DragEnter += (_, e) => e.Effect = ResolveProjectTreeDropEffect(e.Data);
        _projectObjects.DragOver += (_, e) => e.Effect = ResolveProjectTreeDropEffect(e.Data);
        _projectObjects.DragDrop += (_, e) => CompleteProjectTreeDrop(e);
        _projectObjects.MouseMove += (_, e) => UpdateProjectHoverTarget(e.Location);
        _projectObjects.MouseLeave += (_, _) => HidePreview();
        _projectObjects.MouseWheel += (_, _) => HidePreview();
        _projectObjects.KeyDown += (_, _) => HidePreview();
        _projectObjects.ShowNodeToolTips = true;
    }

    private DrawingObjectDefinition? SelectedProjectDrawingObject()
    {
        return SelectedProjectRow()?.DrawingObject;
    }

    private VaultRow? SelectedProjectRow() => _projectObjects.SelectedNode?.Tag as VaultRow;

    private void RaiseSelectedProjectRenameRequest()
    {
        var row = SelectedProjectRow();
        if (row?.Folder is not null)
        {
            AssetFolderRenameRequested?.Invoke(this, new ProjectAssetFolderRequestedEventArgs(row.Folder.Id));
            return;
        }
        RaiseProjectObjectAssetRequest(DrawingObjectRenameRequested);
    }

    private void RaiseSelectedProjectDuplicateRequest()
    {
        var row = SelectedProjectRow();
        if (row?.Folder is not null)
        {
            AssetFolderDuplicateRequested?.Invoke(this, new ProjectAssetFolderRequestedEventArgs(row.Folder.Id));
            return;
        }
        RaiseProjectObjectAssetRequest(DrawingObjectDuplicateRequested);
    }

    private void RequestNewAssetFolder()
    {
        var row = SelectedProjectRow();
        var parentFolderId = row?.Folder?.Id ?? row?.DrawingObject?.AssetFolderId ?? "";
        AssetFolderCreateRequested?.Invoke(this, new ProjectAssetFolderCreateRequestedEventArgs(parentFolderId));
    }

    private void RaiseProjectObjectAssetRequest(EventHandler<DrawingObjectAssetRequestedEventArgs>? requested)
    {
        var drawingObject = SelectedProjectDrawingObject();
        if (drawingObject is null) return;
        requested?.Invoke(this, new DrawingObjectAssetRequestedEventArgs(drawingObject.Id));
    }

    private void RaiseSelectedAssetTagsRequest()
    {
        var drawingObject = SelectedProjectDrawingObject();
        if (drawingObject is null) return;
        DrawingObjectAssetTagsRequested?.Invoke(
            this,
            new DrawingObjectAssetTagsRequestedEventArgs(drawingObject.Id));
    }

    private static Button AddVaultButton(Control parent, string text, int width, Action action)
    {
        var button = new Button { Text = text, Width = width, Height = 30, Margin = new Padding(0, 0, 6, 6) };
        Theme.StyleButton(button);
        button.Click += (_, _) => action();
        parent.Controls.Add(button);
        return button;
    }

    private void BeginItemDrag(ListView list)
    {
        if (list.SelectedItems.Count == 0) return;
        var tag = list.SelectedItems[0].Tag;
        if (tag is VaultItem libraryItem)
        {
            list.DoDragDrop(libraryItem, DragDropEffects.Copy);
            return;
        }

        if (tag is not VaultRow row) return;
        var data = new DataObject();
        data.SetData(typeof(VaultItem), row.Item);
        if (row.DrawingObject is not null && _project is not null)
        {
            data.SetData(
                typeof(DrawingObjectDragData),
                new DrawingObjectDragData(_project.Id, row.DrawingObject.Id));
        }

        list.DoDragDrop(data, DragDropEffects.Copy);
    }

    private void BeginProjectItemDrag(TreeNode? node)
    {
        if (node?.Tag is not VaultRow row || _project is null) return;
        HidePreview();
        var data = new DataObject();
        if (row.Folder is not null)
        {
            data.SetData(
                typeof(ProjectAssetFolderDragData),
                new ProjectAssetFolderDragData(_project.Id, row.Folder.Id));
            _projectObjects.DoDragDrop(data, DragDropEffects.Move);
            return;
        }
        if (row.DrawingObject is null) return;

        data.SetData(typeof(VaultItem), row.Item);
        data.SetData(
            typeof(DrawingObjectDragData),
            new DrawingObjectDragData(_project.Id, row.DrawingObject.Id));
        _projectObjects.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private DragDropEffects ResolveProjectTreeDropEffect(IDataObject? data)
    {
        if (_project is null || data is null) return DragDropEffects.None;
        if (data.GetData(typeof(ProjectAssetFolderDragData)) is ProjectAssetFolderDragData folder
            && string.Equals(folder.ProjectId, _project.Id, StringComparison.Ordinal))
        {
            return DragDropEffects.Move;
        }
        if (data.GetData(typeof(DrawingObjectDragData)) is DrawingObjectDragData drawingObject
            && string.Equals(drawingObject.ProjectId, _project.Id, StringComparison.Ordinal))
        {
            return DragDropEffects.Move;
        }
        return DragDropEffects.None;
    }

    private void CompleteProjectTreeDrop(DragEventArgs e)
    {
        if (_project is null) return;
        var targetFolderId = ProjectTreeDropFolderId(new Point(e.X, e.Y));
        if (e.Data?.GetData(typeof(ProjectAssetFolderDragData)) is ProjectAssetFolderDragData folder
            && string.Equals(folder.ProjectId, _project.Id, StringComparison.Ordinal))
        {
            AssetFolderMoveRequested?.Invoke(
                this,
                new ProjectAssetMoveRequestedEventArgs(folder.FolderId, targetFolderId));
            return;
        }
        if (e.Data?.GetData(typeof(DrawingObjectDragData)) is DrawingObjectDragData drawingObject
            && string.Equals(drawingObject.ProjectId, _project.Id, StringComparison.Ordinal))
        {
            DrawingObjectMoveRequested?.Invoke(
                this,
                new ProjectAssetMoveRequestedEventArgs(drawingObject.DrawingObjectId, targetFolderId));
        }
    }

    private string ProjectTreeDropFolderId(Point screenPoint)
    {
        var node = _projectObjects.GetNodeAt(_projectObjects.PointToClient(screenPoint));
        return node?.Tag switch
        {
            VaultRow { Folder: { } folder } => folder.Id,
            VaultRow { DrawingObject: { } drawingObject } => drawingObject.AssetFolderId,
            _ => ""
        };
    }

    private void AddLibraryPreset(string name, string kind, string detail)
    {
        var item = new ListViewItem(name);
        item.SubItems.Add(kind);
        item.Tag = new VaultItem
        {
            Kind = kind,
            Name = name,
            Detail = detail,
            Payload = detail
        };
        _library.Items.Add(item);
    }

    private void AddSelectedLibraryPreset()
    {
        if (_library.SelectedItems.Count == 0) return;
        if (_library.SelectedItems[0].Tag is not VaultItem preset) return;
        AddVaultItem(CopyVaultItem(preset));
        ActivateSource(VaultSource.Stored, refresh: false);
    }

    private void StoreSelectedProjectObject()
    {
        if (SelectedProjectRow() is not VaultRow { IsProjectObject: true } row) return;
        AddVaultItem(CopyVaultItem(row.Item));
        ActivateSource(VaultSource.Stored, refresh: false);
    }

    private void EnableVaultDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) =>
        {
            e.Effect = e.Data?.GetDataPresent(typeof(VaultItem)) == true ? DragDropEffects.Copy : DragDropEffects.None;
        };
        control.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(typeof(VaultItem)) is not VaultItem item) return;
            AddVaultItem(CopyVaultItem(item));
        };
    }

    private static VaultItem CopyVaultItem(VaultItem item)
    {
        return new VaultItem
        {
            Kind = item.Kind,
            Name = item.Name,
            Detail = item.Detail,
            Payload = item.Payload,
            ReferenceKind = item.ReferenceKind,
            ReferenceId = item.ReferenceId
        };
    }

    private void CaptureSelectedObject()
    {
        if (_scene is null || _selectedObjectProvider is null) return;
        var selected = _selectedObjectProvider();
        if (selected < 0 || selected >= _scene.ObjectCount)
        {
            ModernMessageDialog.Show(this, UiLocalization.T("Select an object on the stage first."), UiLocalization.T("Vault"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var shape = _scene.ShapeKind.Length > selected ? _scene.ShapeKind[selected] : ShapeKind.Rectangle;
        var layer = _scene.ObjectLayer.Length > selected ? _scene.ObjectLayer[selected] : 0;
        var color = Color.FromArgb(_scene.Argb[selected]);
        var payload = string.Join(Environment.NewLine, new[]
        {
            $"Object: {selected}",
            $"Kind: {shape}",
            $"Layer: {layer}",
            $"Center: {_scene.X[selected]:0.##}, {_scene.Y[selected]:0.##}",
            $"Size: {_scene.Width[selected]:0.##} x {_scene.Height[selected]:0.##} vu",
            $"Stroke: {VectorUnits.UnitsToStrokePoints(_scene.Stroke[selected]):0.##} pt / {_scene.Stroke[selected]:0.##} vu",
            $"Color: #{color.ToArgb() & 0x00FFFFFF:X6}"
        });

        AddVaultItem(new VaultItem
        {
            Kind = "Object Snapshot",
            Name = $"{shape} #{selected}",
            Detail = $"Layer {layer}, {_scene.Width[selected]:0.#} x {_scene.Height[selected]:0.#} vu",
            Payload = payload
        });
    }

    private void AddNote()
    {
        if (!PromptDialog.TryAsk(this, "Vault Note", "Note text", out var note)) return;
        AddVaultItem(new VaultItem
        {
            Kind = "Note",
            Name = note.Length > 32 ? note[..32] : note,
            Detail = note,
            Payload = note
        });
    }

    private void AddFileReference()
    {
        using var dialog = new OpenFileDialog
        {
            Title = UiLocalization.T("Add file reference to Vault"),
            Filter = $"{UiLocalization.T("All files")} (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        AddVaultItem(new VaultItem
        {
            Kind = "File Reference",
            Name = Path.GetFileName(dialog.FileName),
            Detail = dialog.FileName,
            Payload = dialog.FileName
        });
    }

    private void RemoveSelectedVaultItem()
    {
        var row = SelectedVaultRow();
        if (row is null || row.IsProjectObject) return;
        HidePreview(clearContent: true);
        _vaultItems.RemoveAll(candidate => candidate.Id == row.Item.Id);
        SaveVault();
        RefreshVault();
    }

    private void OpenSelectedVaultItem()
    {
        var row = SelectedVaultRow();
        if (row is null) return;

        if (row.DrawingObject is not null)
        {
            DrawingObjectOpenRequested?.Invoke(this, new DrawingObjectOpenRequestedEventArgs(row.DrawingObject.Id));
            return;
        }

        var item = row.Item;
        if (string.Equals(item.ReferenceKind, "DrawingObject", StringComparison.Ordinal))
        {
            ModernMessageDialog.Show(this, UiLocalization.T("This symbol is not available in the current project."), item.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (item.Kind == "File Reference" && File.Exists(item.Payload))
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.Payload,
                UseShellExecute = true
            });
            return;
        }

        ModernMessageDialog.Show(this, UiLocalization.T(item.Payload.Length > 0 ? item.Payload : item.Detail), item.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void AddVaultItem(VaultItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ReferenceKind)
            && !string.IsNullOrWhiteSpace(item.ReferenceId)
            && _vaultItems.Any(candidate =>
                string.Equals(candidate.ReferenceKind, item.ReferenceKind, StringComparison.Ordinal)
                && string.Equals(candidate.ReferenceId, item.ReferenceId, StringComparison.Ordinal)))
        {
            return;
        }

        _vaultItems.Insert(0, item);
        SaveVault();
        RefreshVault();
    }

    private void RefreshVault()
    {
        HidePreview(clearContent: true);
        RefreshProjectObjects();

        _vault.BeginUpdate();
        _vault.Items.Clear();
        foreach (var item in _vaultItems)
        {
            var drawingObject = ResolveDrawingObject(item.ReferenceKind, item.ReferenceId);
            var row = new ListViewItem(drawingObject?.Name ?? item.Name);
            row.SubItems.Add(item.Kind);
            row.Tag = new VaultRow(item, drawingObject, IsProjectObject: false);
            _vault.Items.Add(row);
        }

        if (_vault.Items.Count == 0) AddEmptyRow(_vault, "Vault is empty", "No stored items");
        _vault.EndUpdate();
        UpdateVaultSummary();
        UpdateActionButtons();
    }

    private void RebuildProjectObjectRows(ProjectRowsFingerprint fingerprint)
    {
        var expandedFolderIds = EnumerateProjectNodes()
            .Where(node => node.IsExpanded && node.Tag is VaultRow { Folder: not null })
            .Select(node => ((VaultRow)node.Tag!).Folder!.Id)
            .ToHashSet(StringComparer.Ordinal);
        SuspendLayout();
        _projectObjects.BeginUpdate();
        try
        {
            _syncingSelection = true;
            _projectObjects.Nodes.Clear();
            RefreshAssetTagFilterItems();
            PrepareAssetFilterCache();
            if (_project is not null)
            {
                AddProjectFolderBranches("", _projectObjects.Nodes, expandedFolderIds);
                AddProjectDrawingObjectNodes("", _projectObjects.Nodes, includeAll: false);
            }

            if (_projectObjects.Nodes.Count == 0)
            {
                var emptyText = AssetFilterActive ? "No matching assets" : "No symbols";
                _projectObjects.Nodes.Add(new TreeNode(emptyText) { ForeColor = Theme.Muted });
            }
            SynchronizeProjectObjectSelection();
            _projectRowsFingerprint = fingerprint;
            _projectRowsDirty = false;
        }
        finally
        {
            _syncingSelection = false;
            _projectObjects.EndUpdate();
            ResumeLayout(performLayout: false);
            Theme.RefreshTreeViewRowInteraction(_projectObjects);
        }
    }

    private void AddProjectFolderBranches(
        string parentFolderId,
        TreeNodeCollection nodes,
        ISet<string> expandedFolderIds,
        bool includeAll = false)
    {
        if (_project is null) return;
        foreach (var folder in _project.AssetFolders.Where(candidate =>
                     string.Equals(candidate.ParentFolderId, parentFolderId, StringComparison.Ordinal)))
        {
            var includeFolderContents = includeAll || AssetFolderNameMatches(folder);
            if (!includeFolderContents && !AssetFolderHasMatchingContent(folder.Id)) continue;
            var row = new VaultRow(
                new VaultItem { Kind = "Folder", Name = folder.Name, Detail = "Project asset folder" },
                DrawingObject: null,
                IsProjectObject: false,
                Folder: folder);
            var node = new TreeNode($"{folder.Name}/")
            {
                Tag = row,
                ForeColor = Theme.AccentLabel
            };
            nodes.Add(node);
            AddProjectFolderBranches(folder.Id, node.Nodes, expandedFolderIds, includeFolderContents);
            AddProjectDrawingObjectNodes(folder.Id, node.Nodes, includeFolderContents);
            if (expandedFolderIds.Contains(folder.Id)) node.Expand();
        }
    }

    private void AddProjectDrawingObjectNodes(string folderId, TreeNodeCollection nodes, bool includeAll)
    {
        if (_project is null) return;
        foreach (var drawingObject in _project.DrawingObjects.Where(candidate =>
                     string.Equals(candidate.AssetFolderId, folderId, StringComparison.Ordinal)))
        {
            if (!includeAll && !AssetMatchesFilter(drawingObject)) continue;
            var item = drawingObject.ToVaultItem();
            item.Detail = DrawingObjectDetail(drawingObject);
            var node = new TreeNode(drawingObject.Name)
            {
                Tag = new VaultRow(item, drawingObject, IsProjectObject: true)
            };
            ApplyAssetTagNodeStyle(node, drawingObject);
            nodes.Add(node);
        }
    }

    private ProjectRowsFingerprint CreateProjectRowsFingerprint()
    {
        if (_project is null) return default;
        var hash = new HashCode();
        foreach (var drawingObject in _project.DrawingObjects)
        {
            hash.Add(drawingObject.Id, StringComparer.Ordinal);
            hash.Add(drawingObject.Name, StringComparer.Ordinal);
            hash.Add(drawingObject.Kind, StringComparer.Ordinal);
            hash.Add(drawingObject.Detail, StringComparer.Ordinal);
            hash.Add(drawingObject.Scene.ObjectCount);
            hash.Add(drawingObject.Scene.LayerCount);
            hash.Add(drawingObject.Instances.Count);
            hash.Add(drawingObject.AssetFolderId, StringComparer.Ordinal);
            foreach (var tagId in drawingObject.AssetTagIds) hash.Add(tagId, StringComparer.Ordinal);
        }

        foreach (var tag in _project.AssetTags)
        {
            hash.Add(tag.Id, StringComparer.Ordinal);
            hash.Add(tag.Name, StringComparer.Ordinal);
            hash.Add(tag.ColorArgb);
        }

        foreach (var folder in _project.AssetFolders)
        {
            hash.Add(folder.Id, StringComparer.Ordinal);
            hash.Add(folder.Name, StringComparer.Ordinal);
            hash.Add(folder.ParentFolderId, StringComparer.Ordinal);
        }

        return new ProjectRowsFingerprint(
            _project.DrawingObjects.Count,
            _project.AssetFolders.Count,
            hash.ToHashCode());
    }

    private void SynchronizeProjectObjectSelection()
    {
        if (_projectObjects.Nodes.Count == 0) return;
        var previousSync = _syncingSelection;
        _syncingSelection = true;
        try
        {
            TreeNode? selectedNode = null;
            var preferredDrawingObjectId = _selectedProjectDrawingObjectId.Length > 0
                ? _selectedProjectDrawingObjectId
                : _activeDrawingObjectId;
            foreach (var node in EnumerateProjectNodes())
            {
                if (_selectedAssetFolderId.Length > 0
                    && node.Tag is VaultRow { Folder: { } folder }
                    && string.Equals(folder.Id, _selectedAssetFolderId, StringComparison.Ordinal))
                {
                    selectedNode = node;
                    break;
                }
                if (_selectedAssetFolderId.Length == 0
                    && node.Tag is VaultRow { DrawingObject: { } drawingObject }
                    && string.Equals(drawingObject.Id, preferredDrawingObjectId, StringComparison.Ordinal))
                {
                    selectedNode = node;
                }
            }
            _projectObjects.SelectedNode = selectedNode;
            selectedNode?.EnsureVisible();
        }
        finally
        {
            _syncingSelection = previousSync;
        }
    }

    private IEnumerable<TreeNode> EnumerateProjectNodes()
    {
        foreach (TreeNode root in _projectObjects.Nodes)
        {
            foreach (var node in EnumerateProjectNodes(root)) yield return node;
        }
    }

    private static IEnumerable<TreeNode> EnumerateProjectNodes(TreeNode node)
    {
        yield return node;
        foreach (TreeNode child in node.Nodes)
        {
            foreach (var descendant in EnumerateProjectNodes(child)) yield return descendant;
        }
    }

    private static string DrawingObjectDetail(DrawingObjectDefinition drawingObject)
    {
        return $"{drawingObject.Scene.ObjectCount} objects, {drawingObject.Scene.LayerCount} layers, "
            + $"{drawingObject.Instances.Count} nested instances";
    }

    private static void AddEmptyRow(ListView list, string name, string detail)
    {
        var row = new ListViewItem(name) { ForeColor = Theme.Muted };
        row.SubItems.Add(detail);
        list.Items.Add(row);
    }

    private DrawingObjectDefinition? ResolveDrawingObject(string referenceKind, string referenceId)
    {
        if (_project is null
            || !string.Equals(referenceKind, "DrawingObject", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(referenceId))
        {
            return null;
        }

        return _project.DrawingObjects.FirstOrDefault(item => string.Equals(item.Id, referenceId, StringComparison.Ordinal));
    }

    private void UpdateVaultSummary()
    {
        var projectCount = _project?.DrawingObjects.Count ?? 0;
        _vaultSummary.Text = $"({projectCount})";
    }

    private void VaultSelectionChanged(ListView source)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try
        {
            if (source.SelectedItems.Count > 0)
            {
                _projectObjects.SelectedNode = null;
                var other = ReferenceEquals(source, _vault) ? _library : _vault;
                other.SelectedItems.Clear();
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        UpdateActionButtons();
    }

    private void ProjectTreeSelectionChanged()
    {
        if (_syncingSelection) return;
        var row = SelectedProjectRow();
        _selectedAssetFolderId = row?.Folder?.Id ?? "";
        _selectedProjectDrawingObjectId = row?.DrawingObject?.Id ?? "";
        UpdateActionButtons();
    }

    private VaultRow? SelectedVaultRow()
    {
        return _activeSource switch
        {
            VaultSource.Project => SelectedProjectRow(),
            VaultSource.Stored when _vault.SelectedItems.Count > 0 && _vault.SelectedItems[0].Tag is VaultRow vaultRow => vaultRow,
            _ => null
        };
    }

    private void UpdateActionButtons()
    {
        var row = SelectedVaultRow();
        var projectSelected = row is { DrawingObject: not null, IsProjectObject: true };
        SetActionState(_openButton, visible: true, enabled: projectSelected);
        UpdateAssetTagActions(projectSelected);
    }

    private static void SetActionState(Button? button, bool visible, bool enabled)
    {
        if (button is null) return;
        button.Visible = visible;
        button.Enabled = enabled;
    }

    private void UpdateHoverTarget(ListView list, Point location)
    {
        var index = list.GetItemAt(location.X, location.Y)?.Index ?? -1;
        if (ReferenceEquals(_hoverList, list) && _hoverIndex == index) return;

        _hoverTimer.Stop();
        _hoverList = list;
        _hoverIndex = index;
        if (index < 0 || index >= list.Items.Count || list.Items[index].Tag is not VaultRow)
        {
            HidePreview();
            return;
        }

        _hoverTimer.Start();
    }

    private void UpdateProjectHoverTarget(Point location)
    {
        var node = _projectObjects.GetNodeAt(location);
        if (ReferenceEquals(_hoverProjectNode, node)) return;

        _hoverTimer.Stop();
        _hoverList = null;
        _hoverIndex = -1;
        _hoverProjectNode = node?.Tag is VaultRow { DrawingObject: not null } ? node : null;
        if (_hoverProjectNode is null)
        {
            HidePreview();
            return;
        }
        _hoverTimer.Start();
    }

    private void ShowPendingPreview()
    {
        _hoverTimer.Stop();
        if (_hoverProjectNode is { TreeView: not null } projectNode
            && projectNode.Tag is VaultRow { DrawingObject: { } drawingObject }
            && ReferenceEquals(_projectObjects.GetNodeAt(_projectObjects.PointToClient(Cursor.Position)), projectNode)
            && _project is not null)
        {
            _preview ??= new VaultPreviewForm();
            var projectFrame = Math.Max(0, _frameProvider?.Invoke() ?? drawingObject.Scene.EditFrame);
            _preview.ShowDrawingObject(drawingObject, _project, projectFrame);
            _preview.ShowAt(FindForm(), PreviewAnchor(_projectObjects, projectNode.Bounds));
            return;
        }

        var list = _hoverList;
        var index = _hoverIndex;
        if (list is null || list.IsDisposed || index < 0 || index >= list.Items.Count) return;
        var pointer = list.PointToClient(Cursor.Position);
        if (list.GetItemAt(pointer.X, pointer.Y)?.Index != index || list.Items[index].Tag is not VaultRow row) return;

        _preview ??= new VaultPreviewForm();
        var frame = Math.Max(0, _frameProvider?.Invoke() ?? row.DrawingObject?.Scene.EditFrame ?? 0);
        if (row.DrawingObject is not null && _project is not null)
        {
            _preview.ShowDrawingObject(row.DrawingObject, _project, frame);
        }
        else
        {
            _preview.ShowVaultItem(row.Item);
        }

        var itemBounds = list.GetItemRect(index);
        _preview.ShowAt(FindForm(), PreviewAnchor(list, itemBounds));
    }

    private static Rectangle PreviewAnchor(Control source, Rectangle rowBounds)
    {
        return ExpandPreviewAnchor(
            source.RectangleToScreen(source.ClientRectangle),
            source.RectangleToScreen(rowBounds));
    }

    private void HidePreview(bool clearContent = false)
    {
        _hoverTimer.Stop();
        _hoverList = null;
        _hoverIndex = -1;
        _hoverProjectNode = null;
        if (_preview is null) return;
        _preview.Hide();
        if (clearContent) _preview.ClearContent();
    }

    private void LoadVault()
    {
        _vaultItems.Clear();
        var path = VaultPath();
        if (!File.Exists(path)) return;
        try
        {
            var items = JsonSerializer.Deserialize<List<VaultItem>>(File.ReadAllText(path));
            if (items is not null) _vaultItems.AddRange(items);
        }
        catch
        {
            _vaultItems.Add(new VaultItem
            {
                Kind = "System",
                Name = "Vault load failed",
                Detail = "The vault file could not be parsed.",
                Payload = path
            });
        }
    }

    private void SaveVault()
    {
        var path = VaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(_vaultItems, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static string VaultPath() => Path.Combine(Directory.GetCurrentDirectory(), "data", "vault.json");

    private sealed class VaultPreviewForm : Form
    {
        private const int PreviewWidth = 382;
        private const int PreviewHeight = 314;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;

        private readonly VectorScene _emptyScene = new();
        private readonly VectorScene _underlayScene = new();
        private readonly StageControl _stage;
        private readonly System.Windows.Forms.Timer _playbackTimer = new();
        private readonly Label _emptyState = new();
        private readonly Label _title = new();
        private readonly Label _kind = new();
        private readonly Label _metadata = new();
        private readonly Label _detail = new();
        private VectorScene? _contentScene;
        private VectorScene? _contentUnderlay;
        private int _contentFrame;
        private DrawingObjectDefinition? _drawingObject;
        private VectorProject? _project;
        private bool _hasFittedContent;
        private int _playbackStartFrame;
        private decimal _playbackFps = 30m;
        private long _playbackStartedAt;

        public VaultPreviewForm()
        {
            _emptyScene.CreateEmpty();
            _underlayScene.CreateEmpty();
            _stage = new StageControl(_emptyScene) { Dock = DockStyle.Fill, TabStop = false };
            _playbackTimer.Tick += (_, _) => AdvancePlayback();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(PreviewWidth, PreviewHeight);
            BackColor = Theme.BorderHover;
            Padding = new Padding(1);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Stage,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                ColumnCount = 1,
                RowCount = 2
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            Controls.Add(root);

            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Stage, Margin = Padding.Empty };
            root.Controls.Add(content, 0, 0);

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.PanelStrong,
                Padding = new Padding(12, 7, 12, 7),
                Margin = Padding.Empty,
                ColumnCount = 1,
                RowCount = 4
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(footer, 0, 1);

            ConfigurePreviewLabel(_title, Theme.Text, Theme.UiFont(10, FontStyle.Bold));
            ConfigurePreviewLabel(_kind, Theme.AccentLabel, Theme.UiFont(9));
            ConfigurePreviewLabel(_metadata, Theme.Muted, Theme.UiFont(8.8f));
            ConfigurePreviewLabel(_detail, Theme.Muted, Theme.UiFont(8.6f));
            footer.Controls.Add(_title, 0, 0);
            footer.Controls.Add(_kind, 0, 1);
            footer.Controls.Add(_metadata, 0, 2);
            footer.Controls.Add(_detail, 0, 3);

            _emptyState.Dock = DockStyle.Fill;
            _emptyState.BackColor = Theme.Stage;
            _emptyState.ForeColor = Theme.Muted;
            _emptyState.Font = Theme.UiFont(9.5f, FontStyle.Bold);
            _emptyState.TextAlign = ContentAlignment.MiddleCenter;
            _emptyState.AutoEllipsis = true;
            content.Controls.Add(_stage);
            content.Controls.Add(_emptyState);
            _emptyState.BringToFront();
            UiLocalization.Watch(this);
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) StopPlayback();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) StopPlayback();
            base.Dispose(disposing);
            if (disposing) _playbackTimer.Dispose();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
                return parameters;
            }
        }

        public void ShowDrawingObject(DrawingObjectDefinition drawingObject, VectorProject project, int frame)
        {
            StopPlayback();
            frame = Math.Clamp(frame, 0, Math.Max(0, drawingObject.FrameCount - 1));
            _drawingObject = drawingObject;
            _project = project;
            _hasFittedContent = false;
            _stage.BindScene(drawingObject.Scene);
            _contentScene = drawingObject.Scene;
            _contentUnderlay = _underlayScene;
            _title.Text = drawingObject.Name;
            _kind.Text = $"Kind: {drawingObject.Kind}";
            _detail.Text = string.IsNullOrWhiteSpace(drawingObject.Detail)
                ? $"Created {drawingObject.CreatedAt:g}"
                : drawingObject.Detail;
            RenderDrawingObjectFrame(frame);
        }

        public void ShowVaultItem(VaultItem item)
        {
            StopPlayback();
            _drawingObject = null;
            _project = null;
            _hasFittedContent = false;
            _stage.BindScene(_emptyScene);
            _stage.Frame = 0;
            _stage.BindUnderlayScene(null);
            _stage.ResetDefaultView();
            _contentScene = null;
            _contentUnderlay = null;
            _contentFrame = 0;
            _emptyState.Text = string.Equals(item.ReferenceKind, "DrawingObject", StringComparison.Ordinal)
                ? "Symbol unavailable"
                : "No visual preview";
            _emptyState.Visible = true;
            _title.Text = item.Name;
            _kind.Text = item.Kind;
            _metadata.Text = $"Stored {item.CreatedAt:g}";
            _detail.Text = SingleLine(item.Detail.Length > 0 ? item.Detail : item.Payload);
        }

        public void ClearContent()
        {
            StopPlayback();
            _drawingObject = null;
            _project = null;
            _hasFittedContent = false;
            _underlayScene.CreateEmpty();
            _stage.BindScene(_emptyScene);
            _stage.Frame = 0;
            _stage.BindUnderlayScene(null);
            _contentScene = null;
            _contentUnderlay = null;
            _contentFrame = 0;
            _emptyState.Text = "No preview";
            _emptyState.Visible = true;
            _title.Text = "";
            _kind.Text = "";
            _metadata.Text = "";
            _detail.Text = "";
        }

        public void ShowAt(IWin32Window? owner, Rectangle anchor)
        {
            var workingArea = Screen.FromRectangle(anchor).WorkingArea;
            var x = anchor.Right + 10;
            if (x + Width > workingArea.Right) x = anchor.Left - Width - 10;
            x = Math.Clamp(x, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - Width));
            var y = Math.Clamp(anchor.Top, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - Height));
            Location = new Point(x, y);

            if (!Visible) PerformLayout();
            FitContent();
            if (!Visible)
            {
                if (owner is null) Show();
                else Show(owner);
            }

            FitContent();
            Invalidate(true);
            StartPlayback();
        }

        private void StartPlayback()
        {
            if (_drawingObject is null || _project is null || _drawingObject.FrameCount <= 1) return;

            _playbackStartFrame = _contentFrame;
            _playbackFps = Math.Clamp(_project.PlaybackFps, 1m, 120m);
            _playbackStartedAt = Stopwatch.GetTimestamp();
            _playbackTimer.Interval = PreviewTimerIntervalMilliseconds(_playbackFps);
            _playbackTimer.Start();
        }

        private void StopPlayback()
        {
            _playbackTimer.Stop();
            _playbackStartedAt = 0;
        }

        private void AdvancePlayback()
        {
            if (!Visible || _drawingObject is null || _project is null || _drawingObject.FrameCount <= 1)
            {
                StopPlayback();
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var fps = Math.Clamp(_project.PlaybackFps, 1m, 120m);
            if (_playbackStartedAt == 0 || fps != _playbackFps)
            {
                _playbackStartFrame = _contentFrame;
                _playbackFps = fps;
                _playbackStartedAt = now;
                _playbackTimer.Interval = PreviewTimerIntervalMilliseconds(fps);
                return;
            }

            var frame = ResolvePreviewFrame(
                _playbackStartFrame,
                _drawingObject.FrameCount,
                fps,
                Stopwatch.GetElapsedTime(_playbackStartedAt, now));
            if (frame != _contentFrame) RenderDrawingObjectFrame(frame);
        }

        private void RenderDrawingObjectFrame(int frame)
        {
            if (_drawingObject is null || _project is null) return;

            frame = Math.Clamp(frame, 0, Math.Max(0, _drawingObject.FrameCount - 1));
            _underlayScene.CreateEmpty();
            try
            {
                SceneCompositionBuilder.BuildDrawingObjectChildren(
                    _underlayScene,
                    _drawingObject,
                    _project.DrawingObjects,
                    frame,
                    _project.PlaybackFps);
            }
            catch (Exception ex)
            {
                _underlayScene.CreateEmpty();
                AppLog.Error("Vault symbol preview composition failed", ex);
            }

            _stage.Frame = frame;
            _stage.BindUnderlayScene(_underlayScene);
            _contentFrame = frame;

            var objectCount = _drawingObject.Scene.ObjectCount + _underlayScene.ObjectCount;
            var activeObjectCount = CountActiveObjects(_drawingObject.Scene, frame)
                + CountActiveObjects(_underlayScene, frame);
            _emptyState.Text = objectCount == 0 ? "Empty symbol" : "No content at this frame";
            _emptyState.Visible = activeObjectCount == 0;
            _metadata.Text = $"{activeObjectCount}/{objectCount} visible | {_drawingObject.Scene.LayerCount} layers | {_drawingObject.Instances.Count} nested | frame {frame + 1}/{_drawingObject.FrameCount}";
            if (Visible && !_hasFittedContent && activeObjectCount > 0) FitContent();
        }

        private void FitContent()
        {
            if (_contentScene is null || _stage.Width <= 0 || _stage.Height <= 0) return;
            var hasBounds = false;
            var bounds = RectangleF.Empty;
            if (_drawingObject is { FrameCount: > 1 }
                && ReferenceEquals(_contentScene, _drawingObject.Scene))
            {
                IncludeAllFrameSceneBounds(_contentScene, ref hasBounds, ref bounds);
            }
            else
            {
                IncludeSceneBounds(_contentScene, _contentFrame, ref hasBounds, ref bounds);
            }
            if (_contentUnderlay is not null) IncludeSceneBounds(_contentUnderlay, _contentFrame, ref hasBounds, ref bounds);
            if (!hasBounds)
            {
                _hasFittedContent = false;
                _stage.ResetDefaultView();
                return;
            }

            var aspect = _stage.Width / (float)Math.Max(1, _stage.Height);
            var visibleWidth = Math.Max(bounds.Width, bounds.Height * aspect);
            visibleWidth = Math.Clamp(Math.Max(120, visibleWidth * 1.22f), 1, _contentScene.StageWidth);
            _stage.SetVisibleWorldWidth(visibleWidth);
            var center = new PointF(bounds.Left + bounds.Width * 0.5f, bounds.Top + bounds.Height * 0.5f);
            var screenCenter = _stage.WorldToScreen(center.X, center.Y);
            _stage.Pan(_stage.Width * 0.5f - screenCenter.X, _stage.Height * 0.5f - screenCenter.Y);
            _hasFittedContent = true;
        }

        private static void IncludeAllFrameSceneBounds(VectorScene scene, ref bool hasBounds, ref RectangleF bounds)
        {
            for (var i = 0; i < scene.ObjectCount; i++)
            {
                var layer = scene.ObjectLayer[i];
                if (layer >= scene.LayerCount || !scene.IsLayerEffectivelyVisible(layer)) continue;
                var objectBounds = scene.GetObjectWorldBounds(i);
                bounds = hasBounds ? RectangleF.Union(bounds, objectBounds) : objectBounds;
                hasBounds = true;
            }
        }

        private static void IncludeSceneBounds(VectorScene scene, int frame, ref bool hasBounds, ref RectangleF bounds)
        {
            for (var i = 0; i < scene.ObjectCount; i++)
            {
                if (!scene.IsObjectActive(i, frame)) continue;
                var objectBounds = scene.GetObjectWorldBounds(i);
                bounds = hasBounds ? RectangleF.Union(bounds, objectBounds) : objectBounds;
                hasBounds = true;
            }
        }

        private static int CountActiveObjects(VectorScene scene, int frame)
        {
            var count = 0;
            for (var i = 0; i < scene.ObjectCount; i++)
            {
                if (scene.IsObjectActive(i, frame)) count++;
            }

            return count;
        }

        private static void ConfigurePreviewLabel(Label label, Color color, Font font)
        {
            label.Dock = DockStyle.Fill;
            label.ForeColor = color;
            label.BackColor = Theme.PanelStrong;
            label.Font = font;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoEllipsis = true;
            label.Margin = Padding.Empty;
        }

        private static string SingleLine(string value)
        {
            return value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        }
    }

    private sealed class PromptDialog : ModernDialogForm
    {
        private readonly TextBox _input = new();
        private string _value = "";

        private PromptDialog(string title, string label, string initialValue)
            : base(title, new Size(440, 224))
        {
            DialogContent.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = Theme.Text,
                BackColor = Theme.Panel,
                Font = Theme.UiFont(9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            });

            _input.Dock = DockStyle.Top;
            _input.Height = 30;
            Theme.StyleTextBox(_input);
            _input.Text = initialValue;
            DialogContent.Controls.Add(_input);
            _input.BringToFront();

            var ok = AddDialogAction(
                "OK",
                DialogResult.OK,
                DialogActionStyle.Primary,
                () =>
                {
                    _value = _input.Text.Trim();
                    return _value.Length > 0;
                });
            var cancel = AddDialogAction("Cancel", DialogResult.Cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            UiLocalization.Watch(this);
        }

        public static bool TryAsk(IWin32Window owner, string title, string label, out string value)
        {
            return TryAsk(owner, title, label, "", out value);
        }

        public static bool TryAsk(
            IWin32Window owner,
            string title,
            string label,
            string initialValue,
            out string value)
        {
            using var dialog = new PromptDialog(title, label, initialValue);
            var result = dialog.ShowDialog(owner);
            value = dialog._value;
            return result == DialogResult.OK;
        }
    }
}
