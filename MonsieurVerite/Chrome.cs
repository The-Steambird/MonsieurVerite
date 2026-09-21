using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MonsieurVerite;

public static partial class Chrome
{
    private const int SystemBackdropType = 38;
    private const int Style = -16;
    private const long SystemMenu = 0x00080000;
    private const uint FrameChanged = 0x0027; // SWP_FRAMECHANGED | NOZORDER | NOMOVE | NOSIZE
    private const int BackdropNone = 1;
    private const int BackdropAcrylic = 3;

    public static void Solid(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var backdrop = BackdropNone;
        _ = DwmSetWindowAttribute(hwnd, SystemBackdropType, ref backdrop, sizeof(int));
    }

    public static void Acrylic(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var backdrop = BackdropAcrylic;
        if (DwmSetWindowAttribute(hwnd, SystemBackdropType, ref backdrop, sizeof(int)) != 0)
        {
            window.Background = (Brush)window.FindResource("WindowBrush");
        }

        _ = SetWindowLongPtrW(hwnd, Style, GetWindowLongPtrW(hwnd, Style) & ~SystemMenu);
        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, FrameChanged);
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

    public static Thickness MaximizedInset(DpiScale dpi)
    {
        const int sizeFrame = 32, paddedBorder = 92;
        var pixelDpi = (uint)(96 * dpi.DpiScaleX);
        var pixels = GetSystemMetricsForDpi(sizeFrame, pixelDpi)
                     + GetSystemMetricsForDpi(paddedBorder, pixelDpi);
        return new Thickness(pixels / dpi.DpiScaleX);
    }

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
