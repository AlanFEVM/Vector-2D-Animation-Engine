namespace VectorAnimationEngine;

internal sealed class ShortcutProfileEditorPanel : UserControl
{
    private readonly ComboBox _profileSelector = new();
    private readonly Button _newProfile = new() { Text = "New" };
    private readonly Button _renameProfile = new() { Text = "Rename" };
    private readonly Button _deleteProfile = new() { Text = "Delete" };
    private readonly ListView _commandList = new();
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
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        Controls.Add(layout);

        var profileRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 5,
            RowCount = 1,
            Margin = Padding.Empty
        };
        profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        profileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        profileRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        profileRow.Controls.Add(new Label
        {
            Text = "Profile",
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ForeColor = Theme.Muted,
            Font = Theme.UiFont(),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _profileSelector.Dock = DockStyle.Fill;
        _profileSelector.Margin = new Padding(0, 4, 8, 4);
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

        layout.Controls.Add(CreateCommandTable(), 0, 1);

        var actionRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
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
        layout.Controls.Add(_feedback, 0, 3);
    }

    private Control CreateCommandTable()
    {
        _commandList.Dock = DockStyle.Fill;
        _commandList.Margin = Padding.Empty;
        _commandList.AccessibleName = "Tool shortcut map";
        _commandList.Columns.Add("Tool", 420);
        _commandList.Columns.Add("Shortcut", 190, HorizontalAlignment.Center);
        Theme.StyleListView(_commandList);
        _commandList.HeaderStyle = ColumnHeaderStyle.None;
        _commandList.FullRowSelect = true;
        _commandList.MultiSelect = false;
        _commandList.HideSelection = false;
        _commandList.SelectedIndexChanged += (_, _) => RefreshEditState();
        _commandList.DoubleClick += (_, _) => AssignSelectedShortcut();
        _commandList.ClientSizeChanged += (_, _) => ResizeShortcutColumns();

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelStrong,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        header.Controls.Add(HeaderLabel("Tool", ContentAlignment.MiddleLeft), 0, 0);
        header.Controls.Add(HeaderLabel("Shortcut", ContentAlignment.MiddleCenter), 1, 0);
        table.Controls.Add(header, 0, 0);
        table.Controls.Add(_commandList, 0, 1);
        return table;
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

    private static Label HeaderLabel(string text, ContentAlignment alignment)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            Margin = new Padding(10, 0, 4, 0),
            BackColor = Theme.PanelStrong,
            ForeColor = Theme.Text,
            Font = Theme.UiFont(8.5f),
            TextAlign = alignment
        };
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
        _commandList.BeginUpdate();
        try
        {
            _commandList.Items.Clear();
            foreach (var command in ShortcutProfiles.Commands)
            {
                var binding = profile.Bindings.FirstOrDefault(candidate =>
                    string.Equals(candidate.CommandId, command.Id, StringComparison.Ordinal));
                var shortcut = ShortcutProfiles.FormatGestures(binding?.Gestures);
                var row = new ListViewItem(UiLocalization.T(command.DisplayName)) { Tag = command.Id };
                row.SubItems.Add(shortcut.Length == 0 ? UiLocalization.T("Unassigned") : shortcut);
                _commandList.Items.Add(row);
                if (string.Equals(command.Id, selectedCommandId, StringComparison.Ordinal)) row.Selected = true;
            }
        }
        finally
        {
            _commandList.EndUpdate();
        }
        ResizeShortcutColumns();
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
        using var dialog = new ShortcutCaptureDialog(command.DisplayName, current);
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
        return _commandList.SelectedItems.Count == 1
            ? _commandList.SelectedItems[0].Tag as string
            : null;
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

    private void ResizeShortcutColumns()
    {
        if (_commandList.Columns.Count < 2 || _commandList.ClientSize.Width < 260) return;
        const int shortcutWidth = 190;
        _commandList.Columns[0].Width = Math.Max(100, _commandList.ClientSize.Width - shortcutWidth - 28);
        _commandList.Columns[1].Width = shortcutWidth;
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

        public ShortcutCaptureDialog(string commandName, ShortcutGestureRecord? current)
            : base("Assign Shortcut", new Size(460, 250))
        {
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
            if (ShortcutProfiles.IsReservedGesture(keyData))
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
            if (keyData is Keys.Tab or (Keys.Shift | Keys.Tab) or Keys.Escape or Keys.Enter)
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
