using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SimTelemetry.Tracks;

/// <summary>Nombre legible y categoría de un coche a partir de lo que envía el juego.</summary>
public static class CarCatalog
{
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["amr"] = "Aston Martin", ["bmw"] = "BMW", ["mclaren"] = "McLaren", ["ktm"] = "KTM", ["amg"] = "AMG",
        ["ii"] = "II", ["lms"] = "LMS", ["nsx"] = "NSX", ["rc"] = "RC", ["cs"] = "CS", ["st"] = "ST", ["xbow"] = "X-Bow",
    };

    /// <summary>"porsche_991ii_gt3_r" → "Porsche 991 II GT3 R". Los nombres que ya tienen espacios se dejan igual.</summary>
    public static string DisplayName(string car)
    {
        if (string.IsNullOrWhiteSpace(car)) return car;
        if (car.Contains(' ')) return Regex.Replace(car, @"\s*\([^)]+\)\s*$", ""); // EA WRC: la clase ya se muestra aparte
        var parts = car.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w =>
        {
            if (Words.TryGetValue(w, out var known)) return known;
            var m = Regex.Match(w, @"^(\d+)(ii)$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value + " II";
            if (w.Any(char.IsDigit) || w.Length <= 2) return w.ToUpperInvariant(); // gt3, 720s, m4, r8, r
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w);
        });
        return string.Join(" ", parts).Replace("Mercedes AMG", "Mercedes-AMG");
    }

    /// <summary>Categoría (GT3, GT4, WRC2…), o "" si el juego no da información para saberla.</summary>
    public static string Category(string game, string car)
    {
        if (string.IsNullOrWhiteSpace(car)) return "";
        // EA WRC: "Citroën C3 Rally2 (WRC2)" (la clase viene de readme/ids.json).
        var cls = Regex.Match(car, @"\(([^)]+)\)\s*$");
        if (cls.Success) return cls.Groups[1].Value;
        if (!game.Contains("Competizione")) return "";
        string c = car.ToLowerInvariant();
        return c.Contains("cup") ? "Porsche Cup"
             : c.Contains("challenge") ? "Ferrari Challenge"
             : c.Contains("huracan_st") ? "Super Trofeo"
             : c.Contains("m2_cs") ? "TCX"
             : c.Contains("gt2") || c.Contains("935") ? "GT2"
             : c.Contains("gt4") ? "GT4"
             : "GT3"; // el resto de coches de ACC son GT3 (audi_r8_lms, jaguar_g3, lamborghini_gallardo_rex…)
    }
}
