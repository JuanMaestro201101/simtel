using System;
using System.Collections.Generic;

namespace SimTelemetry.Input;

/// <summary>
/// Cómo convierte un juego la posición de tu pedal (lo que lee el volante) en freno o acelerador (lo que envía
/// el juego). Cada juego aplica su propia calibración (saturación, curva, zona muerta), así que el mismo
/// recorrido no da el mismo porcentaje. Se mide sola mientras conduces y el Entrenador la usa para
/// que practiques con el mismo tacto.
/// </summary>
public sealed class PedalFeelCurve
{
    public const int Bins = 21; // 0 %, 5 %, … 100 % de recorrido

    public double[] BrakeSum { get; set; } = new double[Bins];
    public int[] BrakeCount { get; set; } = new int[Bins];
    public double[] ThrottleSum { get; set; } = new double[Bins];
    public int[] ThrottleCount { get; set; } = new int[Bins];
}

public static class PedalFeel
{
    private const int MinSamples = 15;       // muestras por tramo para fiarnos de él
    private const int MaxSamples = 5000;     // a partir de aquí la media se va renovando (por si cambias la calibración del juego)

    /// <summary>Hay datos nuevos sin guardar.</summary>
    public static bool Dirty { get; set; }

    /// <summary>Añade una muestra: posición del pedal (0..1, volante) y valor del juego (0..1) con el pedal quieto.</summary>
    public static void Learn(PedalFeelCurve c, bool brake, float pedal, float game)
    {
        var sum = brake ? c.BrakeSum : c.ThrottleSum;
        var count = brake ? c.BrakeCount : c.ThrottleCount;
        if (sum.Length != PedalFeelCurve.Bins || count.Length != PedalFeelCurve.Bins) return; // ajustes de otra versión
        int i = (int)Math.Round(Math.Clamp(pedal, 0, 1) * (PedalFeelCurve.Bins - 1));
        if (count[i] >= MaxSamples)
        {
            sum[i] -= sum[i] / count[i];
            count[i]--;
        }
        sum[i] += Math.Clamp(game, 0, 1);
        count[i]++;
        Dirty = true;
    }

    /// <summary>Hay tramos suficientes (también con el pedal bastante pisado) para usar la curva.</summary>
    public static bool Ready(PedalFeelCurve c, bool brake)
    {
        var count = brake ? c.BrakeCount : c.ThrottleCount;
        int known = 0, high = 0;
        for (int i = 1; i < count.Length; i++)
            if (count[i] >= MinSamples)
            {
                known++;
                if (i >= 12) high++;
            }
        return known >= 5 && high >= 1;
    }

    /// <summary>Valor que daría el juego para esa posición del pedal (interpolando entre los tramos medidos).</summary>
    public static float Map(PedalFeelCurve c, bool brake, float pedal)
    {
        var sum = brake ? c.BrakeSum : c.ThrottleSum;
        var count = brake ? c.BrakeCount : c.ThrottleCount;
        var xs = new List<double> { 0 };
        var ys = new List<double> { count.Length > 0 && count[0] >= MinSamples ? sum[0] / count[0] : 0 };
        for (int i = 1; i < count.Length; i++)
            if (count[i] >= MinSamples)
            {
                xs.Add(i / (double)(PedalFeelCurve.Bins - 1));
                ys.Add(Math.Max(ys[^1], sum[i] / count[i])); // nunca baja al pisar más
            }
        // Sin datos del final del recorrido suponemos que a fondo es 100 %, como en casi todos los juegos.
        if (xs[^1] < 1)
        {
            xs.Add(1);
            ys.Add(Math.Max(ys[^1], 1));
        }
        double p = Math.Clamp(pedal, 0, 1);
        for (int k = 1; k < xs.Count; k++)
            if (p <= xs[k])
                return (float)(ys[k - 1] + (ys[k] - ys[k - 1]) * (p - xs[k - 1]) / (xs[k] - xs[k - 1]));
        return (float)ys[^1];
    }

    /// <summary>Recorrido del pedal (0..1) con el que el juego ya marca casi el 100 %.</summary>
    public static float FullAt(PedalFeelCurve c, bool brake)
    {
        for (int i = 0; i <= 100; i++)
            if (Map(c, brake, i / 100f) >= 0.98f) return i / 100f;
        return 1;
    }
}
