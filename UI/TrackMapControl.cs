using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SimTelemetry.Analysis;
using SimTelemetry.Recording;

namespace SimTelemetry.UI;

/// <summary>
/// Mapa del circuito visto desde arriba con tu trazada. Con referencia, colorea dónde ganas (verde) y
/// dónde pierdes (rojo) tiempo y dibuja la trazada de referencia para comparar las líneas.
/// Rueda: zoom (con zoom el asfalto va a su anchura real, para ver tu línea dentro de la pista) · arrastrar: mover · doble clic: ver todo.
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
    private double _zoom = 1;
    private Vector _pan;
    private Point? _dragFrom;
    private string _bounds = "";
    private const double TrackWidthM = 12; // anchura típica de un circuito, para dibujar el asfalto a escala con zoom

    public void SetData(LapData? lap, LapData? reference, Comparison? cmp, LapStats? stats, LapStats? refStats)
    {
        _lap = lap != null && lap.HasPosition ? lap : null;
        _ref = reference != null && reference.HasPosition ? reference : null;
        // Otra vuelta del mismo circuito conserva el zoom: así comparas la misma curva vuelta a vuelta.
        string bounds = _lap == null ? "" : $"{_lap.PosX.Min() / 50:0}|{_lap.PosZ.Min() / 50:0}|{_lap.PosX.Max() / 50:0}|{_lap.PosZ.Max() / 50:0}";
        if (bounds != _bounds) ResetZoom();
        _bounds = bounds;
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

    public void ResetZoom()
    {
        _zoom = 1;
        _pan = default;
        InvalidateVisual();
    }

    /// <summary>Acerca el mapa al tramo entre dos distancias de tu vuelta (la curva elegida en «Qué mejorar» o «Frenadas»).</summary>
    public void ZoomTo(double from, double to)
    {
        if (_lap == null || ActualWidth <= 0) return;
        var (P, _) = BaseTransform();
        var pts = Enumerable.Range(0, _lap.Count).Where(i => _lap.Distance[i] >= from && _lap.Distance[i] <= to)
            .Select(i => P(_lap.PosX[i], _lap.PosZ[i])).ToList();
        if (pts.Count < 2) return;
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        var c0 = new Point(ActualWidth / 2, ActualHeight / 2);
        _zoom = Math.Clamp(Math.Min((ActualWidth - 60) / Math.Max(20, maxX - minX), (ActualHeight - 80) / Math.Max(20, maxY - minY)), 1, 40);
        _pan = -(new Point((minX + maxX) / 2, (minY + maxY) / 2) - c0) * _zoom;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_lap == null) return;
        var m = e.GetPosition(this);
        var c0 = new Point(ActualWidth / 2, ActualHeight / 2);
        var b = (m - c0 - _pan) / _zoom; // punto del mapa bajo el ratón, que debe quedarse quieto
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25 : 0.8), 1, 40);
        _pan = _zoom == 1 ? default : m - c0 - b * _zoom;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ResetZoom(); return; }
        _dragFrom = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is not Point from || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        _pan += p - from;
        _dragFrom = p;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();
    }

    /// <summary>Del mundo (x, z) a la pantalla sin zoom, con las dos trazadas encajadas; y cuántos píxeles mide un metro.</summary>
    private (Func<double, double, Point> P, double Scale) BaseTransform()
    {
        double w = ActualWidth, h = ActualHeight;
        var xs = _lap!.PosX.Concat(_ref?.PosX ?? Array.Empty<double>()).ToArray();
        var zs = _lap.PosZ.Concat(_ref?.PosZ ?? Array.Empty<double>()).ToArray();
        double minX = xs.Min(), maxX = xs.Max(), minZ = zs.Min(), maxZ = zs.Max();
        const double pad = 18, top = 30;
        double scale = Math.Min((w - pad * 2) / Math.Max(1, maxX - minX), (h - top - pad - 14) / Math.Max(1, maxZ - minZ));
        double ox = pad + ((w - pad * 2) - (maxX - minX) * scale) / 2;
        double oy = top + ((h - top - pad - 14) - (maxZ - minZ) * scale) / 2;
        return ((x, z) => new Point(ox + (x - minX) * scale, oy + (maxZ - z) * scale), scale);
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

        // Escala común para las dos trazadas, manteniendo proporciones (z hacia arriba), más el zoom del usuario.
        var (baseP, scale) = BaseTransform();
        var c0 = new Point(w / 2, h / 2);
        double zoom = _zoom;
        var pan = _pan;
        Point P(double x, double z) => c0 + (baseP(x, z) - c0) * zoom + pan;
        var view = new Rect(-20, -20, w + 40, h + 40);
        int detail = Math.Max(1, (int)(_lap.Count / (1500 * zoom)));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 8, 8));

        // "Asfalto": la trazada de referencia (o la tuya) en gris grueso; con zoom, a su anchura real.
        var basePath = _ref ?? _lap;
        double asphalt = TrackWidthM * scale * zoom;
        var trackPen = asphalt <= TrackPen.Thickness ? TrackPen
            : Frozen(new Pen(TrackPen.Brush, asphalt) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
        dc.DrawGeometry(null, trackPen, Path(basePath, P, detail));
        if (_ref != null) dc.DrawGeometry(null, zoom > 2 ? Frozen(new Pen(RefPen.Brush, 2) { LineJoin = PenLineJoin.Round }) : RefPen, Path(_ref, P, detail));

        // Tu trazada coloreada: ganancia/pérdida respecto a la referencia, o velocidad si no hay referencia.
        double maxSpeed = Math.Max(1, _lap.Speed.Max()), minSpeed = _lap.Speed.Min();
        int step = detail;
        for (int i = step; i < _lap.Count; i += step)
        {
            Point a = P(_lap.PosX[i - step], _lap.PosZ[i - step]), b = P(_lap.PosX[i], _lap.PosZ[i]);
            if (!view.Contains(a) && !view.Contains(b)) continue;
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
            dc.DrawLine(pen, a, b);
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

        dc.Pop();
        string legend = _cmp != null ? "verde ganas · rojo pierdes · naranja ref." : "azul lento → rojo rápido";
        var lt = Text(legend + " · ● frenadas" + (zoom > 1 ? $" · ×{zoom:0.#}, doble clic: ver todo" : " · rueda: zoom"), 10, Dim, dpi);
        dc.DrawText(lt, new Point(8, h - lt.Height - 4));
    }

    private static Point At(LapData lap, double distance, Func<double, double, Point> p) =>
        p(LapAnalyzer.Interpolate(lap.Distance, lap.PosX, distance), LapAnalyzer.Interpolate(lap.Distance, lap.PosZ, distance));

    private static StreamGeometry Path(LapData lap, Func<double, double, Point> p, int step)
    {
        var g = new StreamGeometry();
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
