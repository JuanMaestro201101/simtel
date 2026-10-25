using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SimTelemetry.UI;

/// <summary>Icono de APEX dibujado en código (sin archivos): una trazada que roza el vértice, marcado en morado, con el piano de la curva.</summary>
public static class AppIcon
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Mismo color morado que «la mejor vuelta» en la app (tema oscuro).</summary>
    private static readonly System.Windows.Media.Color Apex = System.Windows.Media.Color.FromRgb(0xB5, 0x7E, 0xFF);

    private static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);
        float u = size / 64f;
        bool small = size < 40; // a 16–32 px, trazos más gruesos para que se lea en la barra de tareas
        using (var bg = new SolidBrush(Gdi(Palette.OverlayPanel)))
            g.FillRectangle(bg, 0, 0, size, size);
        if (!small)
            using (var kerb = new System.Drawing.Pen(Gdi(Palette.OverlayBrake), 3 * u))
                g.DrawLines(kerb, new[] { new PointF(8 * u, 15 * u), new PointF(8 * u, 8 * u), new PointF(15 * u, 8 * u) });
        using (var line = new System.Drawing.Pen(Gdi(Palette.OverlayText), (small ? 8 : 5) * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawBezier(line, new PointF(12 * u, 58 * u), new PointF(13.3f * u, 31.3f * u), new PointF(28.7f * u, 16f * u), new PointF(58 * u, 12 * u));
        float r = (small ? 8 : 5.5f) * u;
        using (var dot = new SolidBrush(Gdi(Apex)))
            g.FillEllipse(dot, 24.5f * u - r, 26.5f * u - r, 2 * r, 2 * r);
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
