namespace VectorAnimationEngine;

internal sealed partial class LibraryVaultPanel
{
    private readonly TextBox _assetSearchBox = new()
    {
        PlaceholderText = "Search assets or tags...",
        AccessibleName = "Search project assets"
    };
    private readonly ComboBox _assetTagFilter = new()
    {
        AccessibleName = "Filter project assets by tag"
    };
    private readonly SvgIconButton _manageTagsButton = new(SvgIconKind.Swatches)
    {
        Text = "Tags...",
        AccessibleName = "Manage tags for selected asset"
    };
    private readonly System.Windows.Forms.Timer _assetSearchTimer = new() { Interval = 140 };
    private HashSet<string>? _assetFilterMatchingDrawingObjectIds;
    private HashSet<string>? _assetFilterMatchingFolderIds;
    private bool _updatingAssetTagFilter;

    internal static bool TryEditAssetTags(
        IWin32Window owner,
        VectorProject project,
        DrawingObjectDefinition drawingObject,
        out ProjectAssetTagData[] tags,
        out string[] assignedTagIds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(drawingObject);
        using var dialog = new AssetTagDialog(project.AssetTags, drawingObject.AssetTagIds);
        var accepted = dialog.ShowDialog(owner) == DialogResult.OK;
        tags = dialog.Tags;
        assignedTagIds = dialog.AssignedTagIds;
        return accepted;
    }

    private Control BuildAssetFilterBar()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(0, 3, 0, 3)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        _assetSearchBox.Dock = DockStyle.Fill;
        _assetSearchBox.Margin = new Padding(0, 1, 0, 3);
        Theme.StyleTextBox(_assetSearchBox);
        _assetSearchBox.TextChanged += (_, _) => QueueAssetFilterRefresh();
        layout.Controls.Add(_assetSearchBox, 0, 0);
        layout.SetColumnSpan(_assetSearchBox, 2);

        _assetTagFilter.Dock = DockStyle.Fill;
        _assetTagFilter.Margin = new Padding(0, 2, 6, 1);
        Theme.StyleComboBox(_assetTagFilter);
        _assetTagFilter.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingAssetTagFilter) return;
            _projectRowsDirty = true;
            RefreshProjectObjects();
        };
        layout.Controls.Add(_assetTagFilter, 0, 1);

        _manageTagsButton.Dock = DockStyle.Fill;
        _manageTagsButton.Margin = new Padding(0, 2, 0, 1);
        Theme.StyleStandardButton(_manageTagsButton);
        _manageTagsButton.Click += (_, _) => RaiseSelectedAssetTagsRequest();
        _toolTip.SetToolTip(_manageTagsButton, "Manage tags for selected asset");
        layout.Controls.Add(_manageTagsButton, 1, 1);

        _assetSearchTimer.Tick += (_, _) =>
        {
            _assetSearchTimer.Stop();
            _projectRowsDirty = true;
            RefreshProjectObjects();
        };
        return layout;
    }

    private void QueueAssetFilterRefresh()
    {
        _assetSearchTimer.Stop();
        _assetSearchTimer.Start();
    }

    private void RefreshAssetTagFilterItems()
    {
        var selectedId = SelectedAssetTagFilterId;
        _updatingAssetTagFilter = true;
        _assetTagFilter.BeginUpdate();
        try
        {
            _assetTagFilter.Items.Clear();
            _assetTagFilter.Items.Add(new AssetTagFilterItem("", "All tags"));
            if (_project is not null)
            {
                foreach (var tag in _project.AssetTags)
                {
                    _assetTagFilter.Items.Add(new AssetTagFilterItem(tag.Id, tag.Name));
                }
            }

            var selectedIndex = 0;
            for (var index = 1; index < _assetTagFilter.Items.Count; index++)
            {
                if (_assetTagFilter.Items[index] is not AssetTagFilterItem item
                    || !string.Equals(item.Id, selectedId, StringComparison.Ordinal))
                {
                    continue;
                }
                selectedIndex = index;
                break;
            }
            _assetTagFilter.SelectedIndex = selectedIndex;
        }
        finally
        {
            _assetTagFilter.EndUpdate();
            _updatingAssetTagFilter = false;
        }
    }

    private string SelectedAssetTagFilterId =>
        _assetTagFilter.SelectedItem is AssetTagFilterItem item ? item.Id : "";

    private string AssetSearchText => _assetSearchBox.Text.Trim();

    private bool AssetFilterActive => AssetSearchText.Length > 0 || SelectedAssetTagFilterId.Length > 0;

    private bool AssetMatchesFilter(DrawingObjectDefinition drawingObject)
    {
        return !AssetFilterActive
            || _assetFilterMatchingDrawingObjectIds?.Contains(drawingObject.Id) == true;
    }

    private bool AssetMatchesFilterCore(DrawingObjectDefinition drawingObject)
    {
        if (_project is null) return false;
        var selectedTagId = SelectedAssetTagFilterId;
        if (selectedTagId.Length > 0
            && !drawingObject.AssetTagIds.Contains(selectedTagId, StringComparer.Ordinal))
        {
            return false;
        }

        var search = AssetSearchText;
        if (search.Length == 0) return true;
        if (ContainsSearch(drawingObject.Name, search)
            || ContainsSearch(drawingObject.Kind, search)
            || ContainsSearch(drawingObject.Detail, search))
        {
            return true;
        }

        var assignedIds = drawingObject.AssetTagIds.ToHashSet(StringComparer.Ordinal);
        return _project.AssetTags.Any(tag => assignedIds.Contains(tag.Id) && ContainsSearch(tag.Name, search));
    }

    private bool AssetFolderNameMatches(ProjectAssetFolder folder)
    {
        if (!AssetFilterActive) return true;
        return SelectedAssetTagFilterId.Length == 0
            && AssetSearchText.Length > 0
            && ContainsSearch(folder.Name, AssetSearchText);
    }

    private bool AssetFolderHasMatchingContent(string folderId)
    {
        return !AssetFilterActive || _assetFilterMatchingFolderIds?.Contains(folderId) == true;
    }

    private void PrepareAssetFilterCache()
    {
        if (_project is null || !AssetFilterActive)
        {
            _assetFilterMatchingDrawingObjectIds = null;
            _assetFilterMatchingFolderIds = null;
            return;
        }

        var matchingDrawingObjectIds = new HashSet<string>(StringComparer.Ordinal);
        var matchingFolderIds = new HashSet<string>(StringComparer.Ordinal);
        var foldersById = _project.AssetFolders.ToDictionary(folder => folder.Id, StringComparer.Ordinal);

        void IncludeFolderAndAncestors(string folderId)
        {
            var currentId = folderId;
            while (currentId.Length > 0 && matchingFolderIds.Add(currentId))
            {
                if (!foldersById.TryGetValue(currentId, out var folder)) break;
                currentId = folder.ParentFolderId;
            }
        }

        foreach (var drawingObject in _project.DrawingObjects)
        {
            if (!AssetMatchesFilterCore(drawingObject)) continue;
            matchingDrawingObjectIds.Add(drawingObject.Id);
            IncludeFolderAndAncestors(drawingObject.AssetFolderId);
        }

        foreach (var folder in _project.AssetFolders)
        {
            if (AssetFolderNameMatches(folder)) IncludeFolderAndAncestors(folder.Id);
        }

        _assetFilterMatchingDrawingObjectIds = matchingDrawingObjectIds;
        _assetFilterMatchingFolderIds = matchingFolderIds;
    }

    private void ApplyAssetTagNodeStyle(TreeNode node, DrawingObjectDefinition drawingObject)
    {
        if (_project is null) return;
        var assignedIds = drawingObject.AssetTagIds.ToHashSet(StringComparer.Ordinal);
        var tags = _project.AssetTags.Where(tag => assignedIds.Contains(tag.Id)).ToArray();
        if (node.Tag is VaultRow row)
        {
            row.TrailingColors = tags.Select(tag => Color.FromArgb(tag.ColorArgb)).ToArray();
        }
        node.BackColor = Color.Empty;
        if (tags.Length == 0)
        {
            node.ToolTipText = drawingObject.Detail;
            return;
        }

        node.ToolTipText = string.Format(
            UiLocalization.T("Tags: {0}"),
            string.Join(", ", tags.Select(tag => tag.Name)));
    }

    private void PopulateAssetTagAssignmentMenu(
        ToolStripMenuItem setTags,
        DrawingObjectDefinition? drawingObject)
    {
        while (setTags.DropDownItems.Count > 0)
        {
            var item = setTags.DropDownItems[0];
            setTags.DropDownItems.RemoveAt(0);
            item.Dispose();
        }
        if (_project is null || drawingObject is null || _project.AssetTags.Count == 0) return;

        var tagMenu = (ToolStripDropDownMenu)setTags.DropDown;
        tagMenu.BackColor = Theme.PanelStrong;
        tagMenu.ForeColor = Theme.Text;
        tagMenu.Font = _projectObjectMenu.Font;
        tagMenu.Renderer = _projectObjectMenu.Renderer;
        tagMenu.Padding = new Padding(6, 5, 6, 5);
        tagMenu.ShowCheckMargin = true;
        tagMenu.ShowImageMargin = true;
        var menuWidth = Math.Clamp(
            _project.AssetTags.Max(tag => TextRenderer.MeasureText(
                tag.Name,
                tagMenu.Font,
                new Size(280, 30),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width) + 78,
            180,
            300);
        var assignedIds = drawingObject.AssetTagIds.ToHashSet(StringComparer.Ordinal);
        foreach (var tag in _project.AssetTags)
        {
            var dot = CreateAssetTagDot(Color.FromArgb(tag.ColorArgb));
            var tagItem = new ToolStripMenuItem(tag.Name)
            {
                Checked = assignedIds.Contains(tag.Id),
                CheckOnClick = true,
                Image = dot,
                Tag = tag.Id,
                AccessibleName = AssetTagMenuAccessibleName(tag.Name, assignedIds.Contains(tag.Id)),
                AutoSize = false,
                BackColor = Theme.PanelStrong,
                ForeColor = Theme.Text,
                Font = tagMenu.Font,
                Margin = Padding.Empty,
                Padding = new Padding(10, 0, 10, 0),
                Size = new Size(menuWidth, 30)
            };
            tagItem.Disposed += (_, _) => dot.Dispose();
            tagItem.Click += (_, _) =>
            {
                DrawingObjectAssetTagAssignmentRequested?.Invoke(
                    this,
                    new DrawingObjectAssetTagAssignmentRequestedEventArgs(
                        drawingObject.Id,
                        tag.Id,
                        tagItem.Checked));
                tagItem.AccessibleName = AssetTagMenuAccessibleName(tag.Name, tagItem.Checked);
            };
            setTags.DropDownItems.Add(tagItem);
        }
    }

    private static string AssetTagMenuAccessibleName(string tagName, bool assigned) =>
        $"{tagName}, {UiLocalization.T(assigned ? "Assigned" : "Not assigned")}";

    private static Bitmap CreateAssetTagDot(Color color)
    {
        const int size = 12;
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(color);
        using var border = new Pen(Theme.Mix(Theme.PanelStrong, Theme.Text, 0.42f));
        graphics.FillEllipse(fill, 2, 2, 8, 8);
        graphics.DrawEllipse(border, 2, 2, 8, 8);
        return bitmap;
    }

    private void UpdateAssetTagActions(bool projectAssetSelected)
    {
        _manageTagsButton.Enabled = projectAssetSelected;
    }

    private static bool ContainsSearch(string? value, string search) =>
        !string.IsNullOrEmpty(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private sealed record AssetTagFilterItem(string Id, string Name)
    {
        public override string ToString() => UiLocalization.T(Name);
    }

    private sealed class AssetTagDialog : ModernDialogForm
    {
        private readonly List<ProjectAssetTagData> _tags;
        private readonly HashSet<string> _assignedTagIds;
        private readonly ListView _list = new();
        private readonly Button _assign = new() { Text = "Assign" };
        private readonly Button _rename = new() { Text = "Rename" };
        private readonly Button _delete = new() { Text = "Delete" };
        private readonly TableLayoutPanel _rgbEditor = new();
        private readonly Panel _rgbPreview = new();
        private readonly Label _rgbHex = new();
        private readonly ColorComponentSlider[] _rgbSliders = [new(), new(), new()];
        private readonly ModernNumericUpDown[] _rgbValues =
        [
            CreateRgbValue("Red channel"),
            CreateRgbValue("Green channel"),
            CreateRgbValue("Blue channel")
        ];
        private bool _updatingRgbEditor;

        public AssetTagDialog(
            IReadOnlyList<ProjectAssetTag> tags,
            IReadOnlyList<string> assignedTagIds)
            : base("Asset Tags", new Size(520, 540))
        {
            _tags = tags.Select(tag => new ProjectAssetTagData(tag.Id, tag.Name, tag.ColorArgb)).ToList();
            _assignedTagIds = assignedTagIds.ToHashSet(StringComparer.Ordinal);
            BuildUi();
            RefreshRows();
            var apply = AddDialogAction("Apply", DialogResult.OK, DialogActionStyle.Primary);
            var cancel = AddDialogAction("Cancel", DialogResult.Cancel);
            AcceptButton = apply;
            CancelButton = cancel;
            UiLocalization.Watch(this);
        }

        public ProjectAssetTagData[] Tags => _tags.ToArray();
        public string[] AssignedTagIds => _tags
            .Where(tag => _assignedTagIds.Contains(tag.Id))
            .Select(tag => tag.Id)
            .ToArray();

        private void BuildUi()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                ColumnCount = 1,
                RowCount = 4,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            DialogContent.Controls.Add(layout);

            layout.Controls.Add(new Label
            {
                Text = "Assign tags to this asset or manage project tag colors.",
                Dock = DockStyle.Fill,
                ForeColor = Theme.Muted,
                BackColor = Theme.Panel,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            }, 0, 0);

            Theme.StyleListView(_list, animateRows: true);
            _list.Dock = DockStyle.Fill;
            _list.Margin = new Padding(0, 4, 0, 6);
            _list.Columns.Add("Tag", 210);
            _list.Columns.Add("Color", 100);
            _list.Columns.Add("Asset", 90);
            _list.SelectedIndexChanged += (_, _) => UpdateButtons();
            _list.DoubleClick += (_, _) => ToggleAssignment();
            _list.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Space) return;
                ToggleAssignment();
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
            _list.Resize += (_, _) => ResizeColumns();
            layout.Controls.Add(_list, 0, 1);

            layout.Controls.Add(BuildRgbEditor(), 0, 2);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Theme.Panel,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            AddAction(actions, "Add", AddTag);
            AddAction(actions, _rename, RenameTag);
            AddAction(actions, _delete, DeleteTag);
            AddAction(actions, _assign, ToggleAssignment, 104);
            layout.Controls.Add(actions, 0, 3);
        }

        private Control BuildRgbEditor()
        {
            _rgbEditor.Dock = DockStyle.Fill;
            _rgbEditor.BackColor = Theme.Panel;
            _rgbEditor.ColumnCount = 3;
            _rgbEditor.RowCount = 4;
            _rgbEditor.Margin = Padding.Empty;
            _rgbEditor.Padding = new Padding(0, 2, 0, 2);
            _rgbEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
            _rgbEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _rgbEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            _rgbEditor.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            for (var index = 0; index < 3; index++)
            {
                _rgbEditor.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            }

            _rgbPreview.Dock = DockStyle.Fill;
            _rgbPreview.Margin = new Padding(2, 2, 8, 2);
            _rgbPreview.BorderStyle = BorderStyle.FixedSingle;
            _rgbPreview.AccessibleName = "Tag Color";
            _rgbEditor.Controls.Add(_rgbPreview, 0, 0);

            var title = new Label
            {
                Text = "RGB color",
                Dock = DockStyle.Fill,
                ForeColor = Theme.Muted,
                BackColor = Theme.Panel,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _rgbEditor.Controls.Add(title, 1, 0);

            _rgbHex.Dock = DockStyle.Fill;
            _rgbHex.ForeColor = Theme.Text;
            _rgbHex.BackColor = Theme.Panel;
            _rgbHex.TextAlign = ContentAlignment.MiddleRight;
            _rgbHex.Font = Theme.UiFont(8.5f);
            _rgbEditor.Controls.Add(_rgbHex, 2, 0);

            string[] labels = ["R", "G", "B"];
            for (var index = 0; index < 3; index++)
            {
                var channel = index;
                _rgbEditor.Controls.Add(new Label
                {
                    Text = labels[index],
                    Dock = DockStyle.Fill,
                    ForeColor = Theme.Muted,
                    BackColor = Theme.Panel,
                    Font = Theme.UiFont(8.5f, FontStyle.Bold),
                    TextAlign = ContentAlignment.MiddleLeft
                }, 0, index + 1);

                var slider = _rgbSliders[index];
                slider.Dock = DockStyle.Fill;
                slider.Margin = new Padding(0, 1, 6, 1);
                slider.AccessibleName = _rgbValues[index].AccessibleName;
                slider.GradientColor = ratio => RgbChannelGradient(channel, ratio);
                slider.ValueChanged += (_, _) => RgbChannelChanged(channel, slider.Value);
                _rgbEditor.Controls.Add(slider, 1, index + 1);

                var value = _rgbValues[index];
                value.Dock = DockStyle.Fill;
                value.Margin = new Padding(0, 1, 0, 1);
                Theme.StyleNumeric(value);
                value.ValueChanged += (_, _) => RgbChannelChanged(channel, (int)value.Value);
                _rgbEditor.Controls.Add(value, 2, index + 1);
            }

            return _rgbEditor;
        }

        private void AddTag()
        {
            if (_tags.Count >= VectorProject.MaxAssetTagCount)
            {
                ModernMessageDialog.Show(this, "The project tag limit has been reached.", "Asset Tags");
                return;
            }
            var tag = new ProjectAssetTagData(
                Guid.NewGuid().ToString("N"),
                NextTagName(),
                CreateRandomTagColor().ToArgb());
            _tags.Add(tag);
            _assignedTagIds.Add(tag.Id);
            RefreshRows(tag.Id);
        }

        private void RenameTag()
        {
            var selected = SelectedTag();
            if (selected is null
                || !PromptDialog.TryAsk(this, "Rename Tag", "Tag name", selected.Value.Name, out var name)
                || !ValidateName(name, selected.Value.Id))
            {
                return;
            }
            ReplaceTag(selected.Value with { Name = name.Trim() });
        }

        private void DeleteTag()
        {
            var selected = SelectedTag();
            if (selected is null) return;
            if (ModernMessageDialog.Show(
                    this,
                    string.Format(
                        UiLocalization.T("Delete tag '{0}' from every project asset?"),
                        selected.Value.Name),
                    "Delete Tag",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }
            _tags.RemoveAll(tag => string.Equals(tag.Id, selected.Value.Id, StringComparison.Ordinal));
            _assignedTagIds.Remove(selected.Value.Id);
            RefreshRows();
        }

        private void ToggleAssignment()
        {
            var selected = SelectedTag();
            if (selected is null) return;
            if (!_assignedTagIds.Add(selected.Value.Id)) _assignedTagIds.Remove(selected.Value.Id);
            RefreshRows(selected.Value.Id);
        }

        private void ReplaceTag(ProjectAssetTagData replacement)
        {
            var index = _tags.FindIndex(tag => string.Equals(tag.Id, replacement.Id, StringComparison.Ordinal));
            if (index < 0) return;
            _tags[index] = replacement;
            RefreshRows(replacement.Id);
        }

        private void RgbChannelChanged(int channel, int value)
        {
            if (_updatingRgbEditor || SelectedTag() is not { } selected) return;
            var red = channel == 0 ? value : _rgbSliders[0].Value;
            var green = channel == 1 ? value : _rgbSliders[1].Value;
            var blue = channel == 2 ? value : _rgbSliders[2].Value;
            var replacement = selected with { ColorArgb = Color.FromArgb(red, green, blue).ToArgb() };
            var index = _tags.FindIndex(tag => string.Equals(tag.Id, replacement.Id, StringComparison.Ordinal));
            if (index < 0 || _list.SelectedItems.Count == 0) return;

            _tags[index] = replacement;
            UpdateTagRow(_list.SelectedItems[0], replacement);
            SyncRgbEditor(replacement);
            _list.Invalidate(_list.SelectedItems[0].Bounds);
        }

        private Color RgbChannelGradient(int channel, float ratio)
        {
            var value = Math.Clamp((int)MathF.Round(ratio * 255f), 0, 255);
            return Color.FromArgb(
                channel == 0 ? value : _rgbSliders[0].Value,
                channel == 1 ? value : _rgbSliders[1].Value,
                channel == 2 ? value : _rgbSliders[2].Value);
        }

        private void SyncRgbEditor(ProjectAssetTagData? selected)
        {
            var enabled = selected is not null;
            _rgbEditor.Enabled = enabled;
            var color = enabled ? Color.FromArgb(selected!.Value.ColorArgb) : Theme.DisabledSurface;
            _updatingRgbEditor = true;
            try
            {
                for (var index = 0; index < 3; index++)
                {
                    var value = index switch
                    {
                        0 => color.R,
                        1 => color.G,
                        _ => color.B
                    };
                    _rgbSliders[index].Value = value;
                    _rgbValues[index].Value = value;
                    _rgbSliders[index].RefreshGradient();
                }
                _rgbPreview.BackColor = color;
                _rgbHex.Text = enabled ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : "#------";
            }
            finally
            {
                _updatingRgbEditor = false;
            }
        }

        private string NextTagName()
        {
            const string baseName = "New tag";
            if (!_tags.Any(tag => string.Equals(tag.Name, baseName, StringComparison.OrdinalIgnoreCase)))
            {
                return baseName;
            }

            for (var suffix = 2; suffix <= VectorProject.MaxAssetTagCount + 1; suffix++)
            {
                var candidate = $"{baseName} {suffix}";
                if (!_tags.Any(tag => string.Equals(tag.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate;
                }
            }
            var fallback = $"{baseName} {Guid.NewGuid():N}";
            return fallback[..Math.Min(fallback.Length, VectorProject.MaxAssetTagNameLength)];
        }

        private static Color CreateRandomTagColor()
        {
            var low = Random.Shared.Next(40, 121);
            var middle = Random.Shared.Next(104, 193);
            var high = Random.Shared.Next(184, 256);
            return Random.Shared.Next(6) switch
            {
                0 => Color.FromArgb(high, middle, low),
                1 => Color.FromArgb(high, low, middle),
                2 => Color.FromArgb(middle, high, low),
                3 => Color.FromArgb(low, high, middle),
                4 => Color.FromArgb(middle, low, high),
                _ => Color.FromArgb(low, middle, high)
            };
        }

        private bool ValidateName(string name, string? exceptId)
        {
            var normalized = name.Trim();
            if (normalized.Length is > 0 and <= VectorProject.MaxAssetTagNameLength
                && !_tags.Any(tag => !string.Equals(tag.Id, exceptId, StringComparison.Ordinal)
                    && string.Equals(tag.Name, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            ModernMessageDialog.Show(
                this,
                "Tag names must be unique and contain 1 to 64 characters.",
                "Asset Tags",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }

        private ProjectAssetTagData? SelectedTag()
        {
            return _list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is ProjectAssetTagData tag
                ? tag
                : null;
        }

        private void RefreshRows(string? selectedId = null)
        {
            selectedId ??= SelectedTag()?.Id;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var tag in _tags)
                {
                    var item = new ListViewItem();
                    item.SubItems.Add("");
                    item.SubItems.Add("");
                    UpdateTagRow(item, tag);
                    _list.Items.Add(item);
                    if (string.Equals(tag.Id, selectedId, StringComparison.Ordinal)) item.Selected = true;
                }
            }
            finally
            {
                _list.EndUpdate();
            }
            Theme.RefreshListViewRowInteraction(_list);
            ResizeColumns();
            UpdateButtons();
        }

        private void UpdateTagRow(ListViewItem item, ProjectAssetTagData tag)
        {
            var color = Color.FromArgb(tag.ColorArgb);
            item.Tag = tag;
            item.Text = tag.Name;
            item.BackColor = Theme.Mix(Theme.Panel, color, Theme.IsLight ? 0.18f : 0.28f);
            item.SubItems[1].Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            item.SubItems[2].Text = _assignedTagIds.Contains(tag.Id) ? "Assigned" : "Not assigned";
        }

        private void ResizeColumns()
        {
            if (_list.Columns.Count != 3 || _list.ClientSize.Width <= 0) return;
            var available = Math.Max(240, _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
            _list.Columns[1].Width = 92;
            _list.Columns[2].Width = 98;
            _list.Columns[0].Width = Math.Max(80, available - _list.Columns[1].Width - _list.Columns[2].Width);
        }

        private void UpdateButtons()
        {
            var selected = SelectedTag();
            var enabled = selected is not null;
            _rename.Enabled = enabled;
            _delete.Enabled = enabled;
            _assign.Enabled = enabled;
            _assign.Text = selected is not null && _assignedTagIds.Contains(selected.Value.Id)
                ? "Unassign"
                : "Assign";
            SyncRgbEditor(selected);
        }

        private static ModernNumericUpDown CreateRgbValue(string accessibleName) => new()
        {
            Minimum = 0,
            Maximum = 255,
            DecimalPlaces = 0,
            Increment = 1,
            AccessibleName = accessibleName
        };

        private static void AddAction(FlowLayoutPanel parent, string text, Action action, int width = 74)
        {
            var button = new Button { Text = text };
            AddAction(parent, button, action, width);
        }

        private static void AddAction(FlowLayoutPanel parent, Button button, Action action, int width = 74)
        {
            button.Width = width;
            button.Height = 32;
            button.Margin = new Padding(0, 4, 6, 4);
            Theme.StyleButton(button);
            button.Click += (_, _) => action();
            parent.Controls.Add(button);
        }
    }
}
