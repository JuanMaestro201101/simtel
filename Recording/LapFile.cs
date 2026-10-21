using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SimTelemetry.Recording;

/// <summary>Cabecera de una vuelta grabada (lo que se lista sin cargar todos los datos).</summary>
public sealed record LapInfo
{
    public string FilePath { get; init; } = "";
    public string Game { get; init; } = "";
    public string Track { get; init; } = "";
    public string Car { get; init; } = "";
    public int Number { get; init; }
    public double LapTime { get; init; }
    public bool Completed { get; init; }
    /// <summary>false si el juego invalidó la vuelta (límites de pista, atajo…).</summary>
    public bool Valid { get; init; } = true;
    /// <summary>1 = con ABS, 0 = sin ABS, -1 = desconocido.</summary>
    public int Abs { get; init; } = -1;
    public string EndReason { get; init; } = "";
    /// <summary>Sesión de pedales del volante: el eje X es el tiempo (no hay distancia ni velocidad).</summary>
    public bool TimeBased { get; init; }
    public DateTime Date { get; init; }
    public double Length { get; init; }
    public float MaxRpm { get; init; }

    public string SessionFolder => Path.GetDirectoryName(FilePath) ?? "";
    public string TimeText => FormatLapTime(LapTime);

    public static string FormatLapTime(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds)) return "--:--.---";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.fff") : t.ToString(@"m\:ss\.fff");
    }
}

/// <summary>Una vuelta completa en memoria, con los canales en arrays paralelos.</summary>
public sealed class LapData
{
    public LapInfo Info { get; init; } = new();
    public double[] Time = Array.Empty<double>();
    public double[] Distance = Array.Empty<double>();
    public double[] Speed = Array.Empty<double>();
    public double[] Throttle = Array.Empty<double>();
    public double[] Brake = Array.Empty<double>();
    public double[] Clutch = Array.Empty<double>();
    public double[] Steering = Array.Empty<double>();
    public double[] Handbrake = Array.Empty<double>();
    public double[] Gear = Array.Empty<double>();
    public double[] Rpm = Array.Empty<double>();
    public double[] LatG = Array.Empty<double>();
    public double[] LonG = Array.Empty<double>();
    /// <summary>Posición en el plano (vacío si el juego no la da).</summary>
    public double[] PosX = Array.Empty<double>();
    public double[] PosZ = Array.Empty<double>();

    public bool HasPosition => PosX.Length == Time.Length && PosX.Length > 1;

    public int Count => Time.Length;
}

/// <summary>Lectura/escritura de vueltas en CSV (se pueden abrir también en Excel).</summary>
public static class LapFile
{
    private const string Columns = "time,distance,speed_kmh,throttle,brake,clutch,steering,handbrake,gear,rpm,lat_g,lon_g";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Write(string path, LapInfo info, IReadOnlyList<RecordedPoint> points)
    {
        var sb = new StringBuilder(points.Count * 64);
        sb.AppendLine("# SimTelemetry 1");
        sb.AppendLine("# game=" + info.Game);
        sb.AppendLine("# track=" + info.Track);
        sb.AppendLine("# car=" + info.Car);
        sb.AppendLine("# lap=" + info.Number.ToString(Inv));
        sb.AppendLine("# laptime=" + info.LapTime.ToString("0.000", Inv));
        sb.AppendLine("# completed=" + (info.Completed ? "1" : "0"));
        sb.AppendLine("# date=" + info.Date.ToString("o", Inv));
        sb.AppendLine("# length=" + info.Length.ToString("0.0", Inv));
        sb.AppendLine("# maxrpm=" + info.MaxRpm.ToString("0", Inv));
        sb.AppendLine("# valid=" + (info.Valid ? "1" : "0"));
        sb.AppendLine("# abs=" + info.Abs.ToString(Inv));
        sb.AppendLine("# end=" + info.EndReason);
        if (info.TimeBased) sb.AppendLine("# xunit=s");
        bool withPosition = points.Count(p => !float.IsNaN(p.PosX) && !float.IsNaN(p.PosZ)) > points.Count / 2;
        sb.AppendLine(withPosition ? Columns + ",x,z" : Columns);
        foreach (var p in points)
        {
            sb.Append(p.Time.ToString("0.000", Inv)).Append(',')
              .Append(p.Distance.ToString("0.00", Inv)).Append(',')
              .Append(p.Speed.ToString("0.00", Inv)).Append(',')
              .Append(p.Throttle.ToString("0.000", Inv)).Append(',')
              .Append(p.Brake.ToString("0.000", Inv)).Append(',')
              .Append(p.Clutch.ToString("0.000", Inv)).Append(',')
              .Append(p.Steering.ToString("0.000", Inv)).Append(',')
              .Append(p.Handbrake.ToString("0.000", Inv)).Append(',')
              .Append(p.Gear.ToString(Inv)).Append(',')
              .Append(p.Rpm.ToString("0", Inv)).Append(',')
              .Append(p.LatG.ToString("0.000", Inv)).Append(',')
              .Append(p.LonG.ToString("0.000", Inv));
            if (withPosition)
            {
                sb.Append(',').Append(float.IsNaN(p.PosX) ? "" : p.PosX.ToString("0.00", Inv))
                  .Append(',').Append(float.IsNaN(p.PosZ) ? "" : p.PosZ.ToString("0.00", Inv));
            }
            sb.AppendLine();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    public static LapInfo ReadInfo(string path)
    {
        var meta = new Dictionary<string, string>();
        using (var reader = new StreamReader(path))
        {
            string? line;
            while ((line = reader.ReadLine()) != null && line.StartsWith('#'))
            {
                int eq = line.IndexOf('=');
                if (eq > 2) meta[line[2..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        return BuildInfo(path, meta);
    }

    public static LapData Read(string path)
    {
        var meta = new Dictionary<string, string>();
        var rows = new List<double[]>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#'))
            {
                int eq = line.IndexOf('=');
                if (eq > 2) meta[line[2..eq].Trim()] = line[(eq + 1)..].Trim();
                continue;
            }
            if (line.Length == 0 || line.StartsWith("time", StringComparison.Ordinal)) continue;
            // Sin Split: recorremos la línea campo a campo (es lo que más tarda al abrir muchas vueltas).
            var row = new double[14];
            var rest = line.AsSpan();
            int cols = 0;
            while (cols < 14)
            {
                int comma = rest.IndexOf(',');
                var field = comma < 0 ? rest : rest[..comma];
                row[cols++] = double.TryParse(field, NumberStyles.Float, Inv, out var v) ? v : double.NaN;
                if (comma < 0) break;
                rest = rest[(comma + 1)..];
            }
            if (cols < 12) continue;
            cols = cols >= 14 ? 14 : 12;
            for (int i = 0; i < 12; i++)
                if (double.IsNaN(row[i])) row[i] = 0;
            if (cols == 12) row[12] = row[13] = double.NaN;
            rows.Add(row);
        }

        // Vueltas antiguas de ACC: los primeros puntos pueden traer aún la distancia del final de la vuelta
        // anterior (5093 m y luego 0 m). Esos puntos son metros antes de la línea: los pasamos a negativo.
        for (int k = 1; k < Math.Min(rows.Count, 60); k++)
        {
            double before = rows[k - 1][1];
            if (before < 500 || rows[k][1] > before * 0.1) continue;
            for (int i = 0; i < k; i++) rows[i][1] -= before + 1; // el último punto antes de la línea queda a -1 m
            break;
        }

        // La distancia debe ser creciente para poder comparar vueltas por posición en pista.
        for (int i = 1; i < rows.Count; i++)
            if (rows[i][1] < rows[i - 1][1]) rows[i][1] = rows[i - 1][1];

        double[] Col(int c)
        {
            var a = new double[rows.Count];
            for (int i = 0; i < a.Length; i++) a[i] = rows[i][c];
            return a;
        }
        return new LapData
        {
            Info = BuildInfo(path, meta),
            Time = Col(0), Distance = Col(1), Speed = Col(2), Throttle = Col(3), Brake = Col(4),
            Clutch = Col(5), Steering = Col(6), Handbrake = Col(7), Gear = Col(8), Rpm = Col(9),
            LatG = Col(10), LonG = Col(11),
            PosX = rows.Count > 0 && rows.Count(r => !double.IsNaN(r[12])) > rows.Count / 2 ? FillGaps(Col(12)) : Array.Empty<double>(),
            PosZ = rows.Count > 0 && rows.Count(r => !double.IsNaN(r[13])) > rows.Count / 2 ? FillGaps(Col(13)) : Array.Empty<double>(),
        };
    }

    /// <summary>Rellena huecos (NaN) con el último valor conocido.</summary>
    private static double[] FillGaps(double[] v)
    {
        double last = v.FirstOrDefault(x => !double.IsNaN(x));
        for (int i = 0; i < v.Length; i++)
        {
            if (double.IsNaN(v[i])) v[i] = last;
            else last = v[i];
        }
        return v;
    }

    private static LapInfo BuildInfo(string path, Dictionary<string, string> m)
    {
        string Get(string k) => m.TryGetValue(k, out var v) ? v : "";
        double Num(string k) => double.TryParse(Get(k), NumberStyles.Float, Inv, out var v) ? v : 0;
        return new LapInfo
        {
            FilePath = path,
            Game = Get("game"),
            Track = Get("track"),
            Car = Get("car"),
            Number = (int)Num("lap"),
            LapTime = Num("laptime"),
            Completed = Get("completed") == "1",
            Date = DateTime.TryParse(Get("date"), Inv, DateTimeStyles.RoundtripKind, out var d) ? d : File.GetLastWriteTime(path),
            Length = Num("length"),
            MaxRpm = (float)Num("maxrpm"),
            Valid = Get("valid") != "0",
            Abs = m.ContainsKey("abs") ? (int)Num("abs") : -1,
            EndReason = Get("end"),
            TimeBased = Get("xunit") == "s",
        };
    }
}

public struct RecordedPoint
{
    public double Time;
    public double Distance;
    public float Speed, Throttle, Brake, Clutch, Steering, Handbrake, Rpm, LatG, LonG;
    public float PosX, PosZ; // NaN si no hay posición
    public int Gear;
}
