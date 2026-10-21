using System;
using System.Collections.Generic;

namespace SimTelemetry.Recording;

/// <summary>
/// Control de calidad de una vuelta antes de usarla: cortes de datos, rebobinados (Forza), saltos de posición
/// (reaparecer en pista) o distancias que no cuadran con la velocidad (longitud del circuito mal medida).
/// Una vuelta así no sirve de referencia: sus tiempos por curva y sus puntos de frenada no son fiables.
/// </summary>
public static class LapQuality
{
    /// <summary>Problemas encontrados (vacío si la vuelta es fiable). Como mucho dos, los primeros.</summary>
    public static List<string> Check(IReadOnlyList<RecordedPoint> p, bool stage = false)
    {
        var problems = new List<string>();
        void Add(string s) { if (problems.Count < 2 && !problems.Contains(s)) problems.Add(s); }
        if (p.Count < 50) return problems;

        // Salida desde parado (parrilla): los primeros metros son lentísimos y la diferencia en vivo
        // de las vueltas lanzadas saldría con segundos de ventaja falsa. En rally todos los tramos salen parados.
        if (!stage && p[0].Speed < 40 && p[^1].Distance - p[0].Distance > 1000)
            Add($"salida desde parado (empieza a {p[0].Speed:0} km/h)");

        double integrated = 0;
        for (int i = 1; i < p.Count; i++)
        {
            var a = p[i - 1];
            var b = p[i];
            double dt = b.Time - a.Time;
            double speed = Math.Max(a.Speed, b.Speed) / 3.6; // m/s
            if (dt > 1.0) Add($"corte de datos de {dt:0.0} s a los {b.Distance:0} m");
            if (dt <= 0 || dt > 1.0) continue;
            integrated += (a.Speed + b.Speed) / 2 / 3.6 * dt;

            double step = b.Distance - a.Distance;
            // Los primeros puntos pueden traer aún la distancia del final de la vuelta anterior (se corrige al leer).
            if (i > 60 && step < -10) Add($"salto atrás a los {a.Distance:0} m (¿rebobinado o reinicio?)");
            if (step > speed * dt * 2 + 25) Add($"salto adelante de {step:0} m a los {a.Distance:0} m");
            if (float.IsFinite(a.PosX) && float.IsFinite(b.PosX) && float.IsFinite(a.PosZ) && float.IsFinite(b.PosZ))
            {
                double jump = Math.Sqrt((b.PosX - a.PosX) * (b.PosX - a.PosX) + (b.PosZ - a.PosZ) * (b.PosZ - a.PosZ));
                if (jump > speed * dt * 2 + 25) Add($"salto de posición de {jump:0} m a los {a.Distance:0} m (¿reaparición en pista?)");
            }
        }

        double covered = p[^1].Distance - p[0].Distance;
        if (covered > 1000 && integrated > 0)
        {
            double ratio = covered / integrated;
            if (ratio < 0.85 || ratio > 1.15)
                Add($"la distancia no cuadra con la velocidad ({ratio:P0} de lo recorrido): la longitud del circuito estaba mal medida");
        }
        return problems;
    }
}
