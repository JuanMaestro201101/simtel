using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace SimTelemetry.UI;

/// <summary>
/// Todos los colores de la app, con los nombres del sistema de diseño.
/// Los de la ventana dependen del tema (oscuro o claro) y del color elegido en Apariencia: <see cref="Apply"/> los calcula
/// y los publica como pinceles con el mismo nombre ("Bg", "Text"…), que los XAML usan con DynamicResource.
/// Lo que se dibuja sobre el juego no cambia nunca. (La página del teléfono, Remote/PhonePage.cs, lleva su propia copia en CSS.)
/// </summary>
public static class Palette
{
    /// <summary>Colores para elegir en Apariencia ("" = el original: amarillo de señalización sobre negro carbono).</summary>
    public static readonly (string Name, string Hex)[] Choices =
    {
        ("Original", ""), ("Naranja", "#FF8A3D"), ("Rojo", "#FF5263"), ("Rosa", "#F062A8"),
        ("Morado", "#A77DFF"), ("Azul", "#4DA3FF"), ("Turquesa", "#2DD4BF"), ("Verde", "#3FCF6E"),
    };

    public static bool Light { get; private set; }

    // Superficies: ventana → tarjeta → lo que va dentro de una tarjeta
    public static Color Bg { get; private set; }
    public static Color Panel { get; private set; }
    public static Color PanelAlt { get; private set; }
    public static Color Line { get; private set; }
    public static Color Hover { get; private set; }
    public static Color Pressed { get; private set; }
    public static Color ListHover { get; private set; }
    public static Color AccentSoft { get; private set; }
    public static Color AccentTint { get; private set; }

    // Texto y estado
    public static Color Text { get; private set; }
    public static Color Dim { get; private set; }
    public static Color Off { get; private set; } // ≥3:1 sobre PanelAlt: un punto que significa algo pide 3:1
    public static Color Accent { get; private set; }
    /// <summary>Texto o marca sobre un relleno de color (acento, verde, ámbar o rojo).</summary>
    public static Color OnFill { get; private set; }
    public static Color Good { get; private set; }
    public static Color Bad { get; private set; }
    /// <summary>Aviso y objetivo (la banda amarilla del Entrenador): no sigue al color elegido, para que signifique siempre lo mismo.</summary>
    public static Color Warn { get; private set; }
    public static Color Info { get; private set; }
    /// <summary>La referencia, que por defecto es tu mejor vuelta: morado, como el mejor tiempo en una pantalla de tiempos.</summary>
    public static Color Reference { get; private set; }
    /// <summary>Fondo de las etiquetas en color de referencia (p. ej. «referencia automática: tu mejor vuelta»).</summary>
    public static Color ReferenceTint { get; private set; }

    // Pedales y series de las gráficas
    public static Color Throttle { get; private set; }
    public static Color Brake { get; private set; }
    public static Color Clutch { get; private set; }
    public static Color LapMine { get; private set; }
    public static Color Steering { get; private set; }
    public static Color Gear { get; private set; }

    // Gráficas, mapa y Entrenador
    public static Color ChartBg { get; private set; }
    public static Color ChartPane { get; private set; }
    public static Color ChartText { get; private set; }
    public static Color ChartDim { get; private set; }
    public static Color ChartGrid { get; private set; }
    public static Color ChartZero { get; private set; }
    public static Color ChartCursor { get; private set; }
    public static Color ChartTooltip { get; private set; }
    public static Color ChartHighlight { get; private set; }
    public static Color TrackLine { get; private set; }
    public static Color TrainerBand { get; private set; }
    public static Color TrainerTrack { get; private set; }
    public static Color TrainerCursor { get; private set; }

    // Lo que se dibuja sobre el juego (y el icono de la app): igual con cualquier tema, tiene que verse sobre cualquier juego
    public static readonly Color OverlayAccent = Color.FromRgb(0xFF, 0xD6, 0x0A);
    public static readonly Color OverlayThrottle = Color.FromRgb(0x34, 0xD0, 0x58);
    public static readonly Color OverlayBrake = Color.FromRgb(0xFF, 0x4D, 0x4D);
    public static readonly Color OverlayClutch = Color.FromRgb(0x4D, 0xA3, 0xFF);
    /// <summary>Fondo del icono de la app y de la vista previa del overlay en En vivo.</summary>
    public static readonly Color OverlayPanel = Color.FromRgb(0x17, 0x1B, 0x22);
    public static readonly Color Backdrop = Color.FromRgb(0x0C, 0x0E, 0x12);
    public static readonly Color BackdropLabel = Color.FromArgb(0xC0, 0x0C, 0x0E, 0x12);
    public static readonly Color OverlayText = Color.FromRgb(0xF2, 0xF4, 0xF8);
    public static readonly Color OverlayDim = Color.FromArgb(0xB0, 0xC8, 0xCE, 0xD8);
    public static readonly Color PedalTrack = Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF);
    public static readonly Color PedalGrid = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
    public static readonly Color InfoBg = Color.FromArgb(0xE6, 0x12, 0x15, 0x1B);
    public static readonly Color InfoBorder = Color.FromArgb(0x80, 0xFF, 0xD6, 0x0A);
    public static readonly Color InfoDim = Color.FromRgb(0xA8, 0xB1, 0xBF);
    public static readonly Color EditHint = Color.FromArgb(0xD0, 0xFF, 0xD6, 0x0A);
    public static readonly Color EditHintText = Color.FromRgb(0x11, 0x11, 0x11);

    /// <summary>Tras <see cref="Apply"/>: lo que se dibuja en código (gráficas, mapa, entrenador) rehace sus pinceles.</summary>
    public static event Action? Changed;

    static Palette() => Compute(light: false, color: "");

    /// <summary>Aplica el tema ("dark" o "light") y el color ("" = original, o "#RRGGBB") a toda la ventana.</summary>
    public static void Apply(string theme, string color)
    {
        Compute(theme == "light", color);
        var resources = Application.Current.Resources;
        foreach (var p in typeof(Palette).GetProperties(BindingFlags.Public | BindingFlags.Static))
            if (p.PropertyType == typeof(Color))
                resources[p.Name] = Frozen(new SolidColorBrush((Color)p.GetValue(null)!));
        Changed?.Invoke();
    }

    private static void Compute(bool light, string color)
    {
        Light = light;
        Color? chosen = Parse(color);
        // El original es negro carbono casi neutro; un color elegido tiñe el fondo con su tono.
        var (h, s) = chosen is Color c ? (ToHsl(c).H, light ? 0.40 : 0.45) : (220.0, 0.09);
        if (light)
        {
            Bg = Hsl(h, s, 0.935); Panel = Hsl(h, s * 0.8, 0.99); PanelAlt = Hsl(h, s, 0.955);
            Line = Hsl(h, s * 0.7, 0.85); Hover = Hsl(h, s, 0.90); Pressed = Hsl(h, s, 0.85); ListHover = Hsl(h, s, 0.95);
            Text = Hsl(h, s * 0.9, 0.12); Dim = Hsl(h, s * 0.4, 0.37); Off = Hsl(h, s * 0.3, 0.52);
        }
        else
        {
            Bg = Hsl(h, s, 0.045); Panel = Hsl(h, s * 0.92, 0.072); PanelAlt = Hsl(h, s * 0.9, 0.11);
            Line = Hsl(h, s * 0.8, 0.165); Hover = Hsl(h, s * 0.85, 0.17); Pressed = Hsl(h, s * 0.8, 0.22); ListHover = Hsl(h, s, 0.11);
            Text = Hsl(h, Math.Min(1, s * 1.4), 0.925); Dim = Hsl(h, s * 0.65, 0.60); Off = Hsl(h, s * 0.5, 0.50);
        }
        // El acento también se usa como texto (enlaces, notas): 4,5:1 sobre todas las superficies.
        Accent = Readable(chosen ?? OverlayAccent, 4.5, Bg, Panel, PanelAlt);
        AccentSoft = Mix(PanelAlt, Accent, light ? 0.14 : 0.16);
        AccentTint = Mix(Panel, Accent, light ? 0.08 : 0.10);
        OnFill = light ? Colors.White : Color.FromRgb(0x11, 0x11, 0x11);

        // Mismos significados en los dos temas; en el claro, tonos más oscuros para que se lean sobre blanco.
        // Colores de la pantalla de tiempos: verde mejora, amarillo más lento, morado el mejor (la referencia).
        Good = Rgb(light ? 0x1A7F37 : 0x2FD46E);
        Bad = Rgb(light ? 0xCF222E : 0xFF453A);
        Warn = Rgb(light ? 0x9A6700 : 0xFFD60A);
        Info = Rgb(light ? 0x0550AE : 0x7AB8FF);
        Reference = Readable(Rgb(light ? 0x7C3AED : 0xB57EFF), 4.5, Bg, Panel, PanelAlt);
        ReferenceTint = Mix(Panel, Reference, light ? 0.10 : 0.16);
        Throttle = Good;
        Brake = Bad;
        Clutch = Rgb(light ? 0x0969DA : 0x4DA3FF);
        LapMine = Rgb(light ? 0x1F2328 : 0xE6EDF3);
        Steering = Rgb(light ? 0xBF3989 : 0xF472B6);
        Gear = Rgb(light ? 0x1B7C83 : 0x8BD5CA);

        ChartBg = Mix(Bg, Panel, 0.4);
        ChartPane = Panel;
        ChartText = Mix(Text, Dim, 0.25);
        ChartDim = Dim;
        ChartGrid = Dim with { A = 0x40 };
        ChartZero = Dim with { A = 0x90 };
        ChartCursor = Text with { A = 0xC0 };
        ChartTooltip = light ? Panel with { A = 0xF2 } : Color.FromArgb(0xE6, 0x0C, 0x0E, 0x12);
        ChartHighlight = Accent with { A = 0x26 };
        TrackLine = Line;
        TrainerBand = light ? Color.FromArgb(0x50, 0xE3, 0xB3, 0x41) : Warn with { A = 0x40 };
        TrainerTrack = Text with { A = 0x40 };
        TrainerCursor = Text with { A = 0xB0 };
    }

    private static Color? Parse(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return null; } // ajustes editados a mano: se queda el original
    }

    /// <summary>Aclara (fondos oscuros) u oscurece (claros) el color hasta llegar al contraste pedido con todos los fondos.</summary>
    public static Color Readable(Color c, double ratio, params Color[] backgrounds)
    {
        var (h, s, l) = ToHsl(c);
        double step = Luminance(backgrounds[0]) < 0.2 ? 0.01 : -0.01;
        for (int i = 0; i < 100 && backgrounds.Min(b => Contrast(c, b)) < ratio; i++)
        {
            l = Math.Clamp(l + step, 0, 1);
            c = Hsl(h, s, l);
        }
        return c;
    }

    /// <summary>Contraste WCAG entre dos colores (1 a 21).</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Ch(byte v) => v / 255.0 <= 0.03928 ? v / 255.0 / 12.92 : Math.Pow((v / 255.0 + 0.055) / 1.055, 2.4);
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static Color Rgb(int hex) => Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);

    private static Color Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-9) return (0, 0, l);
        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h < 0 ? h + 360 : h, s, l);
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
