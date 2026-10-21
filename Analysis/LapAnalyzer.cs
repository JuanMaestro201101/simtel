using System;
using System.Collections.Generic;
using System.Linq;
using SimTelemetry.Recording;

namespace SimTelemetry.Analysis;

public sealed class BrakeZone
{
    public double StartDistance;
    public double StartSpeed;
    public double MinSpeed;
    public double MinSpeedDistance;
    public double ExitSpeed;      // velocidad 80 m después del punto más lento
    public double PeakBrake;
    /// <summary>Marcha en el punto más lento de la curva.</summary>
    public int Gear;
    public double TimeToPeak;     // s desde que empieza a frenar hasta el 90 % de su presión máxima
    public double Duration;
    public double EndDistance;
    /// <summary>Presión de freno en el momento de empezar a girar (0 si ya no frenaba).</summary>
    public double BrakeAtTurnIn;
    /// <summary>Distancia y velocidad al empezar a girar (o en el punto más lento si no hay giro).</summary>
    public double TurnInDistance, TurnInSpeed;
    /// <summary>Hay giro de volante en la curva (si no, es una frenada en recta / chicane suave).</summary>
    public bool HasTurn;
    /// <summary>Parte de la frenada hecha ya girando (trail braking), 0..1.</summary>
    public double TrailShare;
    /// <summary>Se soltó el freno de golpe (de &gt;40 % a casi 0 en menos de 0,15 s).</summary>
    public bool AbruptRelease;
    /// <summary>Nota de 0 a 100 frente a los parámetros ideales.</summary>
    public int Score;
    public List<string> Notes = new();
}

/// <summary>Valores de referencia de una buena frenada (ver la pestaña Aprender).</summary>
public static class BrakeIdeals
{
    public const double MaxTimeToPeak = 0.20;      // s
    public const double PeakWithAbs = 0.90;        // con ABS se puede (y se debe) pisar casi a fondo
    public const double PeakWithoutAbs = 0.80;     // sin ABS, justo antes del bloqueo
    public const double TrailMin = 0.15;           // presión mínima al empezar a girar
    public const double TrailMax = 0.55;
    public const double TurnSteer = 0.12;          // volante a partir del cual consideramos que se gira

    public static double MinPeak(int abs) => abs == 1 ? PeakWithAbs : PeakWithoutAbs;
}

public sealed class LapStats
{
    public double LapTime;
    public double Length;
    public double TopSpeed;
    public double AvgSpeed;
    public double FullThrottlePct;
    public double BrakingPct;
    public double CoastingPct;
    public double OverlapPct;
    public double LimiterPct;
    public double AvgPeakBrake;
    public double AvgTimeToPeak;
    public double SteeringCorrectionsPerKm;
    public int GearShifts;
    public List<BrakeZone> BrakeZones = new();
    public double AvgBrakeScore;
    public int TrailBrakingZones;    // frenadas con giro en las que se hizo trail braking
    public int TurnZones;            // frenadas con giro
    public int AbruptReleases;
}

public enum TipLevel { Good, Info, Warning }

public sealed class Tip
{
    public TipLevel Level { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public double? Distance { get; init; }
    public double? DistanceTo { get; init; }
    /// <summary>Problema detectado (para explicar por qué y cómo, con un ejercicio); null en los avisos informativos.</summary>
    public Problem? Kind { get; init; }
    public Advice? Advice => Kind is Problem p ? Coaching.For(p) : null;

    public string Icon => Level switch { TipLevel.Good => "✔", TipLevel.Warning => "▲", _ => "●" };
}

public sealed class SegmentDelta
{
    public int Index;
    public double From, To;
    public double Delta; // s (+ = más lento que la referencia)
}

/// <summary>Tiempo perdido (o ganado) en una curva frente a la referencia, con la causa por fases.</summary>
public sealed class CornerLoss
{
    /// <summary>Número de curva (el mismo que usan el Ingeniero y la pestaña Circuitos).</summary>
    public int Corner;
    /// <summary>Tramo de la curva: desde su punto de frenada hasta la frenada de la siguiente (la salida se arrastra por la recta).</summary>
    public double From, To;
    public double Loss; // s, + = pierdes
    public BrakeZone Ref = null!;
    public BrakeZone? Mine;
    /// <summary>Causas en orden: frenada → vértice → salida → recta siguiente.</summary>
    public List<string> Causes = new();
    /// <summary>Qué hacer, en pocas palabras ("frena más tarde"), o null si no está claro.</summary>
    public string? Fix;
    /// <summary>El problema principal (el primero de la cadena).</summary>
    public Problem? Main;
}

public sealed class Comparison
{
    public double[] GridDistance = Array.Empty<double>();
    public double[] Delta = Array.Empty<double>(); // s, + = más lento que la referencia
    public List<SegmentDelta> Segments = new();
}

/// <summary>Calcula estadísticas de una vuelta y consejos de mejora (opcionalmente frente a una referencia).</summary>
public static class LapAnalyzer
{
    private const double MinMovingSpeed = 25; // km/h

    public static LapStats Compute(LapData lap)
    {
        var st = new LapStats { LapTime = lap.Info.LapTime > 0 ? lap.Info.LapTime : (lap.Count > 0 ? lap.Time[^1] : 0) };
        int n = lap.Count;
        if (n < 2) return st;

        st.Length = lap.Distance[^1] - Math.Min(0, lap.Distance[0]);
        st.TopSpeed = lap.Speed.Max();
        st.AvgSpeed = st.LapTime > 0 ? st.Length / st.LapTime * 3.6 : 0;

        bool noSpeed = NoSpeed(lap);
        double maxRpm = lap.Info.MaxRpm > 0 ? lap.Info.MaxRpm : lap.Rpm.Max();
        double total = 0, full = 0, braking = 0, coasting = 0, overlap = 0, limiter = 0;
        int corrections = 0;
        double lastSteerRate = 0;
        for (int i = 1; i < n; i++)
        {
            double dt = lap.Time[i] - lap.Time[i - 1];
            if (dt <= 0 || dt > 0.5) continue;
            total += dt;
            double thr = lap.Throttle[i], brk = lap.Brake[i];
            if (thr >= 0.95) full += dt;
            if (brk >= 0.05) braking += dt;
            if (!noSpeed && thr < 0.05 && brk < 0.05 && lap.Speed[i] > MinMovingSpeed) coasting += dt;
            if (thr >= 0.15 && brk >= 0.15) overlap += dt;
            if (maxRpm > 0 && lap.Rpm[i] >= maxRpm * 0.97) limiter += dt;
            if (lap.Gear[i] != lap.Gear[i - 1] && lap.Gear[i] > 0) st.GearShifts++;

            // Una "corrección" es un cambio de sentido brusco del volante.
            double rate = (lap.Steering[i] - lap.Steering[i - 1]) / dt;
            if (Math.Abs(rate) > 1.5)
            {
                if (lastSteerRate != 0 && Math.Sign(rate) != Math.Sign(lastSteerRate)) corrections++;
                lastSteerRate = rate;
            }
        }
        if (total > 0)
        {
            st.FullThrottlePct = full / total * 100;
            st.BrakingPct = braking / total * 100;
            st.CoastingPct = coasting / total * 100;
            st.OverlapPct = overlap / total * 100;
            st.LimiterPct = limiter / total * 100;
        }
        st.SteeringCorrectionsPerKm = st.Length > 100 ? corrections / (st.Length / 1000) : 0;

        st.BrakeZones = FindBrakeZones(lap);
        if (st.BrakeZones.Count > 0)
        {
            st.AvgPeakBrake = st.BrakeZones.Average(z => z.PeakBrake);
            st.AvgTimeToPeak = st.BrakeZones.Average(z => z.TimeToPeak);
            st.AvgBrakeScore = st.BrakeZones.Average(z => z.Score);
            st.TurnZones = st.BrakeZones.Count(z => z.HasTurn);
            st.TrailBrakingZones = st.BrakeZones.Count(z => z.HasTurn && z.BrakeAtTurnIn >= BrakeIdeals.TrailMin);
            st.AbruptReleases = st.BrakeZones.Count(z => z.AbruptRelease);
        }
        return st;
    }

    /// <summary>Frenada que cuenta como curva (las mismas que canta el Ingeniero): con freno de verdad y pérdida de velocidad.</summary>
    public static bool IsCorner(BrakeZone z) => z.PeakBrake >= 0.25 && z.StartSpeed - z.MinSpeed >= 10;

    /// <summary>
    /// Tiempo perdido en cada curva de la referencia y por qué, fase a fase (como hacen las herramientas de coaching:
    /// un error de frenada se arrastra al vértice y a la salida). Vacío si no hay curvas que comparar.
    /// </summary>
    public static List<CornerLoss> CornerLosses(LapData lap, LapStats st, LapData reference, LapStats refSt)
    {
        var result = new List<CornerLoss>();
        if (lap.Count < 2 || reference.Count < 2) return result;
        double maxD = Math.Min(lap.Distance[^1], reference.Distance[^1]);
        double Delta(double x) => TimeAt(lap, x) - TimeAt(reference, x);

        var corners = refSt.BrakeZones.Where(IsCorner).ToList();
        for (int k = 0; k < corners.Count; k++)
        {
            var z = corners[k];
            double from = Math.Max(z.StartDistance, Math.Max(lap.Distance[0], reference.Distance[0]));
            double to = k + 1 < corners.Count ? corners[k + 1].StartDistance : maxD;
            if (to <= from) continue;
            var mine = st.BrakeZones.MinBy(m => Math.Abs(m.StartDistance - z.StartDistance));
            if (mine != null && Math.Abs(mine.StartDistance - z.StartDistance) > 80) mine = null;
            var loss = new CornerLoss { Corner = k + 1, From = from, To = to, Loss = Delta(to) - Delta(from), Ref = z, Mine = mine };
            // En circuito, lo que va de la meta a la primera frenada es la salida de la última curva.
            if (k == corners.Count - 1 && corners.Count > 1 && !lap.Info.Game.Contains("DiRT") && !lap.Info.Game.Contains("WRC"))
                loss.Loss += Delta(corners[0].StartDistance) - Delta(Math.Max(lap.Distance[0], reference.Distance[0]));

            if (mine != null)
            {
                double early = z.StartDistance - mine.StartDistance;
                if (early > 8) { loss.Causes.Add($"frenas {early:0} metros antes"); loss.Fix ??= "frena más tarde"; loss.Main ??= Problem.BrakeEarly; }
                else if (early < -8 && mine.MinSpeed - z.MinSpeed > 3) { loss.Causes.Add($"frenas {-early:0} metros más tarde y entras pasado"); loss.Fix ??= "frena un poco antes"; loss.Main ??= Problem.BrakeLate; }
                if (z.PeakBrake - mine.PeakBrake > 0.15) { loss.Causes.Add($"con menos presión ({mine.PeakBrake:P0} frente a {z.PeakBrake:P0})"); loss.Fix ??= "frena más fuerte"; loss.Main ??= Problem.LowPressure; }
                if (mine.TimeToPeak - z.TimeToPeak > 0.15) { loss.Causes.Add($"pisas el freno más despacio ({mine.TimeToPeak:0.00} s frente a {z.TimeToPeak:0.00} s)"); loss.Fix ??= "pisa el freno más rápido"; loss.Main ??= Problem.SlowAttack; }
                if (z.MinSpeed - mine.MinSpeed > 3) { loss.Causes.Add($"pasas el vértice {z.MinSpeed - mine.MinSpeed:0} km/h más lento ({mine.MinSpeed:0} frente a {z.MinSpeed:0})"); loss.Fix ??= "lleva más velocidad al vértice"; loss.Main ??= Problem.SlowApex; }
                if (z.ExitSpeed - mine.ExitSpeed > 4) { loss.Causes.Add($"sales {z.ExitSpeed - mine.ExitSpeed:0} km/h más lento: acelera antes y de forma progresiva"); loss.Fix ??= "acelera antes"; loss.Main ??= Problem.SlowExit; }
            }
            // La recta siguiente: ¿vas a fondo el mismo tiempo?
            double myFull = ThrottleFullShare(lap, z.EndDistance, to), refFull = ThrottleFullShare(reference, z.EndDistance, to);
            if (refFull - myFull > 0.08) { loss.Causes.Add($"en la recta siguiente vas a fondo menos tiempo ({myFull:P0} frente a {refFull:P0})"); loss.Fix ??= "acelera a fondo antes"; loss.Main ??= Problem.LateThrottle; }
            else if (to - z.EndDistance > 150)
            {
                double slower = AvgSpeedBetween(reference, z.EndDistance, to) - AvgSpeedBetween(lap, z.EndDistance, to);
                if (slower > 4) loss.Causes.Add($"y toda la recta siguiente la haces {slower:0} km/h más lento de media");
            }
            result.Add(loss);
        }
        return result;
    }

    /// <summary>Sesión sin velocidad (sólo pedales del volante).</summary>
    public static bool NoSpeed(LapData lap) => lap.Info.TimeBased || lap.Count == 0 || lap.Speed.Max() < 1;

    private static List<BrakeZone> FindBrakeZones(LapData lap)
    {
        var zones = new List<BrakeZone>();
        bool noSpeed = NoSpeed(lap);
        int n = lap.Count;
        int i = 0;
        while (i < n)
        {
            if (lap.Brake[i] < 0.2) { i++; continue; }

            int start = i;
            while (start > 0 && lap.Brake[start - 1] > 0.02) start--;
            int end = i;
            int peak = i;
            int quiet = 0;
            // La zona continúa mientras haya freno (permitimos huecos cortos de < 0.3 s).
            while (end < n - 1)
            {
                end++;
                if (lap.Brake[end] > lap.Brake[peak]) peak = end;
                if (lap.Brake[end] < 0.05)
                {
                    if (lap.Time[end] - lap.Time[end - quiet - 1] > 0.3) break;
                    quiet++;
                }
                else quiet = 0;
            }

            double duration = lap.Time[end] - lap.Time[start];
            if (duration > 0.25 && (noSpeed || lap.Speed[start] > 40))
            {
                // Punto más lento: desde el inicio de la frenada hasta que se vuelve a acelerar a fondo.
                int k = start, minIdx = start;
                while (k < n - 1 && (k <= end || lap.Throttle[k] < 0.9) && lap.Distance[k] - lap.Distance[start] < 600)
                {
                    if (lap.Speed[k] < lap.Speed[minIdx]) minIdx = k;
                    k++;
                }
                double exitAt = lap.Distance[minIdx] + 80;
                int exitIdx = minIdx;
                while (exitIdx < n - 1 && lap.Distance[exitIdx] < exitAt) exitIdx++;

                // Ataque: tiempo hasta llegar al 90 % de la presión máxima de esta frenada.
                int attack = start;
                while (attack < peak && lap.Brake[attack] < lap.Brake[peak] * 0.9) attack++;

                // Giro: primer punto (desde la frenada hasta el vértice) en que el volante pasa del umbral.
                int turnIn = -1;
                for (int t = start; t <= Math.Max(minIdx, end); t++)
                    if (Math.Abs(lap.Steering[t]) >= BrakeIdeals.TurnSteer) { turnIn = t; break; }
                double brakeAtTurnIn = turnIn >= 0 && turnIn <= end ? lap.Brake[turnIn] : 0;

                double trailTime = 0, zoneTime = 0;
                bool abrupt = false;
                for (int t = start + 1; t <= end; t++)
                {
                    double dt = lap.Time[t] - lap.Time[t - 1];
                    if (dt <= 0 || dt > 0.5) continue;
                    zoneTime += dt;
                    if (lap.Brake[t] >= 0.05 && Math.Abs(lap.Steering[t]) >= BrakeIdeals.TurnSteer) trailTime += dt;
                }
                for (int t = peak; t <= end && !abrupt; t++)
                {
                    if (lap.Brake[t] < 0.4) continue;
                    for (int u = t + 1; u <= end + 1 && u < n && lap.Time[u] - lap.Time[t] <= 0.15; u++)
                        if (lap.Brake[u] <= 0.05) { abrupt = true; break; }
                }

                var zone = new BrakeZone
                {
                    StartDistance = lap.Distance[start],
                    StartSpeed = lap.Speed[start],
                    MinSpeed = lap.Speed[minIdx],
                    MinSpeedDistance = lap.Distance[minIdx],
                    ExitSpeed = lap.Speed[exitIdx],
                    PeakBrake = lap.Brake[peak],
                    Gear = (int)lap.Gear[minIdx],
                    TimeToPeak = lap.Time[attack] - lap.Time[start],
                    Duration = duration,
                    EndDistance = lap.Distance[end],
                    BrakeAtTurnIn = brakeAtTurnIn,
                    TurnInDistance = lap.Distance[turnIn >= 0 ? turnIn : minIdx],
                    TurnInSpeed = lap.Speed[turnIn >= 0 ? turnIn : minIdx],
                    HasTurn = turnIn >= 0,
                    TrailShare = zoneTime > 0 ? trailTime / zoneTime : 0,
                    AbruptRelease = abrupt,
                };
                ScoreZone(zone, lap.Info.Abs, noSpeed);
                zones.Add(zone);
            }
            i = end + 1;
        }
        return zones;
    }

    /// <summary>Puntúa una frenada frente a los valores ideales y anota qué mejorar.</summary>
    private static void ScoreZone(BrakeZone z, int abs, bool noSpeed)
    {
        double score = 100;
        if (z.TimeToPeak > BrakeIdeals.MaxTimeToPeak)
        {
            score -= Math.Min(30, (z.TimeToPeak - BrakeIdeals.MaxTimeToPeak) * 80);
            z.Notes.Add($"ataque lento ({z.TimeToPeak:0.00} s; ideal ≤ {BrakeIdeals.MaxTimeToPeak:0.0} s)");
        }
        double minPeak = BrakeIdeals.MinPeak(abs);
        if (z.PeakBrake < minPeak && (noSpeed ? z.Duration > 0.8 : z.StartSpeed - z.MinSpeed > 30))
        {
            score -= Math.Min(30, (minPeak - z.PeakBrake) * 120);
            z.Notes.Add($"poca presión ({z.PeakBrake:P0}; ideal ≥ {minPeak:P0})");
        }
        if (z.HasTurn && z.BrakeAtTurnIn < BrakeIdeals.TrailMin)
        {
            score -= 20;
            z.Notes.Add("sueltas el freno antes de girar (sin trail braking)");
        }
        else if (z.HasTurn && z.BrakeAtTurnIn > BrakeIdeals.TrailMax)
        {
            score -= 10;
            z.Notes.Add($"giras con mucho freno ({z.BrakeAtTurnIn:P0}): riesgo de bloqueo o subviraje");
        }
        if (z.AbruptRelease)
        {
            score -= 15;
            z.Notes.Add("suelta brusca del pedal");
        }
        z.Score = (int)Math.Round(Math.Clamp(score, 0, 100));
        if (z.Notes.Count == 0) z.Notes.Add("frenada de libro");
    }

    /// <summary>Delta de tiempo frente a la referencia, sobre una rejilla de distancia común.</summary>
    public static Comparison Compare(LapData lap, LapData reference, int segments = 10)
    {
        var cmp = new Comparison();
        if (lap.Count < 2 || reference.Count < 2) return cmp;

        double maxD = Math.Min(lap.Distance[^1], reference.Distance[^1]);
        if (maxD <= 50) return cmp;
        const double step = 5;
        int count = (int)(maxD / step) + 1;
        cmp.GridDistance = new double[count];
        cmp.Delta = new double[count];
        for (int i = 0; i < count; i++)
        {
            double d = i * step;
            cmp.GridDistance[i] = d;
            cmp.Delta[i] = TimeAt(lap, d) - TimeAt(reference, d);
        }

        double segLen = maxD / segments;
        for (int s = 0; s < segments; s++)
        {
            double from = s * segLen, to = (s + 1) * segLen;
            cmp.Segments.Add(new SegmentDelta
            {
                Index = s + 1,
                From = from,
                To = to,
                Delta = (TimeAt(lap, to) - TimeAt(reference, to)) - (TimeAt(lap, from) - TimeAt(reference, from)),
            });
        }
        return cmp;
    }

    /// <summary>Tiempo (s) en que la vuelta pasó por la distancia d (interpolado).</summary>
    public static double TimeAt(LapData lap, double d) => Interpolate(lap.Distance, lap.Time, d);

    public static double Interpolate(double[] xs, double[] ys, double x)
    {
        int n = xs.Length;
        if (n == 0) return double.NaN;
        if (x <= xs[0]) return ys[0];
        if (x >= xs[n - 1]) return ys[n - 1];
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (xs[mid] <= x) lo = mid; else hi = mid;
        }
        double span = xs[hi] - xs[lo];
        double t = span > 1e-9 ? (x - xs[lo]) / span : 0;
        return ys[lo] + (ys[hi] - ys[lo]) * t;
    }

    public static List<Tip> BuildTips(LapData lap, LapStats st, LapData? reference, LapStats? refSt, Comparison? cmp)
    {
        var tips = new List<Tip>();

        // ---- Comparación con la referencia: dónde se pierde el tiempo ----
        if (reference != null && refSt != null && cmp != null && cmp.Segments.Count > 0)
        {
            double totalDelta = cmp.Delta.Length > 0 ? cmp.Delta[^1] : 0;
            var corners = CornerLosses(lap, st, reference, refSt);
            var worst = corners.Where(c => c.Loss > 0.08).OrderByDescending(c => c.Loss).Take(3).ToList();
            if (totalDelta > 0.05)
                tips.Add(new Tip
                {
                    Level = TipLevel.Info,
                    Title = $"Pierdes {totalDelta:0.00} s frente a la referencia",
                    Detail = worst.Count > 0
                        ? "Lo que más te cuesta: " + string.Join(", ", worst.Select(c => $"curva {c.Corner} (+{c.Loss:0.00} s)")) +
                          ". Cada curva cuenta desde su frenada hasta la siguiente."
                        : "La diferencia está repartida por toda la vuelta: compara las gráficas.",
                });
            else if (totalDelta < -0.05)
                tips.Add(new Tip
                {
                    Level = TipLevel.Good,
                    Title = $"Eres {-totalDelta:0.00} s más rápido que la referencia",
                    Detail = "Considera usar esta vuelta como nueva referencia.",
                });

            foreach (var c in worst)
                tips.Add(new Tip
                {
                    Level = TipLevel.Warning,
                    Title = $"Curva {c.Corner} (frenada a los {c.Ref.StartDistance:0} m): +{c.Loss:0.00} s",
                    Detail = c.Causes.Count > 0
                        ? char.ToUpper(c.Causes[0][0]) + string.Join(" → ", c.Causes)[1..] + "."
                        : "Compara las gráficas de velocidad y pedales en esta curva.",
                    Distance = c.From,
                    DistanceTo = c.To,
                    Kind = c.Main,
                });

            foreach (var seg in corners.Count >= 2 ? Enumerable.Empty<SegmentDelta>() : cmp.Segments.Where(s => s.Delta > 0.08).OrderByDescending(s => s.Delta).Take(3))
            {
                var reasons = new List<string>();
                foreach (var z in refSt.BrakeZones.Where(z => z.StartDistance >= seg.From - 30 && z.StartDistance < seg.To))
                {
                    var mine = st.BrakeZones.OrderBy(m => Math.Abs(m.StartDistance - z.StartDistance)).FirstOrDefault();
                    if (mine == null || Math.Abs(mine.StartDistance - z.StartDistance) > 80) continue;
                    double early = z.StartDistance - mine.StartDistance;
                    if (early > 10) reasons.Add($"frenas {early:0} m antes (a los {mine.StartDistance:0} m)");
                    if (z.MinSpeed - mine.MinSpeed > 3) reasons.Add($"pasas el vértice {z.MinSpeed - mine.MinSpeed:0} km/h más lento ({mine.MinSpeed:0} vs {z.MinSpeed:0} km/h)");
                    if (z.ExitSpeed - mine.ExitSpeed > 4) reasons.Add($"sales de la curva {z.ExitSpeed - mine.ExitSpeed:0} km/h más lento: acelera antes y de forma progresiva");
                    if (z.PeakBrake - mine.PeakBrake > 0.15) reasons.Add($"usas menos presión de freno ({mine.PeakBrake:P0} vs {z.PeakBrake:P0})");
                }

                double myFull = ThrottleFullShare(lap, seg.From, seg.To), refFull = ThrottleFullShare(reference, seg.From, seg.To);
                if (refFull - myFull > 0.08) reasons.Add($"vas a fondo menos tiempo ({myFull:P0} vs {refFull:P0})");
                double mySpeed = AvgSpeedBetween(lap, seg.From, seg.To), refSpeed = AvgSpeedBetween(reference, seg.From, seg.To);
                if (reasons.Count == 0 && refSpeed - mySpeed > 2) reasons.Add($"velocidad media {refSpeed - mySpeed:0} km/h menor");

                tips.Add(new Tip
                {
                    Level = TipLevel.Warning,
                    Title = $"Tramo {seg.Index} ({seg.From:0}–{seg.To:0} m): +{seg.Delta:0.00} s",
                    Detail = reasons.Count > 0
                        ? char.ToUpper(reasons[0][0]) + string.Join("; ", reasons)[1..] + "."
                        : "Compara las gráficas de velocidad y pedales en este tramo.",
                    Distance = seg.From,
                    DistanceTo = seg.To,
                });
            }
        }

        // ---- Técnica general (no necesita referencia) ----
        if (st.CoastingPct > 4)
            tips.Add(new Tip
            {
                Level = TipLevel.Warning,
                Kind = Problem.Coasting,
                Title = $"Inercia sin pedales: {st.CoastingPct:0.0}% de la vuelta",
                Detail = "Pasas mucho tiempo sin acelerar ni frenar. Enlaza antes la salida del freno con el acelerador (aunque sea parcial) para mantener el coche cargado.",
            });

        if (st.OverlapPct > 2)
            tips.Add(new Tip
            {
                Level = TipLevel.Warning,
                Kind = Problem.Overlap,
                Title = $"Acelerador y freno a la vez: {st.OverlapPct:0.0}%",
                Detail = "Salvo que lo hagas a propósito (freno con el pie izquierdo en rally), soltar el acelerador del todo antes de frenar mejora la frenada y reduce el desgaste.",
            });

        if (st.BrakeZones.Count >= 3 && st.AvgPeakBrake < BrakeIdeals.MinPeak(lap.Info.Abs) - 0.05)
            tips.Add(new Tip
            {
                Level = TipLevel.Warning,
                Kind = Problem.LowPressure,
                Title = $"Presión máxima de freno media: {st.AvgPeakBrake:P0} (ideal ≥ {BrakeIdeals.MinPeak(lap.Info.Abs):P0})",
                Detail = "Frenas con poca fuerza al inicio. La frenada más eficaz es fuerte al principio (cerca del bloqueo/ABS) y luego se suelta progresivamente al entrar en la curva (trail braking).",
            });

        if (st.BrakeZones.Count >= 3 && st.AvgTimeToPeak > 0.35)
            tips.Add(new Tip
            {
                Level = TipLevel.Warning,
                Kind = Problem.SlowAttack,
                Title = $"Ataque al freno lento: {st.AvgTimeToPeak:0.00} s hasta la presión máxima (ideal ≤ 0,2 s)",
                Detail = "Pisa el freno más rápido y decidido; cada décima en llegar a la presión máxima son metros de frenada perdidos.",
            });

        if (st.TurnZones >= 3 && st.TrailBrakingZones < st.TurnZones / 2.0)
            tips.Add(new Tip
            {
                Level = TipLevel.Warning,
                Kind = Problem.NoTrail,
                Title = $"Trail braking en {st.TrailBrakingZones} de {st.TurnZones} curvas frenadas",
                Detail = "Sueltas el freno del todo antes de girar. Prueba a llegar al punto de giro con un 20–40 % de freno y soltarlo poco a poco hasta cerca del vértice: el morro se mantiene cargado y el coche gira mejor.",
            });

        if (st.AbruptReleases >= 2)
            tips.Add(new Tip
            {
                Level = TipLevel.Warning,
                Kind = Problem.AbruptRelease,
                Title = $"Sueltas el freno de golpe en {st.AbruptReleases} frenadas",
                Detail = "Al soltar de golpe la suspensión delantera rebota y el coche pierde agarre delante (subviraje) justo al entrar en la curva. Practica el ejercicio \"Trail braking\" del Entrenador.",
            });

        if (st.LimiterPct > 3)
            tips.Add(new Tip
            {
                Level = TipLevel.Warning,
                Kind = Problem.Limiter,
                Title = $"En el corte de revoluciones el {st.LimiterPct:0.0}% del tiempo",
                Detail = "Cambia de marcha un poco antes; en el corte no aceleras.",
            });

        if (st.SteeringCorrectionsPerKm > 12)
            tips.Add(new Tip
            {
                Level = TipLevel.Info,
                Kind = Problem.Corrections,
                Title = $"{st.SteeringCorrectionsPerKm:0} correcciones de volante por km",
                Detail = "Muchos contravolantes indican un coche nervioso (sobreviraje) o entradas bruscas. Suaviza el giro y revisa el reglaje si se repite en las mismas curvas.",
            });

        if (tips.All(t => t.Level != TipLevel.Warning))
            tips.Add(new Tip
            {
                Level = TipLevel.Good,
                Title = "Buena técnica de pedales en esta vuelta",
                Detail = $"A fondo {st.FullThrottlePct:0}% del tiempo, frenando {st.BrakingPct:0}%. Busca más tiempo comparando con tu vuelta más rápida.",
            });

        return tips;
    }

    private static double ThrottleFullShare(LapData lap, double from, double to)
    {
        double total = 0, full = 0;
        for (int i = 1; i < lap.Count; i++)
        {
            if (lap.Distance[i] < from || lap.Distance[i] > to) continue;
            double dt = lap.Time[i] - lap.Time[i - 1];
            if (dt <= 0 || dt > 0.5) continue;
            total += dt;
            if (lap.Throttle[i] >= 0.95) full += dt;
        }
        return total > 0 ? full / total : 0;
    }

    private static double AvgSpeedBetween(LapData lap, double from, double to)
    {
        double dt = TimeAt(lap, to) - TimeAt(lap, from);
        return dt > 0 ? (to - from) / dt * 3.6 : 0;
    }
}
