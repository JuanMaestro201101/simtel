using System;
using System.Collections.Generic;
using System.Linq;
using SimTelemetry.Recording;
using SimTelemetry.Tracks;

namespace SimTelemetry.Analysis;

/// <summary>Lo que el asesor sabe y recomienda para la sesión actual (circuito, categoría y coche).</summary>
public sealed class SessionAdvice
{
    public string Game { get; init; } = "";
    public string Track { get; init; } = "";
    public string Car { get; init; } = "";
    public string Category { get; init; } = "";
    public TrackInfo? Info { get; init; }
    public bool IsRally { get; init; }
    /// <summary>Superficie del rally (EA WRC), si se conoce.</summary>
    public (string Surface, string Tip)? Rally { get; init; }
    /// <summary>Una frase con la recomendación principal.</summary>
    public string Headline { get; init; } = "";
    /// <summary>Tus coches de esta categoría en este circuito, del más rápido al más lento (mejor vuelta válida).</summary>
    public List<(string Car, double Best)> Ranking { get; init; } = new();
    public double BestWithCar { get; init; }

    public string Title => string.Join(" · ", new[] { Track, Category, Car }.Where(s => s.Length > 0));
}

public static class SessionAdvisor
{
    public static SessionAdvice Build(string game, string track, string car, IReadOnlyList<LapInfo> allLaps)
    {
        var info = TrackDatabase.Find(track);
        string category = CarCatalog.Category(game, car);
        var here = allLaps.Where(l => l.Game == game && l.Track == track && l.Completed && l.Valid && !l.TimeBased && l.LapTime > 0).ToList();
        var ranking = here
            .Where(l => category.Length == 0 || CarCatalog.Category(game, l.Car) == category)
            .GroupBy(l => l.Car)
            .Select(g => (Car: g.Key, Best: g.Min(l => l.LapTime)))
            .OrderBy(r => r.Best)
            .ToList();
        double mine = ranking.FirstOrDefault(r => r.Car == car).Best;
        string noun = category.Length > 0 ? category : "coche";
        string Time(double t) => LapInfo.FormatLapTime(t);

        bool rally = game.Contains("DiRT") || game.Contains("WRC");
        string headline;
        if (ranking.Count == 0)
            headline = (category.Length > 0 ? $"Primera vez aquí en {category}. " : "Primera vez aquí. ")
                       + (rally ? "Termina el tramo una vez para tener referencia." : info?.CarChoice ?? "Haz unas vueltas limpias para tener referencia.");
        else if (ranking[0].Car == car)
            headline = ranking.Count > 1
                ? $"Llevas tu {noun} más rápido aquí ({Time(mine)}), {ranking[1].Best - mine:0.0} s mejor que con el {CarCatalog.DisplayName(ranking[1].Car)}."
                : $"Tu mejor vuelta aquí con este coche: {Time(mine)}. Prueba otros {(category.Length > 0 ? category : "coches")} para comparar.";
        else if (mine > 0)
            headline = $"Con el {CarCatalog.DisplayName(ranking[0].Car)} fuiste {mine - ranking[0].Best:0.0} s más rápido aquí ({Time(ranking[0].Best)} frente a {Time(mine)}).";
        else
            headline = $"Tu {noun} más rápido aquí es el {CarCatalog.DisplayName(ranking[0].Car)} ({Time(ranking[0].Best)}). Con este coche aún no tienes tiempo.";

        return new SessionAdvice
        {
            Game = game,
            Track = info?.Name ?? track,
            Car = CarCatalog.DisplayName(car),
            Category = category,
            Info = info,
            Rally = rally ? TrackDatabase.Rally(track) : null,
            IsRally = rally,
            Headline = headline,
            Ranking = ranking,
            BestWithCar = mine,
        };
    }
}
