using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SimTelemetry.Training;

namespace SimTelemetry.UI;

/// <summary>
/// Pantalla del entrenador: la curva objetivo avanza de derecha a izquierda (ves lo que viene) y tu pedal
/// se dibuja encima. La línea vertical es "ahora".
/// </summary>
public sealed class TrainerControl : FrameworkElement
{
    private const double Past = 4.0, Future = 2.5;

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    // Pinceles del tema actual: se rehacen al cambiar de tema. La banda y el objetivo van en ámbar (Warn), no en el color elegido.
    private static Brush Back = null!, BandBrush = null!, Dim = null!, TextBrush = null!, GoodBrush = null!, BadBrush = null!, TrackBrush = null!, ThrottleBrush = null!;
    private static Pen TargetPen = null!, GridPen = null!, NowPen = null!, TargetMarkPen = null!;

    static TrainerControl()
    {
        LoadColors();
        Palette.Changed += LoadColors;
    }

    private static void LoadColors()
    {
        Back = Frozen(new SolidColorBrush(Palette.ChartBg));
        BandBrush = Frozen(new SolidColorBrush(Palette.TrainerBand));
        TargetPen = Frozen(new Pen(new SolidColorBrush(Palette.Warn), 1.5) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) });
        TargetMarkPen = Frozen(new Pen(new SolidColorBrush(Palette.Warn), 3));
        GridPen = Frozen(new Pen(new SolidColorBrush(Palette.ChartGrid), 1));
        NowPen = Frozen(new Pen(new SolidColorBrush(Palette.TrainerCursor), 1.5));
        Dim = Frozen(new SolidColorBrush(Palette.Dim));
        TextBrush = Frozen(new SolidColorBrush(Palette.Text));
        GoodBrush = Frozen(new SolidColorBrush(Palette.Good));
        BadBrush = Frozen(new SolidColorBrush(Palette.Bad));
        TrackBrush = Frozen(new SolidColorBrush(Palette.TrainerTrack));
        ThrottleBrush = Frozen(new SolidColorBrush(Palette.Throttle));
    }

    private readonly List<(double T, double V)> _trace = new();

    public PedalExercise? Exercise { get; set; }
    /// <summary>Segundo actual dentro del ejercicio (puede ser negativo durante la cuenta atrás).</summary>
    public double Time { get; set; }
    public double Current { get; set; }

    public void Reset()
    {
        _trace.Clear();
        InvalidateVisual();
    }

    public void AddSample(double t, double v)
    {
        _trace.Add((t, v));
        while (_trace.Count > 0 && _trace[0].T < t - Past - 0.5) _trace.RemoveAt(0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 50 || h < 50) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRoundedRectangle(Back, null, new Rect(0, 0, w, h), 8, 8);

        const double barW = 70, pad = 14;
        var area = new Rect(44, pad, w - 44 - barW - pad * 2, h - pad * 2);
        foreach (double f in new[] { 0, 0.25, 0.5, 0.75, 1 })
        {
            double y = area.Bottom - f * area.Height;
            dc.DrawLine(GridPen, new Point(area.Left, y), new Point(area.Right, y));
            var label = Text($"{f * 100:0}%", 10, Dim, dpi);
            dc.DrawText(label, new Point(area.Left - label.Width - 6, y - label.Height / 2));
        }

        double X(double t) => area.Left + (t - (Time - Past)) / (Past + Future) * area.Width;
        double Y(double v) => area.Bottom - Math.Clamp(v, 0, 1) * area.Height;

        var ex = Exercise;
        if (ex != null)
        {
            // Banda de tolerancia y línea objetivo.
            const int steps = 300;
            var band = new StreamGeometry();
            var line = new StreamGeometry();
            using (var b = band.Open())
            using (var l = line.Open())
            {
                var upper = new List<Point>();
                var lower = new List<Point>();
                for (int i = 0; i <= steps; i++)
                {
                    double t = Time - Past + (Past + Future) * i / steps;
                    double target = t < 0 || t > ex.Duration ? 0 : ex.Target(t);
                    var p = new Point(X(t), Y(target));
                    if (i == 0) l.BeginFigure(p, false, false); else l.LineTo(p, true, false);
                    upper.Add(new Point(p.X, Y(target + ex.Tolerance)));
                    lower.Add(new Point(p.X, Y(target - ex.Tolerance)));
                }
                b.BeginFigure(upper[0], true, true);
                for (int i = 1; i < upper.Count; i++) b.LineTo(upper[i], false, false);
                for (int i = lower.Count - 1; i >= 0; i--) b.LineTo(lower[i], false, false);
            }
            band.Freeze();
            line.Freeze();
            dc.PushClip(new RectangleGeometry(area));
            dc.DrawGeometry(BandBrush, null, band);
            dc.DrawGeometry(null, TargetPen, line);

            // Tu pedal.
            if (_trace.Count > 1)
            {
                var color = ex.Channel == PedalChannel.Brake ? Palette.Brake : Palette.Throttle;
                var pen = new Pen(new SolidColorBrush(color), 2.6) { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    for (int i = 0; i < _trace.Count; i++)
                    {
                        var p = new Point(X(_trace[i].T), Y(_trace[i].V));
                        if (i == 0) c.BeginFigure(p, false, false); else c.LineTo(p, true, false);
                    }
                }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
            }
            dc.Pop();
        }

        double nowX = X(Time);
        dc.DrawLine(NowPen, new Point(nowX, area.Top), new Point(nowX, area.Bottom));

        // Columna con el valor actual y el objetivo.
        var bar = new Rect(area.Right + pad + 14, area.Top, barW - 28, area.Height);
        dc.DrawRoundedRectangle(TrackBrush, null, bar, 4, 4);
        double now = ex != null && Time >= 0 && Time <= ex.Duration ? ex.Target(Time) : double.NaN;
        bool inside = !double.IsNaN(now) && ex != null && Math.Abs(Current - now) <= ex.Tolerance;
        double fill = bar.Height * Math.Clamp(Current, 0, 1);
        dc.DrawRoundedRectangle(inside ? GoodBrush : (ex?.Channel == PedalChannel.Throttle ? ThrottleBrush : BadBrush),
            null, new Rect(bar.X, bar.Bottom - fill, bar.Width, fill), 4, 4);
        if (!double.IsNaN(now))
        {
            double ty = Y(now);
            dc.DrawLine(TargetMarkPen, new Point(bar.X - 6, ty), new Point(bar.Right + 6, ty));
        }
        var value = Text($"{Current * 100:0}%", 14, TextBrush, dpi);
        dc.DrawText(value, new Point(bar.X + (bar.Width - value.Width) / 2, Math.Max(area.Top, bar.Bottom - fill - value.Height - 2)));

        if (ex != null && Time < 0)
        {
            var count = Text(Math.Ceiling(-Time).ToString(CultureInfo.InvariantCulture), 64, TextBrush, dpi);
            dc.DrawText(count, new Point(area.Left + (area.Width - count.Width) / 2, area.Top + (area.Height - count.Height) / 2));
        }
        else if (ex == null)
        {
            var t = Text("Elige un ejercicio y pulsa Empezar", 16, Dim, dpi);
            dc.DrawText(t, new Point(area.Left + (area.Width - t.Width) / 2, area.Top + (area.Height - t.Height) / 2));
        }
    }

    private static FormattedText Text(string s, double size, Brush brush, double dpi) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush, dpi);

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
