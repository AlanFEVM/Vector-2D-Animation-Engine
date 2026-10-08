namespace VectorAnimationEngine;

/// <summary>A leaf control and its layout; no editor, model, or sibling dependencies.</summary>
internal readonly record struct UiSection(Control Control, int? Height = null);

/// <summary>Composes existing controls in visual order without recreating or binding them.</summary>
internal static class UiComposition
{
    /// <summary>Mounts top-docked sections, optionally followed by a filling control.</summary>
    public static bool MountVertical(Control parent, IReadOnlyList<UiSection> sections, Control? fill = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(sections);
        var controls = sections.Select(section => section.Control).ToArray();
        Validate(parent, fill is null ? controls : [.. controls, fill]);
        if (sections.Any(section => section.Height is < 0))
            throw new ArgumentOutOfRangeException(nameof(sections), "Section height cannot be negative.");

        var moved = false;
        parent.SuspendLayout();
        try
        {
            foreach (var section in sections)
            {
                section.Control.Dock = DockStyle.Top;
                if (section.Height is { } height) section.Control.Height = height;
                moved |= Mount(parent, section.Control);
            }
            if (fill is not null)
            {
                fill.Dock = DockStyle.Fill;
                moved |= Mount(parent, fill);
            }
            OrderVertical(parent, controls, fill);
        }
        finally { parent.ResumeLayout(performLayout: false); }
        return moved;
    }

    /// <summary>Orders only children currently owned by this host, from top to bottom.</summary>
    public static void OrderVertical(Control parent, IReadOnlyList<Control> topToBottom, Control? fill = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(topToBottom);
        // WinForms docks from the back of the child collection. Fill must be at the front.
        var frontToBack = fill is null
            ? topToBottom.Reverse().ToArray()
            : new[] { fill }.Concat(topToBottom.Reverse()).ToArray();
        Validate(parent, frontToBack);
        parent.SuspendLayout();
        try
        {
            var tabIndex = 0;
            foreach (var control in topToBottom)
                if (ReferenceEquals(control.Parent, parent)) control.TabIndex = tabIndex++;
            if (fill is not null && ReferenceEquals(fill.Parent, parent)) fill.TabIndex = tabIndex;
            var index = 0;
            foreach (var control in frontToBack)
            {
                if (!ReferenceEquals(control.Parent, parent)) continue;
                if (parent.Controls.GetChildIndex(control) != index)
                    parent.Controls.SetChildIndex(control, index);
                index++;
            }
        }
        finally { parent.ResumeLayout(performLayout: false); }
    }

    private static bool Mount(Control parent, Control control)
    {
        if (ReferenceEquals(control.Parent, parent)) return false;
        parent.Controls.Add(control);
        return true;
    }

    private static void Validate(Control parent, IReadOnlyList<Control> controls)
    {
        ObjectDisposedException.ThrowIf(parent.IsDisposed, parent);
        var unique = new HashSet<Control>();
        foreach (var control in controls)
        {
            ArgumentNullException.ThrowIfNull(control);
            ObjectDisposedException.ThrowIf(control.IsDisposed, control);
            if (!unique.Add(control))
                throw new ArgumentException("A control may appear only once in a composition.", nameof(controls));
            for (Control? ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
                if (ReferenceEquals(ancestor, control))
                    throw new ArgumentException("A composition cannot contain its host or an ancestor.", nameof(controls));
        }
    }
}
