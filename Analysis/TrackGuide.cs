using System;
using System.Collections.Generic;
using System.Linq;
using SimTelemetry.Engineer;
using SimTelemetry.Recording;

namespace SimTelemetry.Analysis;

/// <summary>Una curva de tu guía del circuito: el punto de frenada de tu mejor vuelta y lo constante que eres.</summary>
public sealed class CornerGuide
{
    public required BrakeCue Cue { get; init; }
    /// <summary>Distancia del punto más lento (vértice aproximado).</summary>
    public double ApexDistance { get; init; }
    /// <summary>Desviación típica (m) de tu punto de frenada en el resto de vueltas; NaN si no hay suficientes.</summary>
    public double Spread { get; init; } = double.NaN;
}

public sealed record CarSummary(string Car, double Best, int Laps, int ValidLaps);

/// <summary>Lo aprendido de tus vueltas en un circuito o tramo: referencia, frenadas, vuelta ideal y coches.</summary>
public sealed class TrackGuide
{
    public LapInfo? Reference { get; init; }
    public LapData? ReferenceData { get; init; }
    public LapStats? ReferenceStats { get; init; }
    public List<CornerGuide> Corners { get; init; } = new();
    /// <summary>Suma de tus mejores tramos con ese coche (0 si no hay vueltas completas).</summary>
    public double IdealLap { get; init; }
    public int ValidLaps { get; init; }
    public List<CarSummary> Cars { get; init; } = new();

    /// <summary>
    /// Construye la guía de un circuito con las vueltas de ese juego y circuito.
    /// <paramref name="car"/> elige el coche de referencia (null = el de la vuelta más reciente).
    /// </summary>
    public static TrackGuide Build(IReadOnlyList<LapInfo> laps, Func<LapInfo, LapData> load, string? car = null)
    {
        static bool Good(LapInfo l) => l.Completed && l.Valid && !l.TimeBased && l.LapTime > 0;

        var cars = laps.GroupBy(l => l.Car)
            .Select(g => new CarSummary(g.Key, g.Where(Good).Select(l => l.LapTime).DefaultIfEmpty(0).Min(), g.Count(), g.Count(Good)))
            .OrderBy(c => c.Best <= 0).ThenBy(c => c.Best)
            .ToList();

        car ??= laps.OrderByDescending(l => l.Date).FirstOrDefault()?.Car;
        var mine = laps.Where(l => Good(l) && l.Car == car).OrderBy(l => l.LapTime).ToList();
        if (mine.Count == 0) mine = laps.Where(Good).OrderBy(l => l.LapTime).ToList();
        if (mine.Count == 0) return new TrackGuide { Cars = cars };

        var reference = mine[0];
        var refData = load(reference);
        var refStats = LapAnalyzer.Compute(refData);

        // Otras vueltas (como mucho las 15 mejores): constancia, deceleración del coche y vuelta ideal.
        var others = mine.Skip(1).Take(15).Select(l =>
        {
            try { return load(l); }
            catch (Exception) { return null; }
        }).Where(d => d != null && d.Count > 1).Cast<LapData>().ToList();
        var cues = BrakePlan.Build(refStats, others.Select(LapAnalyzer.Compute).ToList(), DecelModel.Build(others.Prepend(refData)), progressive: false);
        var corners = cues.Select(c => new CornerGuide
        {
            Cue = c,
            ApexDistance = refStats.BrakeZones.First(z => z.StartDistance == c.RefBrakeAt).MinSpeedDistance,
            Spread = c.Spread,
        }).ToList();

        return new TrackGuide
        {
            Reference = reference,
            ReferenceData = refData,
            ReferenceStats = refStats,
            Corners = corners,
            IdealLap = ComputeIdealLap(others.Prepend(refData).ToList()),
            ValidLaps = mine.Count,
            Cars = cars,
        };
    }

    /// <summary>Vuelta ideal: para cada uno de 20 tramos iguales, tu tiempo más rápido en cualquiera de las vueltas.</summary>
    public static double ComputeIdealLap(IReadOnlyList<LapData> laps, int segments = 20)
    {
        laps = laps.Where(l => l.Count > 1).ToList();
        if (laps.Count == 0) return 0;
        double length = laps.Min(l => l.Distance[^1]);
        if (length <= 100) return 0;
        double total = 0;
        for (int s = 0; s < segments; s++)
        {
            double from = length * s / segments, to = length * (s + 1) / segments;
            total += laps.Min(l => LapAnalyzer.TimeAt(l, to) - LapAnalyzer.TimeAt(l, from));
        }
        // Lo que falte hasta la meta (si las vueltas no terminan a la misma distancia) sale de la mejor vuelta.
        var best = laps.MinBy(l => l.Time[^1] - l.Time[0])!;
        return total + (best.Time[^1] - LapAnalyzer.TimeAt(best, length)) + (LapAnalyzer.TimeAt(best, 0) - best.Time[0]);
    }

}
