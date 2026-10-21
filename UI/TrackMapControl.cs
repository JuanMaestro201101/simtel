using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using SimTelemetry.Analysis;
using SimTelemetry.Recording;

namespace SimTelemetry.UI;

/// <summary>
/// Mapa del circuito visto desde arriba con tu trazada. Con referencia, colorea dónde ganas (verde) y
/// dónde pierdes (rojo) tiempo y dibuja la trazada de referencia para comparar las líneas.
/// </summary>
public sealed class TrackMapControl : FrameworkElement
{
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Brush LabelBack = Frozen(new SolidColorBrush(Palette.BackdropLabel));
    // Pinceles del tema actual: se rehacen al cambiar de tema.
    private static Brush Back = null!, Dim = null!, BrakeDot = null!, StartBrush = null!;
    private static Pen TrackPen = null!, RefPen = null!, RefBrakePen = null!;

    static TrackMapControl()
    {
        LoadColors();
        Palette.Changed += LoadColors;
    }

    private static void LoadColors()
    {
        Back = Frozen(new SolidColorBrush(Palette.ChartBg));
        TrackPen = Frozen(new Pen(new SolidColorBrush(Palette.TrackLine), 9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
        RefPen = Frozen(new Pen(new SolidColorBrush(Palette.Reference with { A = 0xC0 }), 1.2) { LineJoin = PenLineJoin.Round });
        Dim = Frozen(new SolidColorBrush(Palette.Dim));
        BrakeDot = Frozen(new SolidColorBrush(Palette.Brake));
        RefBrakePen = Frozen(new Pen(new SolidColorBrush(Palette.Reference), 1.5));
        StartBrush = Frozen(new SolidColorBrush(Palette.ChartText));
    }

    private LapData? _lap, _ref;
    private Comparison? _cmp;
    private LapStats? _stats, _refStats;
    private double? _cursor;

    public void SetData(LapData? lap, LapData? reference, Comparison? cmp, LapStats? stats, LapStats? refStats)
    {
        _lap = lap != null && lap.HasPosition ? lap : null;
        _ref = reference != null && reference.HasPosition ? reference : null;
        _cmp = cmp;
        _stats = stats;
        _refStats = refStats;
        _cursor = null;
        InvalidateVisual();
    }

    public bool HasData => _lap != null;

    /// <summary>Etiquetas sobre la trazada (p. ej. el número de cada curva en su punto de frenada).</summary>
    public IReadOnlyList<(double Distance, string Label)> Labels { get; set; } = Array.Empty<(double, string)>();

    /// <summary>Marca en el mapa la posición correspondiente a una distancia (sincronizado con la gráfica).</summary>
    public void SetCursor(double? distance)
    {
        _cursor = distance;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle(Back, null, new Rect(0, 0, w, h), 8, 8);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_lap == null)
        {
            var t = Text("Mapa no disponible:\neste juego no envía la posición\ndel coche (o la vuelta es antigua).", 11, Dim, dpi);
            t.TextAlignment = TextAlignment.Center;
            dc.DrawText(t, new Point(w / 2, (h - t.Height) / 2));
            return;
        }

        // Escala común para las dos trazadas, manteniendo proporciones (z hacia arriba).
        var xs = _lap.PosX.Concat(_ref?.PosX ?? Array.Empty<double>()).ToArray();
        var zs = _lap.PosZ.Concat(_ref?.PosZ ?? Array.Empty<double>()).ToArray();
        double minX = xs.Min(), maxX = xs.Max(), minZ = zs.Min(), maxZ = zs.Max();
        const double pad = 18, top = 30;
        double scale = Math.Min((w - pad * 2) / Math.Max(1, maxX - minX), (h - top - pad - 14) / Math.Max(1, maxZ - minZ));
        double ox = pad + ((w - pad * 2) - (maxX - minX) * scale) / 2;
        double oy = top + ((h - top - pad - 14) - (maxZ - minZ) * scale) / 2;
        Point P(double x, double z) => new(ox + (x - minX) * scale, oy + (maxZ - z) * scale);

        // "Asfalto": la trazada de referencia (o la tuya) en gris grueso.
        var basePath = _ref ?? _lap;
        dc.DrawGeometry(null, TrackPen, Path(basePath, P));
        if (_ref != null) dc.DrawGeometry(null, RefPen, Path(_ref, P));

        // Tu trazada coloreada: ganancia/pérdida respecto a la referencia, o velocidad si no hay referencia.
        double maxSpeed = Math.Max(1, _lap.Speed.Max()), minSpeed = _lap.Speed.Min();
        int step = Math.Max(1, _lap.Count / 1500);
        for (int i = step; i < _lap.Count; i += step)
        {
            Color c;
            if (_cmp != null && _cmp.Delta.Length > 2)
            {
                double d0 = LapAnalyzer.Interpolate(_cmp.GridDistance, _cmp.Delta, _lap.Distance[i] - 25);
                double d1 = LapAnalyzer.Interpolate(_cmp.GridDistance, _cmp.Delta, _lap.Distance[i] + 25);
                double slope = (d1 - d0) / 50 * 100; // s perdidos cada 100 m
                c = slope > 0.02 ? Lerp(Palette.LapMine, Palette.Bad, Math.Min(1, slope / 0.15))
                  : slope < -0.02 ? Lerp(Palette.LapMine, Palette.Good, Math.Min(1, -slope / 0.15))
                  : Palette.LapMine;
            }
            else
            {
                double f = (_lap.Speed[i] - minSpeed) / Math.Max(1, maxSpeed - minSpeed);
                c = f < 0.5 ? Lerp(Palette.Clutch, Palette.Warn, f * 2)
                            : Lerp(Palette.Warn, Palette.Bad, (f - 0.5) * 2);
            }
            var pen = new Pen(new SolidColorBrush(c), 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawLine(pen, P(_lap.PosX[i - step], _lap.PosZ[i - step]), P(_lap.PosX[i], _lap.PosZ[i]));
        }

        // Puntos de frenada: tuyos (rojo) y de la referencia (aro naranja).
        if (_refStats != null && _ref != null)
            foreach (var z in _refStats.BrakeZones)
                dc.DrawEllipse(null, RefBrakePen, At(_ref, z.StartDistance, P), 5, 5);
        if (_stats != null)
            foreach (var z in _stats.BrakeZones)
                dc.DrawEllipse(BrakeDot, null, At(_lap, z.StartDistance, P), 3, 3);

        foreach (var (distance, label) in Labels)
        {
            var p = At(_lap, distance, P);
            var t = Text(label, 11, Brushes.White, dpi);
            dc.DrawRoundedRectangle(LabelBack, null, new Rect(p.X + 6, p.Y - t.Height / 2 - 1, t.Width + 6, t.Height + 2), 3, 3);
            dc.DrawText(t, new Point(p.X + 9, p.Y - t.Height / 2));
        }

        // Salida / meta.
        var start = P(_lap.PosX[0], _lap.PosZ[0]);
        dc.DrawRectangle(StartBrush, null, new Rect(start.X - 3, start.Y - 3, 6, 6));

        if (_cursor is double cd)
        {
            var p = At(_lap, cd, P);
            dc.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1.5), p, 6, 6);
        }

        string legend = _cmp != null ? "verde ganas · rojo pierdes · naranja ref." : "azul lento → rojo rápido";
        var lt = Text(legend + " · ● frenadas", 10, Dim, dpi);
        dc.DrawText(lt, new Point(8, h - lt.Height - 4));
    }

    private static Point At(LapData lap, double distance, Func<double, double, Point> p) =>
        p(LapAnalyzer.Interpolate(lap.Distance, lap.PosX, distance), LapAnalyzer.Interpolate(lap.Distance, lap.PosZ, distance));

    private static StreamGeometry Path(LapData lap, Func<double, double, Point> p)
    {
        var g = new StreamGeometry();
        int step = Math.Max(1, lap.Count / 1500);
        using (var c = g.Open())
        {
            c.BeginFigure(p(lap.PosX[0], lap.PosZ[0]), false, false);
            for (int i = step; i < lap.Count; i += step) c.LineTo(p(lap.PosX[i], lap.PosZ[i]), true, true);
        }
        g.Freeze();
        return g;
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static FormattedText Text(string s, double size, Brush brush, double dpi) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush, dpi);

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
