using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SimTelemetry.Analysis;
using SimTelemetry.Diagnostics;
using SimTelemetry.Recording;
using SimTelemetry.Telemetry;
using SimTelemetry.Tracks;
using SimTelemetry.UI;

namespace SimTelemetry;

/// <summary>Pestaña "Coches": tu estilo de manejo, con qué coches rindes mejor (tus datos) y qué recomiendan las fuentes (con fecha).</summary>
public partial class MainWindow
{
    private bool _carsDirty = true;
    private int _carsVersion;

    private void RefreshCars()
    {
        _carsDirty = false;
        int version = ++_carsVersion;
        var laps = LapRecorder.ListLaps(_app.DataFolder).Where(InProfile).ToList();
        CarsHost.Content = new TextBlock { Text = "Analizando tus vueltas…", Style = (Style)FindResource("DimText") };
        Task.Run(() =>
        {
            DrivingStyle.Report? report = null;
            try { report = DrivingStyle.Build(laps); }
            catch (Exception ex) { Log.Error("Coches: error al analizar el estilo", ex); }
            Dispatcher.BeginInvoke(() =>
            {
                if (version == _carsVersion) CarsHost.Content = BuildCarsView(report);
            });
        });
    }

    private UIElement BuildCarsView(DrivingStyle.Report? report)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel();
        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);

        // Tu estilo
        if (report == null || report.Traits.Count == 0)
            left.Children.Add(GuideContent.Card("Tu estilo de manejo", GuideContent.Para(
                "Todavía no hay suficientes vueltas válidas (hacen falta 3) para describir tu estilo. Conduce unas vueltas limpias y vuelve aquí.")));
        else
        {
            var headline = new TextBlock { Text = report.Headline, Style = (Style)FindResource("Figure"), FontSize = 22, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            if (!double.IsNaN(report.Overall))
            {
                var overall = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
                overall.Children.Add(new TextBlock { Text = $"{report.Overall:0}", Style = (Style)FindResource("Figure"), FontSize = 44, Foreground = Res("Accent"), HorizontalAlignment = HorizontalAlignment.Right });
                overall.Children.Add(new TextBlock { Text = "GLOBAL", Style = (Style)FindResource("H2"), Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right });
                DockPanel.SetDock(overall, Dock.Right);
                top.Children.Add(overall);
            }
            top.Children.Add(headline);
            var radar = new RadarChart
            {
                Height = 330, Margin = new Thickness(0, 0, 0, 8),
                Axes = report.Traits.Where(t => !double.IsNaN(t.Score)).Select(t => (t.Axis, t.Score)).ToList(),
            };
            left.Children.Add(GuideContent.Card("Tu estilo de manejo", top, radar,
                GuideContent.Table(report.Traits.Select(t => (t.Name, $"{(t.Good ? "✔" : "▲")}  {t.Value}. {t.Meaning}")).ToArray()),
                GuideContent.Para($"Sale de tus {report.Laps} vueltas válidas más recientes{(_app.Profile is { } p ? $" en {p.Name}" : "")}. " +
                                  "Cada atributo va de 0 a 100: 100 es el valor ideal de la técnica y 0 uno claramente mejorable.")));
        }

        // Con qué coches rindes mejor
        var mine = new List<UIElement>();
        if (report != null && report.Verdicts.Count > 0) mine.Add(GuideContent.Bullets(report.Verdicts.ToArray()));
        else mine.Add(GuideContent.Para("Para compararlos necesito vueltas válidas con al menos dos coches: de la misma categoría en el mismo circuito para el ritmo, " +
                                        "o 5 vueltas con cada uno para la técnica."));
        if (report != null && report.Cars.Count > 0)
            mine.Add(GuideContent.Table(report.Cars.Take(8).Select(c => (CarCatalog.DisplayName(c.Car) + (c.Category.Length > 0 ? $" ({c.Category})" : ""),
                $"{c.Laps} vueltas válidas · frenada {c.BrakeScore:0}/100" + (double.IsNaN(c.Spread) ? "" : $" · constancia ±{c.Spread:0.0} %") +
                $" · {c.InvalidShare:P0} inválidas")).ToArray()));
        mine.Add(GuideContent.Para("Sólo comparo el ritmo entre coches de la misma categoría en el mismo circuito: un GT4 siempre será más lento que un GT3, y eso no dice nada de ti."));
        left.Children.Add(GuideContent.Card("Con qué coches rindes mejor (tus datos)", mine.ToArray()));

        // Fuentes externas
        var profiles = _app.Profile is { } prof ? new[] { prof.Key } : new[] { "acc", "forza", "dirt", "wrc" };
        foreach (var key in profiles)
        {
            var advice = CarAdviceBook.ForProfile(key).ToList();
            if (advice.Count == 0) continue;
            var items = new List<UIElement>();
            if (key is "dirt" or "wrc")
                items.Add(GuideContent.Para("No hay análisis medidos de qué coche es más rápido en cada tramo: lo que sigue son opiniones de jugadores. " +
                                            "Para competir por ranking, filtra la clasificación del tramo por coche en el juego y mira con cuál se hacen los mejores tiempos; " +
                                            "en «Con qué coches rindes mejor» verás con cuál vas tú más rápido."));
            if (key == "forza")
                items.Add(GuideContent.Para("En Forza el reglaje y el PI cambian mucho el resultado: tómalo como punto de partida."));
            foreach (var a in advice) items.Add(AdviceBlock(a));
            right.Children.Add(GuideContent.Card($"Qué dicen las fuentes · {GameProfiles.ByKey(key)?.Name ?? key}", items.ToArray()));
        }
        if (right.Children.Count == 0)
            right.Children.Add(GuideContent.Card("Qué dicen las fuentes", GuideContent.Para("No tengo recomendaciones con fuente para este juego.")));
        return grid;
    }

    /// <summary>Una recomendación: para qué, qué coche, por qué y de dónde sale (enlace).</summary>
    private UIElement AdviceBlock(CarAdvice a)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 10) };
        panel.Children.Add(new TextBlock { Text = a.Scope.ToUpper(), Style = (Style)FindResource("H2"), Margin = new Thickness(0) });
        panel.Children.Add(new TextBlock { Text = a.Cars, Style = (Style)FindResource("Figure"), FontSize = 19, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = a.Why, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), LineHeight = 19 });
        panel.Children.Add(GuideContent.Link((a.Community ? "Opinión de jugadores · " : "Fuente: ") + $"{a.Source} ({a.Date})", a.Url));
        return panel;
    }
}
