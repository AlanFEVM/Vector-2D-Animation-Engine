namespace VectorAnimationEngine;

internal sealed class SettingsDialog : Form
{
    private readonly Button _traditionalPreset = new() { Text = "Traditional Flash" };
    private readonly Button _numberPreset = new() { Text = "Number Keys" };
    private readonly Button _englishLanguage = new() { Text = "English" };
    private readonly Button _chineseLanguage = new() { Text = "简体中文" };
    private readonly ListView _shortcutList = new();
    private ToolShortcutPreset _selectedPreset;
    private UiLanguage _selectedLanguage;

    public SettingsDialog()
        : this(ToolShortcutPreset.TraditionalFlash, UiLanguage.English)
    {
    }

    public SettingsDialog(ToolShortcutPreset selectedPreset, UiLanguage selectedLanguage)
    {
        _selectedPreset = selectedPreset;
        _selectedLanguage = selectedLanguage;
        Text = "Settings";
        ClientSize = new Size(520, 590);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Panel;
        Font = Theme.UiFont();
        AccessibleName = "Application settings";

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 7
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        Controls.Add(content);

        var languageHeading = new Label
        {
            Text = "Language",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(10.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        content.Controls.Add(languageHeading, 0, 0);

        var languageSelector = CreateSelector();
        ConfigurePresetButton(_englishLanguage, "Use the English interface");
        ConfigurePresetButton(_chineseLanguage, "使用简体中文界面");
        _englishLanguage.Margin = new Padding(0, 0, 2, 0);
        _chineseLanguage.Margin = new Padding(2, 0, 0, 0);
        _englishLanguage.Click += (_, _) => SelectLanguage(UiLanguage.English);
        _chineseLanguage.Click += (_, _) => SelectLanguage(UiLanguage.SimplifiedChinese);
        languageSelector.Controls.Add(_englishLanguage, 0, 0);
        languageSelector.Controls.Add(_chineseLanguage, 1, 0);
        content.Controls.Add(languageSelector, 0, 1);

        var heading = new Label
        {
            Text = "Tool shortcuts",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(10.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        content.Controls.Add(heading, 0, 2);

        var presetSelector = CreateSelector();
        ConfigurePresetButton(_traditionalPreset, "Use traditional Flash tool shortcuts");
        ConfigurePresetButton(_numberPreset, "Use number-key tool shortcuts");
        _traditionalPreset.Margin = new Padding(0, 0, 2, 0);
        _numberPreset.Margin = new Padding(2, 0, 0, 0);
        _traditionalPreset.Click += (_, _) => SelectPreset(ToolShortcutPreset.TraditionalFlash);
        _numberPreset.Click += (_, _) => SelectPreset(ToolShortcutPreset.NumberKeys);
        presetSelector.Controls.Add(_traditionalPreset, 0, 0);
        presetSelector.Controls.Add(_numberPreset, 1, 0);
        content.Controls.Add(presetSelector, 0, 3);

        var mappingHeading = new Label
        {
            Text = "Shortcut map",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Panel,
            Font = Theme.UiFont(9.2f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        content.Controls.Add(mappingHeading, 0, 4);

        _shortcutList.Dock = DockStyle.Fill;
        _shortcutList.Margin = new Padding(0);
        _shortcutList.AccessibleName = "Tool shortcut map";
        _shortcutList.Columns.Add("Tool", 350);
        _shortcutList.Columns.Add("Shortcut", 90, HorizontalAlignment.Center);
        Theme.StyleListView(_shortcutList);
        _shortcutList.HeaderStyle = ColumnHeaderStyle.None;
        _shortcutList.ClientSizeChanged += (_, _) => ResizeShortcutColumns();

        var shortcutTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        shortcutTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shortcutTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        shortcutTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var shortcutHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.PanelStrong,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        shortcutHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shortcutHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        shortcutHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        shortcutHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shortcutHeader.Controls.Add(ShortcutHeaderLabel("Tool", ContentAlignment.MiddleLeft), 0, 0);
        shortcutHeader.Controls.Add(ShortcutHeaderLabel("Shortcut", ContentAlignment.MiddleCenter), 1, 0);
        shortcutTable.Controls.Add(shortcutHeader, 0, 0);
        shortcutTable.Controls.Add(_shortcutList, 0, 1);
        content.Controls.Add(shortcutTable, 0, 5);

        var commands = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0),
            Margin = Padding.Empty
        };
        var save = new Button { Text = "Save", Width = 84, Height = 30, Margin = Padding.Empty };
        var cancel = new Button { Text = "Cancel", Width = 84, Height = 30, Margin = new Padding(0, 0, 8, 0) };
        Theme.StyleActiveButton(save);
        Theme.StyleButton(cancel);
        save.Click += (_, _) => DialogResult = DialogResult.OK;
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        commands.Controls.Add(save);
        commands.Controls.Add(cancel);
        content.Controls.Add(commands, 0, 6);
        AcceptButton = save;
        CancelButton = cancel;

        SelectPreset(_selectedPreset);
        SelectLanguage(_selectedLanguage);
        UiLocalization.Watch(this);
    }

    public ToolShortcutPreset SelectedPreset => _selectedPreset;
    public UiLanguage SelectedLanguage => _selectedLanguage;

    private static TableLayoutPanel CreateSelector()
    {
        var selector = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 2, 0, 6)
        };
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        selector.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        return selector;
    }

    private static void ConfigurePresetButton(Button button, string accessibleDescription)
    {
        button.Dock = DockStyle.Fill;
        button.AccessibleName = button.Text;
        button.AccessibleDescription = accessibleDescription;
        Theme.StyleButton(button);
    }

    private static Label ShortcutHeaderLabel(string text, ContentAlignment alignment)
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

    private void SelectPreset(ToolShortcutPreset preset)
    {
        _selectedPreset = preset;
        if (preset == ToolShortcutPreset.TraditionalFlash)
        {
            Theme.StyleActiveButton(_traditionalPreset);
            Theme.StyleButton(_numberPreset);
        }
        else
        {
            Theme.StyleButton(_traditionalPreset);
            Theme.StyleActiveButton(_numberPreset);
        }

        _shortcutList.BeginUpdate();
        try
        {
            _shortcutList.Items.Clear();
            foreach (var binding in ToolShortcutMap.DisplayBindings(preset))
            {
                var item = new ListViewItem(binding.Tool);
                item.SubItems.Add(binding.Shortcut);
                _shortcutList.Items.Add(item);
            }
        }
        finally
        {
            _shortcutList.EndUpdate();
        }
    }

    private void SelectLanguage(UiLanguage language)
    {
        _selectedLanguage = language;
        if (language == UiLanguage.English)
        {
            Theme.StyleActiveButton(_englishLanguage);
            Theme.StyleButton(_chineseLanguage);
        }
        else
        {
            Theme.StyleButton(_englishLanguage);
            Theme.StyleActiveButton(_chineseLanguage);
        }
    }

    private void ResizeShortcutColumns()
    {
        if (_shortcutList.Columns.Count < 2 || _shortcutList.ClientSize.Width < 180) return;
        const int shortcutWidth = 90;
        _shortcutList.Columns[0].Width = Math.Max(80, _shortcutList.ClientSize.Width - shortcutWidth - 28);
        _shortcutList.Columns[1].Width = shortcutWidth;
    }
}
