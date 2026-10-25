using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SimTelemetry.UI;

/// <summary>Contenido de la pestaña "Aprender": técnica de frenada, línea de carrera, rally y plan de práctica.</summary>
public static class GuideContent
{
    public static UIElement Build()
    {
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var right = new StackPanel();

        left.Children.Add(Card("Parámetros ideales de una frenada",
            Para("Son los valores con los que APEX puntúa cada frenada en la pestaña Análisis. Úsalos como guía: cada coche y cada curva tienen matices."),
            Table(
                ("Ataque (de 0 a la presión máxima)", "≤ 0,2 s. Pisa con decisión: los metros en los que todavía no frenas a tope son metros perdidos."),
                ("Presión máxima con ABS", "90–100 %. En ACC (GT3/GT4) el ABS evita el bloqueo: frena fuerte de verdad."),
                ("Presión máxima sin ABS", "80–95 %, justo antes de bloquear. Fórmulas y muchos coches de iRacing; en GT de iRacing se recomienda ~85–90 % sin entrar en el ABS."),
                ("Freno al empezar a girar", "20–40 % (trail braking), bajando poco a poco hasta cerca del vértice."),
                ("Suelta del pedal", "Progresiva. Nunca de más del 40 % a 0 en menos de 0,15 s."),
                ("Acelerador y freno a la vez", "0 % (salvo freno con el pie izquierdo en rally)."),
                ("Inercia sin pedales", "Menos del 2 % de la vuelta."),
                ("Consistencia", "Vueltas a menos de 0,5 s entre sí antes de buscar más velocidad."))));

        left.Children.Add(Card("Trail braking (frenar girando)",
            Para("Frenas al máximo en línea recta, porque ahí todo el agarre del neumático puede usarse para frenar. Al empezar a girar no sueltas el freno de golpe: lo vas soltando poco a poco mientras metes volante, y lo terminas de soltar cerca del vértice."),
            Bullets(
                "En la telemetría se ve como una rampa suave hacia abajo que se solapa con el giro del volante, no como un escalón a cero.",
                "Un buen ejemplo: del pico baja hasta ~40 % al girar y se mantiene casi hasta el vértice.",
                "Si el coche subvira al entrar, suelta el freno un poco más despacio (más peso delante).",
                "Si sobrevira al entrar, suelta un poco antes o con menos presión.",
                "Error típico: soltar de golpe al girar. La suspensión delantera rebota, el morro se levanta y el coche no gira."),
            Para("Practícalo en el Entrenador con el ejercicio «Trail braking».")));

        left.Children.Add(Card("Acelerador",
            Bullets(
                "Empieza a abrir gas cuando el coche ya apunta a la salida (en o justo tras el vértice), de forma progresiva hasta el 100 %.",
                "Evita levantar el pie a mitad de curva: descoloca el coche. Si necesitas levantar, entraste demasiado rápido.",
                "En curvas largas usa gas parcial y estable para equilibrar el coche.",
                "Si el control de tracción salta mucho o la rueda patina, abre más despacio.",
                "El tiempo de inercia sin ningún pedal es tiempo perdido: enlaza la suelta del freno con el inicio del gas.")));

        left.Children.Add(Card("Ajustes de tus pedales",
            Bullets(
                "Calibra los pedales en cada juego y deja una zona muerta mínima (1–3 %) para que no frene sola.",
                "Con célula de carga, pon una fuerza máxima que alcances al 100 % sin levantarte del asiento: la presión se repite mejor que el recorrido.",
                "Fija bien el asiento y la base de los pedales; si se mueven, tu memoria muscular no sirve.",
                "Prueba de consistencia: 10 frenadas iguales. Si la presión varía mucho, baja un poco la fuerza máxima y repite.")));

        left.Children.Add(Card("Reglaje: qué tocar según lo que hace el coche",
            Para("Primero descarta la técnica (casi siempre es eso). Si el problema se repite en las mismas curvas, prueba estos cambios de uno en uno:"),
            Table(
                ("No gira al frenar (subviraje de entrada)", "Reparto de frenada un poco hacia atrás, barra delantera más blanda o más alerón delantero. Técnica: suelta el freno más despacio al girar."),
                ("No gira en el centro de la curva", "Más carga delante (alerón o splitter), barra delantera más blanda o trasera más dura. Revisa que los delanteros no estén sobrecalentados."),
                ("La cola se va al frenar", "Reparto de frenada hacia delante, menos freno motor (ACC: mapa de freno motor), barra trasera más blanda."),
                ("La cola se va al acelerar", "Acelera más progresivo, sube el control de tracción, más alerón trasero, detrás más blando y, si se escapa de golpe, menos bloqueo del diferencial al acelerar."),
                ("No sale bien (subviraje al acelerar)", "Barra trasera algo más dura o delantera más blanda; en tracción delantera, menos bloqueo del diferencial."),
                ("Bloqueas ruedas", "Menos fuerza de frenada o reparto hacia el eje que bloquea; con ABS, más nivel de ABS."),
                ("Rebota en pianos y baches", "Suspensión y amortiguación rápida más blandas y algo más de altura."),
                ("Tocas el corte en la recta", "Alarga la última marcha (desarrollos) o quita un poco de alerón.")),
            Para("En Forza: Ajustes del coche → barras estabilizadoras, muelles, amortiguación, aerodinámica, frenos y diferencial. En ACC: Reglaje → Mecánica (presiones, barras, frenos), Aerodinámica (alerón) y Electrónica (TC, ABS, mapa de motor).")));

        left.Children.Add(Card("Reglaje según el circuito y las condiciones",
            Table(
                ("Circuito rápido (Monza, Spa, Le Mans)", "Menos alerón, desarrollos largos y un coche estable en frenadas fuertes."),
                ("Circuito lento (Hungaroring, Zandvoort)", "Más alerón, tracción y desarrollos más cortos."),
                ("Pianos y baches (Zolder, Nordschleife)", "Suspensión más blanda y más altura."),
                ("Lluvia", "Más alerón, suspensión y barras más blandas, TC y ABS más altos y frenadas antes. En ACC el neumático de lluvia trabaja con más presión que el de seco (unos 30 psi)."),
                ("Pista caliente", "Las presiones suben más: sal con menos presión en frío. Vigila la temperatura de los frenos."),
                ("Pista fría o de noche", "Más presión en frío para llegar a la ventana y menos refrigeración de frenos."),
                ("Carrera larga", "Un coche estable y fácil de llevar, que cuide los neumáticos, vale más que el de la vuelta perfecta."),
                ("Rally: tierra / asfalto / nieve", "Tierra: suspensión blanda y alta. Asfalto: más dura y baja. Nieve: neumáticos de clavos y movimientos muy suaves.")),
            Para("En ACC, el Ingeniero te avisa si las presiones en caliente se salen de la ventana (26,0–27,0 psi en seco) y cuánto corregir en frío.")));

        right.Children.Add(Card("Las cuatro fases de una curva",
            Table(
                ("1. Punto de frenada", "Donde empiezas a frenar. Busca una referencia visual fija (cartel de distancia, mancha, piano)."),
                ("2. Punto de giro", "Donde empiezas a meter volante, normalmente desde el borde exterior de la pista."),
                ("3. Vértice", "El punto más interior de la curva. El piano interior suele marcarlo."),
                ("4. Salida", "Vuelves al borde exterior mientras aceleras a fondo.")),
            Para("La regla básica es fuera – dentro – fuera: usar todo el ancho de la pista hace la curva más abierta y permite más velocidad.")));

        right.Children.Add(Card("Línea de carrera",
            Bullets(
                "Línea geométrica: el arco más amplio, con el vértice en el centro de la curva. Es la más suave, pero no siempre la más rápida.",
                "Vértice tardío: giras un poco más tarde y tocas el interior pasado el centro. El coche se endereza antes y aceleras antes. Es la mejor opción en curvas lentas y horquillas antes de una recta.",
                "Vértice temprano: en curvas rápidas o cuando enlazan con otra curva.",
                "Prioriza la curva que da a la recta más larga: una buena salida se arrastra toda la recta.",
                "En curvas enlazadas (eses, chicanes), sacrifica la primera para salir bien de la última.",
                "Perder el vértice suele venir de no frenar lo suficiente o de bloquear al entrar."),
            Para("En la pestaña Análisis, el mapa compara tu trazada con la de referencia: en rojo dónde pierdes tiempo, en verde dónde ganas.")));

        right.Children.Add(Card("Rally (DiRT Rally · EA WRC)",
            Bullets(
                "Freno con el pie izquierdo: un toque de freno sin soltar el gas transfiere peso al eje delantero y ayuda a que el coche rote en la entrada.",
                "Freno de mano en horquillas cerradas; en coches de tracción trasera pisa el embrague a la vez para no calar.",
                "Scandinavian flick: levanta el gas, gira un instante hacia el lado contrario, toca el freno y gira con decisión hacia la curva. El péndulo de peso hace derrapar el coche hacia dentro.",
                "Sobre grava y nieve frena antes y más recto: hay menos agarre que en asfalto.",
                "Escucha las notas del copiloto y mira lejos; reacciona a la nota, no a lo que ya tienes delante.",
                "Inputs suaves: el piloto que menos mueve el volante suele ser el más rápido.",
                "En rally la constancia gana: terminar todos los tramos sin errores vale más que un tramo perfecto.")));

        right.Children.Add(Card("Plan de práctica (30–45 min)",
            Table(
                ("Calentamiento · 5 min", "Entrenador: «Presión exacta de freno» y «Trail braking»."),
                ("Tanda 1 · 10 min", "Suelta el freno justo en el punto de giro en todas las curvas."),
                ("Tanda 2 · 10 min", "Alarga la suelta hasta pasado el vértice."),
                ("Tanda 3 · 10 min", "Elige en cada curva el punto de suelta que mejor te funcione y revisa la telemetría."),
                ("Una curva · 3×7 vueltas", "Elige una sola curva y sube la velocidad de entrada poco a poco hasta empezar a fallar; vuelve un paso atrás."),
                ("Consistencia · 30 min", "Todas las vueltas a menos de 0,5 s entre sí. Empieza lento y ve subiendo.")),
            Para("Cambia una sola cosa cada vez y compara con tu vuelta de referencia: así sabrás qué funcionó.")));

        right.Children.Add(Card("Fuentes",
            Link("Fanatec · Sim racing braking technique", "https://fanatec.com/explorer/games/gaming-tips/sim-racing-braking-technique/"),
            Link("Driver61 · Trail braking en sim racing", "https://driver61.com/sim-racing/trail-braking-sim-racing/"),
            Link("Driver61 · La línea de carrera perfecta", "https://driver61.com/uni/racing-line/"),
            Link("SimXPro · Frenar con pedales de célula de carga", "https://simxpro.com/en-us/blogs/guides/sim-racing-braking-technique-on-load-cell-pedals-pressure-not-travel"),
            Link("Blayze · 5 ejercicios de sim racing", "https://blayze.io/blog/car-racing/5-sim-racing-drills-to-train-for-real-life-driving"),
            Link("Jalopnik · Cómo hacer el Scandinavian flick", "https://jalopnik.com/1829051043")));

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = grid };
    }

    internal static Border Card(string title, params UIElement[] content)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title.ToUpper(), Style = (Style)Application.Current.FindResource("H2") });
        foreach (var c in content) panel.Children.Add(c);
        return new Border { Style = (Style)Application.Current.FindResource("Card"), Child = panel };
    }

    internal static TextBlock Para(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.FindResource("DimText"),
        Margin = new Thickness(0, 2, 0, 8),
        LineHeight = 19,
    };

    internal static StackPanel Bullets(params string[] items)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var item in items)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 3) };
            var dot = new TextBlock { Text = "•", Foreground = (Brush)Application.Current.FindResource("Accent"), Margin = new Thickness(2, 0, 8, 0) };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
            panel.Children.Add(row);
        }
        return panel;
    }

    internal static Grid Table(params (string Key, string Value)[] rows)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock { Text = rows[i].Key, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 12, 3) };
            var value = new TextBlock { Text = rows[i].Value, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.FindResource("DimText"), Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(key, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(key);
            grid.Children.Add(value);
        }
        return grid;
    }

    private static TextBlock Link(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url), Foreground = (Brush)Application.Current.FindResource("Accent") };
        link.RequestNavigate += (_, e) =>
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        };
        return new TextBlock(link) { Margin = new Thickness(0, 2, 0, 4) };
    }
}
