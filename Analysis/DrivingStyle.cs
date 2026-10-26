using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimTelemetry.Recording;
using SimTelemetry.Tracks;

namespace SimTelemetry.Analysis;

/// <summary>
/// Tu estilo de manejo sacado de tus vueltas (no de opiniones): cómo frenas, giras y aceleras,
/// y con qué coches rindes mejor comparando sólo lo comparable.
/// </summary>
public static class DrivingStyle
{
    /// <summary>Un rasgo de tu estilo; <see cref="Axis"/> y <see cref="Score"/> (0–100) son su eje en el gráfico de atributos.</summary>
    public sealed record Trait(string Name, string Value, string Meaning, bool Good)
    {
        public string Axis { get; init; } = "";
        public double Score { get; init; } = double.NaN;
    }
    public sealed record CarLine(string Game, string Car, string Category, int Laps, double BrakeScore, double Spread, double InvalidShare);
    /// <summary>Mejor vuelta válida de un coche en un circuito (la entrada de <see cref="PaceVerdicts"/>).</summary>
    public sealed record Best(string Game, string Track, string Category, string Car, double LapTime);

    public sealed class Report
    {
        public int Laps;
        public string Headline = "";
        public List<Trait> Traits = new();
        /// <summary>Media de los atributos (0–100).</summary>
        public double Overall = double.NaN;
        public List<CarLine> Cars = new();
        public List<string> Verdicts = new();
    }

    public static Report Build(IReadOnlyList<LapInfo> all)
    {
        var report = new Report();
        var laps = all.Where(l => !l.TimeBased && l.Completed && l.LapTime > 0).ToList();
        var valid = laps.Where(l => l.Valid).ToList();

        // shortcut: el estilo sale de tus 40 vueltas válidas más recientes (tu forma de conducir de ahora); si hiciera falta más historia, ampliar.
        var recent = Stats(valid.OrderByDescending(l => l.Date).Take(40));
        report.Laps = recent.Count;
        if (recent.Count >= 3)
        {
            (report.Headline, report.Traits) = Traits(recent);
            var scored = report.Traits.Where(t => !double.IsNaN(t.Score)).ToList();
            if (scored.Count > 0) report.Overall = scored.Average(t => t.Score);
        }

        foreach (var car in laps.GroupBy(l => (l.Game, l.Car)).Where(g => g.Key.Car.Length > 0))
        {
            var carValid = car.Where(l => l.Valid).ToList();
            if (carValid.Count < 3) continue;
            var st = Stats(carValid.OrderByDescending(l => l.Date).Take(15));
            report.Cars.Add(new CarLine(car.Key.Game, car.Key.Car, CarCatalog.Category(car.Key.Game, car.Key.Car), carValid.Count,
                st.Where(s => s.Stats.BrakeZones.Count > 0).Select(s => s.Stats.AvgBrakeScore).DefaultIfEmpty(0).Average(),
                Spread(carValid), 1 - (double)carValid.Count / car.Count()));
        }
        report.Cars = report.Cars.OrderByDescending(c => c.Laps).ToList();

        var bests = valid.Where(l => l.Track.Length > 0 && l.Car.Length > 0)
            .GroupBy(l => (l.Game, l.Track, l.Car))
            .Select(g => new Best(g.Key.Game, g.Key.Track, CarCatalog.Category(g.Key.Game, g.Key.Car), g.Key.Car, g.Min(l => l.LapTime)))
            .ToList();
        report.Verdicts.AddRange(PaceVerdicts(bests));
        report.Verdicts.AddRange(TechniqueVerdicts(report.Cars));
        return report;
    }

    private static List<(LapInfo Info, LapStats Stats)> Stats(IEnumerable<LapInfo> laps)
    {
        var list = new List<(LapInfo, LapStats)>();
        foreach (var l in laps)
        {
            try { list.Add((l, ProgressAnalyzer.CachedStats(l.FilePath))); }
            catch (IOException) { }
            catch (FormatException) { } // vuelta dañada: se ignora
        }
        return list;
    }

    /// <summary>Constancia: desviación de tus vueltas buenas (hasta un 7 % más lentas que tu mejor) en % de la mejor, por coche y circuito.</summary>
    private static double Spread(List<LapInfo> laps)
    {
        double sum = 0;
        int n = 0;
        foreach (var g in laps.GroupBy(l => l.Track))
        {
            double best = g.Min(l => l.LapTime);
            var times = g.Select(l => l.LapTime).Where(t => t <= best * 1.07).ToList();
            if (times.Count < 3) continue;
            double avg = times.Average();
            double sd = Math.Sqrt(times.Sum(t => (t - avg) * (t - avg)) / (times.Count - 1));
            sum += sd / best * 100 * times.Count;
            n += times.Count;
        }
        return n > 0 ? sum / n : double.NaN;
    }

    private static (string, List<Trait>) Traits(List<(LapInfo Info, LapStats Stats)> laps)
    {
        var braking = laps.Where(l => l.Stats.BrakeZones.Count > 0).ToList();
        double attack = braking.Count > 0 ? braking.Average(l => l.Stats.AvgTimeToPeak) : double.NaN;
        double peak = braking.Count > 0 ? braking.Average(l => l.Stats.AvgPeakBrake) : double.NaN;
        double minPeak = braking.Count > 0 ? braking.Average(l => BrakeIdeals.MinPeak(l.Info.Abs)) : BrakeIdeals.PeakWithAbs;
        int turns = laps.Sum(l => l.Stats.TurnZones);
        double trail = turns > 0 ? (double)laps.Sum(l => l.Stats.TrailBrakingZones) / turns : double.NaN;
        double coasting = laps.Average(l => l.Stats.CoastingPct);
        double overlap = laps.Average(l => l.Stats.OverlapPct);
        double corrections = laps.Average(l => l.Stats.SteeringCorrectionsPerKm);
        double abrupt = laps.Average(l => (double)l.Stats.AbruptReleases);

        var t = new List<Trait>();
        if (!double.IsNaN(attack))
            t.Add(new("Ataque al freno", $"{attack:0.00} s hasta la presión máxima", attack <= BrakeIdeals.MaxTimeToPeak
                ? "Rápido y decidido: así se aprovecha la frenada."
                : $"Progresivo: llegas tarde a la presión máxima y frenas más metros de los necesarios (ideal ≤ {BrakeIdeals.MaxTimeToPeak:0.00} s).", attack <= BrakeIdeals.MaxTimeToPeak));
        if (!double.IsNaN(peak))
            t.Add(new("Presión de frenada", $"{peak:P0} de media", peak >= minPeak - 0.02
                ? "Aprovechas el freno."
                : $"Te quedas corto: al empezar a frenar pisa hasta ~{minPeak:P0}.", peak >= minPeak - 0.02));
        if (!double.IsNaN(trail))
            t.Add(new("Trail braking", $"{trail:P0} de las curvas con freno", trail >= 0.6
                ? "Entras soltando el freno poco a poco mientras giras: el coche rota mejor."
                : trail >= 0.3 ? "A veces. Llevar algo de freno al empezar a girar ayudará a que el coche entre."
                : "Casi nunca: sueltas el freno antes de girar y el coche entra peor.", trail >= 0.6));
        t.Add(new("Inercia sin pedales", $"{coasting:0.0} % de la vuelta", coasting <= 4
            ? "Enlazas bien freno y acelerador." : "Mucho tiempo sin pedales: vuelve al acelerador antes, aunque sea parcial.", coasting <= 4));
        t.Add(new("Acelerador y freno a la vez", $"{overlap:0.0} % de la vuelta", overlap <= 2
            ? "Casi nunca, bien." : "A menudo: pisar los dos a la vez gasta tiempo y frenos.", overlap <= 2));
        t.Add(new("Correcciones de volante", $"{corrections:0.0} por km", corrections <= 12
            ? "Manos suaves." : "Muchas: el coche te pide correcciones; suaviza la entrada en curva.", corrections <= 12));
        t.Add(new("Sueltas bruscas del freno", $"{abrupt:0.0} por vuelta", abrupt <= 1
            ? "Sueltas el freno con suavidad." : "Sueltas el freno de golpe: el coche se descompensa al entrar.", abrupt <= 1));

        double spread = Spread(laps.Select(l => l.Info).ToList());
        if (!double.IsNaN(spread))
            t.Add(new("Constancia", $"±{spread:0.0} % entre tus vueltas buenas", spread <= 1
                ? "Muy regular vuelta a vuelta." : "Tus tiempos varían bastante: fíjate referencias de frenada (carteles, pianos).", spread <= 1));

        // Atributos 0–100 para el gráfico: 100 en el valor ideal y 0 en uno claramente malo (los umbrales son los de los consejos).
        var axes = new Dictionary<string, (string Axis, double Score)>
        {
            ["Ataque al freno"] = ("ATAQUE", Score(attack, BrakeIdeals.MaxTimeToPeak, 0.60)),
            ["Presión de frenada"] = ("PRESIÓN", Score(peak, minPeak, minPeak - 0.35)),
            ["Trail braking"] = ("TRAIL BRAKING", Score(trail, 0.8, 0)),
            ["Inercia sin pedales"] = ("FLUIDEZ", Score(coasting, 1, 12)),
            ["Acelerador y freno a la vez"] = ("PRECISIÓN", Score(overlap, 0.5, 8)),
            ["Correcciones de volante"] = ("SUAVIDAD", Score(corrections, 3, 30)),
            ["Sueltas bruscas del freno"] = ("CONTROL", Score(abrupt, 0, 4)),
            ["Constancia"] = ("CONSTANCIA", Score(spread, 0.4, 3)),
        };
        t = t.Select(x => axes.TryGetValue(x.Name, out var a) ? x with { Axis = a.Axis, Score = a.Score } : x).ToList();

        string headline = (double.IsNaN(attack) || attack <= BrakeIdeals.MaxTimeToPeak ? "Frenas con decisión" : "Frenas de forma progresiva") +
                          (double.IsNaN(trail) ? "" : trail >= 0.6 ? ", con mucho trail braking" : trail >= 0.3 ? ", con algo de trail braking" : ", soltando el freno antes de girar") +
                          (corrections <= 12 ? " y con manos suaves." : " y corrigiendo mucho el volante.");
        return (headline, t);
    }

    /// <summary>De un valor a 0–100: 100 en <paramref name="best"/> (o mejor), 0 en <paramref name="worst"/> (o peor). Sirve en los dos sentidos.</summary>
    public static double Score(double value, double best, double worst) =>
        double.IsNaN(value) || best == worst ? double.NaN : Math.Clamp((value - worst) / (best - worst), 0, 1) * 100;

    /// <summary>
    /// Ritmo comparando sólo lo comparable: coches de la misma categoría en el mismo circuito (un GT4 siempre es más lento que un GT3).
    /// </summary>
    public static List<string> PaceVerdicts(IEnumerable<Best> bests)
    {
        var verdicts = new List<string>();
        foreach (var g in bests.Where(b => b.Category.Length > 0).GroupBy(b => (b.Game, b.Track, b.Category))
                     .Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).Take(4))
        {
            var ordered = g.OrderBy(b => b.LapTime).ToList();
            var top = ordered[0];
            var second = ordered[1];
            double gap = (second.LapTime - top.LapTime) / top.LapTime * 100;
            verdicts.Add($"En {TrackName(top.Track)} ({top.Category}) eres un {gap:0.0} % más rápido con el {CarCatalog.DisplayName(top.Car)} " +
                         $"({LapInfo.FormatLapTime(top.LapTime)}) que con el {CarCatalog.DisplayName(second.Car)} ({LapInfo.FormatLapTime(second.LapTime)}).");
        }
        return verdicts;
    }

    private static IEnumerable<string> TechniqueVerdicts(List<CarLine> cars)
    {
        var enough = cars.Where(c => c.Laps >= 5 && !double.IsNaN(c.Spread)).ToList();
        if (enough.Count < 2) yield break;
        var brakes = enough.OrderByDescending(c => c.BrakeScore).ToList();
        if (brakes[0].BrakeScore - brakes[^1].BrakeScore >= 5)
            yield return $"Frenas mejor con el {CarCatalog.DisplayName(brakes[0].Car)} (nota {brakes[0].BrakeScore:0}) que con el {CarCatalog.DisplayName(brakes[^1].Car)} ({brakes[^1].BrakeScore:0}).";
        var steady = enough.OrderBy(c => c.Spread).ToList();
        if (steady[^1].Spread - steady[0].Spread >= 0.3)
            yield return $"Eres más constante con el {CarCatalog.DisplayName(steady[0].Car)} (±{steady[0].Spread:0.0} % entre vueltas) que con el {CarCatalog.DisplayName(steady[^1].Car)} (±{steady[^1].Spread:0.0} %).";
    }

    private static string TrackName(string track) => TrackDatabase.Find(track)?.Name ?? track;
}
