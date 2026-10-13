using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal static partial class Theme
{
    private static readonly ConditionalWeakTable<Control, NativeFrameAdapter> StyledNativeFrames = new();

    private static void StyleNativeFrame(Control control)
    {
        StyledNativeFrames.GetValue(control, static owner => new NativeFrameAdapter(owner));
    }

    // Keep the native editor/list and its keyboard, selection and scroll behavior.
    // Only repaint the non-client frame instead of wrapping or replacing the control.
    private sealed class NativeFrameAdapter : NativeWindow
    {
        private readonly Control _owner;

        internal NativeFrameAdapter(Control owner)
        {
            _owner = owner;
            owner.HandleCreated += (_, _) => Attach();
            owner.HandleDestroyed += (_, _) => ReleaseHandle();
            owner.FontChanged += (_, _) => RefreshFrameLayout();
            owner.BackColorChanged += (_, _) => owner.Invalidate();
            owner.GotFocus += (_, _) => owner.Invalidate();
            owner.LostFocus += (_, _) => owner.Invalidate();
            owner.DpiChangedAfterParent += (_, _) => RefreshFrameLayout();
            if (owner.IsHandleCreated) Attach();
        }

        private void Attach()
        {
            AssignHandle(_owner.Handle);
            RefreshFrameLayout();
        }

        private void RefreshFrameLayout()
        {
            if (!_owner.IsHandleCreated) return;
            RefreshNativeFrame(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
            RefreshMargins();
            _owner.Invalidate();
        }
        private void RefreshMargins()
        {
            if (!_owner.IsHandleCreated || _owner is not TextBox) return;
            var inset = _owner is TextBox { Multiline: false } ? 0 : Math.Max(1, (int)Math.Round(7f * _owner.DeviceDpi / 96f));
            SetFrameMargins(Handle, 0x00D3, new IntPtr(3), new IntPtr(inset | (inset << 16)));
        }

        protected override void WndProc(ref Message message)
        {
            if (!_owner.IsDisposed && message.Msg == 0x0083
                && message.LParam != IntPtr.Zero && _owner is TextBox { Multiline: false })
            {
                // Own the complete client calculation. Adding padding AFTER the
                // native calculation creates a second native edge around the text.
                var bounds = Marshal.PtrToStructure<NativeFrameRect>(message.LParam);
                var width = bounds.Right - bounds.Left;
                var height = bounds.Bottom - bounds.Top;
                var horizontal = Math.Min(Math.Max(1, width / 4),
                    Math.Max(1, (int)Math.Round(7f * _owner.DeviceDpi / 96f)));
                var vertical = Math.Max(1, (height - _owner.Font.Height) / 2);
                if (width > horizontal * 2 && height > vertical * 2)
                {
                    bounds.Left += horizontal;
                    bounds.Right -= horizontal;
                    bounds.Top += vertical;
                    bounds.Bottom -= vertical;
                    Marshal.StructureToPtr(bounds, message.LParam, false);
                    message.Result = IntPtr.Zero;
                    return;
                }
            }
            if (!_owner.IsDisposed && message.Msg == 0x0085 && _owner is TextBox)
            {
                // Suppress the stock edit frame: PaintFrame is its sole owner.
                PaintFrame();
                message.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref message);
            if (_owner.IsDisposed || Handle == IntPtr.Zero) return;
            if (message.Msg is 0x0085 or 0x000F or 0x0086)
                PaintFrame();
        }
        private void PaintFrame()
        {
            var dc = GetFrameDC(Handle);
            if (dc == IntPtr.Zero) return;
            try
            {
                using var graphics = Graphics.FromHdc(dc);
                PaintFrame(graphics);
            }
            finally { ReleaseFrameDC(Handle, dc); }
        }

        private void PaintFrame(Graphics graphics)
        {
            if (!GetFrameRect(Handle, out var window)) return;
            var width = window.Right - window.Left;
            var height = window.Bottom - window.Top;
            if (width <= 0 || height <= 0) return;
            var clientOrigin = _owner.PointToScreen(Point.Empty);
            var client = new Rectangle(clientOrigin.X - window.Left, clientOrigin.Y - window.Top,
                _owner.ClientSize.Width, _owner.ClientSize.Height);
            var saved = graphics.Save();
            try
            {
                // Do not paint over native text, selection, scrollbars or caret.
                graphics.ExcludeClip(client);
                using var outside = new SolidBrush(_owner.Parent?.BackColor ?? Panel);
                graphics.FillRectangle(outside, 0, 0, width, height);
                using var fill = new SolidBrush(_owner.BackColor);
                using var pen = new Pen(ControlOutline, Math.Max(1f, _owner.DeviceDpi / 96f));
                if (_owner is TextBox)
                    PaintUiSurface(graphics, fill, pen, new Rectangle(0, 0, width, height));
                else
                {
                    graphics.FillRectangle(fill, 0, 0, width, height);
                    graphics.DrawRectangle(pen, 0, 0, width - 1, height - 1);
                }
            }
            finally { graphics.Restore(saved); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFrameRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RefreshNativeFrame(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowDC")]
    private static extern IntPtr GetFrameDC(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int ReleaseFrameDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFrameRect(IntPtr window, out NativeFrameRect bounds);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SetFrameMargins(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
