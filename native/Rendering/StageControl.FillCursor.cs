using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    private Cursor? _fillSystemCursor;
    private FillCursorHandle? _fillSystemCursorHandle;
    private int _fillSystemCursorColor;
    private int _fillSystemCursorOutline;
    private int _fillSystemCursorDpi;

    private void ApplyFillSystemCursor(Color color)
    {
        var outline = Theme.Text.ToArgb();
        if (_fillSystemCursor is null || _fillSystemCursorHandle is null || _fillSystemCursorColor != color.ToArgb()
            || _fillSystemCursorOutline != outline || _fillSystemCursorDpi != DeviceDpi)
        {
            var next = CreateFillSystemCursor(color, DeviceDpi, out var nextHandle);
            var previous = _fillSystemCursor;
            var previousHandle = _fillSystemCursorHandle;
            _fillSystemCursor = next;
            _fillSystemCursorHandle = nextHandle;
            _fillSystemCursorColor = color.ToArgb();
            _fillSystemCursorOutline = outline;
            _fillSystemCursorDpi = DeviceDpi;
            Cursor = next;
            previous?.Dispose();
            previousHandle?.Dispose();
        }
        else if (!ReferenceEquals(Cursor, _fillSystemCursor))
        {
            Cursor = _fillSystemCursor;
        }
    }

    private static Cursor CreateFillSystemCursor(Color color, int dpi, out FillCursorHandle cursorHandle)
    {
        var size = Math.Clamp((int)MathF.Round(32f * dpi / 96f), 32, 128);
        var scale = size / 32f;
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform(scale, scale);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            PointF[] bucket = [new(6, 4), new(19, 4), new(26, 11), new(13, 11)];
            using var fill = new SolidBrush(Color.FromArgb(150, color));
            using var outline = new Pen(Color.FromArgb(235, Theme.Text), 1.4f);
            using var drop = new SolidBrush(Color.FromArgb(220, color));
            using var dropOutline = new Pen(Color.FromArgb(235, Theme.Text), 1f);
            graphics.FillPolygon(fill, bucket);
            graphics.DrawPolygon(outline, bucket);
            graphics.FillEllipse(drop, 19, 15, 6, 8);
            graphics.DrawEllipse(dropOutline, 19, 15, 6, 8);
        }

        var icon = bitmap.GetHicon();
        FillCursorIconInfo info = default;
        try
        {
            if (!GetFillCursorIconInfo(icon, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            info.IsIcon = false;
            info.HotspotX = (uint)(size / 2);
            info.HotspotY = (uint)(size / 2);
            var handle = CreateFillCursorIndirect(ref info);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            cursorHandle = new FillCursorHandle(handle);
            try
            {
                return new Cursor(handle);
            }
            catch
            {
                cursorHandle.Dispose();
                throw;
            }
        }
        finally
        {
            if (info.Mask != IntPtr.Zero) DeleteFillCursorBitmap(info.Mask);
            if (info.Color != IntPtr.Zero) DeleteFillCursorBitmap(info.Color);
            DestroyFillCursorIcon(icon);
        }
    }

    private void DisposeFillSystemCursor()
    {
        if (ReferenceEquals(Cursor, _fillSystemCursor)) Cursor = Cursors.Cross;
        _fillSystemCursor?.Dispose();
        _fillSystemCursor = null;
        _fillSystemCursorHandle?.Dispose();
        _fillSystemCursorHandle = null;
    }

    private sealed class FillCursorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal FillCursorHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);

        protected override bool ReleaseHandle() => DestroyFillCursor(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FillCursorIconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("user32.dll", EntryPoint = "GetIconInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFillCursorIconInfo(IntPtr icon, out FillCursorIconInfo info);

    [DllImport("user32.dll", EntryPoint = "CreateIconIndirect", SetLastError = true)]
    private static extern IntPtr CreateFillCursorIndirect(ref FillCursorIconInfo info);

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyFillCursorIcon(IntPtr icon);

    [DllImport("user32.dll", EntryPoint = "DestroyCursor")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyFillCursor(IntPtr cursor);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFillCursorBitmap(IntPtr bitmap);
}
