namespace VectorAnimationEngine;

internal sealed record ExternalSvgAssetDragData(string ProjectId, string AssetId);

internal sealed class ExternalSvgAssetRequestedEventArgs(string assetId) : EventArgs
{
    public string AssetId { get; } = assetId;
}

internal sealed partial class LibraryVaultPanel
{
    private enum AssetCategory
    {
        BasicSymbols,
        ThreeDimensionalSymbols,
        ExternalSvg
    }

    private readonly Dictionary<AssetCategory, SegmentedButton> _assetCategoryButtons = [];
    private readonly ToolStripMenuItem _useExternalSvgAssetMenuItem = new("Use");
    private readonly ToolStripMenuItem _relocateExternalSvgAssetMenuItem = new("Relocate...");
    private readonly ToolStripMenuItem _deleteExternalSvgAssetMenuItem = new("Delete");
    private Func<ExternalSvgAssetDefinition, bool>? _externalSvgAssetAvailabilityProvider;
    private AssetCategory _activeAssetCategory = AssetCategory.BasicSymbols;
    private string _selectedExternalSvgAssetId = "";

    public event EventHandler? ExternalSvgAssetAddRequested;
    public event EventHandler<ExternalSvgAssetRequestedEventArgs>? ExternalSvgAssetUseRequested;
    public event EventHandler<ExternalSvgAssetRequestedEventArgs>? ExternalSvgAssetRelocateRequested;
    public event EventHandler<ExternalSvgAssetRequestedEventArgs>? ExternalSvgAssetDeleteRequested;

    public void SetExternalSvgAssetAvailabilityProvider(
        Func<ExternalSvgAssetDefinition, bool>? availabilityProvider)
    {
        if (ReferenceEquals(_externalSvgAssetAvailabilityProvider, availabilityProvider)) return;
        _externalSvgAssetAvailabilityProvider = availabilityProvider;
        _projectRowsDirty = true;
        if (_project is not null) RefreshProjectObjects();
    }

    private Control BuildAssetCategoryBar()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, 2, 0, 3),
            AccessibleName = "Asset categories",
            AccessibleRole = AccessibleRole.Grouping
        };
        for (var column = 0; column < 3; column++)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        AddAssetCategoryButton(layout, AssetCategory.BasicSymbols, "Basic Symbols", 0);
        AddAssetCategoryButton(layout, AssetCategory.ThreeDimensionalSymbols, "3D Symbols", 1);
        AddAssetCategoryButton(layout, AssetCategory.ExternalSvg, "External SVG", 2);
        RefreshAssetCategoryPresentation();
        return layout;
    }

    private void AddAssetCategoryButton(
        TableLayoutPanel layout,
        AssetCategory category,
        string text,
        int column)
    {
        var button = new SegmentedButton
        {
            Dock = DockStyle.Fill,
            Text = text,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true,
            Margin = new Padding(column == 0 ? 0 : 2, 0, 0, 0),
            AccessibleName = text,
            AccessibleRole = AccessibleRole.RadioButton,
            Tag = category,
            TabIndex = column
        };
        Theme.StyleSegmentedButton(button);
        button.Click += (_, _) => SelectAssetCategory(category);
        _assetCategoryButtons.Add(category, button);
        layout.Controls.Add(button, column, 0);
    }

    private void SelectAssetCategory(AssetCategory category, bool refresh = true)
    {
        if (_activeAssetCategory == category)
        {
            RefreshAssetCategoryPresentation();
            if (refresh)
            {
                _projectRowsDirty = true;
                RefreshProjectObjects();
            }
            return;
        }

        HidePreview(clearContent: true);
        _activeAssetCategory = category;
        _projectRowsDirty = true;
        RefreshAssetCategoryPresentation();
        if (refresh) RefreshProjectObjects();
    }

    private void RefreshAssetCategoryPresentation()
    {
        foreach (var (category, button) in _assetCategoryButtons)
        {
            var selected = category == _activeAssetCategory;
            Theme.StyleSegmentedButton(button, selected);
            button.TabStop = selected;
        }

        var externalSvg = _activeAssetCategory == AssetCategory.ExternalSvg;
        _assetTagFilter.Enabled = !externalSvg;
        _manageTagsButton.Enabled = !externalSvg && SelectedProjectDrawingObject() is not null;
        _newFolderButton.Icon = externalSvg ? SvgIconKind.Add : SvgIconKind.FolderPlus;
        _newFolderButton.AccessibleName = UiLocalization.T(externalSvg ? "Add SVG Link" : "New Folder");
        _toolTip.SetToolTip(
            _newFolderButton,
            UiLocalization.T(externalSvg ? "Add SVG Link..." : "New Folder"));
        if (_openButton is not null)
        {
            _openButton.AccessibleName = UiLocalization.T(
                externalSvg ? "Use selected SVG link" : "Open selected symbol");
            _toolTip.SetToolTip(
                _openButton,
                UiLocalization.T(externalSvg ? "Use selected SVG link" : "Open selected symbol"));
        }
        _newFolderButton.Invalidate();
    }

    private void RequestPrimaryAssetCreation()
    {
        if (_activeAssetCategory == AssetCategory.ExternalSvg)
        {
            ExternalSvgAssetAddRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        RequestNewAssetFolder();
    }

    private bool DrawingObjectMatchesActiveCategory(DrawingObjectDefinition drawingObject)
    {
        var threeDimensional = DrawingObjectAssetKinds.IsThreeDimensional(drawingObject.Kind);
        return _activeAssetCategory switch
        {
            AssetCategory.BasicSymbols => !threeDimensional,
            AssetCategory.ThreeDimensionalSymbols => threeDimensional,
            _ => false
        };
    }

    private AssetCategory CategoryForDrawingObject(DrawingObjectDefinition? drawingObject) =>
        drawingObject is not null
        && DrawingObjectAssetKinds.IsThreeDimensional(drawingObject.Kind)
            ? AssetCategory.ThreeDimensionalSymbols
            : AssetCategory.BasicSymbols;

    private void AddExternalSvgAssetNodes(TreeNodeCollection nodes)
    {
        if (_project is null) return;
        foreach (var asset in _project.ExternalSvgAssets)
        {
            if (!ExternalSvgAssetMatchesSearch(asset)) continue;
            var missing = ExternalSvgAssetIsMissing(asset);
            var displayPath = ExternalSvgAssetDisplayPath(asset);
            var item = new VaultItem
            {
                Kind = "External SVG",
                Name = asset.Name,
                Detail = missing ? "Missing link" : displayPath,
                Payload = displayPath,
                ReferenceKind = "ExternalSvgAsset",
                ReferenceId = asset.Id,
                CreatedAt = asset.CreatedAt
            };
            var node = new TreeNode(asset.Name)
            {
                Tag = new VaultRow(
                    item,
                    DrawingObject: null,
                    IsProjectObject: true,
                    Folder: null,
                    ExternalSvgAsset: asset,
                    ExternalSvgMissing: missing),
                ForeColor = missing ? Theme.Warning : Color.Empty,
                ToolTipText = missing
                    ? $"{UiLocalization.T("Missing link")}: {displayPath}"
                    : displayPath
            };
            nodes.Add(node);
        }
    }

    private bool ExternalSvgAssetMatchesSearch(ExternalSvgAssetDefinition asset)
    {
        var search = AssetSearchText;
        return search.Length == 0
            || ContainsSearch(asset.Name, search)
            || ContainsSearch(asset.SourcePath, search)
            || ContainsSearch(asset.ProjectRelativePath, search);
    }

    private static string ExternalSvgAssetDisplayPath(ExternalSvgAssetDefinition asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.ProjectRelativePath)) return asset.ProjectRelativePath;
        if (!string.IsNullOrWhiteSpace(asset.SourcePath)) return asset.SourcePath;
        return UiLocalization.T("Missing link");
    }

    private bool ExternalSvgAssetIsMissing(ExternalSvgAssetDefinition asset)
    {
        try
        {
            return _externalSvgAssetAvailabilityProvider is { } availabilityProvider
                ? !availabilityProvider(asset)
                : string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath);
        }
        catch
        {
            return true;
        }
    }

    private static bool ExternalSvgRowCanBeUsed(VaultRow? row) =>
        row is { ExternalSvgAsset: not null, ExternalSvgMissing: false };

    private void RaiseSelectedExternalSvgAssetRequest(
        EventHandler<ExternalSvgAssetRequestedEventArgs>? requested,
        bool requireAvailable)
    {
        var row = SelectedProjectRow();
        if (row?.ExternalSvgAsset is not { } asset
            || requireAvailable && row.ExternalSvgMissing)
        {
            return;
        }
        requested?.Invoke(this, new ExternalSvgAssetRequestedEventArgs(asset.Id));
    }

    private void AppendExternalSvgContextMenuItems(ToolStripItemCollection items)
    {
        _useExternalSvgAssetMenuItem.Click += (_, _) => RaiseSelectedExternalSvgAssetRequest(
            ExternalSvgAssetUseRequested,
            requireAvailable: true);
        _relocateExternalSvgAssetMenuItem.Click += (_, _) => RaiseSelectedExternalSvgAssetRequest(
            ExternalSvgAssetRelocateRequested,
            requireAvailable: false);
        _deleteExternalSvgAssetMenuItem.Click += (_, _) => RaiseSelectedExternalSvgAssetRequest(
            ExternalSvgAssetDeleteRequested,
            requireAvailable: false);
        items.AddRange(new ToolStripItem[]
        {
            _useExternalSvgAssetMenuItem,
            _relocateExternalSvgAssetMenuItem,
            _deleteExternalSvgAssetMenuItem
        });
    }

    private void UpdateExternalSvgContextMenu(VaultRow? selected)
    {
        var externalSvgSelected = selected?.ExternalSvgAsset is not null;
        _useExternalSvgAssetMenuItem.Visible = externalSvgSelected;
        _useExternalSvgAssetMenuItem.Enabled = ExternalSvgRowCanBeUsed(selected);
        _relocateExternalSvgAssetMenuItem.Visible = externalSvgSelected;
        _relocateExternalSvgAssetMenuItem.Enabled = externalSvgSelected;
        _deleteExternalSvgAssetMenuItem.Visible = externalSvgSelected;
        _deleteExternalSvgAssetMenuItem.Enabled = externalSvgSelected;
    }

    private int ActiveAssetCategoryCount()
    {
        if (_project is null) return 0;
        return _activeAssetCategory switch
        {
            AssetCategory.BasicSymbols => _project.DrawingObjects.Count(drawingObject =>
                !DrawingObjectAssetKinds.IsThreeDimensional(drawingObject.Kind)),
            AssetCategory.ThreeDimensionalSymbols => _project.DrawingObjects.Count(drawingObject =>
                DrawingObjectAssetKinds.IsThreeDimensional(drawingObject.Kind)),
            _ => _project.ExternalSvgAssets.Count
        };
    }

    private string ActiveAssetCategoryEmptyText() => _activeAssetCategory switch
    {
        AssetCategory.BasicSymbols => "No basic symbols",
        AssetCategory.ThreeDimensionalSymbols => "No 3D symbols",
        _ => "No external SVG links"
    };
}
