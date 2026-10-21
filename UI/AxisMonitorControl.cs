using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SimTelemetry.Input;

namespace SimTelemetry.UI;

/// <summary>Muestra en directo cada eje de cada volante / pedalera conectado, marcando los pedales asignados.</summary>
public sealed class AxisMonitorControl : FrameworkElement
{
    private const double RowH = 17, HeaderH = 22, LabelW = 118;
    // Pinceles del tema actual (se dibuja sobre la tarjeta): se rehacen al cambiar de tema.
    private static Brush TextBrush = null!, Dim = null!, BarBack = null!, BarFill = null!, ClutchBrush = null!, SteerBrush = null!, BrakeBrush = null!, ThrottleBrush = null!;

    static AxisMonitorControl()
    {
        LoadColors();
        Palette.Changed += LoadColors;
    }

    private static void LoadColors()
    {
        TextBrush = Frozen(new SolidColorBrush(Palette.Text));
        Dim = Frozen(new SolidColorBrush(Palette.Dim));
        BarBack = Frozen(new SolidColorBrush(Palette.Line));
        BarFill = Frozen(new SolidColorBrush(Palette.Info));
        ClutchBrush = Frozen(new SolidColorBrush(Palette.Clutch));
        SteerBrush = Frozen(new SolidColorBrush(Palette.Steering));
        BrakeBrush = Frozen(new SolidColorBrush(Palette.Brake));
        ThrottleBrush = Frozen(new SolidColorBrush(Palette.Throttle));
    }
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private IReadOnlyList<WheelDeviceState> _devices = Array.Empty<WheelDeviceState>();
    private PedalMapping _mapping = new();

    public void Update(IReadOnlyList<WheelDeviceState> devices, PedalMapping mapping)
    {
        bool sizeChanged = Rows(devices) != Rows(_devices);
        _devices = devices;
        _mapping = mapping;
        if (sizeChanged) InvalidateMeasure();
        InvalidateVisual();
    }

    private static int Rows(IReadOnlyList<WheelDeviceState> devices)
    {
        int rows = 0;
        foreach (var d in devices)
            foreach (var n in d.AxisNames)
                if (n != null) rows++;
        return rows;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(0, _devices.Count == 0 ? 24 : _devices.Count * HeaderH + Rows(_devices) * RowH + 4);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, y = 0;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_devices.Count == 0)
        {
            dc.DrawText(Text("Ningún volante o pedal conectado", 12, Dim, Face, dpi), new Point(0, 4));
            return;
        }

        foreach (var d in _devices)
        {
            dc.DrawText(Text((d.IsWheel ? "🎮 " : "🕹 ") + d.Name, 12, TextBrush, Bold, dpi, w), new Point(0, y + 2));
            y += HeaderH;
            for (int a = 0; a < WheelInput.AxisCount; a++)
            {
                if (d.AxisNames[a] == null) continue;
                var (role, brush) = Role(d, a);
                string label = d.AxisNames[a]! + (role != null ? $" · {role}" : "");
                dc.DrawText(Text(label, 11, role != null ? brush : Dim, Face, dpi, LabelW - 6), new Point(0, y + 1));
                var bar = new Rect(LabelW, y + 4, Math.Max(10, w - LabelW), RowH - 8);
                dc.DrawRoundedRectangle(BarBack, null, bar, 3, 3);
                double f = Math.Clamp(d.Values[a] / 65535.0, 0, 1);
                dc.DrawRoundedRectangle(role != null ? brush : BarFill, null, new Rect(bar.X, bar.Y, bar.Width * f, bar.Height), 3, 3);
                y += RowH;
            }
        }
    }

    private (string? Role, Brush Brush) Role(WheelDeviceState d, int axis)
    {
        bool Is(PedalAxis p) => p.IsSet && p.DeviceId == d.Id && p.Axis == axis;
        if (Is(_mapping.Brake)) return ("freno", BrakeBrush);
        if (Is(_mapping.Throttle)) return ("acelerador", ThrottleBrush);
        if (Is(_mapping.Clutch)) return ("embrague", ClutchBrush);
        if (axis == d.SteeringAxis) return ("volante", SteerBrush);
        return (null, BarFill);
    }

    private static FormattedText Text(string s, double size, Brush brush, Typeface face, double dpi, double maxWidth = 0)
    {
        var t = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, dpi);
        if (maxWidth > 0)
        {
            t.MaxTextWidth = maxWidth;
            t.MaxLineCount = 1;
            t.Trimming = TextTrimming.CharacterEllipsis;
        }
        return t;
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
