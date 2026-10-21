using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SimTelemetry.UI;

public sealed class ChartSeries
{
    public string Name { get; init; } = "";
    public double[] X { get; init; } = Array.Empty<double>();
    public double[] Y { get; init; } = Array.Empty<double>();
    public Color Color { get; init; } = Colors.White;
    public double Thickness { get; init; } = 1.5;
    public bool Dashed { get; init; }
    public string Format { get; init; } = "0";
    /// <summary>Dibuja un punto en cada dato (útil con pocos datos, p. ej. una por sesión).</summary>
    public bool Markers { get; init; }
}

public sealed class ChartPane
{
    public string Title { get; init; } = "";
    public double Weight { get; init; } = 1;
    public double? Min { get; init; }
    public double? Max { get; init; }
    public bool ZeroLine { get; init; }
    public List<ChartSeries> Series { get; } = new();
}

/// <summary>
/// Gráfica de telemetría con varios paneles apilados que comparten el eje X (distancia).
/// Rueda = zoom, arrastrar = desplazar, doble clic = ver todo. El cursor muestra los valores.
/// </summary>
public sealed class TelemetryChart : FrameworkElement
{
    private const double LeftMargin = 58, RightMargin = 12, BottomMargin = 24, PaneGap = 8;

    // Pinceles del tema actual: se rehacen al cambiar de tema.
    private static Brush BackBrush = null!, PaneBrush = null!, TitleBrush = null!, TextBrush = null!, DimBrush = null!, TooltipBrush = null!, HighlightBrush = null!;
    private static Pen BorderPen = null!, GridPen = null!, ZeroPen = null!, CursorPen = null!;
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface BoldFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly List<ChartPane> _panes = new();
    private double _dataMin, _dataMax = 1, _viewMin, _viewMax = 1;
    private double? _cursorX;
    private Point? _dragStart;
    private double _dragViewMin, _dragViewMax;
    private (double From, double To)? _highlight;

    static TelemetryChart()
    {
        LoadColors();
        Palette.Changed += LoadColors;
    }

    private static void LoadColors()
    {
        BackBrush = Frozen(new SolidColorBrush(Palette.ChartBg));
        PaneBrush = Frozen(new SolidColorBrush(Palette.ChartPane));
        TitleBrush = Frozen(new SolidColorBrush(Palette.ChartPane with { A = 0xC8 }));
        TextBrush = Frozen(new SolidColorBrush(Palette.ChartText));
        DimBrush = Frozen(new SolidColorBrush(Palette.ChartDim));
        TooltipBrush = Frozen(new SolidColorBrush(Palette.ChartTooltip));
        HighlightBrush = Frozen(new SolidColorBrush(Palette.ChartHighlight));
        BorderPen = Frozen(new Pen(new SolidColorBrush(Palette.Line), 1));
        GridPen = Frozen(new Pen(new SolidColorBrush(Palette.ChartGrid), 1));
        ZeroPen = Frozen(new Pen(new SolidColorBrush(Palette.ChartZero), 1));
        CursorPen = Frozen(new Pen(new SolidColorBrush(Palette.ChartCursor), 1));
    }

    public TelemetryChart()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public string XUnit { get; set; } = "m";

    /// <summary>Se dispara al mover el cursor (null = el ratón salió de la gráfica).</summary>
    public event Action<double?>? CursorChanged;

    /// <summary>Margen extra a cada lado del eje X (en unidades de X).</summary>
    public double XPad { get; set; }

    /// <summary>Etiquetas propias del eje X; si se indica, las marcas van en valores enteros.</summary>
    public Func<double, string>? XLabel { get; set; }

    public void SetPanes(IEnumerable<ChartPane> panes)
    {
        _panes.Clear();
        _panes.AddRange(panes);
        var xs = _panes.SelectMany(p => p.Series).Where(s => s.X.Length > 0).ToList();
        _dataMin = xs.Count > 0 ? xs.Min(s => s.X[0]) : 0;
        _dataMax = xs.Count > 0 ? xs.Max(s => s.X[^1]) : 1;
        _dataMin -= XPad;
        _dataMax += XPad;
        if (_dataMax - _dataMin < 1) _dataMax = _dataMin + 1;
        _viewMin = _dataMin;
        _viewMax = _dataMax;
        _highlight = null;
        InvalidateVisual();
    }

    /// <summary>Acerca la vista a un rango de distancia y lo resalta.</summary>
    public void ZoomTo(double from, double to)
    {
        double pad = (to - from) * 0.25;
        _viewMin = Math.Max(_dataMin, from - pad);
        _viewMax = Math.Min(_dataMax, to + pad);
        _highlight = (from, to);
        InvalidateVisual();
    }

    public void ResetZoom()
    {
        _viewMin = _dataMin;
        _viewMax = _dataMax;
        _highlight = null;
        InvalidateVisual();
    }

    private Rect PlotArea => new(LeftMargin, 4, Math.Max(10, ActualWidth - LeftMargin - RightMargin), Math.Max(10, ActualHeight - BottomMargin - 4));

    private double XToPx(double x, Rect area) => area.Left + (x - _viewMin) / (_viewMax - _viewMin) * area.Width;
    private double PxToX(double px, Rect area) => _viewMin + (px - area.Left) / area.Width * (_viewMax - _viewMin);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var area = PlotArea;

        if (_panes.Count == 0)
        {
            var t = Text("Selecciona una vuelta para ver su telemetría", 14, DimBrush, Face, dpi);
            dc.DrawText(t, new Point((ActualWidth - t.Width) / 2, (ActualHeight - t.Height) / 2));
            return;
        }

        double totalWeight = _panes.Sum(p => p.Weight);
        double available = area.Height - PaneGap * (_panes.Count - 1);
        double y = area.Top;
        var paneRects = new List<Rect>();
        foreach (var pane in _panes)
        {
            double h = available * pane.Weight / totalWeight;
            var r = new Rect(area.Left, y, area.Width, Math.Max(10, h));
            paneRects.Add(r);
            DrawPane(dc, pane, r, dpi);
            y += h + PaneGap;
        }

        DrawXAxis(dc, area, dpi);

        if (_cursorX is double cx && cx >= _viewMin && cx <= _viewMax)
        {
            double px = XToPx(cx, area);
            dc.DrawLine(CursorPen, new Point(px, area.Top), new Point(px, area.Bottom));
            for (int i = 0; i < _panes.Count; i++) DrawCursorValues(dc, _panes[i], paneRects[i], cx, px, dpi);

            var label = Text(XLabel != null ? XLabel(Math.Round(cx)) : $"{cx:0} {XUnit}", 11, TextBrush, BoldFace, dpi);
            var lr = new Rect(px - label.Width / 2 - 4, area.Bottom + 3, label.Width + 8, label.Height);
            dc.DrawRoundedRectangle(TooltipBrush, null, lr, 3, 3);
            dc.DrawText(label, new Point(lr.X + 4, lr.Y));
        }
    }

    private void DrawPane(DrawingContext dc, ChartPane pane, Rect r, double dpi)
    {
        dc.DrawRectangle(PaneBrush, BorderPen, r);
        var (min, max) = YRange(pane);

        if (_highlight is { } hl)
        {
            double x0 = Math.Max(r.Left, XToPx(hl.From, r)), x1 = Math.Min(r.Right, XToPx(hl.To, r));
            if (x1 > x0) dc.DrawRectangle(HighlightBrush, null, new Rect(x0, r.Top, x1 - x0, r.Height));
        }

        double step = AxisStep(max - min, Math.Max(1, (int)(r.Height / 14)));
        for (long i = (long)Math.Ceiling(min / step); i * step <= max; i++)
        {
            double v = i * step;
            double py = r.Bottom - (v - min) / (max - min) * r.Height;
            dc.DrawLine(GridPen, new Point(r.Left, py), new Point(r.Right, py));
            var label = Text(FormatAxis(v), 10, DimBrush, Face, dpi);
            dc.DrawText(label, new Point(r.Left - label.Width - 6, SafeClamp(py - label.Height / 2, r.Top - 2, r.Bottom - label.Height + 2)));
        }
        if (pane.ZeroLine && min < 0 && max > 0)
        {
            double pz = r.Bottom - (0 - min) / (max - min) * r.Height;
            dc.DrawLine(ZeroPen, new Point(r.Left, pz), new Point(r.Right, pz));
        }

        dc.PushClip(new RectangleGeometry(r));
        foreach (var s in pane.Series) DrawSeries(dc, s, r, min, max);
        dc.Pop();

        var title = Text(pane.Title, 11, TextBrush, BoldFace, dpi);
        dc.DrawRoundedRectangle(TitleBrush, null, new Rect(r.Left + 3, r.Top + 2, title.Width + 6, title.Height + 2), 3, 3);
        dc.DrawText(title, new Point(r.Left + 6, r.Top + 3));
    }

    private void DrawSeries(DrawingContext dc, ChartSeries s, Rect r, double min, double max)
    {
        int n = Math.Min(s.X.Length, s.Y.Length);
        if (n < 1) return;
        int first = Math.Max(0, LowerBound(s.X, _viewMin) - 1);
        int last = Math.Min(n - 1, LowerBound(s.X, _viewMax) + 1);

        if (s.Markers)
        {
            var brush = new SolidColorBrush(s.Color);
            brush.Freeze();
            for (int i = first; i <= last; i++)
                dc.DrawEllipse(brush, null, new Point(XToPx(s.X[i], r), r.Bottom - (s.Y[i] - min) / (max - min) * r.Height), 3.5, 3.5);
        }
        if (n < 2 || last <= first) return;

        // Si hay muchos puntos por píxel, nos quedamos con el mínimo y el máximo de cada columna.
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool started = false;
            double colPx = double.NaN, colMin = 0, colMax = 0, colFirstY = 0, colLastY = 0;
            void Emit(Point p)
            {
                if (!started) { ctx.BeginFigure(p, false, false); started = true; }
                else ctx.LineTo(p, true, false);
            }
            void FlushColumn()
            {
                if (double.IsNaN(colPx)) return;
                Emit(new Point(colPx, colFirstY));
                if (colMin != colFirstY && colMin != colLastY) Emit(new Point(colPx, colMin));
                if (colMax != colFirstY && colMax != colLastY) Emit(new Point(colPx, colMax));
                Emit(new Point(colPx, colLastY));
            }

            for (int i = first; i <= last; i++)
            {
                double px = Math.Round(XToPx(s.X[i], r));
                double py = r.Bottom - (s.Y[i] - min) / (max - min) * r.Height;
                if (px != colPx)
                {
                    FlushColumn();
                    colPx = px; colMin = colMax = colFirstY = colLastY = py;
                }
                else
                {
                    colMin = Math.Min(colMin, py);
                    colMax = Math.Max(colMax, py);
                    colLastY = py;
                }
            }
            FlushColumn();
        }
        geo.Freeze();

        var pen = new Pen(new SolidColorBrush(s.Color), s.Thickness) { LineJoin = PenLineJoin.Round };
        if (s.Dashed) pen.DashStyle = new DashStyle(new double[] { 3, 2 }, 0);
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private void DrawCursorValues(DrawingContext dc, ChartPane pane, Rect r, double cx, double px, double dpi)
    {
        var lines = new List<FormattedText>();
        foreach (var s in pane.Series)
        {
            if (s.X.Length < 2 || cx < s.X[0] || cx > s.X[^1]) continue;
            double v = Analysis.LapAnalyzer.Interpolate(s.X, s.Y, cx);
            lines.Add(Text($"{s.Name}: {v.ToString(s.Format, CultureInfo.CurrentCulture)}", 10.5, new SolidColorBrush(s.Color), BoldFace, dpi));
        }
        if (lines.Count == 0) return;
        double w = lines.Max(l => l.Width) + 10, h = lines.Sum(l => l.Height) + 6;
        double x = px + 8 + w > r.Right ? px - 8 - w : px + 8;
        var box = new Rect(x, r.Top + 4, w, h);
        dc.DrawRoundedRectangle(TooltipBrush, BorderPen, box, 3, 3);
        double y = box.Top + 3;
        foreach (var l in lines)
        {
            dc.DrawText(l, new Point(box.Left + 5, y));
            y += l.Height;
        }
    }

    private void DrawXAxis(DrawingContext dc, Rect area, double dpi)
    {
        double range = _viewMax - _viewMin;
        if (!double.IsFinite(range) || range <= 0) return;
        double step = NiceStep(range / 8);
        if (XLabel != null) step = Math.Max(1, Math.Round(step));
        for (long i = (long)Math.Ceiling(_viewMin / step); i * step <= _viewMax; i++)
        {
            double v = i * step; // entero × paso: sin "-0" ni decimales acumulados
            double px = XToPx(v, area);
            dc.DrawLine(GridPen, new Point(px, area.Top), new Point(px, area.Bottom));
            string text = XLabel != null ? XLabel(v) : v >= 1000 && step >= 100 ? $"{v / 1000:0.#} km" : $"{v:0}";
            var label = Text(text, 10, DimBrush, Face, dpi);
            dc.DrawText(label, new Point(px - label.Width / 2, area.Bottom + 4));
        }
    }

    private (double min, double max) YRange(ChartPane pane)
    {
        double min = pane.Min ?? double.MaxValue, max = pane.Max ?? double.MinValue;
        if (pane.Min == null || pane.Max == null)
        {
            foreach (var s in pane.Series)
            {
                int first = Math.Max(0, LowerBound(s.X, _viewMin));
                int last = Math.Min(s.Y.Length - 1, LowerBound(s.X, _viewMax));
                for (int i = first; i <= last; i++)
                {
                    if (pane.Min == null) min = Math.Min(min, s.Y[i]);
                    if (pane.Max == null) max = Math.Max(max, s.Y[i]);
                }
            }
            if (min == double.MaxValue) min = 0;
            if (max == double.MinValue) max = 1;
            // Con un solo valor (p. ej. una sesión en Progreso), margen según el valor: si no, el eje inventa decimales (22,93 – 22,95).
            double pad = max - min > 1e-9 ? Math.Max((max - min) * 0.08, 0.01) : Math.Max(Math.Abs(max) * 0.1, 1);
            if (pane.Min == null) min -= pad;
            if (pane.Max == null) max += pad;
        }
        if (!double.IsFinite(min) || !double.IsFinite(max)) (min, max) = (0, 1);
        if (max - min < 1e-6) max = min + 1;
        return (min, max);
    }

    private static int LowerBound(double[] xs, double x)
    {
        int lo = 0, hi = xs.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (xs[mid] < x) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static double NiceStep(double raw)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
        double n = raw / mag;
        return (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * mag;
    }

    /// <summary>Paso redondo (1, 2, 5, 10…) para que quepan como mucho <paramref name="maxTicks"/> marcas.</summary>
    public static double AxisStep(double range, int maxTicks)
    {
        double step = NiceStep(range / Math.Min(maxTicks, 3));
        while (range / step > maxTicks) step = NiceStep(step * 2.01);
        return step;
    }

    private static string FormatAxis(double v) =>
        Math.Abs(v) >= 100 ? v.ToString("0", CultureInfo.CurrentCulture) : v.ToString("0.##", CultureInfo.CurrentCulture);

    // ---------------- Interacción ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var area = PlotArea;
        var p = e.GetPosition(this);
        if (_dragStart is Point start && e.LeftButton == MouseButtonState.Pressed)
        {
            double dx = (p.X - start.X) / area.Width * (_dragViewMax - _dragViewMin);
            double span = _dragViewMax - _dragViewMin;
            _viewMin = SafeClamp(_dragViewMin - dx, _dataMin, _dataMax - span);
            _viewMax = _viewMin + span;
        }
        _cursorX = p.X >= area.Left && p.X <= area.Right ? PxToX(p.X, area) : null;
        CursorChanged?.Invoke(_cursorX);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _cursorX = null;
        CursorChanged?.Invoke(null);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2)
        {
            ResetZoom();
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragViewMin = _viewMin;
        _dragViewMax = _viewMax;
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragStart = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var area = PlotArea;
        double anchor = PxToX(SafeClamp(e.GetPosition(this).X, area.Left, area.Right), area);
        double factor = e.Delta > 0 ? 0.8 : 1.25;
        double full = _dataMax - _dataMin;
        double span = Math.Clamp((_viewMax - _viewMin) * factor, Math.Min(20, full), full);
        double t = (anchor - _viewMin) / (_viewMax - _viewMin);
        _viewMin = SafeClamp(anchor - span * t, _dataMin, _dataMax - span);
        _viewMax = _viewMin + span;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Como Math.Clamp, pero si por redondeo el mínimo queda por encima del máximo devuelve el mínimo en vez de fallar.</summary>
    private static double SafeClamp(double v, double min, double max) => Math.Max(min, Math.Min(v, max));

    private static FormattedText Text(string s, double size, Brush brush, Typeface face, double dpi) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, dpi);

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
