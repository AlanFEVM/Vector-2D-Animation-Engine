using System.Globalization;

namespace VectorAnimationEngine;

internal sealed class ReleaseNotesPanel : UserControl
{
    private const int ScrollRailAllowance = 12;
    private readonly Panel _focusFrame = new();
    private readonly ThemedScrollPanel _scrollHost = new();
    private readonly TableLayoutPanel _content = new();
    private readonly List<Label> _wrappingLabels = [];
    private readonly UiLanguage _language;

    public ReleaseNotesPanel()
        : this(ReleaseNotesCatalog.Entries, UiLocalization.CurrentLanguage)
    {
    }

    internal ReleaseNotesPanel(IReadOnlyList<ReleaseNoteEntry> entries, UiLanguage language)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) throw new ArgumentException("At least one release note is required.", nameof(entries));

        _language = Enum.IsDefined(language) ? language : UiLanguage.English;
        BackColor = Theme.Panel;
        MinimumSize = new Size(440, 300);
        AccessibleRole = AccessibleRole.Document;
        AccessibleName = UiLocalization.T("Release Notes", _language);
        AccessibleDescription = string.Format(
            CultureInfo.CurrentCulture,
            UiLocalization.T("Release notes for version {0}", _language),
            entries[0].Version);

        _focusFrame.BackColor = Theme.Border;
        _focusFrame.Dock = DockStyle.Fill;
        _focusFrame.Padding = new Padding(1);
        _focusFrame.TabStop = false;
        Controls.Add(_focusFrame);

        _scrollHost.Dock = DockStyle.Fill;
        _scrollHost.BackColor = Theme.Panel;
        _scrollHost.ContentPadding = new Padding(22, 18, 22, 24);
        _scrollHost.TabStop = true;
        _scrollHost.TabIndex = 0;
        _scrollHost.AccessibleRole = AccessibleRole.Document;
        _scrollHost.AccessibleName = AccessibleName;
        _scrollHost.AccessibleDescription = UiLocalization.T(
            "Use arrow keys, Page Up, Page Down, Home, or End to read the release notes.",
            _language);
        _scrollHost.Enter += (_, _) => _focusFrame.BackColor = Theme.Accent;
        _scrollHost.Leave += (_, _) => _focusFrame.BackColor = Theme.Border;
        _scrollHost.Resize += (_, _) => UpdateWrappingWidths();
        _focusFrame.Controls.Add(_scrollHost);

        _content.AutoSize = true;
        _content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _content.BackColor = Theme.Panel;
        _content.ColumnCount = 1;
        _content.Dock = DockStyle.Top;
        _content.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
        _content.Margin = Padding.Empty;
        _content.Padding = Padding.Empty;
        _content.RowCount = 0;
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _scrollHost.Content.Controls.Add(_content);

        _scrollHost.SuspendContentLayout();
        _content.SuspendLayout();
        try
        {
            for (var index = 0; index < entries.Count; index++)
            {
                if (index > 0) AddEntrySeparator();
                AddEntry(entries[index]);
            }
        }
        finally
        {
            _content.ResumeLayout(performLayout: true);
            _scrollHost.ResumeContentLayout(performLayout: true);
        }

        UpdateWrappingWidths();
    }

    internal void FocusContent() => _scrollHost.Focus();

    private void AddEntry(ReleaseNoteEntry entry)
    {
        var localized = entry.ContentFor(_language);
        var versionText = string.Format(
            CultureInfo.CurrentCulture,
            UiLocalization.T("Version {0}", _language),
            entry.Version);
        AddLabel(versionText, Theme.UiFont(12.5f, FontStyle.Bold), Theme.AccentLabel, new Padding(0, 0, 0, 2));

        var dateValue = _language == UiLanguage.SimplifiedChinese
            ? entry.ReleaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : entry.ReleaseDate.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        var releaseDateText = string.Format(
            CultureInfo.CurrentCulture,
            UiLocalization.T("Released: {0}", _language),
            dateValue);
        AddLabel(releaseDateText, Theme.UiFont(9), Theme.Muted, new Padding(0, 0, 0, 12));
        AddLabel(localized.Title, Theme.UiFont(10.5f, FontStyle.Bold), Theme.Text, new Padding(0, 0, 0, 8));
        AddLabel(localized.Summary, Theme.UiFont(9.5f), Theme.Text, new Padding(0, 0, 0, 18));

        foreach (var section in localized.Sections)
        {
            AddLabel(section.Heading, Theme.UiFont(9.7f, FontStyle.Bold), Theme.AccentLabel, new Padding(0, 0, 0, 7));
            foreach (var item in section.Items)
            {
                AddLabel($"\u2022  {item}", Theme.UiFont(9.5f), Theme.Text, new Padding(0, 0, 0, 8), item);
            }
            AddSpacer(8);
        }
    }

    private void AddEntrySeparator()
    {
        var separator = new Panel
        {
            BackColor = Theme.Border,
            Dock = DockStyle.Top,
            Height = 1,
            Margin = new Padding(0, 16, 0, 18),
            TabStop = false
        };
        AddRow(separator);
    }

    private void AddSpacer(int height)
    {
        AddRow(new Panel
        {
            BackColor = Theme.Panel,
            Dock = DockStyle.Top,
            Height = height,
            Margin = Padding.Empty,
            TabStop = false
        });
    }

    private void AddLabel(string text, Font font, Color color, Padding margin, string? accessibleName = null)
    {
        var label = new Label
        {
            AutoSize = true,
            BackColor = Theme.Panel,
            Dock = DockStyle.Top,
            Font = font,
            ForeColor = color,
            Margin = margin,
            MaximumSize = new Size(560, 0),
            Text = text,
            TextAlign = ContentAlignment.TopLeft,
            UseMnemonic = false,
            AccessibleName = accessibleName ?? text,
            AccessibleRole = AccessibleRole.StaticText
        };
        label.MouseDown += (_, _) => _scrollHost.Focus();
        _wrappingLabels.Add(label);
        AddRow(label);
    }

    private void AddRow(Control control)
    {
        var row = _content.RowCount++;
        _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content.Controls.Add(control, 0, row);
    }

    private void UpdateWrappingWidths()
    {
        if (_wrappingLabels.Count == 0 || _scrollHost.ClientSize.Width <= 0) return;
        var contentPadding = _scrollHost.ContentPadding;
        var width = Math.Max(
            200,
            _scrollHost.ClientSize.Width
            - ScrollRailAllowance
            - contentPadding.Horizontal);
        if (_wrappingLabels[0].MaximumSize.Width == width) return;

        _content.SuspendLayout();
        try
        {
            foreach (var label in _wrappingLabels) label.MaximumSize = new Size(width, 0);
        }
        finally
        {
            _content.ResumeLayout(performLayout: true);
        }
    }
}
