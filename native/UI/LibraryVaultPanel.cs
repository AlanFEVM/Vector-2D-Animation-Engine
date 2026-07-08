using System.Text.Json;

namespace VectorAnimationEngine;

internal sealed class VaultItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "Item";
    public string Name { get; set; } = "Untitled";
    public string Detail { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

internal sealed class LibraryVaultPanel : UserControl
{
    private readonly ListView _library = new();
    private readonly ListView _vault = new();
    private readonly Label _vaultSummary = new();
    private readonly List<VaultItem> _vaultItems = [];
    private VectorScene? _scene;
    private Func<int>? _selectedObjectProvider;

    public LibraryVaultPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        Padding = new Padding(8);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            BackColor = Theme.Panel,
            FixedPanel = FixedPanel.Panel1,
            IsSplitterFixed = false
        };
        Controls.Add(split);

        BuildLibrary(split.Panel1);
        BuildVault(split.Panel2);
        EnableVaultDrop(this);
        EnableVaultDrop(_vault);
        LoadVault();
        RefreshVault();
    }

    public void BindScene(VectorScene scene, Func<int> selectedObjectProvider)
    {
        _scene = scene;
        _selectedObjectProvider = selectedObjectProvider;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        foreach (Control child in Controls)
        {
            if (child is SplitContainer split && split.Height > 320)
            {
                split.SplitterDistance = Math.Max(170, split.Height / 2);
            }
        }
    }

    private void BuildLibrary(Control parent)
    {
        parent.BackColor = Theme.Panel;
        parent.Padding = new Padding(0, 0, 0, 6);

        var title = Header("Library");
        parent.Controls.Add(title);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 38,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Theme.Panel,
            WrapContents = false
        };
        parent.Controls.Add(buttons);

        var addPreset = new Button { Text = "Add to Vault", Width = 112, Height = 30 };
        Theme.StyleButton(addPreset);
        addPreset.Click += (_, _) => AddSelectedLibraryPreset();
        buttons.Controls.Add(addPreset);

        ConfigureList(_library);
        _library.Columns.Add("Asset", 112);
        _library.Columns.Add("Type", 86);
        _library.Dock = DockStyle.Fill;
        parent.Controls.Add(_library);
        _library.BringToFront();

        AddLibraryPreset("Basic Shapes", "Primitive Set", "Rectangle, ellipse, triangle, polygon, star and line presets.");
        AddLibraryPreset("Material Swatches", "Material Set", "Teal, amber, coral, violet and white starter swatches.");
        AddLibraryPreset("Animation Timing", "Timing Preset", "24 FPS looping timeline range for hand-drawn animation.");
        AddLibraryPreset("Stress Scene Setup", "Benchmark Preset", "1000 layers, 100000 objects and 100000000 virtual atoms.");
    }

    private void BuildVault(Control parent)
    {
        parent.BackColor = Theme.Panel;
        parent.Padding = new Padding(0);

        parent.Controls.Add(Header("Vault"));

        _vaultSummary.Dock = DockStyle.Top;
        _vaultSummary.Height = 24;
        _vaultSummary.ForeColor = Theme.Muted;
        _vaultSummary.BackColor = Theme.Panel;
        _vaultSummary.Font = Theme.UiFont();
        _vaultSummary.TextAlign = ContentAlignment.MiddleLeft;
        _vaultSummary.AutoEllipsis = true;
        parent.Controls.Add(_vaultSummary);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 76,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Theme.Panel,
            WrapContents = true
        };
        parent.Controls.Add(buttons);

        AddVaultButton(buttons, "Capture", 78, CaptureSelectedObject);
        AddVaultButton(buttons, "Note", 58, AddNote);
        AddVaultButton(buttons, "File", 58, AddFileReference);
        AddVaultButton(buttons, "Remove", 78, RemoveSelectedVaultItem);
        AddVaultButton(buttons, "Open", 58, OpenSelectedVaultItem);

        ConfigureList(_vault);
        _vault.Columns.Add("Item", 116);
        _vault.Columns.Add("Kind", 76);
        _vault.Dock = DockStyle.Fill;
        parent.Controls.Add(_vault);
        _vault.BringToFront();
    }

    private static Label Header(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
    }

    private static void ConfigureList(ListView list)
    {
        list.BackColor = Theme.Panel;
        list.ForeColor = Theme.Text;
        list.BorderStyle = BorderStyle.None;
        list.Font = Theme.UiFont(9.2f);
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.MultiSelect = false;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
    }

    private static void AddVaultButton(Control parent, string text, int width, Action action)
    {
        var button = new Button { Text = text, Width = width, Height = 30, Margin = new Padding(0, 0, 6, 6) };
        Theme.StyleButton(button);
        button.Click += (_, _) => action();
        parent.Controls.Add(button);
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
        AddVaultItem(new VaultItem
        {
            Kind = preset.Kind,
            Name = preset.Name,
            Detail = preset.Detail,
            Payload = preset.Payload
        });
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
            AddVaultItem(new VaultItem
            {
                Kind = item.Kind,
                Name = item.Name,
                Detail = item.Detail,
                Payload = item.Payload
            });
        };
    }

    private void CaptureSelectedObject()
    {
        if (_scene is null || _selectedObjectProvider is null) return;
        var selected = _selectedObjectProvider();
        if (selected < 0 || selected >= _scene.ObjectCount)
        {
            MessageBox.Show("Select an object on the stage first.", "Vault", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            Title = "Add file reference to Vault",
            Filter = "All files (*.*)|*.*",
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
        if (_vault.SelectedItems.Count == 0) return;
        if (_vault.SelectedItems[0].Tag is not VaultItem item) return;
        _vaultItems.RemoveAll(candidate => candidate.Id == item.Id);
        SaveVault();
        RefreshVault();
    }

    private void OpenSelectedVaultItem()
    {
        if (_vault.SelectedItems.Count == 0) return;
        if (_vault.SelectedItems[0].Tag is not VaultItem item) return;

        if (item.Kind == "File Reference" && File.Exists(item.Payload))
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.Payload,
                UseShellExecute = true
            });
            return;
        }

        MessageBox.Show(item.Payload.Length > 0 ? item.Payload : item.Detail, item.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void AddVaultItem(VaultItem item)
    {
        _vaultItems.Insert(0, item);
        SaveVault();
        RefreshVault();
    }

    private void RefreshVault()
    {
        _vault.BeginUpdate();
        _vault.Items.Clear();
        foreach (var item in _vaultItems)
        {
            var row = new ListViewItem(item.Name);
            row.SubItems.Add(item.Kind);
            row.ToolTipText = item.Detail;
            row.Tag = item;
            _vault.Items.Add(row);
        }

        _vault.EndUpdate();
        _vaultSummary.Text = $"{_vaultItems.Count} stored items";
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
