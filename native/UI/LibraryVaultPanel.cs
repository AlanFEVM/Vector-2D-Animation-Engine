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

internal sealed class LibraryVaultPanel : UserControl
{
    private readonly ListView _library = new();
    private readonly ListView _projectObjects = new();
    private readonly ListView _vault = new();
    private readonly AnimatedContextMenuStrip _projectObjectMenu = new();
    private readonly Panel _contentHost = new();
    private readonly Label _vaultSummary = new();
    private readonly List<VaultItem> _vaultItems = [];
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 220 };
    private Button? _openButton;
    private VectorProject? _project;
    private VectorScene? _scene;
    private Func<int>? _selectedObjectProvider;
    private Func<int>? _frameProvider;
    private VaultPreviewForm? _preview;
    private ListView? _hoverList;
    private int _hoverIndex = -1;
    private bool _syncingSelection;
    private bool _projectRowsDirty = true;
    private ProjectRowsFingerprint _projectRowsFingerprint;
    private string _activeDrawingObjectId = "";
    private VaultSource _activeSource = VaultSource.Project;

    private sealed record VaultRow(VaultItem Item, DrawingObjectDefinition? DrawingObject, bool IsProjectObject);
    private readonly record struct ProjectRowsFingerprint(int Count, int Hash);

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

    public LibraryVaultPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        Padding = new Padding(8);

        BuildVaultWorkspace();
        _hoverTimer.Tick += (_, _) => ShowPendingPreview();
        VisibleChanged += (_, _) =>
        {
            if (!Visible) HidePreview(clearContent: true);
        };
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
        HidePreview(clearContent: true);
        SynchronizeProjectObjectSelection();
        UpdateActionButtons();
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
            _projectObjectMenu.Dispose();
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

        _projectRowsDirty = true;
        if (Visible) RefreshProjectObjects();
    }

    private void BuildVaultWorkspace()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        header.Controls.Add(new Label
        {
            Text = "Project Assets",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        }, 0, 0);
        _vaultSummary.Dock = DockStyle.Fill;
        _vaultSummary.ForeColor = Theme.Muted;
        _vaultSummary.BackColor = Theme.Panel;
        _vaultSummary.Font = Theme.UiFont(8.5f);
        _vaultSummary.TextAlign = ContentAlignment.MiddleCenter;
        _vaultSummary.AutoEllipsis = true;
        header.Controls.Add(_vaultSummary, 1, 0);
        _openButton = new Button
        {
            Text = "Open",
            Dock = DockStyle.Fill,
            Margin = new Padding(2, 3, 0, 3),
            AccessibleName = "Open selected drawing object"
        };
        Theme.StyleButton(_openButton);
        _openButton.Click += (_, _) => OpenSelectedVaultItem();
        header.Controls.Add(_openButton, 2, 0);
        layout.Controls.Add(header, 0, 0);

        _contentHost.Dock = DockStyle.Fill;
        _contentHost.BackColor = Theme.Panel;
        _contentHost.Margin = Padding.Empty;
        layout.Controls.Add(_contentHost, 0, 1);
        ConfigureContentLists();

        ActivateSource(VaultSource.Project, refresh: false);
    }

    private void ConfigureContentLists()
    {
        ConfigureList(_projectObjects);
        _projectObjects.HeaderStyle = ColumnHeaderStyle.None;
        _projectObjects.Columns.Add("Drawing Object", 128);
        _projectObjects.Columns.Add("Content", 86);
        ConfigureResponsiveColumns(_projectObjects, 128);
        ConfigureVaultList(_projectObjects);
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
        var activeList = source switch
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
        var rename = new ToolStripMenuItem("Rename");
        rename.Click += (_, _) => RaiseProjectObjectAssetRequest(DrawingObjectRenameRequested);
        var duplicate = new ToolStripMenuItem("Duplicate");
        duplicate.Click += (_, _) => RaiseProjectObjectAssetRequest(DrawingObjectDuplicateRequested);
        var delete = new ToolStripMenuItem("Delete");
        delete.Click += (_, _) => RaiseProjectObjectAssetRequest(DrawingObjectDeleteRequested);
        _projectObjectMenu.Items.AddRange(new ToolStripItem[] { rename, duplicate, new ToolStripSeparator(), delete });
        _projectObjectMenu.Opening += (_, e) =>
        {
            var selected = SelectedProjectDrawingObject();
            if (selected is null)
            {
                e.Cancel = true;
                return;
            }

            delete.Enabled = (_project?.DrawingObjects.Count ?? 0) > 1;
        };
        _projectObjects.ContextMenuStrip = _projectObjectMenu;
        _projectObjects.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var item = _projectObjects.GetItemAt(e.X, e.Y);
            if (item is not null) item.Selected = true;
        };
    }

    private DrawingObjectDefinition? SelectedProjectDrawingObject()
    {
        return _projectObjects.SelectedItems.Count > 0
            && _projectObjects.SelectedItems[0].Tag is VaultRow { IsProjectObject: true } row
            ? row.DrawingObject
            : null;
    }

    private void RaiseProjectObjectAssetRequest(EventHandler<DrawingObjectAssetRequestedEventArgs>? requested)
    {
        var drawingObject = SelectedProjectDrawingObject();
        if (drawingObject is null) return;
        requested?.Invoke(this, new DrawingObjectAssetRequestedEventArgs(drawingObject.Id));
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
        if (_projectObjects.SelectedItems.Count == 0) return;
        if (_projectObjects.SelectedItems[0].Tag is not VaultRow { IsProjectObject: true } row) return;
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
            MessageBox.Show(UiLocalization.T("Select an object on the stage first."), UiLocalization.T("Vault"), MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            MessageBox.Show(UiLocalization.T("This drawing object is not available in the current project."), item.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        MessageBox.Show(UiLocalization.T(item.Payload.Length > 0 ? item.Payload : item.Detail), item.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        SuspendLayout();
        _projectObjects.BeginUpdate();
        try
        {
            _syncingSelection = true;
            _projectObjects.Items.Clear();
            if (_project is not null)
            {
                var rows = new ListViewItem[_project.DrawingObjects.Count];
                for (var index = 0; index < rows.Length; index++)
                {
                    var drawingObject = _project.DrawingObjects[index];
                    var item = drawingObject.ToVaultItem();
                    item.Detail = DrawingObjectDetail(drawingObject);
                    var row = new ListViewItem(drawingObject.Name);
                    row.SubItems.Add($"{drawingObject.Scene.ObjectCount} obj / {drawingObject.Instances.Count} inst");
                    row.Tag = new VaultRow(item, drawingObject, IsProjectObject: true);
                    rows[index] = row;
                }

                if (rows.Length > 0) _projectObjects.Items.AddRange(rows);
            }

            if (_projectObjects.Items.Count == 0) AddEmptyRow(_projectObjects, "No drawing objects", "Empty project");
            SynchronizeProjectObjectSelection();
            _projectRowsFingerprint = fingerprint;
            _projectRowsDirty = false;
        }
        finally
        {
            _syncingSelection = false;
            _projectObjects.EndUpdate();
            ResizeResponsiveColumns(_projectObjects, 128);
            ResumeLayout(performLayout: false);
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
        }

        return new ProjectRowsFingerprint(_project.DrawingObjects.Count, hash.ToHashCode());
    }

    private void SynchronizeProjectObjectSelection()
    {
        if (_projectObjects.Items.Count == 0) return;
        var previousSync = _syncingSelection;
        _syncingSelection = true;
        try
        {
            foreach (ListViewItem row in _projectObjects.Items)
            {
                var selected = row.Tag is VaultRow { DrawingObject: { } drawingObject }
                    && string.Equals(drawingObject.Id, _activeDrawingObjectId, StringComparison.Ordinal);
                if (row.Selected != selected) row.Selected = selected;
            }
        }
        finally
        {
            _syncingSelection = previousSync;
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
        _vaultSummary.Text = projectCount.ToString();
    }

    private void VaultSelectionChanged(ListView source)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try
        {
            var other = ReferenceEquals(source, _projectObjects) ? _vault : _projectObjects;
            if (source.SelectedItems.Count > 0) other.SelectedItems.Clear();
        }
        finally
        {
            _syncingSelection = false;
        }

        UpdateActionButtons();
    }

    private VaultRow? SelectedVaultRow()
    {
        return _activeSource switch
        {
            VaultSource.Project when _projectObjects.SelectedItems.Count > 0 && _projectObjects.SelectedItems[0].Tag is VaultRow projectRow => projectRow,
            VaultSource.Stored when _vault.SelectedItems.Count > 0 && _vault.SelectedItems[0].Tag is VaultRow vaultRow => vaultRow,
            _ => null
        };
    }

    private void UpdateActionButtons()
    {
        var row = SelectedVaultRow();
        var projectSelected = row is { IsProjectObject: true };
        SetActionState(_openButton, visible: true, enabled: projectSelected);
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

    private void ShowPendingPreview()
    {
        _hoverTimer.Stop();
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
        var topLeft = list.PointToScreen(itemBounds.Location);
        var anchor = new Rectangle(topLeft, itemBounds.Size);
        _preview.ShowAt(FindForm(), anchor);
    }

    private void HidePreview(bool clearContent = false)
    {
        _hoverTimer.Stop();
        _hoverList = null;
        _hoverIndex = -1;
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
        private readonly Label _emptyState = new();
        private readonly Label _title = new();
        private readonly Label _kind = new();
        private readonly Label _metadata = new();
        private readonly Label _detail = new();
        private VectorScene? _contentScene;
        private VectorScene? _contentUnderlay;
        private int _contentFrame;

        public VaultPreviewForm()
        {
            _emptyScene.CreateEmpty();
            _underlayScene.CreateEmpty();
            _stage = new StageControl(_emptyScene) { Dock = DockStyle.Fill, TabStop = false };

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
            frame = Math.Clamp(frame, 0, Math.Max(0, drawingObject.FrameCount - 1));
            _underlayScene.CreateEmpty();
            try
            {
                SceneCompositionBuilder.BuildDrawingObjectChildren(
                    _underlayScene,
                    drawingObject,
                    project.DrawingObjects,
                    frame);
            }
            catch (Exception ex)
            {
                _underlayScene.CreateEmpty();
                AppLog.Error("Vault drawing-object preview composition failed", ex);
            }

            _stage.BindScene(drawingObject.Scene);
            _stage.Frame = frame;
            _stage.BindUnderlayScene(_underlayScene);
            _contentScene = drawingObject.Scene;
            _contentUnderlay = _underlayScene;
            _contentFrame = frame;

            var objectCount = drawingObject.Scene.ObjectCount + _underlayScene.ObjectCount;
            var activeObjectCount = CountActiveObjects(drawingObject.Scene, frame) + CountActiveObjects(_underlayScene, frame);
            _emptyState.Text = objectCount == 0 ? "Empty drawing object" : "No content at this frame";
            _emptyState.Visible = activeObjectCount == 0;
            _title.Text = drawingObject.Name;
            _kind.Text = $"Drawing Object | {drawingObject.Kind}";
            _metadata.Text = $"{activeObjectCount}/{objectCount} visible | {drawingObject.Scene.LayerCount} layers | {drawingObject.Instances.Count} nested | frame {frame + 1}";
            _detail.Text = string.IsNullOrWhiteSpace(drawingObject.Detail)
                ? $"Created {drawingObject.CreatedAt:g}"
                : drawingObject.Detail;
        }

        public void ShowVaultItem(VaultItem item)
        {
            _stage.BindScene(_emptyScene);
            _stage.Frame = 0;
            _stage.BindUnderlayScene(null);
            _stage.ResetDefaultView();
            _contentScene = null;
            _contentUnderlay = null;
            _contentFrame = 0;
            _emptyState.Text = string.Equals(item.ReferenceKind, "DrawingObject", StringComparison.Ordinal)
                ? "Drawing object unavailable"
                : "No visual preview";
            _emptyState.Visible = true;
            _title.Text = item.Name;
            _kind.Text = item.Kind;
            _metadata.Text = $"Stored {item.CreatedAt:g}";
            _detail.Text = SingleLine(item.Detail.Length > 0 ? item.Detail : item.Payload);
        }

        public void ClearContent()
        {
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

            if (!Visible)
            {
                if (owner is null) Show();
                else Show(owner);
            }

            FitContent();
            Invalidate(true);
        }

        private void FitContent()
        {
            if (_contentScene is null || _stage.Width <= 0 || _stage.Height <= 0) return;
            var hasBounds = false;
            var bounds = RectangleF.Empty;
            IncludeSceneBounds(_contentScene, _contentFrame, ref hasBounds, ref bounds);
            if (_contentUnderlay is not null) IncludeSceneBounds(_contentUnderlay, _contentFrame, ref hasBounds, ref bounds);
            if (!hasBounds)
            {
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

    private sealed class PromptDialog : Form
    {
        private readonly TextBox _input = new();
        private string _value = "";

        private PromptDialog(string title, string label)
        {
            Text = title;
            Width = 420;
            Height = 180;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Panel;
            Font = Theme.UiFont();

            Controls.Add(new Label
            {
                Text = label,
                Left = 16,
                Top = 14,
                Width = 360,
                Height = 24,
                ForeColor = Theme.Text,
                BackColor = Theme.Panel,
                Font = Theme.UiFont(9.5f, FontStyle.Bold)
            });

            _input.Left = 16;
            _input.Top = 44;
            _input.Width = 370;
            _input.Height = 28;
            Theme.StyleTextBox(_input);
            Controls.Add(_input);

            var ok = new Button { Text = "OK", Left = 226, Top = 88, Width = 76, Height = 30 };
            Theme.StyleButton(ok);
            ok.Click += (_, _) =>
            {
                _value = _input.Text.Trim();
                DialogResult = _value.Length == 0 ? DialogResult.None : DialogResult.OK;
            };
            Controls.Add(ok);

            var cancel = new Button { Text = "Cancel", Left = 310, Top = 88, Width = 76, Height = 30 };
            Theme.StyleButton(cancel);
            cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            UiLocalization.Watch(this);
        }

        public static bool TryAsk(IWin32Window owner, string title, string label, out string value)
        {
            using var dialog = new PromptDialog(title, label);
            var result = dialog.ShowDialog(owner);
            value = dialog._value;
            return result == DialogResult.OK;
        }
    }
}
