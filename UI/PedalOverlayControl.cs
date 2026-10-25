using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SimTelemetry.UI;

/// <summary>
/// Dibuja la gráfica de acelerador/freno (historial de los últimos segundos), las columnas de cada pedal
/// y la marcha/velocidad. Se usa en el overlay y en la vista previa de la app.
/// </summary>
public sealed class PedalOverlayControl : FrameworkElement
{
    // Fijos, no siguen al tema: el overlay tiene que verse igual sobre cualquier juego.
    public static readonly Color ThrottleColor = Palette.OverlayThrottle;
    public static readonly Color BrakeColor = Palette.OverlayBrake;
    public static readonly Color ClutchColor = Palette.OverlayClutch;

    private static readonly Brush ThrottleBrush = Freeze(new SolidColorBrush(ThrottleColor));
    private static readonly Brush BrakeBrush = Freeze(new SolidColorBrush(BrakeColor));
    private static readonly Brush ClutchBrush = Freeze(new SolidColorBrush(ClutchColor));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Palette.OverlayText));
    private static readonly Brush DimTextBrush = Freeze(new SolidColorBrush(Palette.OverlayDim));
    private static readonly Brush BarBackBrush = Freeze(new SolidColorBrush(Palette.PedalTrack));
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Palette.PedalGrid), 1));
    private static readonly Pen ThrottlePen = Freeze(new Pen(ThrottleBrush, 2.2) { LineJoin = PenLineJoin.Round });
    private static readonly Pen BrakePen = Freeze(new Pen(BrakeBrush, 2.2) { LineJoin = PenLineJoin.Round });
    private static readonly Pen ClutchPen = Freeze(new Pen(ClutchBrush, 1.4) { LineJoin = PenLineJoin.Round });
    private static readonly Typeface Face = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Condensed);
    private static readonly Typeface BoldFace = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Condensed);

    private readonly double[] _t = new double[PedalBuffer.Capacity];
    private readonly float[] _thr = new float[PedalBuffer.Capacity];
    private readonly float[] _brk = new float[PedalBuffer.Capacity];
    private readonly float[] _clu = new float[PedalBuffer.Capacity];
    private long _renderedVersion = -1;
    private SolidColorBrush? _bg;

    // Reloj propio a 30 fotogramas por segundo (y a 5 sin datos). Con CompositionTarget.Rendering WPF
    // redibujaba a 60 por segundo y, en la ventana transparente del overlay, cada fotograma costaba mucha CPU.
    private static readonly TimeSpan Animating = TimeSpan.FromMilliseconds(1000.0 / 30), Idle = TimeSpan.FromMilliseconds(200);
    private readonly System.Windows.Threading.DispatcherTimer _clock = new() { Interval = Idle };

    public PedalOverlayControl()
    {
        _clock.Tick += (_, _) => OnFrame();
        IsVisibleChanged += (_, _) => _clock.IsEnabled = IsVisible;
    }

    private bool NeedsFrames => IsVisible && Buffer != null && (DemoWhenIdle || Buffer.Now - Buffer.LastWrite < Settings.WindowSeconds + 1)
                                && Window.GetWindow(this)?.WindowState != WindowState.Minimized;

    private void OnFrame()
    {
        bool animate = NeedsFrames;
        var interval = animate ? Animating : Idle;
        if (_clock.Interval != interval) _clock.Interval = interval;
        if (animate || (Buffer != null && Buffer.Version != _renderedVersion)) InvalidateVisual();
    }

    public PedalBuffer? Buffer { get; set; }
    /// <summary>Diferencia en vivo con la mejor vuelta (s); NaN si no hay.</summary>
    public Func<double>? Delta { get; set; }
    /// <summary>Última curva terminada y lo que ganaste o perdiste en ella.</summary>
    public Func<Engineer.SectorResult?>? Sector { get; set; }
    public OverlaySettings Settings { get; set; } = new();

    /// <summary>Muestra datos de ejemplo cuando no llega telemetría (útil al colocar el overlay).</summary>
    public bool DemoWhenIdle { get; set; }

    private bool HasLiveData => Buffer != null && Buffer.Now - Buffer.LastWrite < 1.5;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 30 || h < 30) return;
        var s = Settings;
        _renderedVersion = Buffer?.Version ?? -1;

        byte alpha = (byte)(Math.Clamp(s.BackgroundOpacity, 0, 1) * 255);
        if (_bg == null || _bg.Color.A != alpha) _bg = Freeze(new SolidColorBrush(Palette.Backdrop with { A = alpha }));
        dc.DrawRoundedRectangle(_bg, null, new Rect(0, 0, w, h), 2, 2);

        bool live = HasLiveData;
        bool demo = !live && DemoWhenIdle;
        double now = Buffer?.Now ?? 0;

        float thr, brk, clu, speed;
        int gear;
        if (demo)
        {
            (thr, brk, clu) = DemoPedals(now);
            gear = 3 + (int)(Math.Sin(now * 0.4) * 1.5);
            speed = 120 + (float)(Math.Sin(now * 0.7) * 40);
        }
        else if (!live)
        {
            thr = brk = clu = speed = 0;
            gear = 0;
        }
        else
        {
            thr = Buffer?.Throttle ?? 0;
            brk = Buffer?.Brake ?? 0;
            clu = Buffer?.Clutch ?? 0;
            gear = Buffer?.Gear ?? 0;
            speed = Buffer?.SpeedKmh ?? 0;
        }

        const double pad = 8;
        double top = pad, innerH = h - pad * 2, right = w - pad;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (s.ShowGearSpeed)
        {
            double gw = Math.Clamp(innerH * 0.62, 44, 90);
            DrawGear(dc, new Rect(right - gw, top, gw, innerH), gear, speed, demo || (live && Buffer?.HasVehicleData == true), dpi);
            right -= gw + 8;
        }

        if (s.ShowBars)
        {
            double bw = Math.Clamp(innerH * 0.17, 12, 28);
            var bars = s.ShowClutch
                ? new[] { (clu, ClutchBrush), (brk, BrakeBrush), (thr, ThrottleBrush) }
                : new[] { (brk, BrakeBrush), (thr, ThrottleBrush) };
            double total = bars.Length * bw + (bars.Length - 1) * 5;
            double x = right - total;
            foreach (var (value, brush) in bars)
            {
                DrawBar(dc, new Rect(x, top, bw, innerH), value, brush, dpi);
                x += bw + 5;
            }
            right -= total + 10;
        }

        if (s.ShowTrace && right - pad > 40)
        {
            var area = new Rect(pad, top, right - pad, innerH);
            foreach (double frac in new[] { 0.25, 0.5, 0.75 })
            {
                double y = area.Bottom - frac * area.Height;
                dc.DrawLine(GridPen, new Point(area.Left, y), new Point(area.Right, y));
            }

            if (demo) DrawDemoTrace(dc, area, now);
            else if (Buffer != null) DrawTrace(dc, area, now);
            double delta = demo ? Math.Sin(now * 0.3) * 0.4 : Delta?.Invoke() ?? double.NaN;
            if (s.ShowDelta && (live || demo) && double.IsFinite(delta))
            {
                double bottom = DrawDelta(dc, area, delta, dpi);
                // Al salir de cada curva, unos segundos: cuánto ganaste o perdiste en ella.
                var sector = demo ? new Engineer.SectorResult(3, -0.12, DateTime.UtcNow) : Sector?.Invoke();
                if (sector != null && (DateTime.UtcNow - sector.AtUtc).TotalSeconds < 4)
                    DrawSector(dc, area, bottom, sector, dpi);
            }

            if (!live && !demo)
            {
                var text = Text("Esperando telemetría…", 12, DimTextBrush, Face, dpi);
                dc.DrawText(text, new Point(area.Left + (area.Width - text.Width) / 2, area.Top + (area.Height - text.Height) / 2));
            }
        }
    }

    private void DrawTrace(DrawingContext dc, Rect area, double now)
    {
        double window = Math.Max(1, Settings.WindowSeconds);
        int n = Buffer!.Snapshot(now - window - 0.1, _t, _thr, _brk, _clu);
        if (n < 2) return;

        dc.PushClip(new RectangleGeometry(area));
        if (Settings.ShowClutch) dc.DrawGeometry(null, ClutchPen, Line(area, now, window, _clu, n));
        dc.DrawGeometry(null, ThrottlePen, Line(area, now, window, _thr, n));
        dc.DrawGeometry(null, BrakePen, Line(area, now, window, _brk, n));
        dc.Pop();
    }

    private StreamGeometry Line(Rect area, double now, double window, float[] values, int n)
    {
        var g = new StreamGeometry();
        // Más de ~1,5 puntos por píxel no se ven y cuestan CPU en cada fotograma (60 por segundo).
        int stride = Math.Max(1, (int)(n / Math.Max(1, area.Width * 1.5)));
        using (var ctx = g.Open())
        {
            for (int i = 0; i < n; i = i < n - 1 && i + stride > n - 1 ? n - 1 : i + stride)
            {
                var p = new Point(area.Right - (now - _t[i]) / window * area.Width,
                                  area.Bottom - Math.Clamp(values[i], 0, 1) * area.Height);
                if (i == 0) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, false);
            }
        }
        g.Freeze();
        return g;
    }

    /// <summary>Diferencia con la mejor vuelta arriba a la derecha de la gráfica: "-0.32" en verde (más rápido) o "+0.15" en rojo.</summary>
    private static double DrawDelta(DrawingContext dc, Rect area, double delta, double dpi)
    {
        var brush = delta <= 0 ? ThrottleBrush : BrakeBrush;
        var text = Text(delta.ToString("+0.00;-0.00", CultureInfo.InvariantCulture), Math.Clamp(area.Height * 0.2, 12, 26), brush, BoldFace, dpi);
        var box = new Rect(area.Right - text.Width - 10, area.Top + 2, text.Width + 8, text.Height + 2);
        dc.DrawRoundedRectangle(DeltaBack, null, box, 1, 1);
        dc.DrawText(text, new Point(box.X + 4, box.Y + 1));
        return box.Bottom;
    }

    /// <summary>"C3 -0.12": tiempo ganado (verde) o perdido (rojo) en la última curva.</summary>
    private static void DrawSector(DrawingContext dc, Rect area, double top, Engineer.SectorResult sector, double dpi)
    {
        var brush = sector.Delta <= 0 ? ThrottleBrush : BrakeBrush;
        var text = Text($"C{sector.Corner} {sector.Delta.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)}", Math.Clamp(area.Height * 0.14, 10, 18), brush, Face, dpi);
        var box = new Rect(area.Right - text.Width - 10, top + 3, text.Width + 8, text.Height + 2);
        dc.DrawRoundedRectangle(DeltaBack, null, box, 1, 1);
        dc.DrawText(text, new Point(box.X + 4, box.Y + 1));
    }

    private static readonly Brush DeltaBack = Freeze(new SolidColorBrush(Palette.BackdropLabel));

    private void DrawDemoTrace(DrawingContext dc, Rect area, double now)
    {
        double window = Math.Max(1, Settings.WindowSeconds);
        const int steps = 240;
        var series = new[] { (ThrottlePen, 0), (BrakePen, 1), (ClutchPen, 2) };
        dc.PushClip(new RectangleGeometry(area));
        foreach (var (pen, channel) in series)
        {
            if (channel == 2 && !Settings.ShowClutch) continue;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                for (int i = 0; i <= steps; i++)
                {
                    double t = now - window + window * i / steps;
                    var (a, b, c) = DemoPedals(t);
                    float v = channel == 0 ? a : channel == 1 ? b : c;
                    var p = new Point(area.Left + area.Width * i / steps, area.Bottom - v * area.Height);
                    if (i == 0) ctx.BeginFigure(p, false, false); else ctx.LineTo(p, true, false);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
        dc.Pop();
    }

    private static (float thr, float brk, float clu) DemoPedals(double t)
    {
        double phase = Math.Sin(t * 1.1);
        float thr = (float)Math.Clamp(phase * 1.6 + 0.35, 0, 1);
        float brk = (float)Math.Clamp(-phase * 1.8 - 0.55, 0, 1);
        float clu = (float)Math.Clamp(Math.Sin(t * 2.3) * 3 - 2.4, 0, 1);
        return (thr, brk, clu);
    }

    private static void DrawBar(DrawingContext dc, Rect r, float value, Brush brush, double dpi)
    {
        double labelH = Math.Clamp(r.Width * 0.75, 11, 16);
        var barRect = new Rect(r.X, r.Y, r.Width, Math.Max(4, r.Height - labelH - 2));
        dc.DrawRoundedRectangle(BarBackBrush, null, barRect, 1, 1);
        double fillH = barRect.Height * Math.Clamp(value, 0, 1);
        if (fillH > 0.5)
            dc.DrawRoundedRectangle(brush, null, new Rect(barRect.X, barRect.Bottom - fillH, barRect.Width, fillH), 1, 1);

        var text = Text(((int)Math.Round(value * 100)).ToString(CultureInfo.InvariantCulture), labelH * 0.72, TextBrush, Face, dpi);
        dc.DrawText(text, new Point(r.X + (r.Width - text.Width) / 2, r.Bottom - text.Height));
    }

    private static void DrawGear(DrawingContext dc, Rect r, int gear, float speed, bool hasData, double dpi)
    {
        string g = !hasData ? "-" : gear < 0 ? "R" : gear == 0 ? "N" : gear.ToString(CultureInfo.InvariantCulture);
        var gearText = Text(g, Math.Min(r.Height * 0.55, r.Width * 0.8), TextBrush, BoldFace, dpi);
        var speedText = Text(hasData ? $"{speed:0}" : "--", Math.Min(r.Height * 0.2, 20), TextBrush, Face, dpi);
        var unitText = Text("km/h", Math.Min(r.Height * 0.11, 11), DimTextBrush, Face, dpi);

        double totalH = gearText.Height + speedText.Height + unitText.Height - 6;
        double y = r.Y + (r.Height - totalH) / 2;
        dc.DrawText(gearText, new Point(r.X + (r.Width - gearText.Width) / 2, y));
        y += gearText.Height - 6;
        dc.DrawText(speedText, new Point(r.X + (r.Width - speedText.Width) / 2, y));
        y += speedText.Height - 2;
        dc.DrawText(unitText, new Point(r.X + (r.Width - unitText.Width) / 2, y));
    }

    private static FormattedText Text(string s, double size, Brush brush, Typeface face, double dpi) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, Math.Max(6, size), brush, dpi);

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
