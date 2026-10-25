using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SimTelemetry.Analysis;
using SimTelemetry.Diagnostics;
using SimTelemetry.Recording;
using SimTelemetry.Tracks;
using SimTelemetry.UI;

namespace SimTelemetry;

/// <summary>
/// Pestaña "Circuitos": guía de cada circuito o tramo con lo aprendido de tus vueltas (puntos de frenada,
/// vuelta ideal, qué coche te va mejor) y la información de la biblioteca.
/// </summary>
public partial class MainWindow
{
    private bool _tracksDirty = true;
    private int _trackViewVersion;

    private sealed class TrackItem
    {
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string? Game { get; init; }
        public string? Track { get; init; }
        public TrackInfo? Info { get; init; }
        public List<LapInfo> Laps { get; init; } = new();
        public string Key => $"{Game}|{Track}";
    }

    private static bool IsRally(string game) => game.Contains("DiRT") || game.Contains("WRC");

    /// <summary>El circuito de la biblioteca para un juego y pista (por nombre o porque lo asociaste tú).</summary>
    private TrackInfo? FindTrack(string game, string track) =>
        _app.Settings.TrackLinks.TryGetValue($"{game}|{track}", out var name) ? TrackDatabase.ByName(name) : TrackDatabase.Find(track);

    private void RefreshTracks()
    {
        _tracksDirty = false;
        string? selectedKey = (TrackList.SelectedItem as TrackItem)?.Key;
        var laps = LapRecorder.ListLaps(_app.DataFolder);
        var recorded = laps
            .Where(l => !l.TimeBased && l.Track.Length > 0 && InProfile(l))
            .GroupBy(l => (l.Game, l.Track))
            .Select(g =>
            {
                var info = FindTrack(g.Key.Game, g.Key.Track);
                var best = g.Where(l => l.Completed && l.Valid).MinBy(l => l.LapTime);
                return new TrackItem
                {
                    Title = info?.Name ?? g.Key.Track,
                    Subtitle = $"{g.Key.Game} · {g.Count()} vuelta(s)" + (best != null ? $" · mejor {best.TimeText}" : ""),
                    Game = g.Key.Game,
                    Track = g.Key.Track,
                    Info = info,
                    Laps = g.ToList(),
                };
            })
            .OrderByDescending(i => i.Laps.Max(l => l.Date))
            .ToList();
        var profile = _app.Profile;
        var library = TrackDatabase.All
            .Where(t => recorded.All(r => r.Info != t) && (profile == null || TrackDatabase.InGame(t, profile)))
            .OrderBy(t => t.Name)
            .Select(t => new TrackItem { Title = t.Name, Subtitle = $"Biblioteca · {t.Country} · {t.LengthKm:0.0##} km", Info = t });
        var items = recorded.Concat(library).ToList();
        TrackList.ItemsSource = items;
        TrackList.SelectedItem = items.FirstOrDefault(i => i.Key == selectedKey) ?? items.FirstOrDefault();
    }

#if DEBUG
    /// <summary>Para las capturas de prueba: el circuito con más vueltas.</summary>
    internal void SelectBusiestTrack()
    {
        if (TrackList.ItemsSource is List<TrackItem> items && items.Count > 0)
            TrackList.SelectedItem = items.MaxBy(i => i.Laps.Count);
    }
#endif

    private void OnTrackSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TrackList.SelectedItem is not TrackItem item) return;
        int version = ++_trackViewVersion;
        if (item.Laps.Count == 0)
        {
            TrackHost.Content = BuildTrackView(item, null);
            return;
        }
        TrackHost.Content = GuideContent.Para("Analizando tus vueltas…");
        Task.Run(() =>
        {
            TrackGuide? guide = null;
            try { guide = TrackGuide.Build(item.Laps, l => LapFile.Read(l.FilePath)); }
            catch (Exception ex) { Log.Error("No se pudo analizar el circuito", ex); }
            Dispatcher.BeginInvoke(() =>
            {
                if (version == _trackViewVersion) TrackHost.Content = BuildTrackView(item, guide);
            });
        });
    }

    private UIElement BuildTrackView(TrackItem item, TrackGuide? guide)
    {
        var info = item.Info;
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = (info?.Name ?? item.Track ?? "").ToUpper(), Style = (Style)FindResource("Figure"), FontSize = 30 });
        var facts = new List<string>();
        if (item.Game != null) facts.Add(item.Game);
        if (info != null) facts.Add($"{info.Country} · {info.LengthKm:0.0##} km · {info.Turns} curvas");
        root.Children.Add(new TextBlock { Text = string.Join(" · ", facts), Style = (Style)FindResource("DimText"), Margin = new Thickness(0, 2, 0, 12) });

        if (guide?.Reference != null) AddLearned(root, item, guide);
        else if (item.Laps.Count > 0)
            root.Children.Add(GuideContent.Card("Tu referencia",
                GuideContent.Para(IsRally(item.Game ?? "")
                    ? "Todavía no tienes una pasada completa por este tramo. Termina el tramo y la app aprenderá tus puntos de frenada."
                    : "Todavía no tienes una vuelta completa y válida aquí. Haz una vuelta limpia, empezando en la línea de meta, y la app aprenderá tus puntos de frenada.")));

        var advice = item.Game != null && item.Track != null ? CarAdviceBook.ForTrack(item.Game, item.Track).ToList() : new List<CarAdvice>();
        if (advice.Count > 0)
            root.Children.Add(GuideContent.Card("Coche recomendado aquí (fuentes)", advice.Select(AdviceBlock).ToArray()));
        if (info != null) AddLibrary(root, info, item.Game);
        else if (item.Game != null && !IsRally(item.Game)) root.Children.Add(BuildLinkCard(item));
        if (item.Game != null && IsRally(item.Game)) root.Children.Add(RallyCard());
        return root;
    }

    private void AddLearned(StackPanel root, TrackItem item, TrackGuide g)
    {
        var r = g.Reference!;
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var map = new TrackMapControl { Height = 340, Margin = new Thickness(0, 0, 12, 12) };
        map.SetData(g.ReferenceData, null, null, g.ReferenceStats, null);
        map.Labels = g.Corners.Select(c => (c.Cue.BrakeAt, c.Cue.Corner.ToString())).ToList();
        grid.Children.Add(map);

        var rows = new List<(string, string)>
        {
            ("Tu mejor vuelta", $"{r.TimeText}{(r.Car.Length > 0 ? " · " + Tracks.CarCatalog.DisplayName(r.Car) : "")} · {r.Date:dd/MM/yyyy}"),
        };
        if (g.IdealLap > 0)
        {
            double gain = r.LapTime - g.IdealLap;
            rows.Add(("Vuelta ideal", gain > 0.05
                ? $"{LapInfo.FormatLapTime(g.IdealLap)}: juntando tus mejores tramos ganarías {gain:0.00} s"
                : $"{LapInfo.FormatLapTime(g.IdealLap)}: tu mejor vuelta ya junta casi todos tus mejores tramos"));
        }
        rows.Add(("Vueltas válidas", $"{g.ValidLaps} con este coche"));
        rows.Add(("Frenadas", $"{g.Corners.Count} (numeradas en el mapa; las mismas que canta el Ingeniero)"));
        var summary = GuideContent.Card("Tu referencia", GuideContent.Table(rows.ToArray()),
            GuideContent.Para("La trazada del mapa es la de tu mejor vuelta: es tu línea de referencia. Compárala con otras vueltas en la pestaña Análisis."));
        Grid.SetColumn(summary, 1);
        grid.Children.Add(summary);
        root.Children.Add(grid);

        if (g.Corners.Count > 0)
            root.Children.Add(GuideContent.Card("Puntos de frenada (de tu mejor vuelta)",
                GuideContent.Table(g.Corners.Select(c =>
                {
                    var q = c.Cue;
                    string text = $"Frena a los {q.BrakeAt:0} m ({c.ApexDistance - q.BrakeAt:0} m antes del vértice) · {q.Intensity} ({q.Peak:P0}) · " +
                                  $"{q.EntrySpeed:0} → {q.MinSpeed:0} km/h" + (q.Gear > 0 ? $" · {q.Gear}ª" : "");
                    if (!double.IsNaN(c.Spread))
                        text += c.Spread <= 10 ? $" · muy constante (±{c.Spread:0} m)" : $" · varías ±{c.Spread:0} m: busca una referencia fija";
                    if (q.Potential >= 5)
                        text += $" · tu coche permitiría frenar ~{q.Potential:0} m más tarde";
                    return ($"Curva {q.Corner}", text);
                }).ToArray()),
                GuideContent.Para("La distancia se cuenta desde la línea de meta (o desde la salida del tramo). En pista, busca una referencia visual en ese punto: un cartel, una marca o el inicio de un piano."),
                GuideContent.Para("«Tu coche permitiría frenar más tarde»: la app mide en tus vueltas cuánto frena tu coche en recta con el freno a fondo (lo que ya has conseguido, no un valor teórico) y calcula dónde podrías empezar a frenar para llegar igual de lento al punto de giro. Ve acercándote poco a poco: el Ingeniero puede pedírtelo con el objetivo progresivo.")));

        if (g.Cars.Count > 0)
        {
            var best = g.Cars.FirstOrDefault(c => c.Best > 0);
            var carRows = g.Cars.Select(c => (
                (c.Car.Length > 0 ? c.Car : "(sin nombre)") + (c == best && g.Cars.Count > 1 ? "  ★" : ""),
                c.Best > 0 ? $"mejor {LapInfo.FormatLapTime(c.Best)} · {c.ValidLaps} válidas de {c.Laps}" : $"{c.Laps} vuelta(s), ninguna completa y válida")).ToArray();
            string advice = g.Cars.Count == 1
                ? "Sólo has corrido aquí con un coche. Prueba otros: la app compara tus tiempos y te dirá con cuál eres más rápido."
                : best != null ? $"Para ti, el más rápido aquí es {best.Car} (★). Si vas a correr en este circuito, empieza por ese." : "";
            root.Children.Add(GuideContent.Card("Tus coches en este circuito", GuideContent.Table(carRows), GuideContent.Para(advice)));
        }
    }

    private static void AddLibrary(StackPanel root, TrackInfo info, string? game)
    {
        root.Children.Add(GuideContent.Card("Cómo es", GuideContent.Para(info.Character)));
        root.Children.Add(GuideContent.Card("Curvas clave", GuideContent.Table(info.Corners.Select(c => (c.Name, c.Tip)).ToArray())));
        var rows = new List<(string, string)>
        {
            ("Dónde adelantar", info.Overtaking),
            ("Reglaje", info.Setup),
            ("Qué coche conviene", info.CarChoice),
        };
        if (info.AccGt3Pace != null && (game == null || game.Contains("Competizione")))
            rows.Add(("Ritmo rápido en ACC", $"{info.AccGt3Pace} con un GT3 en seco (orientativo; varía con el coche y el parche)"));
        root.Children.Add(GuideContent.Card("Estrategia y reglaje", GuideContent.Table(rows.ToArray())));
    }

    /// <summary>Para juegos que no dicen el nombre del circuito (Forza da sólo un número): asociarlo a uno de la biblioteca.</summary>
    private UIElement BuildLinkCard(TrackItem item)
    {
        var list = new ListBox { Width = 300, MaxHeight = 220, ItemsSource = TrackDatabase.All.Select(t => t.Name).OrderBy(n => n).ToList(), Margin = new Thickness(0, 0, 8, 0) };
        var button = new Button { Content = "Asociar", VerticalAlignment = VerticalAlignment.Top };
        button.Click += (_, _) =>
        {
            if (list.SelectedItem is not string name) return;
            _app.Settings.TrackLinks[item.Key] = name;
            _app.Settings.Save();
            RefreshTracks();
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(list);
        row.Children.Add(button);
        return GuideContent.Card("¿Qué circuito es?",
            GuideContent.Para($"El juego lo llama «{item.Track}». Si es uno de la biblioteca, asócialo para ver su información aquí."),
            row);
    }

    private static UIElement RallyCard() => GuideContent.Card("En rally",
        GuideContent.Bullets(
            "La referencia es tu mejor pasada completa por este mismo tramo: el Ingeniero te canta esas frenadas como si fueran notas.",
            "Escucha a tu copiloto antes que a los pitidos: las notas del juego mandan; los avisos sólo te recuerdan dónde frenaste la otra vez.",
            "En tierra frena antes y más en línea recta; en asfalto puedes frenar más tarde y girando.",
            "Freno con el pie izquierdo para cargar el morro en curvas rápidas sin perder revoluciones."));
}
