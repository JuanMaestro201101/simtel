using System;
using System.Linq;

namespace SimTelemetry.Telemetry;

/// <summary>Perfil de un juego: qué datos se muestran en la app y cómo habla el ingeniero (circuito o rally).</summary>
public sealed record GameProfile(string Key, string Name, bool Rally)
{
    /// <summary>¿Este nombre de juego (de la telemetría, de una vuelta o del proceso) es de este perfil?</summary>
    public bool Matches(string? game) => GameProfiles.For(game) == this;
}

public static class GameProfiles
{
    public static readonly GameProfile Acc = new("acc", "ACC", false);
    public static readonly GameProfile Ac = new("ac", "Assetto Corsa", false);
    public static readonly GameProfile IRacing = new("iracing", "iRacing", false);
    public static readonly GameProfile Forza = new("forza", "Forza", false);
    public static readonly GameProfile Dirt = new("dirt", "DiRT Rally", true);
    public static readonly GameProfile Wrc = new("wrc", "EA WRC", true);
    public static readonly GameProfile BeamNg = new("beamng", "BeamNG / LFS", false);

    public static readonly GameProfile[] All = { Acc, Ac, IRacing, Forza, Dirt, Wrc, BeamNg };

    /// <summary>Perfil de un juego por su nombre ("Assetto Corsa Competizione", "DiRT Rally / DiRT Rally 2.0"…); null si no se reconoce.</summary>
    public static GameProfile? For(string? game)
    {
        if (string.IsNullOrWhiteSpace(game)) return null;
        bool Has(string s) => game.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("Competizione")) return Acc;
        if (Has("Assetto Corsa")) return Ac;
        if (Has("iRacing")) return IRacing;
        if (Has("Forza")) return Forza;
        if (Has("DiRT")) return Dirt;
        if (Has("WRC")) return Wrc;
        if (Has("BeamNG") || Has("LFS") || Has("OutGauge")) return BeamNg;
        return null;
    }

    public static GameProfile? ByKey(string? key) => All.FirstOrDefault(p => p.Key == key);
}
