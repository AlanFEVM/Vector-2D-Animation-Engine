using System.Drawing.Drawing2D;

namespace VectorAnimationEngine;

internal sealed partial class TimelineStrip
{
    private const int ToolbarHeaderLogicalHeight = 34;
    private const int TabGroupRowLogicalHeight = 30;
    private const string TabGroupOverflowId = "__timeline_tab_group_overflow__";

    private readonly SvgIconButton _tabGroupAddButton = new(SvgIconKind.Add)
    {
        AccessibleName = "Create timeline tab group",
        TabStop = true,
        Visible = false
    };
    private readonly Button _tabGroupOverflowButton = new()
    {
        Text = "...",
        AccessibleName = "More timeline tab groups",
        TabStop = true,
        Visible = false,
        AutoSize = false,
        UseMnemonic = false
    };
    private readonly AnimatedContextMenuStrip _tabGroupContextMenu = new();
    private readonly AnimatedContextMenuStrip _tabGroupOverflowMenu = new();
    private readonly ToolStripMenuItem _moveLayerToTabGroupMenuItem = new(UiLocalization.T("Move to tab group"));
    private string? _hoveredTabGroupId;
    private string? _focusedTabGroupId;
    private string _tabGroupToolTipText = "";

    private readonly record struct TabGroupItem(string Id, string Name, bool BuiltIn);

    private readonly record struct TabGroupTabLayout(
        string Id,
        string Name,
        Rectangle Bounds,
        bool BuiltIn);

    private readonly record struct TabGroupLayoutSnapshot(
        IReadOnlyList<TabGroupTabLayout> VisibleTabs,
        IReadOnlyList<TabGroupItem> OverflowItems,
        Rectangle OverflowBounds,
        Rectangle AddBounds);

    private int ToolbarHeaderHeight => ScaleTimelineMetric(ToolbarHeaderLogicalHeight);

    private int TabGroupRowHeight => ScaleTimelineMetric(TabGroupRowLogicalHeight);

    private int HeaderHeight => ToolbarHeaderHeight + TabGroupRowHeight;

    public event EventHandler? TabGroupEditStarting;
    public event EventHandler? TabGroupEditCompleted;

    public bool SetActiveTabGroup(string groupId)
    {
        return _timeline is not null && _timeline.SetActiveTabGroup(groupId);
    }

    private IWin32Window DialogOwner => (IWin32Window?)FindForm() ?? (IWin32Window)this;

    private void InitializeTabGroupUi()
    {
        Theme.StyleToolbarButton(_tabGroupAddButton);
        Theme.StyleToolbarButton(_tabGroupOverflowButton);
        _tabGroupOverflowButton.Width = ScaleTimelineMetric(30);
        _tabGroupOverflowButton.Height = ScaleTimelineMetric(24);
        _layerControlToolTip.SetToolTip(_tabGroupAddButton, UiLocalization.T("Create timeline tab group"));
        _layerControlToolTip.SetToolTip(_tabGroupOverflowButton, UiLocalization.T("More timeline tab groups"));
        _tabGroupAddButton.Click += (_, _) => CreateTimelineTabGroup();
        _tabGroupOverflowButton.Click += (_, _) => ShowTabGroupOverflowMenu();
        _tabGroupContextMenu.AccessibleName = "Timeline tab group actions";
        _tabGroupOverflowMenu.AccessibleName = "Timeline tab groups";
        _layerContextMenu.Items.Add(_moveLayerToTabGroupMenuItem);
        _layerContextMenu.Opening += HandleLayerTabGroupContextMenuOpening;
        Controls.Add(_tabGroupOverflowButton);
        Controls.Add(_tabGroupAddButton);
    }

    private void DisposeTabGroupUi()
    {
        ClearTabGroupMenuItems(_tabGroupContextMenu.Items);
        ClearTabGroupMenuItems(_tabGroupOverflowMenu.Items);
        _tabGroupContextMenu.Dispose();
        _tabGroupOverflowMenu.Dispose();
    }

    private IReadOnlyList<TabGroupItem> TabGroupItems()
    {
        var items = new List<TabGroupItem>
        {
            new(AnimationTimeline.AllTabGroupId, UiLocalization.T("All"), true)
        };
        if (_timeline is null) return items;

        foreach (var group in _timeline.TabGroups)
        {
            if (string.Equals(group.Id, AnimationTimeline.AllTabGroupId, StringComparison.Ordinal)) continue;
            var name = group.Id switch
            {
                AnimationTimeline.DefaultTabGroupId => UiLocalization.T("Layers"),
                AnimationTimeline.TerrainTabGroupId => UiLocalization.T("Terrain"),
                _ => group.Name
            };
            items.Add(new TabGroupItem(
                group.Id,
                string.IsNullOrWhiteSpace(name) ? group.Id : name,
                string.Equals(group.Id, AnimationTimeline.DefaultTabGroupId, StringComparison.Ordinal)
                    || string.Equals(group.Id, AnimationTimeline.TerrainTabGroupId, StringComparison.Ordinal)));
        }

        return items;
    }

    private Rectangle TabGroupBarBounds() =>
        new(0, ToolbarHeaderHeight, Math.Max(0, Width), TabGroupRowHeight);

    private TabGroupLayoutSnapshot BuildTabGroupLayout()
    {
        var addBounds = new Rectangle(
            Math.Max(0, Width - ScaleTimelineMetric(30) - ScaleTimelineMetric(8)),
            ToolbarHeaderHeight + Math.Max(1, (TabGroupRowHeight - ScaleTimelineMetric(24)) / 2),
            ScaleTimelineMetric(26),
            ScaleTimelineMetric(24));
        var right = Math.Max(0, addBounds.Left - ScaleTimelineMetric(6));
        var left = ScaleTimelineMetric(8);
        var items = TabGroupItems();
        var widths = items
            .Select(item => Math.Clamp(
                TextRenderer.MeasureText(
                    item.Name,
                    Font,
                    Size.Empty,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + ScaleTimelineMetric(22),
                ScaleTimelineMetric(54),
                ScaleTimelineMetric(178)))
            .ToArray();

        var totalWidth = widths.Sum() + Math.Max(0, widths.Length - 1) * ScaleTimelineMetric(4);
        var overflowRequired = totalWidth > Math.Max(0, right - left);
        var overflowWidth = ScaleTimelineMetric(32);
        if (overflowRequired) right = Math.Max(left, right - overflowWidth - ScaleTimelineMetric(4));

        var visible = new List<TabGroupTabLayout>(items.Count);
        var overflow = new List<TabGroupItem>();
        var x = left;
        for (var index = 0; index < items.Count; index++)
        {
            var width = widths[index];
            if (x + width <= right || visible.Count == 0 && index == 0)
            {
                if (visible.Count == 0 && x + width > right)
                {
                    width = Math.Max(ScaleTimelineMetric(48), right - x);
                }

                if (width > 0)
                {
                    visible.Add(new TabGroupTabLayout(
                        items[index].Id,
                        items[index].Name,
                        new Rectangle(x, ToolbarHeaderHeight + ScaleTimelineMetric(3), width, Math.Max(1, TabGroupRowHeight - ScaleTimelineMetric(6))),
                        items[index].BuiltIn));
                    x += width + ScaleTimelineMetric(4);
                    continue;
                }
            }

            overflow.Add(items[index]);
        }

        if (overflow.Count == 0 && visible.Count < items.Count)
        {
            overflow.AddRange(items.Skip(visible.Count));
        }

        var overflowBounds = overflow.Count == 0
            ? Rectangle.Empty
            : new Rectangle(
                Math.Max(left, addBounds.Left - overflowWidth - ScaleTimelineMetric(6)),
                ToolbarHeaderHeight + Math.Max(1, (TabGroupRowHeight - ScaleTimelineMetric(24)) / 2),
                overflowWidth,
                ScaleTimelineMetric(24));

        var activeId = _timeline?.ActiveTabGroupId;
        if (!overflowBounds.IsEmpty
            && visible.Count > 0
            && activeId is not null
            && !visible.Any(tab => string.Equals(tab.Id, activeId, StringComparison.Ordinal)))
        {
            var last = visible[^1];
            var activeItem = items.FirstOrDefault(item => string.Equals(item.Id, activeId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(activeItem.Id))
            {
                visible[^1] = new TabGroupTabLayout(activeItem.Id, activeItem.Name, last.Bounds, activeItem.BuiltIn);
                overflow.RemoveAll(item => string.Equals(item.Id, activeItem.Id, StringComparison.Ordinal));
                overflow.Insert(0, new TabGroupItem(last.Id, last.Name, last.BuiltIn));
            }
        }

        return new TabGroupLayoutSnapshot(visible, overflow, overflowBounds, addBounds);
    }

    private void DrawTabGroupBar(Graphics graphics, TimelineLayout layout, Rectangle clipBounds)
    {
        var bar = TabGroupBarBounds();
        if (!clipBounds.IntersectsWith(bar)) return;

        var background = Theme.Mix(Theme.Top, Theme.PanelStrong, 0.36f);
        using var backgroundBrush = new SolidBrush(background);
        using var borderPen = new Pen(Theme.ReadableUiColor(background, Theme.Border), 1f);
        graphics.FillRectangle(backgroundBrush, bar);
        graphics.DrawLine(borderPen, bar.Left, bar.Bottom - 1, bar.Right, bar.Bottom - 1);

        if (_shotFilterActive)
        {
            SvgIcons.Draw(
                graphics,
                SvgIconKind.Camera,
                new Rectangle(ScaleTimelineMetric(9), ToolbarHeaderHeight + ScaleTimelineMetric(8), ScaleTimelineMetric(14), ScaleTimelineMetric(14)),
                Theme.ReadableText(background, Theme.Accent));
            TextRenderer.DrawText(
                graphics,
                UiLocalization.T("Camera tracks"),
                Font,
                new Rectangle(
                    ScaleTimelineMetric(30),
                    ToolbarHeaderHeight,
                    Math.Max(24, bar.Width - ScaleTimelineMetric(38)),
                    bar.Height),
                Theme.ReadableText(background, Theme.Text),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            return;
        }

        var activeId = _timeline?.ActiveTabGroupId;
        var snapshot = BuildTabGroupLayout();
        foreach (var tab in snapshot.VisibleTabs)
        {
            var customColor = GetTabGroupColor(tab.Id);
            var active = string.Equals(activeId, tab.Id, StringComparison.Ordinal);
            var hovered = string.Equals(_hoveredTabGroupId, tab.Id, StringComparison.Ordinal);
            var fill = active
                ? Theme.Mix(background, Theme.AccentSurface, Theme.IsLight ? 0.62f : 0.34f)
                : hovered
                    ? Theme.Mix(background, Theme.PanelHover, 0.76f)
                    : background;
            if (customColor is { } tint)
            {
                fill = Theme.Mix(fill, tint, active ? 0.28f : hovered ? 0.22f : 0.14f);
            }
            using var fillBrush = new SolidBrush(fill);
            graphics.FillRectangle(fillBrush, tab.Bounds);
            if (active || hovered)
            {
                using var tabBorder = new Pen(Theme.ReadableUiColor(fill, active ? Theme.Accent : Theme.BorderHover));
                graphics.DrawRectangle(tabBorder, tab.Bounds.X, tab.Bounds.Y, tab.Bounds.Width - 1, tab.Bounds.Height - 1);
            }

            var textColor = Theme.ReadableText(fill, active ? Theme.Text : Theme.Muted);
            var textBounds = Rectangle.Inflate(tab.Bounds, -ScaleTimelineMetric(9), 0);
            if (customColor is { } swatchColor)
            {
                var side = ScaleTimelineMetric(4);
                var swatch = new Rectangle(tab.Bounds.Left + ScaleTimelineMetric(2), tab.Bounds.Top + (tab.Bounds.Height - side) / 2, side, side);
                using var swatchBrush = new SolidBrush(swatchColor);
                using var swatchPen = new Pen(Theme.ReadableUiColor(swatchColor, Theme.Border));
                graphics.FillRectangle(swatchBrush, swatch);
                graphics.DrawRectangle(swatchPen, swatch);
            }
            TextRenderer.DrawText(
                graphics,
                tab.Name,
                Font,
                textBounds,
                textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        if (!snapshot.OverflowBounds.IsEmpty && string.Equals(_hoveredTabGroupId, TabGroupOverflowId, StringComparison.Ordinal))
        {
            using var hover = new SolidBrush(Color.FromArgb(35, Theme.Accent));
            graphics.FillRectangle(hover, snapshot.OverflowBounds);
        }
    }

    private void LayoutTabGroupControls(TimelineLayout layout)
    {
        var snapshot = BuildTabGroupLayout();
        var showAdd = !_shotFilterActive && _timeline is not null && !snapshot.AddBounds.IsEmpty;
        var showOverflow = !_shotFilterActive && _timeline is not null && !snapshot.OverflowBounds.IsEmpty;
        _tabGroupAddButton.Visible = showAdd;
        _tabGroupOverflowButton.Visible = showOverflow;
        if (showAdd && _tabGroupAddButton.Bounds != snapshot.AddBounds) _tabGroupAddButton.Bounds = snapshot.AddBounds;
        if (showOverflow && _tabGroupOverflowButton.Bounds != snapshot.OverflowBounds)
        {
            _tabGroupOverflowButton.Bounds = snapshot.OverflowBounds;
        }
    }

    private string? TabGroupAt(Point point, out bool overflow)
    {
        overflow = false;
        var snapshot = BuildTabGroupLayout();
        if (snapshot.OverflowBounds.Contains(point))
        {
            overflow = true;
            return TabGroupOverflowId;
        }

        foreach (var tab in snapshot.VisibleTabs)
        {
            if (tab.Bounds.Contains(point)) return tab.Id;
        }

        return null;
    }

    private bool TryHandleTabGroupClick(Point point)
    {
        if (!TabGroupBarBounds().Contains(point)) return false;
        if (_shotFilterActive) return true;
        var id = TabGroupAt(point, out var overflow);
        if (overflow)
        {
            ShowTabGroupOverflowMenu();
            return true;
        }

        if (id is not null)
        {
            _focusedTabGroupId = id;
            _timeline.SetActiveTabGroup(id);
            Focus();
        }

        return true;
    }

    private bool TryShowTabGroupContextMenu(Point point)
    {
        if (!TabGroupBarBounds().Contains(point)) return false;
        if (_shotFilterActive) return true;
        var id = TabGroupAt(point, out var overflow);
        if (overflow)
        {
            ShowTabGroupOverflowMenu();
            return true;
        }

        if (id is null) return true;
        _focusedTabGroupId = id;
        ShowTabGroupContextMenu(id, point);
        return true;
    }

    private void ShowTabGroupContextMenu(string id, Point point)
    {
        if (_shotFilterActive) return;
        ClearTabGroupMenuItems(_tabGroupContextMenu.Items);
        var item = TabGroupItems().FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(item.Id)) return;

        var builtIn = item.BuiltIn;
        var rename = new ToolStripMenuItem(UiLocalization.T("Rename tab group..."))
        {
            Enabled = !builtIn,
            AccessibleName = "Rename timeline tab group"
        };
        rename.Click += (_, _) => RenameTimelineTabGroup(id);
        var delete = new ToolStripMenuItem(UiLocalization.T("Delete tab group"))
        {
            Enabled = !builtIn,
            AccessibleName = "Delete timeline tab group"
        };
        delete.Click += (_, _) => DeleteTimelineTabGroup(id);
        _tabGroupContextMenu.Items.Add(rename);
        _tabGroupContextMenu.Items.Add(delete);
        AddTabGroupColorCommands(_tabGroupContextMenu.Items, id);
        _tabGroupContextMenu.Show(this, point);
    }

    private void ShowTabGroupOverflowMenu()
    {
        if (_shotFilterActive) return;
        ClearTabGroupMenuItems(_tabGroupOverflowMenu.Items);
        foreach (var item in TabGroupItems())
        {
            var menuItem = new ToolStripMenuItem(item.Name)
            {
                Checked = string.Equals(_timeline.ActiveTabGroupId, item.Id, StringComparison.Ordinal),
                CheckOnClick = false,
                Tag = item.Id,
                AccessibleName = $"Show {item.Name} timeline tab group"
            };
            menuItem.Click += (_, _) => _timeline.SetActiveTabGroup((string)menuItem.Tag!);
            FitTabGroupMenuText(menuItem);
            SetTabGroupMenuSwatch(menuItem, item.Id);
            _tabGroupOverflowMenu.Items.Add(menuItem);
        }

        var customGroups = TabGroupItems().Where(item => item.Id != AnimationTimeline.AllTabGroupId).ToArray();
        if (customGroups.Length > 0)
        {
            _tabGroupOverflowMenu.Items.Add(new ToolStripSeparator());
            foreach (var item in customGroups)
            {
                var options = new ToolStripMenuItem(string.Format(UiLocalization.T("Edit '{0}'"), item.Name));
                FitTabGroupMenuText(options);
                SetTabGroupMenuSwatch(options, item.Id);
                AddTabGroupColorCommands(options.DropDownItems, item.Id);
                _tabGroupOverflowMenu.Items.Add(options);
                if (item.BuiltIn) continue;
                var rename = new ToolStripMenuItem(
                    string.Format(UiLocalization.T("Rename '{0}'..."), item.Name))
                {
                    AccessibleName = $"Rename {item.Name} timeline tab group"
                };
                rename.Click += (_, _) => RenameTimelineTabGroup(item.Id);
                FitTabGroupMenuText(rename);
                options.DropDownItems.Add(rename);

                var delete = new ToolStripMenuItem(
                    string.Format(UiLocalization.T("Delete '{0}'"), item.Name))
                {
                    AccessibleName = $"Delete {item.Name} timeline tab group"
                };
                delete.Click += (_, _) => DeleteTimelineTabGroup(item.Id);
                FitTabGroupMenuText(delete);
                options.DropDownItems.Add(delete);
            }
        }

        var location = _tabGroupOverflowButton.Visible
            ? new Point(_tabGroupOverflowButton.Left, _tabGroupOverflowButton.Bottom)
            : new Point(Math.Max(0, Width - ScaleTimelineMetric(48)), ToolbarHeaderHeight + TabGroupRowHeight);
        _tabGroupOverflowMenu.Show(this, location);
    }

    private void FitTabGroupMenuText(ToolStripMenuItem item)
    {
        var fullText = item.Text ?? string.Empty;
        item.ToolTipText = fullText;
        item.AccessibleName = fullText;
        var width = ScaleTimelineMetric(240);
        if (TextRenderer.MeasureText(fullText, _tabGroupOverflowMenu.Font).Width <= width) return;
        var starts = System.Globalization.StringInfo.ParseCombiningCharacters(fullText);
        for (var length = starts.Length - 1; length >= 0; length--)
        {
            var shortened = fullText[..starts[length]] + "...";
            if (TextRenderer.MeasureText(shortened, _tabGroupOverflowMenu.Font).Width > width) continue;
            item.Text = shortened;
            return;
        }
    }

    private Color? GetTabGroupColor(string id) =>
        _timeline.TabGroups.FirstOrDefault(group => group.Id == id)?.ColorArgb is { } argb
            ? Color.FromArgb(argb)
            : null;

    private void AddTabGroupColorCommands(ToolStripItemCollection items, string id)
    {
        if (id == AnimationTimeline.AllTabGroupId) return;
        var choose = new ToolStripMenuItem(UiLocalization.T("Tab group color..."));
        SetTabGroupMenuSwatch(choose, id);
        choose.Click += (_, _) => EditTabGroupColor(id);
        var reset = new ToolStripMenuItem(UiLocalization.T("Reset tab group color"))
        {
            Enabled = GetTabGroupColor(id) is not null
        };
        reset.Click += (_, _) => PerformTabGroupEdit(() => _timeline.SetTabGroupColor(id, null));
        items.Add(choose);
        items.Add(reset);
    }

    private void EditTabGroupColor(string id)
    {
        var timeline = _timeline;
        if (!timeline.TabGroups.Any(group => group.Id == id)) return;
        using var picker = new ProfessionalColorPickerDialog(
            GetTabGroupColor(id) ?? Theme.Accent, UiLocalization.T("Tab Group Color"));
        picker.ColorChanged += (_, _) =>
        {
            if (ReferenceEquals(timeline, _timeline)) timeline.SetTabGroupColor(id, picker.Color.ToArgb());
        };
        TabGroupEditStarting?.Invoke(this, EventArgs.Empty);
        try { picker.ShowDialog(DialogOwner); }
        finally { TabGroupEditCompleted?.Invoke(this, EventArgs.Empty); }
    }

    private void SetTabGroupMenuSwatch(ToolStripMenuItem item, string id)
    {
        if (GetTabGroupColor(id) is not { } color) return;
        var side = ScaleTimelineMetric(16);
        var bitmap = new Bitmap(side, side);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(color))
        using (var pen = new Pen(Theme.ReadableUiColor(color, Theme.Border)))
        {
            graphics.FillRectangle(brush, 1, 1, side - 2, side - 2);
            graphics.DrawRectangle(pen, 1, 1, side - 3, side - 3);
        }
        item.Image = bitmap;
        item.Disposed += (_, _) => bitmap.Dispose();
    }

    private static void ClearTabGroupMenuItems(ToolStripItemCollection items)
    {
        while (items.Count > 0)
        {
            var item = items[0];
            items.RemoveAt(0);
            item.Dispose();
        }
    }

    private void CreateTimelineTabGroup()
    {
        if (_timeline is null) return;
        if (!TabGroupNameDialog.TryAsk(DialogOwner, UiLocalization.T("New Timeline Tab Group"), UiLocalization.T("Group name"), "", out var name)) return;

        var createdId = string.Empty;
        PerformTabGroupEdit(() =>
        {
            createdId = _timeline.CreateTabGroup(name);
            if (createdId.Length > 0) _timeline.SetActiveTabGroup(createdId);
            return createdId.Length > 0;
        });
        if (createdId.Length > 0) _focusedTabGroupId = createdId;
    }

    private void RenameTimelineTabGroup(string id)
    {
        if (_timeline is null || IsBuiltInTabGroup(id)) return;
        var group = _timeline.TabGroups.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (group is null) return;
        if (!TabGroupNameDialog.TryAsk(DialogOwner, UiLocalization.T("Rename Timeline Tab Group"), UiLocalization.T("Group name"), group.Name, out var name)) return;
        PerformTabGroupEdit(() => _timeline.RenameTabGroup(id, name));
    }

    private void DeleteTimelineTabGroup(string id)
    {
        if (_timeline is null || IsBuiltInTabGroup(id)) return;
        var group = _timeline.TabGroups.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (group is null) return;
        if (ModernMessageDialog.Show(
                DialogOwner,
                string.Format(
                    UiLocalization.T("Delete the timeline tab group '{0}'? Tracks in it will move to Layers."),
                    group.Name),
                UiLocalization.T("Delete Timeline Tab Group"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        PerformTabGroupEdit(() => _timeline.RemoveTabGroup(id));
    }

    private static bool IsBuiltInTabGroup(string id) =>
        string.Equals(id, AnimationTimeline.AllTabGroupId, StringComparison.Ordinal)
        || string.Equals(id, AnimationTimeline.DefaultTabGroupId, StringComparison.Ordinal)
        || string.Equals(id, AnimationTimeline.TerrainTabGroupId, StringComparison.Ordinal);

    private void PerformTabGroupEdit(Func<bool> edit)
    {
        TabGroupEditStarting?.Invoke(this, EventArgs.Empty);
        try
        {
            edit();
        }
        finally
        {
            TabGroupEditCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HandleLayerTabGroupContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shotFilterActive)
        {
            e.Cancel = true;
            return;
        }

        _moveLayerToTabGroupMenuItem.Text = UiLocalization.T("Move to tab group");
        ClearTabGroupMenuItems(_moveLayerToTabGroupMenuItem.DropDownItems);
        var activeTrack = GetActiveTrackIndex();
        var selected = SelectedLayerTrackIndices();
        if (activeTrack >= 0 && selected.Count == 0 && IsTrackSelectableRow(activeTrack)) selected = [activeTrack];
        var candidates = selected
            .Where(index => index >= 0 && index < TrackCount && IsTrackSelectableRow(index))
            .Distinct()
            .ToArray();
        _moveLayerToTabGroupMenuItem.Enabled = candidates.Length > 0;
        if (candidates.Length == 0) return;

        foreach (var item in TabGroupItems().Where(item => !string.Equals(item.Id, AnimationTimeline.AllTabGroupId, StringComparison.Ordinal)))
        {
            var child = new ToolStripMenuItem(item.Name)
            {
                Tag = item.Id,
                Enabled = true,
                Checked = candidates.All(index => string.Equals(_timeline.Tracks[index].TabGroupId, item.Id, StringComparison.Ordinal)),
                AccessibleName = $"Move selected tracks to {item.Name}"
            };
            child.Click += (_, _) => MoveSelectedTracksToTabGroup((string)child.Tag!);
            FitTabGroupMenuText(child);
            SetTabGroupMenuSwatch(child, item.Id);
            _moveLayerToTabGroupMenuItem.DropDownItems.Add(child);
        }
    }

    private void MoveSelectedTracksToTabGroup(string groupId)
    {
        if (_shotFilterActive
            || _timeline is null
            || string.Equals(groupId, AnimationTimeline.AllTabGroupId, StringComparison.Ordinal)) return;
        var activeTrack = GetActiveTrackIndex();
        var selected = SelectedLayerTrackIndices();
        if (activeTrack >= 0 && selected.Count == 0 && IsTrackSelectableRow(activeTrack)) selected = [activeTrack];
        var candidates = selected
            .Where(index => index >= 0 && index < TrackCount && IsTrackSelectableRow(index))
            .Distinct()
            .ToArray();
        if (candidates.Length == 0) return;

        PerformTabGroupEdit(() =>
        {
            var changed = false;
            foreach (var index in candidates)
            {
                changed |= _timeline.SetTrackTabGroup(_timeline.Tracks[index].Id, groupId);
            }

            return changed;
        });
    }

    private bool IsTrackInActiveTabGroup(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= TrackCount) return false;
        if (_shotFilterActive && IsSceneShotTrack(trackIndex)) return true;
        var active = _timeline.ActiveTabGroupId;
        return string.Equals(active, AnimationTimeline.AllTabGroupId, StringComparison.Ordinal)
            || string.Equals(_timeline.Tracks[trackIndex].TabGroupId, active, StringComparison.Ordinal);
    }

    private bool IsTrackActiveInCurrentTabGroup(int trackIndex)
    {
        if (!IsTrackInActiveTabGroup(trackIndex)) return false;
        return VisibleTrackPosition(trackIndex) >= 0;
    }

    private bool TryHandleTabGroupKey(KeyEventArgs e)
    {
        if (_shotFilterActive) return false;
        if (!Focused
            || _focusedTabGroupId is null
            || e.Modifiers != Keys.None
            || e.KeyCode is not (Keys.Left or Keys.Right or Keys.Enter or Keys.Space)) return false;
        var items = TabGroupItems();
        if (items.Count == 0) return false;
        var current = _focusedTabGroupId ?? _timeline.ActiveTabGroupId;
        var index = items.ToList().FindIndex(item => string.Equals(item.Id, current, StringComparison.Ordinal));
        if (index < 0) index = 0;
        if (e.KeyCode is Keys.Left or Keys.Right)
        {
            var next = Math.Clamp(index + (e.KeyCode == Keys.Left ? -1 : 1), 0, items.Count - 1);
            _focusedTabGroupId = items[next].Id;
            _timeline.SetActiveTabGroup(_focusedTabGroupId);
        }
        else
        {
            _timeline.SetActiveTabGroup(items[index].Id);
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
        return true;
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _focusedTabGroupId = null;
        base.OnLostFocus(e);
    }

    private void UpdateTabGroupHover(Point location, TimelineLayout layout)
    {
        if (_shotFilterActive)
        {
            if (_hoveredTabGroupId is not null)
            {
                _hoveredTabGroupId = null;
                Invalidate(TabGroupBarBounds());
            }

            SetTabGroupToolTip("");
            return;
        }

        var previous = _hoveredTabGroupId;
        _hoveredTabGroupId = TabGroupBarBounds().Contains(location)
            ? TabGroupAt(location, out _)
            : null;
        if (!string.Equals(previous, _hoveredTabGroupId, StringComparison.Ordinal))
        {
            Invalidate(TabGroupBarBounds());
        }

        var tooltip = _hoveredTabGroupId switch
        {
            TabGroupOverflowId => "More timeline tab groups",
            null => "",
            _ => TabGroupItems().FirstOrDefault(item => string.Equals(item.Id, _hoveredTabGroupId, StringComparison.Ordinal)).Name
        };
        SetTabGroupToolTip(UiLocalization.T(tooltip));
    }

    private void SetTabGroupToolTip(string text)
    {
        if (string.Equals(_tabGroupToolTipText, text, StringComparison.Ordinal)) return;
        _tabGroupToolTipText = text;
        _layerControlToolTipText = text;
        _layerControlToolTip.SetToolTip(this, text);
    }

    private void HandleTabGroupsChanged(object? sender, EventArgs e)
    {
        InvalidateTimelineStructureCache();
        RemoveCollapsedFrameSelection();
        PruneLayerSelection();
        EnsureActiveTrackVisible();
        EnsureCurrentFrameVisible();
        LayoutHeaderControls(CreateLayout(), IsOnionSkinControlsAvailable());
        Invalidate();
    }

    private sealed class TabGroupNameDialog : ModernDialogForm
    {
        private readonly TextBox _input = new();
        private string _value = "";

        private TabGroupNameDialog(string title, string label, string initialValue)
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
            _input.AccessibleName = "Timeline tab group name";
            Theme.StyleTextBox(_input);
            _input.Text = initialValue;
            DialogContent.Controls.Add(_input);
            _input.BringToFront();
            _input.MaxLength = 80;
            var ok = AddDialogAction(UiLocalization.T("OK"), DialogResult.OK, DialogActionStyle.Primary, () =>
            {
                _value = _input.Text.Trim();
                return _value.Length > 0;
            });
            var cancel = AddDialogAction(UiLocalization.T("Cancel"), DialogResult.Cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            UiLocalization.Watch(this);
        }

        public static bool TryAsk(IWin32Window owner, string title, string label, string initialValue, out string value)
        {
            using var dialog = new TabGroupNameDialog(title, label, initialValue);
            var result = dialog.ShowDialog(owner);
            value = dialog._value;
            return result == DialogResult.OK;
        }
    }
}
