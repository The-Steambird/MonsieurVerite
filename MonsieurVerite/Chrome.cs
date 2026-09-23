using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace MonsieurVerite;

public static partial class Chrome
{
    private const int SystemBackdropType = 38;
    private const int Style = -16;
    private const long SystemMenu = 0x00080000;
    private const long Resizable = 0x00050000; // WS_THICKFRAME | WS_MAXIMIZEBOX
    private const uint FrameChanged = 0x0027; // SWP_FRAMECHANGED | NOZORDER | NOMOVE | NOSIZE
    private const int BackdropNone = 1;

    public static void Solid(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var backdrop = BackdropNone;
        _ = DwmSetWindowAttribute(hwnd, SystemBackdropType, ref backdrop, sizeof(int));
    }

    /// <summary>
    /// DWM acrylic composes its first frame unfrosted and drops the material when the window is
    /// inactive, so the glass is a blurred snapshot of the owner taken once at open.
    /// </summary>
    public static void Frost(Window dialog)
    {
        Solid(dialog);
        var hwnd = new WindowInteropHelper(dialog).Handle;
        _ = SetWindowLongPtrW(hwnd, Style, GetWindowLongPtrW(hwnd, Style) & ~SystemMenu);
        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, FrameChanged);

        dialog.Background = (Brush)dialog.FindResource("WindowBrush");
        if (dialog.Owner?.Content is not FrameworkElement { ActualWidth: >= 1, ActualHeight: >= 1 } scene
            || dialog.Content is not Panel root)
        {
            return;
        }

        var glass = new ImageBrush(Frosted(scene, (Color)dialog.FindResource("FrostColor")))
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

    /// <summary>
    /// ShowDialog disables the owner, and the resize styles are what let a drag snap it or a
    /// double click maximize it under the dialog.
    /// </summary>
    public static void Modal(Window owner, bool open)
    {
        var hwnd = new WindowInteropHelper(owner).Handle;
        var style = GetWindowLongPtrW(hwnd, Style);
        _ = SetWindowLongPtrW(hwnd, Style, open ? style & ~Resizable : style | Resizable);
        if (open)
        {
            _ = EnableWindow(hwnd, true);
        }
    }

    public static Thickness MaximizedInset(DpiScale dpi)
    {
        const int sizeFrame = 32, paddedBorder = 92;
        var pixelDpi = (uint)(96 * dpi.DpiScaleX);
        var pixels = GetSystemMetricsForDpi(sizeFrame, pixelDpi)
                     + GetSystemMetricsForDpi(paddedBorder, pixelDpi);
        return new Thickness(pixels / dpi.DpiScaleX);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnableWindow(
        IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

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
