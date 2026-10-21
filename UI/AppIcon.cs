using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimTelemetry.UI;

/// <summary>Icono de la app dibujado en código (dos columnas de pedal y una traza), sin archivos externos.</summary>
public static class AppIcon
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);
        float u = size / 32f;
        using (var bg = new SolidBrush(Gdi(Palette.OverlayPanel)))
            g.FillEllipse(bg, 0, 0, size - 1, size - 1);
        using (var red = new SolidBrush(Gdi(Palette.OverlayBrake)))
            g.FillRectangle(red, 18 * u, 14 * u, 4 * u, 10 * u);
        using (var green = new SolidBrush(Gdi(Palette.OverlayThrottle)))
            g.FillRectangle(green, 24 * u, 8 * u, 4 * u, 16 * u);
        using (var pen = new System.Drawing.Pen(Gdi(Palette.OverlayAccent), 2.2f * u))
            g.DrawLines(pen, new[] { new PointF(5 * u, 20 * u), new PointF(9 * u, 12 * u), new PointF(12 * u, 17 * u), new PointF(15 * u, 9 * u) });
        return bmp;
    }

    private static System.Drawing.Color Gdi(System.Windows.Media.Color c) => System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);

    public static Icon CreateIcon()
    {
        using var bmp = Draw(32);
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    public static ImageSource CreateImageSource()
    {
        using var bmp = Draw(64);
        IntPtr h = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(h);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
