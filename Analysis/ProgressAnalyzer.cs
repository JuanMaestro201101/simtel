using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimTelemetry.Recording;

namespace SimTelemetry.Analysis;

/// <summary>Resumen de una sesión para ver la evolución en un mismo circuito o tramo.</summary>
public sealed class SessionProgress
{
    public string Folder = "";
    public DateTime Date;
    public int Laps;
    public int CompletedLaps;
    public double Best;          // s, 0 si no hay vueltas completas
    public double Average;       // s, media de vueltas completas
    public double StdDev;        // s, desviación de vueltas completas (consistencia)
    public double FullThrottle;  // % medio
    public double Coasting;
    public double Overlap;
    public double PeakBrake;     // 0..1
    public double TimeToPeak;    // s
    public double Corrections;   // por km
}

public static class ProgressAnalyzer
{
    /// <summary>Clave que agrupa vueltas comparables: mismo juego y mismo circuito/tramo.</summary>
    public static string ComboKey(LapInfo l) => l.Game + "|" + l.Track;

    // Estadísticas ya calculadas de cada vuelta (las vueltas guardadas no cambian): abrir Progreso otra vez es instantáneo.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Written, LapStats Stats)> StatsCache = new();

    private static LapStats CachedStats(string path)
    {
        var written = File.GetLastWriteTimeUtc(path);
        if (StatsCache.TryGetValue(path, out var hit) && hit.Written == written) return hit.Stats;
        var stats = LapAnalyzer.Compute(LapFile.Read(path));
        StatsCache[path] = (written, stats);
        return stats;
    }

    public static List<SessionProgress> Build(IEnumerable<LapInfo> laps)
    {
        var result = new List<SessionProgress>();
        foreach (var session in laps.GroupBy(l => l.SessionFolder))
        {
            var all = session.ToList();
            var completed = all.Where(l => l.Completed && l.LapTime > 0).ToList();
            // Para la técnica usamos las vueltas completas; si no hay ninguna, todas.
            var forStats = completed.Count > 0 ? completed : all;

            var stats = new List<LapStats>();
            foreach (var lap in forStats)
            {
                try { stats.Add(CachedStats(lap.FilePath)); }
                catch (IOException) { }
            }

            var p = new SessionProgress
            {
                Folder = session.Key,
                Date = all.Min(l => l.Date),
                Laps = all.Count,
                CompletedLaps = completed.Count,
            };
            if (completed.Count > 0)
            {
                var times = completed.Select(l => l.LapTime).ToList();
                p.Best = times.Min();
                p.Average = times.Average();
                p.StdDev = times.Count > 1 ? Math.Sqrt(times.Sum(t => (t - p.Average) * (t - p.Average)) / (times.Count - 1)) : 0;
            }
            if (stats.Count > 0)
            {
                p.FullThrottle = stats.Average(s => s.FullThrottlePct);
                p.Coasting = stats.Average(s => s.CoastingPct);
                p.Overlap = stats.Average(s => s.OverlapPct);
                var braking = stats.Where(s => s.BrakeZones.Count > 0).ToList();
                if (braking.Count > 0)
                {
                    p.PeakBrake = braking.Average(s => s.AvgPeakBrake);
                    p.TimeToPeak = braking.Average(s => s.AvgTimeToPeak);
                }
                p.Corrections = stats.Average(s => s.SteeringCorrectionsPerKm);
            }
            result.Add(p);
        }
        return result.OrderBy(p => p.Date).ToList();
    }

    /// <summary>Conclusiones comparando la primera sesión con la última (y con la mejor marca).</summary>
    public static List<Tip> Summarize(IReadOnlyList<SessionProgress> sessions, bool trackKnown)
    {
        var tips = new List<Tip>();
        var timed = sessions.Where(s => s.Best > 0).ToList();

        if (!trackKnown)
            tips.Add(new Tip
            {
                Level = TipLevel.Info,
                Title = "Este juego no indica el circuito",
                Detail = "Las vueltas de distintos circuitos pueden estar mezcladas: compara los tiempos con cuidado.",
            });

        if (timed.Count == 0)
        {
            tips.Add(new Tip
            {
                Level = TipLevel.Info,
                Title = "Aún no hay vueltas completas aquí",
                Detail = "Completa al menos una vuelta (o un tramo entero) para empezar a medir tu progreso.",
            });
            return tips;
        }

        var record = timed.OrderBy(s => s.Best).First();
        tips.Add(new Tip
        {
            Level = TipLevel.Good,
            Title = $"Récord personal: {LapInfo.FormatLapTime(record.Best)}",
            Detail = $"Logrado el {record.Date:dd/MM/yyyy}. {timed.Sum(s => s.CompletedLaps)} vueltas completas en {sessions.Count} sesión(es).",
        });

        if (timed.Count < 2)
        {
            tips.Add(new Tip
            {
                Level = TipLevel.Info,
                Title = "Vuelve otro día para ver tu evolución",
                Detail = "Con dos o más sesiones aquí verás cómo cambian tus tiempos y tu técnica.",
            });
            return tips;
        }

        var first = timed[0];
        var last = timed[^1];

        double gain = first.Best - last.Best;
        tips.Add(new Tip
        {
            Level = gain > 0.05 ? TipLevel.Good : gain < -0.05 ? TipLevel.Warning : TipLevel.Info,
            Title = gain > 0.05 ? $"Mejor vuelta {gain:0.00} s más rápida que en tu primera sesión"
                  : gain < -0.05 ? $"Tu última sesión fue {-gain:0.00} s más lenta que la primera"
                  : "Tu mejor tiempo se mantiene estable",
            Detail = $"Primera sesión {LapInfo.FormatLapTime(first.Best)} → última {LapInfo.FormatLapTime(last.Best)}.",
        });

        if (first.CompletedLaps > 1 && last.CompletedLaps > 1)
        {
            double d = first.StdDev - last.StdDev;
            if (Math.Abs(d) > 0.1)
                tips.Add(new Tip
                {
                    Level = d > 0 ? TipLevel.Good : TipLevel.Warning,
                    Title = d > 0 ? "Eres más constante" : "Eres menos constante",
                    Detail = $"Variación entre vueltas: {first.StdDev:0.00} s → {last.StdDev:0.00} s. " +
                             (d > 0 ? "Repetir vueltas parecidas es la base para ir más rápido." : "Busca repetir las mismas referencias de frenada en cada vuelta antes de arriesgar más."),
                });
        }

        void Trend(string what, double a, double b, double threshold, bool lowerIsBetter, string unit, string advice, Problem problem)
        {
            double delta = b - a;
            if (Math.Abs(delta) < threshold) return;
            bool better = lowerIsBetter ? delta < 0 : delta > 0;
            tips.Add(new Tip
            {
                Level = better ? TipLevel.Good : TipLevel.Warning,
                Title = $"{what}: {a:0.#}{unit} → {b:0.#}{unit}",
                Detail = better ? "Vas por buen camino." : advice,
                Kind = better ? null : problem, // si empeora: por qué, cómo y qué ejercicio
            });
        }

        Trend("Inercia sin pedales", first.Coasting, last.Coasting, 1, true, " %",
            "Has vuelto a dejar el coche sin pedales más tiempo; enlaza freno y acelerador.", Problem.Coasting);
        Trend("Acelerador y freno a la vez", first.Overlap, last.Overlap, 1, true, " %",
            "Pisas los dos pedales más a menudo que antes.", Problem.Overlap);
        Trend("A fondo", first.FullThrottle, last.FullThrottle, 3, false, " %",
            "Vas a fondo menos tiempo que antes: revisa las salidas de curva.", Problem.LateThrottle);
        Trend("Presión máx. de freno", first.PeakBrake * 100, last.PeakBrake * 100, 5, false, " %",
            "Frenas con menos fuerza que antes: ataca el pedal con decisión.", Problem.LowPressure);

        return tips;
    }
}
