using System;
using System.Collections.Generic;
using System.Linq;
using SimTelemetry.Recording;

namespace SimTelemetry.Analysis;

/// <summary>
/// Cuánto puede frenar tu coche de verdad: la deceleración en línea recta con el freno casi a fondo,
/// medida en tus propias vueltas (tramos de 20 km/h, percentil 90). Con aerodinámica frena más a más velocidad.
/// </summary>
public sealed class DecelModel
{
    private const double BinKmh = 20;
    private readonly double[] _accel; // m/s² por tramo de velocidad, NaN si no hay datos

    private DecelModel(double[] accel) => _accel = accel;

    public bool Valid => _accel.Count(a => !double.IsNaN(a)) >= 3;

    public static DecelModel Build(IEnumerable<LapData> laps)
    {
        var bins = Enumerable.Range(0, 20).Select(_ => new List<double>()).ToArray();
        foreach (var lap in laps)
        {
            for (int i = 3; i < lap.Count - 3; i++)
            {
                double dt = lap.Time[i + 3] - lap.Time[i - 3];
                if (dt <= 0 || dt > 0.5 || lap.Brake[i] < 0.85 || Math.Abs(lap.Steering[i]) >= 0.05 || lap.Speed[i] < 25) continue;
                double a = -(lap.Speed[i + 3] - lap.Speed[i - 3]) / 3.6 / dt;
                int b = (int)(lap.Speed[i] / BinKmh);
                if (a > 0 && b < bins.Length) bins[b].Add(a);
            }
        }
        return new DecelModel(bins.Select(xs => xs.Count >= 10 ? xs.OrderBy(x => x).ElementAt((int)(xs.Count * 0.9)) : double.NaN).ToArray());
    }

    /// <summary>Sin datos: no se calcula potencial.</summary>
    public static DecelModel None => new(Enumerable.Repeat(double.NaN, 20).ToArray());

    /// <summary>Modelo de deceleración constante (para las comprobaciones).</summary>
    public static DecelModel Constant(double g) => new(Enumerable.Repeat(g * 9.81, 20).ToArray());

    /// <summary>Deceleración (m/s²) a esa velocidad, interpolando entre tramos medidos.</summary>
    public double Accel(double kmh)
    {
        double x = kmh / BinKmh - 0.5;
        int lo = -1, hi = -1;
        for (int i = 0; i < _accel.Length; i++)
        {
            if (double.IsNaN(_accel[i])) continue;
            if (i <= x) lo = i;
            if (i >= x && hi < 0) hi = i;
        }
        if (lo < 0) return _accel[hi];
        if (hi < 0 || hi == lo) return _accel[lo]; // por encima de lo medido: sin extrapolar (conservador)
        return _accel[lo] + (_accel[hi] - _accel[lo]) * (x - lo) / (hi - lo);
    }

    /// <summary>Metros que necesita el coche para bajar de v0 a v1 frenando al máximo (más 0,1 s de ataque del pedal).</summary>
    public double IdealDistance(double v0Kmh, double v1Kmh, double attackSeconds = 0.1)
    {
        double d = 0;
        for (double v = v1Kmh; v < v0Kmh; v += 1)
        {
            double vm = Math.Min(v + 0.5, v0Kmh) / 3.6;
            d += vm * (Math.Min(1, v0Kmh - v) / 3.6) / Accel(v + 0.5);
        }
        return d + v0Kmh / 3.6 * attackSeconds / 2;
    }

    /// <summary>
    /// Metros que podrías retrasar el punto de frenada: lo que tardaste en bajar hasta la velocidad de giro
    /// menos lo que necesita el coche frenando a tope. NaN si la frenada es demasiado corta para medirlo.
    /// </summary>
    public double Potential(BrakeZone z)
    {
        double actual = z.TurnInDistance - z.StartDistance;
        if (!Valid || z.StartSpeed - z.TurnInSpeed < 20 || actual < 20) return double.NaN;
        return Math.Max(0, actual - IdealDistance(z.StartSpeed, z.TurnInSpeed));
    }
}
