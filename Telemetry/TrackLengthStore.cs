using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SimTelemetry.Telemetry;

/// <summary>
/// Longitudes de circuito medidas por la propia app (algunos juegos, como ACC, no la publican).
/// Se guardan en %APPDATA%\SimTelemetry\tracklengths.json.
/// </summary>
public static class TrackLengthStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, double>? _lengths;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimTelemetry", "tracklengths.json");

    private static string Key(string game, string track) => (game + "|" + track).ToLowerInvariant();

    public static double Get(string game, string track)
    {
        lock (Gate)
        {
            Load();
            return _lengths!.TryGetValue(Key(game, track), out var v) ? v : 0;
        }
    }

    /// <summary>Registra una medida (media móvil para suavizar diferencias de trazada).</summary>
    public static double Report(string game, string track, double measured)
    {
        if (measured < 500 || measured > 30000 || string.IsNullOrEmpty(track)) return Get(game, track);
        lock (Gate)
        {
            Load();
            string key = Key(game, track);
            double value = _lengths!.TryGetValue(key, out var old) && Math.Abs(old - measured) < old * 0.05
                ? old * 0.7 + measured * 0.3
                : measured;
            _lengths[key] = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_lengths, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (IOException)
            {
            }
            return value;
        }
    }

    private static void Load()
    {
        if (_lengths != null) return;
        try
        {
            _lengths = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch (Exception)
        {
            _lengths = new();
        }
    }
}
