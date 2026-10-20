using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal static partial class Theme
{
    internal static void StyleComboBoxSelection(ComboBox box, float fontSize, int height)
    {
        if (StyledComboBoxes.TryGetValue(box, out var state) && state is ComboBoxSurfaceAdapter adapter)
            adapter.SetSelectionStyle(fontSize, height);
    }

    private sealed class ComboBoxSurfaceAdapter : NativeWindow
    {
        private readonly ComboBox _box;
        private bool _hovered;
        private float? _selectionFontSize;
        private int? _selectionHeight;

        internal void SetSelectionStyle(float fontSize, int height)
        {
            _selectionFontSize = fontSize;
            _selectionHeight = height;
            RefreshSelectionHeight();
        }

        internal ComboBoxSurfaceAdapter(ComboBox box)
        {
            _box = box;
            box.HandleCreated += (_, _) => { AssignHandle(box.Handle); RefreshSelectionHeight(); };
            box.HandleDestroyed += (_, _) => ReleaseHandle();
            box.MouseEnter += (_, _) => { _hovered = true; box.Invalidate(); };
            box.MouseLeave += (_, _) => { _hovered = false; box.Invalidate(); };
            box.GotFocus += (_, _) => box.Invalidate();
            box.LostFocus += (_, _) => box.Invalidate();
            box.DropDown += (_, _) =>
            {
                box.DropDownHeight = box.ItemHeight * Math.Min(8, Math.Max(1, box.Items.Count)) + 2;
                box.Invalidate();
            };
            box.DropDownClosed += (_, _) => box.Invalidate();
            box.EnabledChanged += (_, _) => box.Invalidate();
            box.SelectedIndexChanged += (_, _) => box.Invalidate();
            box.TextChanged += (_, _) => box.Invalidate();
            box.FontChanged += (_, _) => RefreshSelectionHeight();
            box.DpiChangedAfterParent += (_, _) => RefreshSelectionHeight();
            if (box.IsHandleCreated) { AssignHandle(box.Handle); RefreshSelectionHeight(); }
        }

        internal void RefreshSelectionHeight()
        {
            if (!_box.IsHandleCreated || _box.DropDownStyle != ComboBoxStyle.DropDownList) return;
            // CB_SETITEMHEIGHT(-1) sizes the closed selector independently of
            // popup rows. A tall popup row must not clip a compact inspector row.
            var height = Math.Max(_box.Font.Height + 2, (int)Math.Round(16f * _box.DeviceDpi / 96f));
            if (_selectionHeight is int selectionHeight)
                height = Math.Max(height, (int)Math.Round(selectionHeight * _box.DeviceDpi / 96f) - 6);
            SendComboMessage(_box.Handle, 0x0153, new IntPtr(-1), new IntPtr(height));
            _box.Invalidate();
        }

        internal static bool KeyboardFocusVisible(ComboBox box)
        {
            // Follow WinForms' keyboard-cue state. A hovered native popup row
            // can report Focus too, even when focus cues are hidden for mouse use.
            const int wmQueryUiState = 0x0129;
            const int hideFocus = 0x0001;
            return box.IsHandleCreated
                && (SendComboMessage(box.Handle, wmQueryUiState, IntPtr.Zero, IntPtr.Zero).ToInt64() & hideFocus) == 0;
        }

        protected override void WndProc(ref Message message)
        {
            if (_box.DropDownStyle != ComboBoxStyle.DropDownList || _box.IsDisposed)
            {
                base.WndProc(ref message);
                return;
            }
            if (message.Msg == 0x000F)
            {
                // Own the paint transaction instead of drawing over the native
                // ComboBox after it has validated/painted a different region.
                var dc = BeginComboPaint(Handle, out var paint);
                try
                {
                    if (dc != IntPtr.Zero)
                    {
                        using var graphics = Graphics.FromHdc(dc);
                        PaintBuffered(graphics);
                    }
                }
                finally { EndComboPaint(Handle, ref paint); }
                message.Result = IntPtr.Zero;
            }
            else if ((message.Msg == 0x0318 || message.Msg == 0x0317) && message.WParam != IntPtr.Zero)
            {
                using var graphics = Graphics.FromHdc(message.WParam);
                PaintBuffered(graphics);
                message.Result = IntPtr.Zero;
            }
            else if (message.Msg == 0x0014) message.Result = new IntPtr(1);
            else base.WndProc(ref message);
        }

        private void PaintBuffered(Graphics target)
        {
            if (_box.ClientSize.Width <= 0 || _box.ClientSize.Height <= 0) return;
            using var buffer = new Bitmap(_box.ClientSize.Width, _box.ClientSize.Height);
            buffer.SetResolution(_box.DeviceDpi, _box.DeviceDpi);
            using (var graphics = Graphics.FromImage(buffer)) Paint(graphics);
            target.DrawImageUnscaled(buffer, 0, 0);
        }

        private void Paint(Graphics graphics)
        {
            var highContrast = SystemInformation.HighContrast;
            var active = _box.Focused || _box.DroppedDown;
            var background = highContrast ? SystemColors.Window
                : !_box.Enabled ? DisabledSurface
                : _box.DroppedDown ? Mix(FieldFocus, AccentSurface, 0.28f)
                : _box.Focused ? FieldFocus : _hovered ? FieldHover : _box.BackColor;
            var foreground = highContrast ? _box.Enabled ? SystemColors.WindowText : SystemColors.GrayText
                : ReadableText(background, _box.Enabled ? Text : DisabledText);
            var outline = highContrast ? active ? SystemColors.Highlight : SystemColors.WindowText
                : ControlOutline;
            var scale = _box.DeviceDpi / 96f;
            graphics.Clear(_box.Parent?.BackColor ?? Panel);
            using var fill = new SolidBrush(background);
            using var border = new Pen(outline, Math.Max(1f, scale));
            // Reserve a pixel outside the stroke so its antialiasing cannot be
            // cut at the bottom/right edge, including partial native repaints.
            PaintUiSurface(graphics, fill, border, Rectangle.Inflate(_box.ClientRectangle, -1, -1));
            var arrowWidth = (int)Math.Round(24 * scale);
            var padding = (int)Math.Round(7 * scale);
            var rtl = _box.RightToLeft == RightToLeft.Yes;
            var bounds = new Rectangle(rtl ? arrowWidth : padding, 1,
                Math.Max(0, _box.ClientSize.Width - arrowWidth - padding), Math.Max(0, _box.ClientSize.Height - 2));
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            flags |= rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left;
            var label = _box.SelectedIndex >= 0 ? _box.GetItemText(_box.SelectedItem) : _box.Text;
            using var selectionFont = _selectionFontSize is float fontSize ? UiFont(fontSize) : null;
            TextRenderer.DrawText(graphics, UiLocalization.T(label), selectionFont ?? _box.Font, bounds, foreground, flags);
            var x = rtl ? arrowWidth / 2f : _box.ClientSize.Width - arrowWidth / 2f;
            var y = _box.ClientSize.Height / 2f;
            var arrowColor = highContrast ? foreground
                : ReadableText(background, !_box.Enabled ? DisabledText : active ? AccentLabel : Muted);
            using var arrow = new SolidBrush(arrowColor);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var direction = _box.DroppedDown ? -1f : 1f;
            graphics.FillPolygon(arrow, [new PointF(x - 3 * scale, y - direction * 1.5f * scale),
                new PointF(x + 3 * scale, y - direction * 1.5f * scale),
                new PointF(x, y + direction * 2 * scale)]);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ComboPaintState
        {
            public IntPtr DeviceContext;
            public int Erase;
            public int Left, Top, Right, Bottom;
            public int Restore, IncrementalUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] Reserved;
        }

        [DllImport("user32.dll", EntryPoint = "BeginPaint")]
        private static extern IntPtr BeginComboPaint(IntPtr window, out ComboPaintState state);

        [DllImport("user32.dll", EntryPoint = "EndPaint")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EndComboPaint(IntPtr window, ref ComboPaintState state);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendComboMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }
}
