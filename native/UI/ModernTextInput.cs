using System.ComponentModel;

namespace VectorAnimationEngine;

/// <summary>A themed field surface around a borderless native text editor.</summary>
internal sealed class ModernTextInput : UserControl
{
    private readonly TextBox _editor = new() { BorderStyle = BorderStyle.None, AutoSize = true };
    private bool _hovered;

    public ModernTextInput()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoScaleMode = AutoScaleMode.None;
        TabStop = false;
        Height = Theme.ControlHeightCompact;
        Font = Theme.UiFont();
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
        Controls.Add(_editor);
        _editor.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        _editor.KeyDown += (_, e) => OnKeyDown(e);
        _editor.KeyUp += (_, e) => OnKeyUp(e);
        _editor.KeyPress += (_, e) => OnKeyPress(e);
        _editor.GotFocus += (_, _) => { Invalidate(); };
        _editor.LostFocus += (_, _) => { Invalidate(); };
        _editor.MouseEnter += (_, _) => SetHover(true);
        _editor.MouseLeave += (_, _) => SetHover(ClientRectangle.Contains(PointToClient(Cursor.Position)));
        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(ClientRectangle.Contains(PointToClient(Cursor.Position)));
        MouseDown += (_, _) => _editor.Focus();
        LayoutEditor();
    }

    [Browsable(true)]
    public override string Text
    {
        get => _editor?.Text ?? base.Text;
        set
        {
            if (_editor is null) { base.Text = value ?? string.Empty; return; }
            if (_editor.Text != value) _editor.Text = value ?? string.Empty;
        }
    }

    public int MaxLength { get => _editor.MaxLength; set => _editor.MaxLength = value; }
    public CharacterCasing CharacterCasing { get => _editor.CharacterCasing; set => _editor.CharacterCasing = value; }
    public bool ReadOnly { get => _editor.ReadOnly; set => _editor.ReadOnly = value; }
    public void SelectAll() => _editor.SelectAll();

    internal int PreferredFieldHeight => Math.Max(
        (int)Math.Ceiling(Theme.ControlHeightCompact * DeviceDpi / 96f),
        (_editor?.PreferredHeight ?? Font.Height) + (int)Math.Ceiling(10f * DeviceDpi / 96f));

    public override Size GetPreferredSize(Size proposedSize) =>
        new(proposedSize.Width > 0 ? proposedSize.Width : Width, PreferredFieldHeight);

    internal void RefreshTheme()
    {
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        LayoutEditor();
    }

    private void LayoutEditor()
    {
        if (_editor is null) return;
        var inset = Math.Max(2, (int)Math.Round(8f * DeviceDpi / 96f));
        _editor.Font = Font;
        var editorHeight = Math.Min(_editor.PreferredHeight, Math.Max(1, ClientSize.Height - 4));
        _editor.SetBounds(inset, Math.Max(2, (ClientSize.Height - editorHeight) / 2),
            Math.Max(1, ClientSize.Width - inset * 2), editorHeight);
        _editor.AccessibleName = AccessibleName;
        _editor.AccessibleDescription = AccessibleDescription;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        LayoutEditor();
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        if (_editor is not null) _editor.ForeColor = ForeColor;
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutEditor();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var highContrast = SystemInformation.HighContrast;
        var surface = highContrast ? SystemColors.Window
            : !Enabled ? Theme.DisabledSurface
            : Theme.Mix(Theme.PanelStrong, Theme.Field, ContainsFocus ? 0.35f : _hovered ? 0.45f : 0.60f);
        _editor.BackColor = surface;
        _editor.ForeColor = highContrast ? Enabled ? SystemColors.WindowText : SystemColors.GrayText
            : Enabled ? ForeColor : Theme.DisabledText;
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Panel);
        using var fill = new SolidBrush(surface);
        using var border = new Pen(Theme.ControlOutline, Math.Max(1f, DeviceDpi / 96f));
        // Leave a full device pixel outside the entire stroke, including the
        // square-corner path at fractional DPI. The child editor stays inside.
        var surfaceBounds = Rectangle.Inflate(ClientRectangle, -1, -1);
        if (surfaceBounds.Width > 0 && surfaceBounds.Height > 0)
            Theme.PaintUiSurface(e.Graphics, fill, border, surfaceBounds);
        if (ContainsFocus && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3),
                highContrast ? SystemColors.WindowText : Theme.Muted, surface);
    }

    private void SetHover(bool value)
    {
        if (_hovered == value) return;
        _hovered = value;
        Invalidate();
    }
}
