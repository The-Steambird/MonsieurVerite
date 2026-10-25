using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace MonsieurVerite.Views;

public static partial class Chrome
{
    private const int SystemBackdropType = 38;
    private const int Style = -16;
    private const long SystemMenu = 0x00080000;
    private const uint FrameChanged = 0x0027; // SWP_FRAMECHANGED | NOZORDER | NOMOVE | NOSIZE
    private const int BackdropNone = 1;

    public static void Solid(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var backdrop = BackdropNone;
        _ = DwmSetWindowAttribute(hwnd, SystemBackdropType, ref backdrop, sizeof(int));
    }

    /// <summary>Snap layouts need the maximize button reported as HTMAXBUTTON.</summary>
    public static void HookCaption(Window window, Button maximize)
    {
        const int hitTest = 0x0084, buttonDown = 0x00A1, buttonUp = 0x00A2, mouseLeave = 0x02A2;
        const int caption = 2, maxButton = 9;
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            switch (msg)
            {
                case hitTest when maximize.IsVisible:
                    var over = Over(maximize, lParam);
                    maximize.Tag = over ? "hot" : null;
                    handled = over;
                    return over ? maxButton : IntPtr.Zero;
                case buttonDown when wParam == maxButton:
                    handled = true;
                    return IntPtr.Zero;
                case buttonDown when wParam == caption:
                    // The click never reaches WPF's mouse events, because the toolbar is caption.
                    App.Blur(window);
                    return IntPtr.Zero;
                case buttonUp when wParam == maxButton:
                    handled = true;
                    ToggleMaximized(window);
                    return IntPtr.Zero;
                case mouseLeave:
                    maximize.Tag = null;
                    return IntPtr.Zero;
                default:
                    return IntPtr.Zero;
            }
        });
    }

    public static void ToggleMaximized(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private static bool Over(FrameworkElement element, IntPtr packedScreenPoint)
    {
        var packed = packedScreenPoint.ToInt64();
        var screen = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
        return new Rect(element.RenderSize).Contains(element.PointFromScreen(screen));
    }

    public static void Frost(Window dialog)
    {
        Solid(dialog);
        var hwnd = new WindowInteropHelper(dialog).Handle;
        var style = GetWindowLongPtrW(hwnd, Style);
        _ = SetWindowLongPtrW(hwnd, Style, style & ~SystemMenu);
        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, FrameChanged);

        dialog.Background = (Brush)dialog.FindResource("WindowBrush");
        if (dialog.Owner?.Content is not FrameworkElement { ActualWidth: >= 1, ActualHeight: >= 1 } scene
            || dialog.Content is not Panel root)
        {
            return;
        }

        var tint = (Color)dialog.FindResource("FrostColor");
        var glass = new ImageBrush(Frosted(scene, tint))
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        root.Background = glass;

        var dpi = VisualTreeHelper.GetDpi(scene);
        var origin = dialog.PointToScreen(default);
        var sceneOrigin = scene.PointToScreen(default);
        glass.Viewbox = new Rect(
            (origin.X - sceneOrigin.X) / dpi.DpiScaleX,
            (origin.Y - sceneOrigin.Y) / dpi.DpiScaleY,
            dialog.ActualWidth, dialog.ActualHeight);
    }

    private static RenderTargetBitmap Frosted(FrameworkElement scene, Color tint)
    {
        const double scale = 0.5, blur = 20, tintOpacity = 0.4;
        var width = (int)Math.Ceiling(scene.ActualWidth * scale);
        var height = (int)Math.Ceiling(scene.ActualHeight * scale);
        var bounds = new Rect(0, 0, scene.ActualWidth, scene.ActualHeight);

        var snapshot = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale,
            PixelFormats.Pbgra32);
        snapshot.Render(scene);

        var blurred = new DrawingVisual { Effect = new BlurEffect { Radius = blur } };
        using (var context = blurred.RenderOpen())
        {
            context.DrawImage(snapshot, bounds);
        }

        var tinted = new DrawingVisual();
        using (var context = tinted.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(tint) { Opacity = tintOpacity }, null, bounds);
        }

        var layers = new ContainerVisual();
        layers.Children.Add(blurred);
        layers.Children.Add(tinted);
        var frosted = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale,
            PixelFormats.Pbgra32);
        frosted.Render(layers);
        frosted.Freeze();
        return frosted;
    }

    public static ImageBrush Bleed(Color canvas, Color accent, DpiScale dpi)
    {
        const double radiusX = 760, radiusY = 540, strength = 0.14;
        var width = (int)Math.Ceiling(radiusX * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(radiusY * dpi.DpiScaleY);
        var pixels = new byte[width * height * 4];
        var noise = new Random(0);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dx = x / (width - 1.0);
                var dy = y / (height - 1.0);
                var edge = Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
                var mix = strength * edge * edge;
                var i = (y * width + x) * 4;
                pixels[i] = Dither(canvas.B, accent.B, mix, noise);
                pixels[i + 1] = Dither(canvas.G, accent.G, mix, noise);
                pixels[i + 2] = Dither(canvas.R, accent.R, mix, noise);
                pixels[i + 3] = 255;
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
            PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();

        var brush = new ImageBrush(bitmap)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        brush.Freeze();
        return brush;
    }

    private static byte Dither(byte from, byte to, double mix, Random noise) =>
        (byte)Math.Round(from + (to - from) * mix + noise.NextDouble() - 0.5);

    public static void Flash(Window window)
    {
        const uint tray = 0x2, untilForeground = 0xC;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = new WindowInteropHelper(window).Handle,
            Flags = tray | untilForeground,
        };
        _ = FlashWindowEx(ref info);
    }

    public static Thickness MaximizedInset(DpiScale dpi)
    {
        const int sizeFrame = 32, paddedBorder = 92;
        var pixelDpi = (uint)(96 * dpi.DpiScaleX);
        var pixels = GetSystemMetricsForDpi(sizeFrame, pixelDpi)
                     + GetSystemMetricsForDpi(paddedBorder, pixelDpi);
        return new Thickness(pixels / dpi.DpiScaleX);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    private static partial long GetWindowLongPtrW(IntPtr hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial long SetWindowLongPtrW(IntPtr hwnd, int index, long value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);
}
