using System.Diagnostics;
using System.Globalization;

namespace VectorAnimationEngine;

internal sealed class ReleaseManagerForm : Form
{
    private readonly ReleaseDocumentStore _store;
    private readonly ReleasePublishRunner _publisher;
    private readonly ToolTip _toolTip = new();
    private readonly Panel _versionPane = new();
    private readonly ListBox _versionList = new();
    private readonly Button _newVersionButton = new();
    private readonly Button _deleteVersionButton = new();
    private readonly Button _saveButton = new();
    private readonly ReleaseManagerTabControl _tabs = new();
    private readonly TabPage _editorPage = new();
    private readonly TabPage _previewPage = new();
    private readonly TabPage _publishPage = new();
    private readonly TextBox _versionBox = new();
    private readonly TextBox _releaseDateBox = new();
    private readonly ModernToggleSwitch _visibleToggle = new();
    private readonly Button _englishButton = new();
    private readonly Button _chineseButton = new();
    private readonly TextBox _titleBox = new();
    private readonly TextBox _summaryBox = new();
    private readonly ListBox _sectionList = new();
    private readonly TextBox _sectionHeadingBox = new();
    private readonly TextBox _sectionItemsBox = new();
    private readonly SvgIconButton _addSectionButton = new(SvgIconKind.Add);
    private readonly SvgIconButton _removeSectionButton = new(SvgIconKind.Remove);
    private readonly SvgIconButton _moveSectionUpButton = new(SvgIconKind.ChevronUp);
    private readonly SvgIconButton _moveSectionDownButton = new(SvgIconKind.ChevronDown);
    private readonly Button _previewEnglishButton = new();
    private readonly Button _previewChineseButton = new();
    private readonly Panel _previewHost = new();
    private readonly Label _previewStatus = new();
    private readonly TextBox _outputDirectoryBox = new();
    private readonly Button _browseOutputButton = new();
    private readonly Button _publishButton = new();
    private readonly Button _cancelPublishButton = new();
    private readonly Label _publishVersionLabel = new();
    private readonly TextBox _resultPathBox = new();
    private readonly TextBox _resultHashBox = new();
    private readonly Label _resultSizeLabel = new();
    private readonly Button _openResultButton = new();
    private readonly RichTextBox _publishLog = new();
    private readonly Label _statusLabel = new();

    private ReleaseNotesManifestDocument _document = new();
    private ReleaseNotesManifestRelease? _selectedRelease;
    private Version _sourceVersion = new();
    private UiLanguage _editorLanguage = UiLanguage.English;
    private UiLanguage _previewLanguage = UiLanguage.English;
    private bool _updating;
    private bool _dirty;
    private bool _publishing;
    private CancellationTokenSource? _publishCancellation;

    public ReleaseManagerForm()
        : this(RepositoryLocator.Find())
    {
    }

    internal ReleaseManagerForm(string repositoryRoot)
    {
        _store = new ReleaseDocumentStore(repositoryRoot);
        _publisher = new ReleasePublishRunner(repositoryRoot);

        Text = "Release Manager";
        BackColor = Theme.App;
        ForeColor = Theme.Text;
        Font = Theme.UiFont();
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(920, 620);
        ClientSize = new Size(1120, 740);
        KeyPreview = true;
        AccessibleName = "Vector 2D Animation Engine Release Manager";

        BuildUi();
        WireEvents();
        Theme.StyleToolTip(_toolTip);
        LoadDocument();
    }

    private bool IsChinese => UiLocalization.CurrentLanguage == UiLanguage.SimplifiedChinese;
    private string T(string english, string chinese) => IsChinese ? chinese : english;

    private void BuildUi()
    {
        SuspendLayout();
        Controls.Add(BuildBody());
        Controls.Add(BuildHeader());
        Controls.Add(BuildStatusBar());
        ResumeLayout(performLayout: true);
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            BackColor = Theme.Top,
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(16, 8, 12, 8)
        };
        var layout = new TableLayoutPanel
        {
            BackColor = Theme.Top,
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        var title = CreateLabel("Release Manager", 12.5f, FontStyle.Bold, Theme.Text);
        title.BackColor = Theme.Top;
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.BottomLeft;
        layout.Controls.Add(title, 0, 0);

        var repository = CreateLabel(_store.RepositoryRoot, 8.7f, FontStyle.Regular, Theme.Muted);
        repository.BackColor = Theme.Top;
        repository.Dock = DockStyle.Fill;
        repository.AutoEllipsis = true;
        repository.TextAlign = ContentAlignment.TopLeft;
        layout.SetColumnSpan(repository, 2);
        layout.Controls.Add(repository, 0, 1);

        _saveButton.Text = T("Save Version", "保存版本");
        _saveButton.Dock = DockStyle.Fill;
        _saveButton.Margin = new Padding(8, 2, 0, 2);
        _saveButton.AccessibleName = _saveButton.Text;
        Theme.StylePrimaryButton(_saveButton);
        layout.Controls.Add(_saveButton, 2, 0);
        header.Controls.Add(layout);
        return header;
    }

    private Control BuildBody()
    {
        var split = new SplitContainer
        {
            BackColor = Theme.Border,
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            IsSplitterFixed = false,
            Size = new Size(900, 620),
            Panel1MinSize = 210,
            Panel2MinSize = 620,
            SplitterDistance = 248,
            SplitterWidth = 1
        };
        split.Panel1.BackColor = Theme.Panel;
        split.Panel2.BackColor = Theme.Panel;
        split.Panel1.Controls.Add(BuildVersionPane());
        split.Panel2.Controls.Add(BuildWorkspace());
        return split;
    }

    private Control BuildVersionPane()
    {
        _versionPane.BackColor = Theme.Panel;
        _versionPane.Dock = DockStyle.Fill;
        _versionPane.Padding = new Padding(12);

        var layout = new TableLayoutPanel
        {
            BackColor = Theme.Panel,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        var heading = CreateSectionLabel(T("VERSIONS", "版本"));
        layout.Controls.Add(heading, 0, 0);

        _versionList.Dock = DockStyle.Fill;
        _versionList.FormattingEnabled = true;
        _versionList.AccessibleName = T("Release versions", "发布版本");
        Theme.StyleListBox(_versionList);
        layout.Controls.Add(_versionList, 0, 1);

        var actions = new TableLayoutPanel
        {
            BackColor = Theme.Panel,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 8, 0, 0),
            RowCount = 1
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        _newVersionButton.Text = T("New version", "新建版本");
        _newVersionButton.Dock = DockStyle.Fill;
        _newVersionButton.Margin = new Padding(0, 0, 4, 0);
        Theme.StyleButton(_newVersionButton);
        _deleteVersionButton.Text = T("Delete", "删除");
        _deleteVersionButton.Dock = DockStyle.Fill;
        _deleteVersionButton.Margin = new Padding(4, 0, 0, 0);
        Theme.StyleDangerButton(_deleteVersionButton);
        actions.Controls.Add(_newVersionButton, 0, 0);
        actions.Controls.Add(_deleteVersionButton, 1, 0);
        layout.Controls.Add(actions, 0, 2);
        _versionPane.Controls.Add(layout);
        return _versionPane;
    }

    private Control BuildWorkspace()
    {
        _tabs.BackColor = Theme.Top;
        _tabs.Dock = DockStyle.Fill;
        _tabs.AccessibleName = T("Release workflow", "发布工作区");

        _editorPage.Text = T("Content", "内容");
        _previewPage.Text = T("Preview", "预览");
        _publishPage.Text = T("Publish", "发布");
        foreach (var page in new[] { _editorPage, _previewPage, _publishPage })
        {
            page.BackColor = Theme.Panel;
            page.ForeColor = Theme.Text;
            page.Padding = new Padding(14);
        }
        _editorPage.Controls.Add(BuildEditor());
        _previewPage.Controls.Add(BuildPreview());
        _publishPage.Controls.Add(BuildPublish());
        _tabs.TabPages.AddRange([_editorPage, _previewPage, _publishPage]);
        return _tabs;
    }

    private Control BuildEditor()
    {
        var layout = new TableLayoutPanel
        {
            BackColor = Theme.Panel,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 6
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildMetadataEditor(), 0, 0);
        layout.Controls.Add(BuildLocaleSelector(_englishButton, _chineseButton), 0, 1);
        layout.Controls.Add(BuildLabeledTextBox(T("Title", "标题"), _titleBox, multiline: false), 0, 2);
        layout.Controls.Add(BuildLabeledTextBox(T("Summary", "摘要"), _summaryBox, multiline: true), 0, 3);
        layout.Controls.Add(BuildSectionToolbar(), 0, 4);
        layout.Controls.Add(BuildSectionEditor(), 0, 5);
        return layout;
    }

    private Control BuildMetadataEditor()
    {
        var layout = new TableLayoutPanel
        {
            BackColor = Theme.Panel,
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            RowCount = 2,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.Controls.Add(CreateFieldLabel(T("Version", "版本号")), 0, 0);
        layout.Controls.Add(CreateFieldLabel(T("Release date", "发布日期")), 1, 0);
        layout.Controls.Add(CreateFieldLabel(T("In-app visibility", "程序内显示")), 2, 0);

        StyleTextBox(_versionBox, multiline: false);
        _versionBox.Dock = DockStyle.Fill;
        _versionBox.Margin = new Padding(0, 0, 10, 3);
        _versionBox.AccessibleDescription = "Strict x.y.z version";
        layout.Controls.Add(_versionBox, 0, 1);
        StyleTextBox(_releaseDateBox, multiline: false);
        _releaseDateBox.Dock = DockStyle.Fill;
        _releaseDateBox.Margin = new Padding(0, 0, 10, 3);
        _releaseDateBox.AccessibleDescription = "Release date in yyyy-MM-dd format";
        layout.Controls.Add(_releaseDateBox, 1, 1);
        _visibleToggle.Text = T("Show in application", "在程序中显示");
        _visibleToggle.Dock = DockStyle.Left;
        _visibleToggle.AccessibleName = _visibleToggle.Text;
        layout.Controls.Add(_visibleToggle, 2, 1);
        return layout;
    }

    private Control BuildLocaleSelector(Button english, Button chinese)
    {
        var panel = new FlowLayoutPanel
        {
            BackColor = Theme.Panel,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            Padding = new Padding(0, 6, 0, 4),
            WrapContents = false
        };
        ConfigureSegmentedButton(english, "English", 104);
        ConfigureSegmentedButton(chinese, "简体中文", 104);
        english.Margin = Padding.Empty;
        chinese.Margin = Padding.Empty;
        panel.Controls.Add(english);
        panel.Controls.Add(chinese);
        return panel;
    }

    private Control BuildLabeledTextBox(string labelText, TextBox box, bool multiline)
    {
        var layout = new TableLayoutPanel
        {
            BackColor = Theme.Panel,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(CreateFieldLabel(labelText), 0, 0);
        StyleTextBox(box, multiline);
        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(0, 0, 0, 6);
        layout.Controls.Add(box, 0, 1);
        return layout;
    }

    private Control BuildSectionToolbar()
    {
        var layout = new TableLayoutPanel
        {
            BackColor = Theme.Panel,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            RowCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(CreateSectionLabel(T("SECTIONS", "分区")), 0, 0);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            BackColor = Theme.Panel,
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            WrapContents = false
        };
        ConfigureIconButton(_addSectionButton, T("Add section", "添加分区"));
        ConfigureIconButton(_removeSectionButton, T("Remove section", "删除分区"));
        ConfigureIconButton(_moveSectionUpButton, T("Move section up", "上移分区"));
        ConfigureIconButton(_moveSectionDownButton, T("Move section down", "下移分区"));
        actions.Controls.AddRange([_addSectionButton, _removeSectionButton, _moveSectionUpButton, _moveSectionDownButton]);
        layout.Controls.Add(actions, 1, 0);
        return layout;
    }

    private Control BuildSectionEditor()
    {
        var split = new SplitContainer
        {
            BackColor = Theme.Border,
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            Size = new Size(620, 300),
            Panel1MinSize = 180,
            Panel2MinSize = 300,
            SplitterDistance = 225,
            SplitterWidth = 1
        };
        split.Panel1.BackColor = Theme.Panel;
        split.Panel1.Padding = new Padding(0, 0, 10, 0);
        split.Panel2.BackColor = Theme.Panel;
        split.Panel2.Padding = new Padding(10, 0, 0, 0);

        _sectionList.Dock = DockStyle.Fill;
        _sectionList.FormattingEnabled = true;
        _sectionList.AccessibleName = T("Release note sections", "更新说明分区");
        Theme.StyleListBox(_sectionList);
        split.Panel1.Controls.Add(_sectionList);

        var details = new TableLayoutPanel
        {
            BackColor = Theme.Panel,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 4
        };
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        details.Controls.Add(CreateFieldLabel(T("Section heading", "分区标题")), 0, 0);
        StyleTextBox(_sectionHeadingBox, multiline: false);
        _sectionHeadingBox.Dock = DockStyle.Fill;
        _sectionHeadingBox.Margin = new Padding(0, 0, 0, 6);
        details.Controls.Add(_sectionHeadingBox, 0, 1);
        details.Controls.Add(CreateFieldLabel(T("Items (one paragraph per line)", "条目（每行一段）")), 0, 2);
        StyleTextBox(_sectionItemsBox, multiline: true);
        _sectionItemsBox.Dock = DockStyle.Fill;
        details.Controls.Add(_sectionItemsBox, 0, 3);
        split.Panel2.Controls.Add(details);
        return split;
    }

    private Control BuildPreview()
    {
        var layout = new TableLayoutPanel
        {
            BackColor = Theme.Panel,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildLocaleSelector(_previewEnglishButton, _previewChineseButton), 0, 0);

        _previewHost.BackColor = Theme.Panel;
        _previewHost.Dock = DockStyle.Fill;
        _previewHost.Padding = Padding.Empty;
        _previewStatus.BackColor = Theme.Panel;
        _previewStatus.Dock = DockStyle.Fill;
        _previewStatus.Font = Theme.UiFont(9.5f);
        _previewStatus.ForeColor = Theme.Muted;
        _previewStatus.TextAlign = ContentAlignment.MiddleCenter;
        _previewStatus.AccessibleRole = AccessibleRole.StaticText;
        _previewHost.Controls.Add(_previewStatus);
        layout.Controls.Add(_previewHost, 0, 1);
        return layout;
    }

    private Control BuildPublish()
    {
        var layout = new TableLayoutPanel
        {
            BackColor = Theme.Panel,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 7
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _publishVersionLabel.Dock = DockStyle.Fill;
        _publishVersionLabel.Font = Theme.UiFont(11, FontStyle.Bold);
        _publishVersionLabel.ForeColor = Theme.Text;
        _publishVersionLabel.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_publishVersionLabel, 0, 0);
        layout.Controls.Add(BuildOutputDirectory(), 0, 1);
        layout.Controls.Add(BuildPublishActions(), 0, 2);
        layout.Controls.Add(CreateSectionLabel(T("VERIFIED RESULT", "已验证结果")), 0, 3);
        layout.Controls.Add(BuildResultFields(), 0, 4);
        layout.Controls.Add(CreateSectionLabel(T("LIVE LOG", "实时日志")), 0, 5);

        _publishLog.BackColor = Theme.Field;
        _publishLog.ForeColor = Theme.Text;
        _publishLog.BorderStyle = BorderStyle.FixedSingle;
        _publishLog.DetectUrls = false;
        _publishLog.Dock = DockStyle.Fill;
        _publishLog.Font = new Font("Cascadia Mono", 8.6f);
        _publishLog.ReadOnly = true;
        _publishLog.WordWrap = false;
        _publishLog.AccessibleName = T("Live publish log", "实时发布日志");
        layout.Controls.Add(_publishLog, 0, 6);
        return layout;
    }

    private Control BuildOutputDirectory()
    {
        var layout = new TableLayoutPanel
        {
            BackColor = Theme.Panel,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.Controls.Add(CreateFieldLabel(T("Output directory", "输出目录")), 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 2);
        StyleTextBox(_outputDirectoryBox, multiline: false);
        _outputDirectoryBox.Dock = DockStyle.Fill;
        _outputDirectoryBox.Margin = new Padding(0, 0, 8, 2);
        layout.Controls.Add(_outputDirectoryBox, 0, 1);
        _browseOutputButton.Text = T("Browse...", "浏览...");
        _browseOutputButton.Dock = DockStyle.Fill;
        _browseOutputButton.Margin = new Padding(0, 0, 0, 2);
        Theme.StyleButton(_browseOutputButton);
        layout.Controls.Add(_browseOutputButton, 1, 1);
        return layout;
    }

    private Control BuildPublishActions()
    {
        var panel = new FlowLayoutPanel
        {
            BackColor = Theme.Panel,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 7, 0, 6),
            WrapContents = false
        };
        _publishButton.Text = T("Publish selected version", "发布所选版本");
        _publishButton.Width = 180;
        _publishButton.Height = 32;
        Theme.StylePrimaryButton(_publishButton);
        _cancelPublishButton.Text = T("Cancel", "取消");
        _cancelPublishButton.Width = 92;
        _cancelPublishButton.Height = 32;
        _cancelPublishButton.Enabled = false;
        Theme.StyleButton(_cancelPublishButton);
        panel.Controls.AddRange([_publishButton, _cancelPublishButton]);
        return panel;
    }

    private Control BuildResultFields()
    {
        var layout = new TableLayoutPanel
        {
            BackColor = Theme.Panel,
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        ConfigureReadOnlyResultBox(_resultPathBox);
        ConfigureReadOnlyResultBox(_resultHashBox);
        layout.Controls.Add(CreateFieldLabel(T("Artifact", "产物")), 0, 0);
        layout.Controls.Add(_resultPathBox, 1, 0);
        _openResultButton.Text = T("Open folder", "打开目录");
        _openResultButton.Dock = DockStyle.Fill;
        _openResultButton.Margin = new Padding(8, 0, 0, 3);
        _openResultButton.Enabled = false;
        Theme.StyleButton(_openResultButton);
        layout.Controls.Add(_openResultButton, 2, 0);
        layout.Controls.Add(CreateFieldLabel("SHA-256"), 0, 1);
        layout.Controls.Add(_resultHashBox, 1, 1);
        layout.SetColumnSpan(_resultHashBox, 2);
        layout.Controls.Add(CreateFieldLabel(T("Size", "大小")), 0, 2);
        _resultSizeLabel.Dock = DockStyle.Fill;
        _resultSizeLabel.ForeColor = Theme.Muted;
        _resultSizeLabel.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_resultSizeLabel, 1, 2);
        layout.SetColumnSpan(_resultSizeLabel, 2);
        return layout;
    }

    private Control BuildStatusBar()
    {
        var status = new Panel
        {
            BackColor = Theme.Top,
            Dock = DockStyle.Bottom,
            Height = 28,
            Padding = new Padding(12, 0, 12, 0)
        };
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.ForeColor = Theme.Muted;
        _statusLabel.Font = Theme.UiFont(8.8f);
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.AutoEllipsis = true;
        status.Controls.Add(_statusLabel);
        return status;
    }

    private void WireEvents()
    {
        _versionList.SelectedIndexChanged += (_, _) => SelectRelease(_versionList.SelectedItem as ReleaseNotesManifestRelease);
        _versionList.Format += (_, args) =>
        {
            if (args.ListItem is ReleaseNotesManifestRelease release) args.Value = release.ToDisplayString();
        };
        _newVersionButton.Click += (_, _) => CreateVersion();
        _deleteVersionButton.Click += (_, _) => DeleteSelectedVersion();
        _saveButton.Click += (_, _) => SaveDocument();
        _versionBox.TextChanged += (_, _) => UpdateSelectedRelease();
        _releaseDateBox.TextChanged += (_, _) => UpdateSelectedRelease();
        _visibleToggle.CheckedChanged += (_, _) => UpdateSelectedRelease();
        _titleBox.TextChanged += (_, _) => UpdateSelectedLocale();
        _summaryBox.TextChanged += (_, _) => UpdateSelectedLocale();
        _englishButton.Click += (_, _) => SetEditorLanguage(UiLanguage.English);
        _chineseButton.Click += (_, _) => SetEditorLanguage(UiLanguage.SimplifiedChinese);
        _previewEnglishButton.Click += (_, _) => SetPreviewLanguage(UiLanguage.English);
        _previewChineseButton.Click += (_, _) => SetPreviewLanguage(UiLanguage.SimplifiedChinese);
        _sectionList.SelectedIndexChanged += (_, _) => LoadSelectedSection();
        _sectionList.Format += (_, args) =>
        {
            if (args.ListItem is ReleaseNotesManifestSection section)
            {
                args.Value = string.IsNullOrWhiteSpace(section.Heading)
                    ? T("Untitled section", "未命名分区")
                    : section.Heading;
            }
        };
        _sectionHeadingBox.TextChanged += (_, _) => UpdateSelectedSection();
        _sectionItemsBox.TextChanged += (_, _) => UpdateSelectedSection();
        _addSectionButton.Click += (_, _) => AddSection();
        _removeSectionButton.Click += (_, _) => RemoveSection();
        _moveSectionUpButton.Click += (_, _) => MoveSection(-1);
        _moveSectionDownButton.Click += (_, _) => MoveSection(1);
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab == _previewPage) RefreshPreview();
            if (_tabs.SelectedTab == _publishPage) RefreshPublishState();
        };
        _browseOutputButton.Click += (_, _) => BrowseOutputDirectory();
        _publishButton.Click += async (_, _) => await PublishAsync();
        _cancelPublishButton.Click += (_, _) => RequestPublishCancellation();
        _openResultButton.Click += (_, _) => OpenResultFolder();
        FormClosing += HandleFormClosing;

        _toolTip.SetToolTip(_addSectionButton, T("Add section", "添加分区"));
        _toolTip.SetToolTip(_removeSectionButton, T("Remove selected section", "删除所选分区"));
        _toolTip.SetToolTip(_moveSectionUpButton, T("Move selected section up", "上移所选分区"));
        _toolTip.SetToolTip(_moveSectionDownButton, T("Move selected section down", "下移所选分区"));
    }

    private void LoadDocument()
    {
        _document = _store.LoadManifest();
        _sourceVersion = _store.LoadSourceVersion();
        _outputDirectoryBox.Text = Path.Combine(_store.RepositoryRoot, "artifacts", "release");
        RefreshVersionList(_sourceVersion.ToString(3));
        SetDirty(false);
        SetStatus(string.Format(
            CultureInfo.CurrentCulture,
            T("Loaded {0} releases. Source version: {1}", "已加载 {0} 个版本。当前源码版本：{1}"),
            _document.Releases.Count,
            _sourceVersion.ToString(3)));
    }

    private void RefreshVersionList(string? selectedVersion)
    {
        _updating = true;
        _versionList.BeginUpdate();
        try
        {
            _versionList.Items.Clear();
            foreach (var release in _document.Releases) _versionList.Items.Add(release);
            var selected = _document.Releases.FirstOrDefault(release => release.Version == selectedVersion)
                ?? _document.Releases.FirstOrDefault();
            if (selected is not null) _versionList.SelectedItem = selected;
        }
        finally
        {
            _versionList.EndUpdate();
            _updating = false;
        }
        SelectRelease(_versionList.SelectedItem as ReleaseNotesManifestRelease);
    }

    private void SelectRelease(ReleaseNotesManifestRelease? release)
    {
        if (_updating) return;
        _selectedRelease = release;
        _updating = true;
        try
        {
            var hasRelease = release is not null;
            _editorPage.Enabled = hasRelease && !_publishing;
            _deleteVersionButton.Enabled = hasRelease && !_publishing;
            _versionBox.Text = release?.Version ?? string.Empty;
            _releaseDateBox.Text = release?.ReleaseDate ?? string.Empty;
            _visibleToggle.Checked = release?.ShowInApplication ?? false;
        }
        finally
        {
            _updating = false;
        }
        LoadSelectedLocale();
        RefreshPreview();
        RefreshPublishState();
    }

    private void CreateVersion()
    {
        var release = ReleaseDocumentStore.CreateNextPatch(_document);
        _document.Releases.Insert(0, release);
        RefreshVersionList(release.Version);
        SetDirty(true);
        _versionBox.Focus();
        _versionBox.SelectAll();
    }

    private void DeleteSelectedVersion()
    {
        if (_selectedRelease is null) return;
        var result = ModernMessageDialog.Show(
            this,
            string.Format(
                CultureInfo.CurrentCulture,
                T("Delete version {0} and its bilingual release notes?", "删除版本 {0} 及其双语更新说明？"),
                _selectedRelease.Version),
            T("Delete version", "删除版本"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (result != DialogResult.Yes) return;

        _document.Releases.Remove(_selectedRelease);
        RefreshVersionList(null);
        SetDirty(true);
    }

    private void UpdateSelectedRelease()
    {
        if (_updating || _selectedRelease is null) return;
        _selectedRelease.Version = _versionBox.Text.Trim();
        _selectedRelease.ReleaseDate = _releaseDateBox.Text.Trim();
        _selectedRelease.ShowInApplication = _visibleToggle.Checked;
        _versionList.Refresh();
        SetDirty(true);
        RefreshPublishState();
    }

    private ReleaseNotesManifestLocale? SelectedLocale => _selectedRelease is null
        ? null
        : _editorLanguage == UiLanguage.SimplifiedChinese
            ? _selectedRelease.Locales.SimplifiedChinese
            : _selectedRelease.Locales.English;

    private void SetEditorLanguage(UiLanguage language)
    {
        if (_editorLanguage == language) return;
        _editorLanguage = language;
        LoadSelectedLocale();
    }

    private void LoadSelectedLocale()
    {
        _updating = true;
        try
        {
            Theme.StyleSegmentedButton(_englishButton, _editorLanguage == UiLanguage.English);
            Theme.StyleSegmentedButton(_chineseButton, _editorLanguage == UiLanguage.SimplifiedChinese);
            var locale = SelectedLocale;
            _titleBox.Text = locale?.Title ?? string.Empty;
            _summaryBox.Text = locale?.Summary ?? string.Empty;
            RefreshSectionList(locale?.Sections.FirstOrDefault());
        }
        finally
        {
            _updating = false;
        }
        LoadSelectedSection();
    }

    private void UpdateSelectedLocale()
    {
        if (_updating || SelectedLocale is not { } locale) return;
        locale.Title = _titleBox.Text;
        locale.Summary = _summaryBox.Text;
        SetDirty(true);
    }

    private void RefreshSectionList(ReleaseNotesManifestSection? selection)
    {
        var locale = SelectedLocale;
        _sectionList.BeginUpdate();
        try
        {
            _sectionList.Items.Clear();
            if (locale is not null)
            {
                foreach (var section in locale.Sections) _sectionList.Items.Add(section);
            }
            if (selection is not null && _sectionList.Items.Contains(selection)) _sectionList.SelectedItem = selection;
            else if (_sectionList.Items.Count > 0) _sectionList.SelectedIndex = 0;
        }
        finally
        {
            _sectionList.EndUpdate();
        }
    }

    private void LoadSelectedSection()
    {
        var section = _sectionList.SelectedItem as ReleaseNotesManifestSection;
        _updating = true;
        try
        {
            _sectionHeadingBox.Text = section?.Heading ?? string.Empty;
            _sectionItemsBox.Lines = section?.Items.ToArray() ?? [];
            var enabled = section is not null && !_publishing;
            _sectionHeadingBox.Enabled = enabled;
            _sectionItemsBox.Enabled = enabled;
            _removeSectionButton.Enabled = enabled;
            _moveSectionUpButton.Enabled = enabled && _sectionList.SelectedIndex > 0;
            _moveSectionDownButton.Enabled = enabled && _sectionList.SelectedIndex < _sectionList.Items.Count - 1;
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateSelectedSection()
    {
        if (_updating || _sectionList.SelectedItem is not ReleaseNotesManifestSection section) return;
        var previousItems = section.Items;
        var nextItems = _sectionItemsBox.Lines
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        section.Heading = _sectionHeadingBox.Text;
        section.Items = nextItems;
        SynchronizeOtherLocaleItems(_sectionList.SelectedIndex, previousItems, nextItems);
        _sectionList.Refresh();
        SetDirty(true);
    }

    private void AddSection()
    {
        if (_selectedRelease is null) return;
        var englishSection = new ReleaseNotesManifestSection
        {
            Heading = "New section",
            Items = ["Describe this change."]
        };
        var chineseSection = new ReleaseNotesManifestSection
        {
            Heading = "新分区",
            Items = ["描述本项改动。"]
        };
        _selectedRelease.Locales.English.Sections.Add(englishSection);
        _selectedRelease.Locales.SimplifiedChinese.Sections.Add(chineseSection);
        var selectedSection = _editorLanguage == UiLanguage.SimplifiedChinese
            ? chineseSection
            : englishSection;
        RefreshSectionList(selectedSection);
        LoadSelectedSection();
        SetDirty(true);
        _sectionHeadingBox.Focus();
        _sectionHeadingBox.SelectAll();
    }

    private void RemoveSection()
    {
        if (_selectedRelease is null || _sectionList.SelectedItem is not ReleaseNotesManifestSection) return;
        var index = _sectionList.SelectedIndex;
        var locale = SelectedLocale!;
        _selectedRelease.Locales.English.Sections.RemoveAt(index);
        _selectedRelease.Locales.SimplifiedChinese.Sections.RemoveAt(index);
        var nextSelection = locale.Sections.Count == 0
            ? null
            : locale.Sections[Math.Min(index, locale.Sections.Count - 1)];
        RefreshSectionList(nextSelection);
        LoadSelectedSection();
        SetDirty(true);
    }

    private void MoveSection(int offset)
    {
        if (_selectedRelease is null || _sectionList.SelectedItem is not ReleaseNotesManifestSection section) return;
        var locale = SelectedLocale!;
        var oldIndex = _sectionList.SelectedIndex;
        var newIndex = oldIndex + offset;
        if (newIndex < 0 || newIndex >= locale.Sections.Count) return;
        MoveSection(_selectedRelease.Locales.English.Sections, oldIndex, newIndex);
        MoveSection(_selectedRelease.Locales.SimplifiedChinese.Sections, oldIndex, newIndex);
        RefreshSectionList(section);
        LoadSelectedSection();
        SetDirty(true);
    }

    private void SynchronizeOtherLocaleItems(
        int sectionIndex,
        IReadOnlyList<string> previousItems,
        IReadOnlyList<string> nextItems)
    {
        if (_selectedRelease is null || sectionIndex < 0) return;
        var otherLocale = _editorLanguage == UiLanguage.SimplifiedChinese
            ? _selectedRelease.Locales.English
            : _selectedRelease.Locales.SimplifiedChinese;
        if (sectionIndex >= otherLocale.Sections.Count) return;

        var items = otherLocale.Sections[sectionIndex].Items;
        var placeholder = _editorLanguage == UiLanguage.SimplifiedChinese
            ? "Describe this change."
            : "描述本项改动。";
        var commonSuffixCount = 0;
        while (commonSuffixCount < previousItems.Count
               && commonSuffixCount < nextItems.Count
               && string.Equals(
                   previousItems[previousItems.Count - commonSuffixCount - 1],
                   nextItems[nextItems.Count - commonSuffixCount - 1],
                   StringComparison.Ordinal))
        {
            commonSuffixCount++;
        }

        if (nextItems.Count > previousItems.Count)
        {
            var insertionIndex = Math.Clamp(previousItems.Count - commonSuffixCount, 0, items.Count);
            items.InsertRange(
                insertionIndex,
                Enumerable.Repeat(placeholder, nextItems.Count - previousItems.Count));
        }
        else if (nextItems.Count < previousItems.Count)
        {
            var removalIndex = Math.Clamp(nextItems.Count - commonSuffixCount, 0, items.Count);
            var removalCount = Math.Min(previousItems.Count - nextItems.Count, items.Count - removalIndex);
            if (removalCount > 0) items.RemoveRange(removalIndex, removalCount);
        }
    }

    private static void MoveSection(List<ReleaseNotesManifestSection> sections, int oldIndex, int newIndex)
    {
        var section = sections[oldIndex];
        sections.RemoveAt(oldIndex);
        sections.Insert(newIndex, section);
    }

    private void SaveDocument()
    {
        if (_publishing || _selectedRelease is null) return;
        if (!ReleaseDocumentStore.TryParseStrictVersion(_selectedRelease.Version, out var selectedVersion))
        {
            ShowValidationErrors([T("Selected version must use strict x.y.z format.", "所选版本必须使用严格的 x.y.z 格式。")]);
            return;
        }

        var errors = _store.Validate(_document, selectedVersion);
        if (errors.Count > 0)
        {
            ShowValidationErrors(errors);
            return;
        }

        try
        {
            var selectedText = selectedVersion.ToString(3);
            _store.Save(_document, selectedVersion);
            _sourceVersion = selectedVersion;
            RefreshVersionList(selectedText);
            SetDirty(false);
            SetStatus(string.Format(
                CultureInfo.CurrentCulture,
                T("Saved manifest and source version {0}.", "已保存清单和源码版本 {0}。"),
                selectedText));
        }
        catch (Exception exception)
        {
            ModernMessageDialog.Show(
                this,
                exception.Message,
                T("Save failed", "保存失败"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void ShowValidationErrors(IEnumerable<string> errors)
    {
        var message = T("Resolve these issues before saving:\n\n", "保存前请解决以下问题：\n\n")
            + string.Join(Environment.NewLine, errors.Select(error => "• " + error));
        ModernMessageDialog.Show(
            this,
            message,
            T("Release notes validation", "更新说明验证"),
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void SetPreviewLanguage(UiLanguage language)
    {
        _previewLanguage = language;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        Theme.StyleSegmentedButton(_previewEnglishButton, _previewLanguage == UiLanguage.English);
        Theme.StyleSegmentedButton(_previewChineseButton, _previewLanguage == UiLanguage.SimplifiedChinese);
        foreach (Control control in _previewHost.Controls.Cast<Control>().ToArray())
        {
            _previewHost.Controls.Remove(control);
            if (control != _previewStatus) control.Dispose();
        }

        try
        {
            if (_selectedRelease is null) throw new InvalidDataException(T("Select a version to preview.", "请选择要预览的版本。"));
            var entry = ReleaseDocumentStore.ToReleaseNoteEntry(_selectedRelease);
            var panel = new ReleaseNotesPanel([entry], _previewLanguage)
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty
            };
            _previewHost.Controls.Add(panel);
        }
        catch (Exception exception)
        {
            _previewStatus.Text = exception.Message;
            _previewHost.Controls.Add(_previewStatus);
        }
    }

    private void RefreshPublishState()
    {
        var selectedVersion = _selectedRelease?.Version ?? T("No version selected", "未选择版本");
        _publishVersionLabel.Text = string.Format(
            CultureInfo.CurrentCulture,
            T("Formal release {0}", "正式发布 {0}"),
            selectedVersion);
        var selectedIsSavedSource = !_dirty
            && _selectedRelease is not null
            && string.Equals(_selectedRelease.Version, _sourceVersion.ToString(3), StringComparison.Ordinal)
            && ReleaseDocumentStore.TryParseStrictVersion(_selectedRelease.Version, out _);
        var sourceFilesUnchanged = selectedIsSavedSource && _store.SourceFilesMatchLoaded();
        var publishErrors = selectedIsSavedSource
            ? _store.ValidateForPublish(_document, _sourceVersion)
            : [];
        var canPublish = selectedIsSavedSource && sourceFilesUnchanged && publishErrors.Count == 0;
        _publishButton.Enabled = !_publishing && canPublish;
        var disabledReason = !sourceFilesUnchanged
            ? T(
                "Release source files changed outside Release Manager. Reload before publishing.",
                "发布源文件已被外部修改。请重新载入后再发布。")
            : publishErrors.Any(error => error.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
            ? T("Replace all placeholder release-note content before publishing.", "请先替换全部更新说明占位内容。")
            : T("Resolve release-note validation errors before publishing.", "请先解决更新说明验证错误。");
        var publishDescription = canPublish
            ? T("Build the selected formal release.", "构建所选正式版本。")
            : !selectedIsSavedSource
                ? T("Save the selected version before publishing.", "发布前请先保存所选版本。")
                : disabledReason;
        _publishButton.AccessibleDescription = publishDescription;
        _toolTip.SetToolTip(_publishButton, publishDescription);
    }

    private void BrowseOutputDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = T("Select an empty formal release output directory", "选择一个空的正式发布输出目录"),
            InitialDirectory = Directory.Exists(_outputDirectoryBox.Text) ? _outputDirectoryBox.Text : _store.RepositoryRoot,
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _outputDirectoryBox.Text = dialog.SelectedPath;
    }

    private async Task PublishAsync()
    {
        if (_publishing || _selectedRelease is null) return;
        if (_dirty || !string.Equals(_selectedRelease.Version, _sourceVersion.ToString(3), StringComparison.Ordinal))
        {
            ModernMessageDialog.Show(
                this,
                T("Save the selected version before publishing.", "发布前请先保存所选版本。"),
                T("Publish", "发布"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(_outputDirectoryBox.Text)) return;
        ReleaseSourceFingerprints sourceFingerprints;
        try
        {
            sourceFingerprints = _store.GetLoadedFingerprints();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ModernMessageDialog.Show(
                this,
                exception.Message,
                T("Release sources changed", "发布源文件已变更"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            RefreshPublishState();
            return;
        }
        var publishErrors = _store.ValidateForPublish(_document, _sourceVersion);
        if (publishErrors.Count > 0)
        {
            ShowValidationErrors(publishErrors);
            return;
        }

        _publishCancellation = new CancellationTokenSource();
        SetPublishing(true);
        ClearPublishResult();
        _publishLog.Clear();
        AppendPublishLog(string.Format(
            CultureInfo.CurrentCulture,
            T("Publishing version {0}...", "正在发布版本 {0}..."),
            _sourceVersion.ToString(3)));

        try
        {
            var result = await _publisher.PublishAsync(
                _sourceVersion,
                _outputDirectoryBox.Text,
                sourceFingerprints,
                AppendPublishLog,
                _publishCancellation.Token);
            _resultPathBox.Text = result.Path;
            _resultHashBox.Text = result.Sha256;
            _resultSizeLabel.Text = $"{FormatBytes(result.SizeBytes)} / {FormatBytes(result.MaximumBytes)}";
            _openResultButton.Enabled = true;
            AppendPublishLog(T("Publish completed and verified.", "发布完成并已验证。"));
            SetStatus(string.Format(
                CultureInfo.CurrentCulture,
                T("Published {0} ({1}).", "已发布 {0}（{1}）。"),
                result.Version,
                FormatBytes(result.SizeBytes)));
        }
        catch (OperationCanceledException)
        {
            AppendPublishLog(T("Publish cancelled.", "发布已取消。"));
            SetStatus(T("Publish cancelled.", "发布已取消。"));
        }
        catch (Exception exception)
        {
            AppendPublishLog("ERROR: " + exception.Message);
            ModernMessageDialog.Show(
                this,
                exception.Message,
                T("Publish failed", "发布失败"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _publishCancellation.Dispose();
            _publishCancellation = null;
            SetPublishing(false);
        }
    }

    private void SetPublishing(bool publishing)
    {
        _publishing = publishing;
        _versionPane.Enabled = !publishing;
        _editorPage.Enabled = !publishing && _selectedRelease is not null;
        _saveButton.Enabled = !publishing;
        _browseOutputButton.Enabled = !publishing;
        _outputDirectoryBox.Enabled = !publishing;
        _cancelPublishButton.Enabled = publishing;
        RefreshPublishState();
    }

    private void RequestPublishCancellation()
    {
        if (_publishCancellation is null || _publishCancellation.IsCancellationRequested) return;
        _publishCancellation.Cancel();
        _cancelPublishButton.Enabled = false;
        SetStatus(T(
            "Cancellation requested; waiting for the current packaging step.",
            "已请求取消；正在等待当前打包步骤完成。"));
    }

    private void AppendPublishLog(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AppendPublishLog(line)));
            return;
        }
        _publishLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        _publishLog.SelectionStart = _publishLog.TextLength;
        _publishLog.ScrollToCaret();
    }

    private void ClearPublishResult()
    {
        _resultPathBox.Clear();
        _resultHashBox.Clear();
        _resultSizeLabel.Text = string.Empty;
        _openResultButton.Enabled = false;
    }

    private void OpenResultFolder()
    {
        if (!File.Exists(_resultPathBox.Text)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { "/select,", _resultPathBox.Text },
            UseShellExecute = true
        });
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        Text = dirty ? "Release Manager *" : "Release Manager";
        RefreshPublishState();
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    private void HandleFormClosing(object? sender, FormClosingEventArgs args)
    {
        if (_publishing)
        {
            args.Cancel = true;
            SetStatus(T("Cancel publishing before closing.", "关闭前请先取消发布。"));
            return;
        }
        if (!_dirty) return;

        var result = ModernMessageDialog.Show(
            this,
            T("Discard unsaved release changes?", "放弃尚未保存的版本改动？"),
            T("Unsaved changes", "未保存的改动"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        args.Cancel = result != DialogResult.Yes;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S))
        {
            SaveDocument();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _publishCancellation?.Cancel();
            _publishCancellation?.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Label CreateLabel(string text, float size, FontStyle style, Color color) => new()
    {
        AutoEllipsis = true,
        AutoSize = false,
        BackColor = Theme.Panel,
        Font = Theme.UiFont(size, style),
        ForeColor = color,
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
        UseMnemonic = false
    };

    private static Label CreateFieldLabel(string text)
    {
        var label = CreateLabel(text, 8.8f, FontStyle.Regular, Theme.Muted);
        label.Dock = DockStyle.Fill;
        label.AccessibleRole = AccessibleRole.StaticText;
        return label;
    }

    private static Label CreateSectionLabel(string text)
    {
        var label = CreateLabel(text, 8.5f, FontStyle.Bold, Theme.AccentLabel);
        label.Dock = DockStyle.Fill;
        label.AccessibleRole = AccessibleRole.StaticText;
        return label;
    }

    private static void StyleTextBox(TextBox box, bool multiline)
    {
        box.Multiline = multiline;
        box.AcceptsReturn = multiline;
        box.ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None;
        Theme.StyleTextBox(box);
    }

    private static void ConfigureSegmentedButton(Button button, string text, int width)
    {
        button.Text = text;
        button.Width = width;
        button.Height = Theme.ControlHeight;
        button.AccessibleName = text;
        button.AccessibleRole = AccessibleRole.RadioButton;
        Theme.StyleSegmentedButton(button, active: false);
    }

    private void ConfigureIconButton(SvgIconButton button, string accessibleName)
    {
        button.Width = Theme.ControlHeightCompact;
        button.Height = Theme.ControlHeightCompact;
        button.Margin = new Padding(2, 0, 0, 0);
        button.AccessibleName = accessibleName;
        button.AccessibleRole = AccessibleRole.PushButton;
        Theme.StyleToolbarButton(button);
    }

    private static void ConfigureReadOnlyResultBox(TextBox box)
    {
        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(0, 2, 0, 3);
        box.ReadOnly = true;
        Theme.StyleTextBox(box);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / (1024d * 1024d):0.00} MiB";
        if (bytes >= 1024) return $"{bytes / 1024d:0.0} KiB";
        return bytes + " B";
    }
}

internal static class ReleaseManagerDisplayExtensions
{
    public static string ToDisplayString(this ReleaseNotesManifestRelease release) =>
        $"{release.Version}    {release.ReleaseDate}" + (release.ShowInApplication ? string.Empty : "  [hidden]");
}
