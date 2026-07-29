using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed class MarqueeOverlayWindow : IDisposable
{
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const uint UlwAlpha = 0x00000002;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private readonly Control _stage;
    private readonly OverlayNativeWindow _window = new();
    private readonly SolidBrush _clearBrush = new(Color.Transparent);
    private readonly SolidBrush _fillBrush = new(Color.FromArgb(34, 112, 204, 255));
    private readonly Pen _borderPen = new(Color.FromArgb(230, 112, 204, 255), 1f)
    {
        DashStyle = DashStyle.Dash
    };
    private IntPtr _screenDc;
    private IntPtr _memoryDc;
    private IntPtr _dib;
    private IntPtr _previousBitmap;
    private Bitmap? _surface;
    private Graphics? _graphics;
    private Size _surfaceSize;
    private IntPtr _ownerHandle;
    private bool _windowShown;
    private bool _disposed;

    public MarqueeOverlayWindow(Control stage)
    {
        _stage = stage;
    }

    public Rectangle ScreenBounds { get; private set; }
    public long UpdateCount { get; private set; }

    public void Prepare()
    {
        if (_disposed
            || !_stage.IsHandleCreated
            || _stage.ClientSize.Width <= 0
            || _stage.ClientSize.Height <= 0
            || _stage.FindForm() is not { IsHandleCreated: true } owner)
        {
            return;
        }

        if (_ownerHandle != owner.Handle)
        {
            _window.EnsureHandle(owner.Handle);
            _ownerHandle = owner.Handle;
            _windowShown = false;
        }
        EnsureSurface(_stage.ClientSize);
        PrimeWindow();
    }

    public bool TryShow(Point start, Point end)
    {
        if (_disposed
            || !_stage.IsHandleCreated
            || !_stage.Visible
            || _stage.FindForm() is not { Visible: true, IsHandleCreated: true })
        {
            return false;
        }

        Prepare();
        if (_window.Handle == IntPtr.Zero || _graphics is null || _surface is null) return false;

        var clientBounds = Rectangle.Intersect(
            _stage.ClientRectangle,
            Rectangle.FromLTRB(
                Math.Min(start.X, end.X),
                Math.Min(start.Y, end.Y),
                Math.Max(start.X, end.X),
                Math.Max(start.Y, end.Y)));
        if (clientBounds.Width < 2 || clientBounds.Height < 2)
        {
            ScreenBounds = Rectangle.Empty;
            UpdateCount++;
            return true;
        }

        _graphics.CompositingMode = CompositingMode.SourceCopy;
        _graphics.FillRectangle(_fillBrush, clientBounds);
        _graphics.DrawRectangle(
            _borderPen,
            clientBounds.X,
            clientBounds.Y,
            clientBounds.Width - 1,
            clientBounds.Height - 1);
        _graphics.Flush();

        ScreenBounds = new Rectangle(_stage.PointToScreen(clientBounds.Location), clientBounds.Size);
        var destination = new NativePoint(ScreenBounds.X, ScreenBounds.Y);
        var size = new NativeSize(ScreenBounds.Width, ScreenBounds.Height);
        var source = new NativePoint(clientBounds.X, clientBounds.Y);
        var blend = new BlendFunction
        {
            BlendOp = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1
        };
        if (!UpdateLayeredWindow(
                _window.Handle,
                _screenDc,
                ref destination,
                ref size,
                _memoryDc,
                ref source,
                0,
                ref blend,
                UlwAlpha))
        {
            Hide();
            return false;
        }

        if (!_windowShown)
        {
            ShowWindow(_window.Handle, SwShowNoActivate);
            _windowShown = true;
        }
        UpdateCount++;
        return true;
    }

    public void Hide()
    {
        ScreenBounds = Rectangle.Empty;
        if (_window.Handle != IntPtr.Zero && _windowShown) ShowWindow(_window.Handle, SwHide);
        _windowShown = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        ReleaseSurface();
        _window.Dispose();
        _borderPen.Dispose();
        _fillBrush.Dispose();
        _clearBrush.Dispose();
    }

    private void EnsureSurface(Size size)
    {
        if (_surfaceSize == size && _graphics is not null) return;
        ReleaseSurface();

        _screenDc = GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero) return;
        _memoryDc = CreateCompatibleDC(_screenDc);
        if (_memoryDc == IntPtr.Zero)
        {
            ReleaseSurface();
            return;
        }

        var bitmapInfo = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = size.Width,
                Height = -size.Height,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb,
                SizeImage = (uint)(size.Width * size.Height * 4)
            }
        };
        _dib = CreateDIBSection(_screenDc, ref bitmapInfo, DibRgbColors, out var bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            ReleaseSurface();
            return;
        }

        _previousBitmap = SelectObject(_memoryDc, _dib);
        _surface = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits);
        _graphics = Graphics.FromImage(_surface);
        _graphics.SmoothingMode = SmoothingMode.None;
        _graphics.CompositingMode = CompositingMode.SourceCopy;
        _graphics.Clear(Color.Transparent);
        _surfaceSize = size;
    }

    private void PrimeWindow()
    {
        if (_windowShown || _window.Handle == IntPtr.Zero || _memoryDc == IntPtr.Zero || _graphics is null) return;
        _graphics.CompositingMode = CompositingMode.SourceCopy;
        _graphics.FillRectangle(_clearBrush, 0, 0, 1, 1);
        _graphics.Flush();
        var screenPoint = _stage.PointToScreen(Point.Empty);
        var destination = new NativePoint(screenPoint.X, screenPoint.Y);
        var size = new NativeSize(1, 1);
        var source = new NativePoint(0, 0);
        var blend = new BlendFunction
        {
            BlendOp = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1
        };
        if (!UpdateLayeredWindow(
                _window.Handle,
                _screenDc,
                ref destination,
                ref size,
                _memoryDc,
                ref source,
                0,
                ref blend,
                UlwAlpha))
        {
            return;
        }

        ShowWindow(_window.Handle, SwShowNoActivate);
        _windowShown = true;
    }

    private void ReleaseSurface()
    {
        _graphics?.Dispose();
        _graphics = null;
        _surface?.Dispose();
        _surface = null;
        if (_memoryDc != IntPtr.Zero && _previousBitmap != IntPtr.Zero)
        {
            SelectObject(_memoryDc, _previousBitmap);
        }
        _previousBitmap = IntPtr.Zero;
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        _dib = IntPtr.Zero;
        if (_memoryDc != IntPtr.Zero) DeleteDC(_memoryDc);
        _memoryDc = IntPtr.Zero;
        if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
        _screenDc = IntPtr.Zero;
        _surfaceSize = Size.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr deviceContext,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr drawingObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr drawingObject);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr window,
        IntPtr destinationDeviceContext,
        ref NativePoint destination,
        ref NativeSize size,
        IntPtr sourceDeviceContext,
        ref NativePoint source,
        int colorKey,
        ref BlendFunction blend,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    private sealed class OverlayNativeWindow : NativeWindow, IDisposable
    {
        private const int WsPopup = unchecked((int)0x80000000);
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExLayered = 0x00080000;
        private const int WsExNoActivate = 0x08000000;
        private const int WmNcHitTest = 0x0084;
        private static readonly IntPtr HtTransparent = new(-1);
        private IntPtr _owner;

        public void EnsureHandle(IntPtr owner)
        {
            if (Handle != IntPtr.Zero && _owner == owner) return;
            DisposeHandle();
            _owner = owner;
            CreateHandle(new CreateParams
            {
                Caption = string.Empty,
                X = -32_000,
                Y = -32_000,
                Width = 1,
                Height = 1,
                Parent = owner,
                Style = WsPopup,
                ExStyle = WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate
            });
        }

        public void Dispose()
        {
            DisposeHandle();
            GC.SuppressFinalize(this);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmNcHitTest)
            {
                message.Result = HtTransparent;
                return;
            }
            base.WndProc(ref message);
        }

        private void DisposeHandle()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
            _owner = IntPtr.Zero;
        }
    }
}
