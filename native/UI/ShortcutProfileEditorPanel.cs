namespace VectorAnimationEngine;

internal sealed class ShortcutProfileEditorPanel : UserControl
{
    private readonly ComboBox _profileSelector = new();
    private readonly Button _newProfile = new() { Text = "New" };
    private readonly Button _renameProfile = new() { Text = "Rename" };
    private readonly Button _deleteProfile = new() { Text = "Delete" };
    private readonly ThemedTableList _commandList = new();
    private readonly ThemedTableList _categoryList = new();
    private string _selectedCategoryId = "all";
    private readonly Button _assignShortcut = new() { Text = "Assign" };
    private readonly Button _clearShortcut = new() { Text = "Clear" };
    private readonly Label _feedback = new();
    private readonly List<ShortcutProfileRecord> _customProfiles;
    private string _activeProfileId;

    public ShortcutProfileEditorPanel()
        : this(ShortcutProfiles.TraditionalFlashProfileId, [])
    {
    }

    public ShortcutProfileEditorPanel(
        string? activeProfileId,
        IEnumerable<ShortcutProfileRecord>? customProfiles)
    {
        var normalized = ShortcutProfiles.Normalize(activeProfileId, customProfiles);
        _activeProfileId = normalized.ActiveProfileId;
        _customProfiles = normalized.CustomProfiles
            .Select(ShortcutProfiles.CloneProfile)
            .ToList();

        BackColor = Theme.Panel;
        BuildUi();
        RefreshProfiles();
        UiLocalization.Watch(this);
    }

    public string ActiveProfileId => _activeProfileId;

    public ShortcutProfileRecord[] CustomProfiles =>
        _customProfiles.Select(ShortcutProfiles.CloneProfile).ToArray();

    private void BuildUi()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = new Padding(16, 14, 16, 10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        Controls.Add(layout);

        var profileRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 5,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 12)
        };
        profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        profileRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        profileRow.Controls.Add(new Label
        {
            Text = "Profile",
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ForeColor = Theme.Muted,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 0)
        }, 0, 0);

        _profileSelector.Dock = DockStyle.Fill;
        _profileSelector.Margin = new Padding(0, 5, 8, 5);
        _profileSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        Theme.StyleComboBox(_profileSelector);
        _profileSelector.AccessibleName = "Shortcut profile";
        _profileSelector.SelectedIndexChanged += (_, _) => SelectProfileFromCombo();
        profileRow.Controls.Add(_profileSelector, 1, 0);

        ConfigureCommandButton(_newProfile, "Create shortcut profile");
        ConfigureCommandButton(_renameProfile, "Rename shortcut profile");
        ConfigureCommandButton(_deleteProfile, "Delete shortcut profile", CommandButtonRole.Danger);
        _newProfile.Click += (_, _) => CreateProfile();
        _renameProfile.Click += (_, _) => RenameProfile();
        _deleteProfile.Click += (_, _) => DeleteProfile();
        profileRow.Controls.Add(_newProfile, 2, 0);
        profileRow.Controls.Add(_renameProfile, 3, 0);
        profileRow.Controls.Add(_deleteProfile, 4, 0);
        layout.Controls.Add(profileRow, 0, 0);

        var commandBrowser = CreateCommandBrowser();
        commandBrowser.Margin = new Padding(0, 0, 0, 12);
        layout.Controls.Add(commandBrowser, 0, 1);

        var actionRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        actionRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        ConfigureCommandButton(_assignShortcut, "Assign selected shortcut", CommandButtonRole.Primary);
        ConfigureCommandButton(_clearShortcut, "Clear selected shortcut");
        _assignShortcut.Click += (_, _) => AssignSelectedShortcut();
        _clearShortcut.Click += (_, _) => ClearSelectedShortcut();
        actionRow.Controls.Add(_assignShortcut, 1, 0);
        actionRow.Controls.Add(_clearShortcut, 2, 0);
        layout.Controls.Add(actionRow, 0, 2);

        _feedback.Dock = DockStyle.Fill;
        _feedback.BackColor = Theme.Panel;
        _feedback.ForeColor = Theme.Danger;
        _feedback.Font = Theme.UiFont(8.8f);
        _feedback.TextAlign = ContentAlignment.MiddleLeft;
        _feedback.AutoEllipsis = true;
        _feedback.AccessibleRole = AccessibleRole.StaticText;
        _feedback.Margin = new Padding(2, 0, 0, 0);
        layout.Controls.Add(_feedback, 0, 3);
    }

    private Control CreateCommandBrowser()
    {
        var browser = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        browser.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        browser.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        browser.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _categoryList.Dock = DockStyle.Fill;
        _categoryList.Margin = new Padding(0, 0, 14, 0);
        _categoryList.AccessibleName = "Shortcut categories";
        var categories = new List<ThemedTableList.Row>();
        foreach (var (id, label) in new (string, string)[]
        {
            ("all", "All"),
            ("file", "Project Files"),
            ("edit", "Editing"),
            ("tool", "Drawing Tools"),
            ("tool-group", "Tool Groups"),
            ("drawing", "Drawing"),
            ("view", "View"),
            ("timeline", "Timeline")
        })
        {
            categories.Add(new ThemedTableList.Row(id, new[] { UiLocalization.T(label) }));
        }
        _categoryList.SetRows(categories, 1);
        _categoryList.SelectById(_selectedCategoryId, raiseEvent: false);
        _categoryList.SelectionChanged += (_, _) =>
        {
            _selectedCategoryId = _categoryList.SelectedId ?? "all";
            RefreshCommandList();
            SetFeedback("");
        };

        browser.Controls.Add(_categoryList, 0, 0);
        browser.Controls.Add(CreateCommandTable(), 1, 0);
        return browser;
    }

    private static string CommandCategory(ShortcutCommandDefinition command)
    {
        if (command.Kind == ShortcutCommandKind.Tool) return "tool";
        if (command.Kind == ShortcutCommandKind.Vault) return "view";
        if (command.Kind != ShortcutCommandKind.Workbench) return "tool-group";
        var separator = command.Id.IndexOf('.');
        return separator >= 0 ? command.Id[..separator] : "all";
    }

    private Control CreateCommandTable()
    {
        _commandList.Dock = DockStyle.Fill;
        _commandList.Margin = Padding.Empty;
        _commandList.AccessibleName = "Shortcut command map";
        _commandList.Headers = new[] { UiLocalization.T("Command"), UiLocalization.T("Shortcut") };
        _commandList.LastColumnWidth = 190;
        _commandList.SelectionChanged += (_, _) => RefreshEditState();
        _commandList.RowActivate += (_, _) => AssignSelectedShortcut();

        return _commandList;
    }

    private static void ConfigureCommandButton(
        Button button,
        string accessibleName,
        CommandButtonRole role = CommandButtonRole.Standard)
    {
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(4);
        button.AccessibleName = accessibleName;
        switch (role)
        {
            case CommandButtonRole.Primary:
                Theme.StylePrimaryButton(button);
                break;
            case CommandButtonRole.Danger:
                Theme.StyleDangerButton(button);
                break;
            default:
                Theme.StyleStandardButton(button);
                break;
        }
    }

    private enum CommandButtonRole
    {
        Standard,
        Primary,
        Danger
    }

    // Owner-drawn table replacing the ListView in the shortcut editor: paints its
    // own header, rows, hover/selection state and a slim scrollbar so column
    // alignment stays exact without native list artifacts.
    private sealed class ThemedTableList : Control
    {
        public sealed record Row(string Id, string[] Cells);

        private const int ScrollbarWidth = 10;
        private const int ColumnPadding = 10;
        private readonly List<Row> _rows = new();
        private string[]? _headers;
        private int _lastColumnWidth = 190;
        private int _selectedIndex = -1;
        private int _hoverIndex = -1;
        private int _scrollOffset;
        private bool _draggingThumb;
        private int _dragAnchor;
        private int _columnCount = 1;

        public ThemedTableList()
        {
            SetStyle(
                ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            Font = Theme.UiFont(9.2f);
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
        }

        public string[]? Headers
        {
            get => _headers;
            set
            {
                _headers = value;
                Invalidate();
            }
        }

        public int LastColumnWidth
        {
            get => _lastColumnWidth;
            set
            {
                _lastColumnWidth = Math.Max(1, value);
                Invalidate();
            }
        }

        public string? SelectedId =>
            _selectedIndex is >= 0 && _selectedIndex < _rows.Count ? _rows[_selectedIndex].Id : null;

        public event EventHandler? SelectionChanged;

        public event EventHandler? RowActivate;

        public void SetRows(IEnumerable<Row> rows, int columnCount)
        {
            _rows.Clear();
            _rows.AddRange(rows);
            _columnCount = Math.Max(1, columnCount);
            _hoverIndex = -1;
            _selectedIndex = Math.Min(_selectedIndex, _rows.Count - 1);
            ClampScroll();
            Invalidate();
        }

        public void SelectById(string? id, bool raiseEvent = true)
        {
            var index = id is null
                ? -1
                : _rows.FindIndex(row => string.Equals(row.Id, id, StringComparison.Ordinal));
            if (index < 0 && _rows.Count > 0) index = 0;
            SetSelectedIndex(index, raiseEvent);
        }

        private void SetSelectedIndex(int index, bool raiseEvent)
        {
            if (index == _selectedIndex) return;
            _selectedIndex = index;
            if (index >= 0) EnsureVisible(index);
            Invalidate();
            if (raiseEvent) SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private int HeaderHeight => _headers is null ? 0 : Font.Height + 12;

        private int RowHeight => Font.Height + 12;

        private int RowsViewHeight => Math.Max(0, Height - HeaderHeight);

        private int ContentHeight => _rows.Count * RowHeight;

        private bool HasScrollbar => ContentHeight > RowsViewHeight;

        private int MaxScrollOffset => Math.Max(0, ContentHeight - RowsViewHeight);

        private int RowsAreaWidth => Math.Max(1, Width - (HasScrollbar ? ScrollbarWidth : 0));

        private int[] ComputeColumnWidths()
        {
            var width = RowsAreaWidth;
            if (_columnCount <= 1) return new[] { width };
            var scale = DeviceDpi / 96f;
            var lastWidth = Math.Min(
                width / 2,
                Math.Min(width * 2 / 5, Math.Max(1, (int)MathF.Round(_lastColumnWidth * scale))));
            var result = new int[_columnCount];
            var remaining = Math.Max(1, width - lastWidth);
            for (var i = 0; i < _columnCount; i++) result[i] = remaining / _columnCount;
            result[0] += remaining - result[0] * _columnCount;
            result[^1] = lastWidth;
            return result;
        }

        private void ClampScroll()
        {
            _scrollOffset = Math.Clamp(_scrollOffset, 0, MaxScrollOffset);
        }

        private void EnsureVisible(int index)
        {
            if (RowsViewHeight <= 0 || _rows.Count == 0) return;
            var top = index * RowHeight;
            var bottom = top + RowHeight;
            if (top < _scrollOffset) _scrollOffset = top;
            else if (bottom > _scrollOffset + RowsViewHeight) _scrollOffset = bottom - RowsViewHeight;
            ClampScroll();
        }

        private int RowIndexAt(int y)
        {
            var headerHeight = HeaderHeight;
            if (y < headerHeight || y >= Height) return -1;
            var index = (y - headerHeight + _scrollOffset) / RowHeight;
            return index >= 0 && index < _rows.Count ? index : -1;
        }

        private Rectangle TrackBounds()
        {
            var headerHeight = HeaderHeight;
            return new Rectangle(Width - ScrollbarWidth + 3, headerHeight, 4, Math.Max(0, Height - headerHeight));
        }

        private Rectangle ThumbBounds()
        {
            if (!HasScrollbar) return Rectangle.Empty;
            var track = TrackBounds();
            var thumbHeight = Math.Max(24, track.Height * RowsViewHeight / Math.Max(1, ContentHeight));
            var top = track.Top
                + (track.Height - thumbHeight) * _scrollOffset / Math.Max(1, MaxScrollOffset);
            return new Rectangle(track.X, top, track.Width, thumbHeight);
        }

        protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) switch
        {
            Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End => true,
            _ => base.IsInputKey(keyData)
        };

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_rows.Count == 0) return;
            var current = Math.Max(0, _selectedIndex);
            var page = Math.Max(1, RowsViewHeight / RowHeight - 1);
            var index = e.KeyCode switch
            {
                Keys.Up => Math.Max(0, _selectedIndex < 0 ? 0 : _selectedIndex - 1),
                Keys.Down => Math.Min(_rows.Count - 1, _selectedIndex < 0 ? 0 : _selectedIndex + 1),
                Keys.PageUp => Math.Max(0, current - page),
                Keys.PageDown => Math.Min(_rows.Count - 1, current + page),
                Keys.Home => 0,
                Keys.End => _rows.Count - 1,
                _ => -1
            };
            if (index < 0) return;
            e.Handled = true;
            SetSelectedIndex(index, raiseEvent: true);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            if (HasScrollbar && e.X >= Width - ScrollbarWidth)
            {
                var thumb = ThumbBounds();
                if (thumb.Contains(e.Location))
                {
                    _draggingThumb = true;
                    _dragAnchor = e.Y - thumb.Top;
                    Capture = true;
                    Invalidate();
                }
                return;
            }
            var index = RowIndexAt(e.Y);
            if (index < 0) return;
            SetSelectedIndex(index, raiseEvent: true);
            if (e.Clicks >= 2) RowActivate?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_draggingThumb)
            {
                var track = TrackBounds();
                var thumb = ThumbBounds();
                var span = Math.Max(1, track.Height - thumb.Height);
                _scrollOffset = (int)MathF.Round((e.Y - _dragAnchor - track.Top) * MaxScrollOffset / (float)span);
                ClampScroll();
                Invalidate();
                return;
            }
            var index = RowIndexAt(e.Y);
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_draggingThumb) return;
            _draggingThumb = false;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex == -1) return;
            _hoverIndex = -1;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scrollOffset -= Math.Sign(e.Delta) * RowHeight * 3;
            ClampScroll();
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ClampScroll();
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            ClampScroll();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var graphics = e.Graphics;
            graphics.Clear(BackColor);
            var headerHeight = HeaderHeight;
            var widths = ComputeColumnWidths();
            var rowsAreaWidth = RowsAreaWidth;
            var boundaries = new int[widths.Length + 1];
            for (var i = 0; i < widths.Length; i++) boundaries[i + 1] = boundaries[i] + widths[i];

            if (_headers is not null)
            {
                using (var fill = new SolidBrush(Theme.PanelStrong))
                    graphics.FillRectangle(fill, 0, 0, Width, headerHeight);
                for (var i = 0; i < widths.Length; i++)
                {
                    var cellBounds = new Rectangle(
                        boundaries[i] + ColumnPadding, 0, Math.Max(1, widths[i] - ColumnPadding * 2), headerHeight);
                    TextRenderer.DrawText(
                        graphics, _headers[i], Font, cellBounds, Theme.Muted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }

            var first = _scrollOffset / RowHeight;
            var last = Math.Min(
                _rows.Count - 1,
                (int)MathF.Ceiling((_scrollOffset + RowsViewHeight) / (float)RowHeight));
            for (var i = first; i <= last; i++)
            {
                var selected = i == _selectedIndex;
                var hovered = i == _hoverIndex && !_draggingThumb;
                var top = headerHeight + i * RowHeight - _scrollOffset;
                var rowBounds = new Rectangle(0, top, rowsAreaWidth, RowHeight);
                var background = Theme.AnimatedRowBackgroundColor(
                    i % 2 == 1 ? Theme.Mix(Theme.Panel, Theme.PanelStrong, 0.4f) : Theme.Panel,
                    hovered ? 1f : 0f, selected ? 1f : 0f, 0f);
                using (var fill = new SolidBrush(background))
                    graphics.FillRectangle(fill, rowBounds);
                var row = _rows[i];
                for (var c = 0; c < widths.Length && c < row.Cells.Length; c++)
                {
                    var cellBounds = new Rectangle(
                        boundaries[c] + ColumnPadding, top,
                        Math.Max(1, widths[c] - ColumnPadding * 2), RowHeight);
                    TextRenderer.DrawText(
                        graphics, row.Cells[c], Font, cellBounds,
                        Theme.ReadableText(background, Theme.Text),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                if (selected)
                {
                    var barHeight = Math.Max(6, RowHeight - 8);
                    using var accent = new SolidBrush(Theme.Accent);
                    graphics.FillRectangle(accent, 0, top + (RowHeight - barHeight) / 2, 3, barHeight);
                }
                if (Focused && selected)
                {
                    Theme.DrawKeyboardFocus(
                        graphics,
                        new Rectangle(2, top + 1, Math.Max(1, rowsAreaWidth - 6), RowHeight - 3),
                        Theme.ReadableText(background, Theme.Accent),
                        background);
                }
            }

            // Grid lines: hairline under every row, full-height column separators,
            // and an outer frame so the list reads as a proper table.
            var rowLine = Theme.Mix(Theme.Panel, Theme.Border, 0.55f);
            using (var gridPen = new Pen(rowLine))
            {
                if (_headers is not null)
                    graphics.DrawLine(gridPen, 0, headerHeight - 1, rowsAreaWidth, headerHeight - 1);
                for (var i = first; i <= last; i++)
                {
                    var lineY = headerHeight + (i + 1) * RowHeight - _scrollOffset - 1;
                    if (lineY < headerHeight - 1 || lineY >= Height) continue;
                    graphics.DrawLine(gridPen, 0, lineY, rowsAreaWidth, lineY);
                }
            }
            using (var borderPen = new Pen(Theme.Border))
            {
                for (var i = 1; i < widths.Length; i++)
                    graphics.DrawLine(borderPen, boundaries[i], 0, boundaries[i], Height - 1);
                graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
            }

            if (!HasScrollbar) return;
            using (var trackFill = new SolidBrush(Theme.Mix(Theme.Panel, Theme.PanelStrong, 0.5f)))
                graphics.FillRectangle(trackFill, TrackBounds());
            using var thumbFill = new SolidBrush(_draggingThumb ? Theme.Accent : Theme.Border);
            Theme.FillUiRectangle(graphics, thumbFill, ThumbBounds());
        }
    }

    private void RefreshProfiles()
    {
        var previousId = _activeProfileId;
        _profileSelector.BeginUpdate();
        try
        {
            _profileSelector.Items.Clear();
            foreach (var profile in ShortcutProfiles.EnumerateProfiles(_customProfiles))
            {
                var displayName = ShortcutProfiles.IsBuiltInProfileId(profile.Id)
                    ? UiLocalization.T(profile.Name)
                    : profile.Name;
                _profileSelector.Items.Add(new ProfileItem(profile.Id, displayName));
            }
        }
        finally
        {
            _profileSelector.EndUpdate();
        }
        Theme.UpdateComboBoxDropDownHeight(_profileSelector);

        var selectedIndex = Enumerable.Range(0, _profileSelector.Items.Count)
            .FirstOrDefault(index => _profileSelector.Items[index] is ProfileItem item
                && string.Equals(item.Id, previousId, StringComparison.Ordinal), -1);
        if (selectedIndex < 0) selectedIndex = 0;
        _profileSelector.SelectedIndex = selectedIndex;
        SelectProfileFromCombo();
    }

    private void SelectProfileFromCombo()
    {
        if (_profileSelector.SelectedItem is not ProfileItem item) return;
        _activeProfileId = item.Id;
        RefreshCommandList();
        RefreshEditState();
        SetFeedback("");
    }

    private void RefreshCommandList()
    {
        var selectedCommandId = SelectedCommandId();
        var profile = ActiveProfile();
        var rows = new List<ThemedTableList.Row>();
        foreach (var command in ShortcutProfiles.Commands)
        {
            if (_selectedCategoryId != "all" && CommandCategory(command) != _selectedCategoryId) continue;
            var binding = profile.Bindings.FirstOrDefault(candidate =>
                string.Equals(candidate.CommandId, command.Id, StringComparison.Ordinal));
            var shortcut = ShortcutProfiles.FormatGestures(binding?.Gestures);
            rows.Add(new ThemedTableList.Row(command.Id, new[]
            {
                UiLocalization.T(command.DisplayName),
                shortcut.Length == 0 ? UiLocalization.T("Unassigned") : shortcut
            }));
        }
        _commandList.SetRows(rows, 2);
        _commandList.SelectById(selectedCommandId);
        RefreshEditState();
    }

    private void RefreshEditState()
    {
        var editable = ShortcutProfiles.IsCustomProfileId(_activeProfileId);
        _newProfile.Enabled = _customProfiles.Count < ShortcutProfiles.MaximumCustomProfiles;
        _renameProfile.Enabled = editable;
        _deleteProfile.Enabled = editable;
        _assignShortcut.Enabled = editable && SelectedCommandId() is not null;
        _clearShortcut.Enabled = _assignShortcut.Enabled;
    }

    private void CreateProfile()
    {
        if (_customProfiles.Count >= ShortcutProfiles.MaximumCustomProfiles)
        {
            SetFeedback("Up to 32 custom shortcut profiles can be created.");
            return;
        }

        var source = ActiveProfile();
        var initialName = $"{source.Name} Copy";
        using var dialog = new ProfileNameDialog(
            "Create Shortcut Profile",
            initialName,
            name => ProfileNameAvailable(name, null));
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var created = ShortcutProfiles.CreateCustomProfile(dialog.ProfileName, source, _customProfiles);
        _customProfiles.Add(created);
        _activeProfileId = created.Id;
        RefreshProfiles();
    }

    private void RenameProfile()
    {
        var profile = ActiveCustomProfile();
        if (profile is null) return;
        using var dialog = new ProfileNameDialog(
            "Rename Shortcut Profile",
            profile.Name,
            name => ProfileNameAvailable(name, profile.Id));
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!ShortcutProfiles.TryRenameCustomProfile(
                profile,
                dialog.ProfileName,
                _customProfiles,
                out var renamed))
        {
            SetFeedback("A shortcut profile already uses that name.");
            return;
        }
        ReplaceCustomProfile(renamed);
        RefreshProfiles();
    }

    private void DeleteProfile()
    {
        var profile = ActiveCustomProfile();
        if (profile is null) return;
        var result = ModernMessageDialog.Show(
            this,
            string.Format(UiLocalization.T("Delete shortcut profile '{0}'?"), profile.Name),
            UiLocalization.T("Delete Shortcut Profile"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes) return;
        _customProfiles.RemoveAll(candidate => string.Equals(candidate.Id, profile.Id, StringComparison.Ordinal));
        _activeProfileId = ShortcutProfiles.TraditionalFlashProfileId;
        RefreshProfiles();
    }

    private void AssignSelectedShortcut()
    {
        var profile = ActiveCustomProfile();
        var commandId = SelectedCommandId();
        if (profile is null || commandId is null) return;
        ShortcutProfiles.TryGetCommand(commandId, out var command);
        var current = profile.Bindings.FirstOrDefault(binding => binding.CommandId == commandId)?.Gestures.FirstOrDefault();
        using var dialog = new ShortcutCaptureDialog(command.Id, command.DisplayName, current);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Gesture is null) return;

        var conflictId = ShortcutProfiles.FindConflictCommandId(profile, commandId, dialog.Gesture);
        var replaceConflicts = false;
        if (conflictId is not null)
        {
            ShortcutProfiles.TryGetCommand(conflictId, out var conflictCommand);
            var message = string.Format(
                UiLocalization.T("{0} is already assigned to {1}. Replace it?"),
                ShortcutProfiles.FormatGesture(dialog.Gesture),
                UiLocalization.T(conflictCommand.DisplayName));
            if (ModernMessageDialog.Show(
                    this,
                    message,
                    UiLocalization.T("Shortcut Conflict"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }
            replaceConflicts = true;
        }

        if (!ShortcutProfiles.TrySetBinding(
                profile,
                commandId,
                [dialog.Gesture],
                replaceConflicts,
                out var updated,
                out _,
                out var error))
        {
            SetFeedback(error ?? "The shortcut could not be assigned.");
            return;
        }

        ReplaceCustomProfile(updated);
        RefreshCommandList();
        if (ShortcutProfiles.TryGetKeyData(dialog.Gesture, out var keyData)
            && ShortcutProfiles.HasContextualFixedShortcutConflict(keyData))
        {
            SetFeedback("This shortcut is overridden in some focused or 3D contexts.", warning: true);
        }
        else
        {
            SetFeedback("");
        }
    }

    private void ClearSelectedShortcut()
    {
        var profile = ActiveCustomProfile();
        var commandId = SelectedCommandId();
        if (profile is null || commandId is null) return;
        if (!ShortcutProfiles.TrySetBinding(
                profile,
                commandId,
                [],
                replaceConflicts: true,
                out var updated,
                out _,
                out var error))
        {
            SetFeedback(error ?? "The shortcut could not be cleared.");
            return;
        }
        ReplaceCustomProfile(updated);
        RefreshCommandList();
        SetFeedback("");
    }

    private ShortcutProfileRecord ActiveProfile()
    {
        return ShortcutProfiles.ResolveProfile(_activeProfileId, _customProfiles);
    }

    private ShortcutProfileRecord? ActiveCustomProfile()
    {
        return _customProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, _activeProfileId, StringComparison.Ordinal));
    }

    private string? SelectedCommandId()
    {
        return _commandList.SelectedId;
    }

    private bool ProfileNameAvailable(string? name, string? currentId)
    {
        var normalized = name?.Trim() ?? "";
        if (normalized.Length is < 1 or > ShortcutProfiles.MaximumProfileNameLength
            || normalized.Any(char.IsControl)) return false;
        if (ShortcutProfiles.BuiltInProfiles.Any(profile =>
                string.Equals(profile.Name, normalized, StringComparison.OrdinalIgnoreCase))) return false;
        return _customProfiles.All(profile =>
            string.Equals(profile.Id, currentId, StringComparison.Ordinal)
            || !string.Equals(profile.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private void ReplaceCustomProfile(ShortcutProfileRecord profile)
    {
        var index = _customProfiles.FindIndex(candidate =>
            string.Equals(candidate.Id, profile.Id, StringComparison.Ordinal));
        if (index >= 0) _customProfiles[index] = ShortcutProfiles.CloneProfile(profile);
    }

    private void SetFeedback(string message, bool warning = false)
    {
        _feedback.Text = UiLocalization.T(message);
        _feedback.ForeColor = warning ? Theme.Warning : Theme.Danger;
        _feedback.AccessibleName = _feedback.Text;
    }

    private sealed record ProfileItem(string Id, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed class ProfileNameDialog : ModernDialogForm
    {
        private readonly TextBox _name = new();
        private readonly Label _error = new();

        public ProfileNameDialog(string title, string initialName, Func<string, bool> validator)
            : base(title, new Size(440, 230))
        {
            DialogContent.Controls.Add(new Label
            {
                Text = "Profile name",
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                Font = Theme.UiFont(9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            });
            _name.Dock = DockStyle.Top;
            _name.Height = Theme.ControlHeight;
            _name.MaxLength = ShortcutProfiles.MaximumProfileNameLength;
            _name.Text = initialName;
            _name.AccessibleName = "Shortcut profile name";
            Theme.StyleTextBox(_name);
            DialogContent.Controls.Add(_name);
            _name.BringToFront();
            _error.Dock = DockStyle.Top;
            _error.Height = 30;
            _error.BackColor = Theme.Panel;
            _error.ForeColor = Theme.Danger;
            _error.Font = Theme.UiFont(8.8f);
            _error.TextAlign = ContentAlignment.MiddleLeft;
            DialogContent.Controls.Add(_error);
            _error.BringToFront();
            var save = AddDialogAction(
                "Save",
                DialogResult.OK,
                DialogActionStyle.Primary,
                () =>
                {
                    if (validator(_name.Text)) return true;
                    _error.Text = UiLocalization.T("Enter a unique profile name.");
                    _error.AccessibleName = _error.Text;
                    return false;
                });
            var cancel = AddDialogAction("Cancel", DialogResult.Cancel);
            AcceptButton = save;
            CancelButton = cancel;
            Shown += (_, _) =>
            {
                _name.Focus();
                _name.SelectAll();
            };
            UiLocalization.Watch(this);
        }

        public string ProfileName => _name.Text.Trim();
    }

    private sealed class ShortcutCaptureDialog : ModernDialogForm
    {
        private readonly ShortcutCaptureTextBox _capture = new();
        private readonly Label _error = new();
        private readonly Button _apply;

        private readonly string _commandId;

        public ShortcutCaptureDialog(string commandId, string commandName, ShortcutGestureRecord? current)
            : base("Assign Shortcut", new Size(460, 250))
        {
            _commandId = commandId;
            DialogContent.Controls.Add(new Label
            {
                Text = commandName,
                Dock = DockStyle.Top,
                Height = 34,
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                Font = Theme.UiFont(10, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            });
            _capture.Dock = DockStyle.Top;
            _capture.Height = Theme.ControlHeight;
            _capture.AccessibleName = string.Format(
                UiLocalization.T("Shortcut for {0}"),
                UiLocalization.T(commandName));
            Theme.StyleTextBox(_capture);
            DialogContent.Controls.Add(_capture);
            _capture.BringToFront();
            _error.Dock = DockStyle.Top;
            _error.Height = 30;
            _error.BackColor = Theme.Panel;
            _error.ForeColor = Theme.Danger;
            _error.Font = Theme.UiFont(8.8f);
            _error.TextAlign = ContentAlignment.MiddleLeft;
            DialogContent.Controls.Add(_error);
            _error.BringToFront();
            _apply = AddDialogAction("Assign", DialogResult.OK, DialogActionStyle.Primary, () => Gesture is not null);
            var cancel = AddDialogAction("Cancel", DialogResult.Cancel);
            AcceptButton = _apply;
            CancelButton = cancel;
            if (current is not null && ShortcutProfiles.TryGetKeyData(current, out _)) SetGesture(current);
            _capture.ShortcutCaptured += CaptureShortcut;
            Shown += (_, _) => _capture.Focus();
            UiLocalization.Watch(this);
        }

        public ShortcutGestureRecord? Gesture { get; private set; }

        private void CaptureShortcut(Keys keyData)
        {
            if (!ShortcutProfiles.TryCreateGesture(keyData, out var gesture))
            {
                SetError("Invalid shortcut");
                return;
            }
            if (!ShortcutProfiles.IsGestureAllowed(_commandId, keyData))
            {
                SetError("This shortcut is reserved by the application.");
                return;
            }
            SetGesture(gesture);
        }

        private void SetGesture(ShortcutGestureRecord gesture)
        {
            Gesture = gesture;
            _capture.Text = ShortcutProfiles.FormatGesture(gesture);
            SetError("");
        }

        private void SetError(string message)
        {
            _error.Text = UiLocalization.T(message);
            _error.AccessibleName = _error.Text;
        }
    }

    private sealed class ShortcutCaptureTextBox : TextBox
    {
        public ShortcutCaptureTextBox()
        {
            ReadOnly = true;
            ShortcutsEnabled = false;
            TextAlign = HorizontalAlignment.Center;
            Cursor = Cursors.Hand;
        }

        public event Action<Keys>? ShortcutCaptured;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (IsModifierKey(e.KeyCode))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            ShortcutCaptured?.Invoke(e.KeyData);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData is Keys.Tab or (Keys.Shift | Keys.Tab) or Keys.Escape)
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }
            var keyCode = keyData & Keys.KeyCode;
            if (IsModifierKey(keyCode)) return true;
            ShortcutCaptured?.Invoke(keyData);
            return true;
        }

        private static bool IsModifierKey(Keys keyCode)
        {
            return keyCode is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
                or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
                or Keys.Menu or Keys.LMenu or Keys.RMenu;
        }
    }
}
