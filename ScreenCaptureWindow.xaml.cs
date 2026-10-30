using System.Runtime.InteropServices;
using System.Windows.Threading;
using D = System.Drawing;
using F = System.Windows.Forms;
using I = System.Windows.Input;
using W = System.Windows;
using WMI = System.Windows.Media.Imaging;

namespace deepseek_copilot;

public partial class ScreenCaptureWindow : W.Window
{
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    public static void ForceForeground(IntPtr hWnd)
    {
        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var currentThread = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != currentThread)
        {
            AttachThreadInput(currentThread, fgThread, true);
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
            AttachThreadInput(currentThread, fgThread, false);
        }
        else
        {
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
    }

    private WMI.BitmapSource? _screenSource;

    // Physical (device pixel) rectangle of the whole virtual desktop and of this window.
    private readonly D.Rectangle _virtualRect;
    private W.Rect _windowPhysRect;

    // Physical pixels per WPF device-independent pixel for this window. A window is rendered
    // as one surface at a single DPI, so this factor maps the WPF canvas 1:1 to the desktop,
    // no matter how many monitors with different scaling factors it spans.
    private double _pxPerDip = 1;
    private bool _geometryReady;

    private readonly List<W.Controls.Image> _screenImages = new();
    private W.Point _start;
    private W.Rect _selection;
    private bool _drawing;

    public event Action<WMI.BitmapSource>? Confirmed;
    public event Action? Cancelled;

    public ScreenCaptureWindow()
    {
        InitializeComponent();

        _virtualRect = VirtualScreenRect();

        // Grab the desktop before this window is shown, otherwise the overlay captures itself.
        CaptureDesktop();

        // Rough first guess so the overlay is already close to the right place when it appears.
        // OnLoaded replaces it with the exact physical rectangle.
        Left = W.SystemParameters.VirtualScreenLeft;
        Top = W.SystemParameters.VirtualScreenTop;
        Width = W.SystemParameters.VirtualScreenWidth;
        Height = W.SystemParameters.VirtualScreenHeight;

        Loaded += OnLoaded;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private static D.Rectangle VirtualScreenRect()
    {
        var virtualScreen = F.SystemInformation.VirtualScreen;
        if (virtualScreen.Width > 0 && virtualScreen.Height > 0)
            return virtualScreen;

        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;
        foreach (var screen in F.Screen.AllScreens)
        {
            left = Math.Min(left, screen.Bounds.Left);
            top = Math.Min(top, screen.Bounds.Top);
            right = Math.Max(right, screen.Bounds.Right);
            bottom = Math.Max(bottom, screen.Bounds.Bottom);
        }
        return right > left && bottom > top
            ? D.Rectangle.FromLTRB(left, top, right, bottom)
            : D.Rectangle.Empty;
    }

    private void CaptureDesktop()
    {
        if (_virtualRect.Width <= 0 || _virtualRect.Height <= 0) return;

        using var bitmap = new D.Bitmap(_virtualRect.Width, _virtualRect.Height, D.Imaging.PixelFormat.Format32bppArgb);
        using (var g = D.Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(_virtualRect.Left, _virtualRect.Top, 0, 0,
                new D.Size(_virtualRect.Width, _virtualRect.Height), D.CopyPixelOperation.SourceCopy);
        }

        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = W.Interop.Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero,
                W.Int32Rect.Empty, WMI.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            _screenSource = source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    private void OnLoaded(object sender, W.RoutedEventArgs e)
    {
        ApplyGeometry();
        BuildScreenImages();
        _geometryReady = true;

        Activate();
        I.Keyboard.Focus(this);
        ForceForeground(new W.Interop.WindowInteropHelper(this).Handle);
    }

    /// <summary>
    /// Pins the window to the exact physical bounds of the virtual desktop. WPF positions
    /// windows in DIPs using the window's own DPI, which is ambiguous while the window spans
    /// monitors with different scaling; device pixels are not.
    /// </summary>
    private void ApplyGeometry()
    {
        var hwnd = new W.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            SetWindowPos(hwnd, IntPtr.Zero, _virtualRect.Left, _virtualRect.Top,
                _virtualRect.Width, _virtualRect.Height, SWP_NOZORDER | SWP_NOACTIVATE);
            FlushLayout();

            if (GetWindowRect(hwnd, out var rect) && Matches(rect)) break;
        }

        if (!GetWindowRect(hwnd, out var phys))
            phys = new RECT { Left = _virtualRect.Left, Top = _virtualRect.Top, Right = _virtualRect.Right, Bottom = _virtualRect.Bottom };

        _windowPhysRect = new W.Rect(phys.Left, phys.Top, phys.Right - phys.Left, phys.Bottom - phys.Top);

        UpdateLayout();
        _pxPerDip = ActualWidth > 0 ? _windowPhysRect.Width / ActualWidth : DpiScaleOfWindow(hwnd);
        if (!(_pxPerDip > 0)) _pxPerDip = 1;
    }

    private bool Matches(RECT rect) =>
        rect.Left == _virtualRect.Left && rect.Top == _virtualRect.Top &&
        rect.Right - rect.Left == _virtualRect.Width && rect.Bottom - rect.Top == _virtualRect.Height;

    private static double DpiScaleOfWindow(IntPtr hwnd)
    {
        var dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : 0;
        return dpi > 0 ? dpi / 96d : 1;
    }

    private void FlushLayout()
    {
        UpdateLayout();
        // Pump the dispatcher so a pending DPI change (and its layout) is applied before
        // the window rectangle is measured again.
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    protected override void OnDpiChanged(W.DpiScale oldDpi, W.DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // Moving onto another monitor may make WPF resize the window to its suggested
        // rectangle; restore the exact desktop rectangle and re-map the contents.
        if (!_geometryReady) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyGeometry();
            BuildScreenImages();
        }), DispatcherPriority.Loaded);
    }

    private void BuildScreenImages()
    {
        foreach (var image in _screenImages)
            RootCanvas.Children.Remove(image);
        _screenImages.Clear();

        if (_screenSource == null) return;

        var sourceWidth = _screenSource.PixelWidth;
        var sourceHeight = _screenSource.PixelHeight;
        var insertAt = 0;

        foreach (var screen in F.Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            var x0 = Math.Max(0, Math.Min(sourceWidth, bounds.Left - _virtualRect.Left));
            var y0 = Math.Max(0, Math.Min(sourceHeight, bounds.Top - _virtualRect.Top));
            var x1 = Math.Max(0, Math.Min(sourceWidth, bounds.Right - _virtualRect.Left));
            var y1 = Math.Max(0, Math.Min(sourceHeight, bounds.Bottom - _virtualRect.Top));
            if (x1 <= x0 || y1 <= y0) continue;

            var source = new WMI.CroppedBitmap(_screenSource, new W.Int32Rect(x0, y0, x1 - x0, y1 - y0));
            source.Freeze();

            var image = new W.Controls.Image
            {
                Source = source,
                Stretch = System.Windows.Media.Stretch.Fill,
                Width = (x1 - x0) / _pxPerDip,
                Height = (y1 - y0) / _pxPerDip,
                IsHitTestVisible = false
            };
            W.Controls.Canvas.SetLeft(image, (_virtualRect.Left + x0 - _windowPhysRect.X) / _pxPerDip);
            W.Controls.Canvas.SetTop(image, (_virtualRect.Top + y0 - _windowPhysRect.Y) / _pxPerDip);

            RootCanvas.Children.Insert(insertAt++, image);
            _screenImages.Add(image);
        }
    }

    private void OnMouseLeftButtonDown(object sender, I.MouseButtonEventArgs e)
    {
        _start = e.GetPosition(RootCanvas);
        _selection = new W.Rect(_start, new W.Size(0, 0));
        _drawing = true;
        HintText.Visibility = W.Visibility.Collapsed;
        ActionPanel.Visibility = W.Visibility.Collapsed;
        SelectionBorder.Visibility = W.Visibility.Collapsed;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, I.MouseEventArgs e)
    {
        if (!_drawing) return;

        var current = e.GetPosition(RootCanvas);
        _selection = new W.Rect(_start, current);
        UpdateSelectionVisual();
    }

    private void OnMouseLeftButtonUp(object sender, I.MouseButtonEventArgs e)
    {
        if (!_drawing) return;

        _drawing = false;
        ReleaseMouseCapture();

        if (_selection.Width < 3 || _selection.Height < 3)
        {
            ResetSelection();
            return;
        }

        ShowActionPanel();
    }

    private void UpdateSelectionVisual()
    {
        var r = _selection;
        var canvasWidth = RootCanvas.ActualWidth;
        var canvasHeight = RootCanvas.ActualHeight;

        DarkLeft.Width = r.X;
        DarkLeft.Height = canvasHeight;
        W.Controls.Canvas.SetLeft(DarkLeft, 0);
        W.Controls.Canvas.SetTop(DarkLeft, 0);

        DarkTop.Width = r.Width;
        DarkTop.Height = r.Y;
        W.Controls.Canvas.SetLeft(DarkTop, r.X);
        W.Controls.Canvas.SetTop(DarkTop, 0);

        DarkRight.Width = canvasWidth - (r.X + r.Width);
        DarkRight.Height = canvasHeight;
        W.Controls.Canvas.SetLeft(DarkRight, r.X + r.Width);
        W.Controls.Canvas.SetTop(DarkRight, 0);

        DarkBottom.Width = r.Width;
        DarkBottom.Height = canvasHeight - (r.Y + r.Height);
        W.Controls.Canvas.SetLeft(DarkBottom, r.X);
        W.Controls.Canvas.SetTop(DarkBottom, r.Y + r.Height);

        SelectionBorder.Width = r.Width;
        SelectionBorder.Height = r.Height;
        W.Controls.Canvas.SetLeft(SelectionBorder, r.X);
        W.Controls.Canvas.SetTop(SelectionBorder, r.Y);
        SelectionBorder.Visibility = W.Visibility.Visible;
    }

    private void ResetSelection()
    {
        SelectionBorder.Visibility = W.Visibility.Collapsed;
        ActionPanel.Visibility = W.Visibility.Collapsed;
        DarkLeft.Width = 0;
        DarkTop.Width = 0;
        DarkRight.Width = 0;
        DarkBottom.Width = 0;
    }

    private void ShowActionPanel()
    {
        ActionPanel.Visibility = W.Visibility.Visible;
        ActionPanel.Measure(new W.Size(double.PositiveInfinity, double.PositiveInfinity));
        ActionPanel.UpdateLayout();

        var r = _selection;
        var panelWidth = ActionPanel.ActualWidth;
        var panelHeight = ActionPanel.ActualHeight;

        var x = r.X + r.Width - panelWidth;
        var y = r.Y + r.Height + 6;
        if (y + panelHeight > RootCanvas.ActualHeight)
            y = r.Y - panelHeight - 6;
        x = Math.Max(0, Math.Min(x, RootCanvas.ActualWidth - panelWidth));
        y = Math.Max(0, y);

        W.Controls.Canvas.SetLeft(ActionPanel, x);
        W.Controls.Canvas.SetTop(ActionPanel, y);
    }

    private void OnConfirmClick(object sender, W.RoutedEventArgs e)
    {
        var image = CropSelection();
        if (image == null)
        {
            Close();
            return;
        }

        W.Clipboard.SetImage(image);
        Confirmed?.Invoke(image);
        Close();
    }

    private WMI.BitmapSource? CropSelection()
    {
        if (_screenSource == null) return null;

        // Selection is in WPF canvas DIPs; map both corners through the window's own
        // physical mapping, then into capture bitmap coordinates.
        var left = Math.Min(_selection.Left, _selection.Right);
        var top = Math.Min(_selection.Top, _selection.Bottom);
        var right = Math.Max(_selection.Left, _selection.Right);
        var bottom = Math.Max(_selection.Top, _selection.Bottom);

        var x0 = Clamp((int)Math.Round(ToPhysicalX(left)) - _virtualRect.Left, 0, _screenSource.PixelWidth);
        var y0 = Clamp((int)Math.Round(ToPhysicalY(top)) - _virtualRect.Top, 0, _screenSource.PixelHeight);
        var x1 = Clamp((int)Math.Round(ToPhysicalX(right)) - _virtualRect.Left, 0, _screenSource.PixelWidth);
        var y1 = Clamp((int)Math.Round(ToPhysicalY(bottom)) - _virtualRect.Top, 0, _screenSource.PixelHeight);

        var w = Math.Max(1, x1 - x0);
        var h = Math.Max(1, y1 - y0);
        return new WMI.CroppedBitmap(_screenSource, new W.Int32Rect(x0, y0, w, h));
    }

    private double ToPhysicalX(double dipX) => _windowPhysRect.X + dipX * _pxPerDip;

    private double ToPhysicalY(double dipY) => _windowPhysRect.Y + dipY * _pxPerDip;

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

    private void OnCancelClick(object sender, W.RoutedEventArgs e)
    {
        Cancelled?.Invoke();
        Close();
    }

    private void OnPreviewKeyDown(object sender, I.KeyEventArgs e)
    {
        if (e.Key == I.Key.Escape)
        {
            Cancelled?.Invoke();
            Close();
        }
        else if (e.Key == I.Key.Enter && ActionPanel.Visibility == W.Visibility.Visible)
        {
            OnConfirmClick(sender, e);
        }
    }
}
