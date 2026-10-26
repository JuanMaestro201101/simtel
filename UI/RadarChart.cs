using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace SimTelemetry.UI;

/// <summary>Gráfico de radar de los atributos del piloto (0–100 en cada eje), como la ficha de un juego de carreras.</summary>
public sealed class RadarChart : FrameworkElement
{
    private static readonly Typeface Face = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Condensed);
    private IReadOnlyList<(string Label, double Value)> _axes = Array.Empty<(string, double)>();

    public IReadOnlyList<(string Label, double Value)> Axes
    {
        get => _axes;
        set { _axes = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        int n = _axes.Count;
        if (n < 3) return;
        double w = ActualWidth, h = ActualHeight, dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var c = new Point(w / 2, h / 2);
        double r = Math.Min(w, h) / 2 - 34; // margen para las etiquetas
        Point At(int i, double f)
        {
            double a = -Math.PI / 2 + i * 2 * Math.PI / n;
            return new Point(c.X + Math.Cos(a) * r * f, c.Y + Math.Sin(a) * r * f);
        }
        // Colores leídos al dibujar: siguen al tema sin suscripciones.
        var grid = new Pen(new SolidColorBrush(Palette.ChartGrid), 1);
        var accent = Palette.Accent;

        foreach (double f in new[] { 0.25, 0.5, 0.75, 1.0 })
            dc.DrawGeometry(null, grid, Polygon(Enumerable.Range(0, n).Select(i => At(i, f))));
        for (int i = 0; i < n; i++) dc.DrawLine(grid, c, At(i, 1));

        var points = Enumerable.Range(0, n).Select(i => At(i, Math.Clamp(_axes[i].Value, 0, 100) / 100)).ToList();
        dc.DrawGeometry(new SolidColorBrush(accent with { A = 0x40 }), new Pen(new SolidColorBrush(accent), 2) { LineJoin = PenLineJoin.Round }, Polygon(points));
        var dot = new SolidColorBrush(accent);
        foreach (var p in points) dc.DrawEllipse(dot, null, p, 3, 3);

        var text = new SolidColorBrush(Palette.Text);
        var dim = new SolidColorBrush(Palette.Dim);
        for (int i = 0; i < n; i++)
        {
            var label = new FormattedText(_axes[i].Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 13, dim, dpi);
            var value = new FormattedText($"{_axes[i].Value:0}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 16, text, dpi);
            var p = At(i, 1);
            double dx = p.X - c.X, dy = p.Y - c.Y;
            double bw = Math.Max(label.Width, value.Width), bh = label.Height + value.Height - 4;
            // La etiqueta va por fuera del eje: a la izquierda, a la derecha o centrada según el ángulo.
            double x = Math.Abs(dx) < 8 ? p.X - bw / 2 : dx > 0 ? p.X + 8 : p.X - bw - 8;
            double y = Math.Abs(dy) < 8 ? p.Y - bh / 2 : dy > 0 ? p.Y + 4 : p.Y - bh - 4;
            dc.DrawText(label, new Point(x + (bw - label.Width) / 2, y));
            dc.DrawText(value, new Point(x + (bw - value.Width) / 2, y + label.Height - 4));
        }
    }

    private static StreamGeometry Polygon(IEnumerable<Point> points)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            bool first = true;
            foreach (var p in points)
            {
                if (first) ctx.BeginFigure(p, true, true);
                else ctx.LineTo(p, true, false);
                first = false;
            }
        }
        g.Freeze();
        return g;
    }
}
