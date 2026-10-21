using System;
using System.Collections.Generic;
using System.Linq;
using SimTelemetry.Diagnostics;

namespace SimTelemetry.Input;

/// <summary>
/// Calibración "manos libres": pide pisar cada pedal y detecta solo qué eje es, en qué dirección se mueve y
/// su recorrido. No hace falta pulsar nada con el ratón.
/// <para>
/// La posición "suelto" es siempre la que tenía el pedal ANTES de pisarlo, y el pedal sólo se da por detectado
/// cuando vuelve a ella. Así no importa si lo mantienes pisado un rato (antes eso lo invertía).
/// </para>
/// </summary>
public sealed class PedalDetector
{
    public enum Step { GetReady, Brake, Throttle, Clutch, Done }

    private const int FullRange = 65535;
    private const int Untouched = 32767;                 // algunos volantes (G29) marcan esto hasta el primer toque
    private const double MinTravel = 0.6 * FullRange;   // recorrido mínimo de una pisada "a fondo"
    private const double MoveThreshold = 0.25 * FullRange;
    private const double ReturnTolerance = 0.15 * FullRange;
    private const double StableNoise = 0.015 * FullRange;
    private const double StableTime = 0.4;
    private const double ClutchTimeout = 10;

    /// <summary>Seguimiento de un eje durante un paso.</summary>
    private sealed class AxisTrack
    {
        public readonly List<(double T, int V)> History = new();
        public int Baseline;
        public bool BaselineKnown;
        public bool UntouchedBaseline;   // la base es el 32767 "de fábrica" de un eje que aún no se ha tocado
        public bool InExcursion;
        public int Peak;
        public double HeldSince = double.NaN;
    }

    private readonly Dictionary<(string Dev, int Axis), AxisTrack> _tracks = new();
    // Para la comprobación final: valores de los pedales ya detectados mientras se pisan los demás.
    private readonly Dictionary<PedalAxis, List<float>> _verify = new();
    private double _stepStart = double.NaN;

    public Step Current { get; private set; } = Step.GetReady;
    public PedalMapping Result { get; } = new();
    public List<string> Detected { get; } = new();

    /// <summary>true si el pedal se está manteniendo pisado (hay que soltarlo para terminar).</summary>
    public bool WaitingForRelease { get; private set; }

    public string Prompt => Current switch
    {
        Step.GetReady => "Suelta todos los pedales…",
        Step.Brake => WaitingForRelease ? "Bien. Ahora SUELTA el freno del todo" : "Pisa el FRENO a fondo y suéltalo",
        Step.Throttle => WaitingForRelease ? "Bien. Ahora SUELTA el acelerador del todo" : "Ahora pisa el ACELERADOR a fondo y suéltalo",
        Step.Clutch => WaitingForRelease ? "Bien. Ahora SUELTA el embrague del todo"
            : "Por último, pisa el EMBRAGUE a fondo y suéltalo (si no tienes, espera unos segundos)",
        _ => "¡Pedales detectados!",
    };

    /// <summary>Progreso del paso actual (0..1) para mostrar una barra.</summary>
    public double Progress { get; private set; }

    public void Update(IReadOnlyList<WheelDeviceState> devices, double now)
    {
        if (Current == Step.Done) return;
        if (double.IsNaN(_stepStart)) StartStep(now);

        CollectVerification(devices);

        if (Current == Step.GetReady)
        {
            // Durante la espera aprendemos el reposo de cada eje.
            TrackAll(devices, now, detect: false);
            Progress = Math.Min(1, (now - _stepStart) / 1.5);
            if (now - _stepStart >= 1.5) Advance(now, keepTracks: true);
            return;
        }

        if (Current == Step.Clutch && now - _stepStart > ClutchTimeout && !_tracks.Values.Any(t => t.InExcursion))
        {
            Detected.Add("Embrague: no detectado (se omite)");
            Advance(now);
            return;
        }

        var found = TrackAll(devices, now, detect: true);
        if (found == null) return;

        var (device, axis, released, pressed) = found.Value;
        var pedal = new PedalAxis
        {
            DeviceId = device.Id,
            DeviceName = device.Name,
            Axis = axis,
            AxisName = device.AxisNames[axis] ?? WheelInput.AxisLabels[axis],
            Released = released,
            Pressed = pressed,
        };
        string name = Current switch { Step.Brake => "Freno", Step.Throttle => "Acelerador", _ => "Embrague" };
        if (Current == Step.Brake) Result.Brake = pedal;
        else if (Current == Step.Throttle) Result.Throttle = pedal;
        else Result.Clutch = pedal;
        _verify[pedal] = new List<float>();
        Detected.Add($"{name}: eje «{pedal.AxisName}» ✔");
        Log.Info($"Pedal detectado: {name} = {device.Name} eje {WheelInput.AxisLabels[axis]} «{pedal.AxisName}», suelto={released}, a fondo={pressed}");
        Advance(now);
    }

    /// <summary>
    /// Sigue cada eje libre. Devuelve el pedal detectado cuando un eje ha recorrido al menos el 60 % desde su
    /// reposo y ha vuelto a él.
    /// </summary>
    private (WheelDeviceState Device, int Axis, int Released, int Pressed)? TrackAll(
        IReadOnlyList<WheelDeviceState> devices, double now, bool detect)
    {
        (WheelDeviceState, int, int, int)? result = null;
        double bestProgress = 0;
        bool waiting = false;

        foreach (var d in devices)
        {
            for (int a = 0; a < WheelInput.AxisCount; a++)
            {
                if (d.AxisNames[a] == null || a == d.SteeringAxis || IsAssigned(d.Id, a)) continue;
                var key = (d.Id, a);
                if (!_tracks.TryGetValue(key, out var t)) _tracks[key] = t = new AxisTrack();
                int v = d.Values[a];

                t.History.Add((now, v));
                t.History.RemoveAll(h => h.T < now - StableTime);
                bool stable = t.History.Count > 2 && now - t.History[0].T >= StableTime * 0.8
                              && t.History.Max(h => h.V) - t.History.Min(h => h.V) < StableNoise;

                if (!t.InExcursion)
                {
                    if (stable)
                    {
                        t.Baseline = v;
                        t.BaselineKnown = true;
                        t.UntouchedBaseline = v == Untouched && t.History.All(h => h.V == Untouched);
                    }
                    else if (!t.BaselineKnown && t.History.Count > 0)
                    {
                        t.Baseline = t.History[0].V;
                        t.BaselineKnown = true;
                    }
                    if (t.BaselineKnown && Math.Abs(v - t.Baseline) >= MoveThreshold)
                    {
                        t.InExcursion = true;
                        t.Peak = v;
                        t.HeldSince = double.NaN;
                    }
                    continue;
                }

                if (Math.Abs(v - t.Baseline) > Math.Abs(t.Peak - t.Baseline)) t.Peak = v;
                double travel = Math.Abs(t.Peak - t.Baseline);
                bool returned = Math.Abs(v - t.Baseline) <= ReturnTolerance;

                if (stable && !returned)
                {
                    if (t.UntouchedBaseline && travel < MinTravel)
                    {
                        // Primer toque de un eje "sin estrenar": el valor saltó del centro a su reposo real.
                        t.Baseline = v;
                        t.UntouchedBaseline = false;
                        t.InExcursion = false;
                        continue;
                    }
                    if (double.IsNaN(t.HeldSince)) t.HeldSince = now;
                    if (travel >= MinTravel) waiting = true; // pisado a fondo y mantenido: esperamos a que lo suelte
                }
                else t.HeldSince = double.NaN;

                double progress = Math.Min(1, travel / MinTravel) * 0.7 + (travel >= MinTravel && returned ? 0.3 : 0);
                bestProgress = Math.Max(bestProgress, progress);

                if (stable && returned)
                {
                    t.InExcursion = false;
                    if (detect && travel >= MinTravel && result == null)
                        result = (d, a, t.Baseline, t.Peak);
                }
            }
        }

        WaitingForRelease = waiting;
        Progress = bestProgress;
        return result;
    }

    /// <summary>Mientras pisas otros pedales, los ya detectados deberían estar sueltos.</summary>
    private void CollectVerification(IReadOnlyList<WheelDeviceState> devices)
    {
        foreach (var (pedal, samples) in _verify)
        {
            var d = devices.FirstOrDefault(x => x.Id == pedal.DeviceId);
            if (d == null || samples.Count > 2000) continue;
            samples.Add((d.Values[pedal.Axis] - pedal.Released) / (float)(pedal.Pressed - pedal.Released));
        }
    }

    /// <summary>
    /// Comprobación final: si un pedal pasó la mayor parte del tiempo "a fondo" mientras pisabas los demás,
    /// es que quedó al revés, y lo invertimos.
    /// </summary>
    private void VerifyDirections()
    {
        foreach (var (pedal, samples) in _verify)
        {
            if (samples.Count < 30) continue;
            double nearPressed = samples.Count(s => s > 0.85) / (double)samples.Count;
            if (nearPressed < 0.7) continue;
            (pedal.Released, pedal.Pressed) = (pedal.Pressed, pedal.Released);
            Log.Info($"Pedal «{pedal.AxisName}» estaba invertido: corregido automáticamente");
            Detected.Add($"«{pedal.AxisName}» estaba al revés: corregido ✔");
        }
    }

    /// <summary>Salta el paso del embrague.</summary>
    public void SkipClutch(IReadOnlyList<WheelDeviceState> devices, double now)
    {
        if (Current != Step.Clutch) return;
        Detected.Add("Embrague: omitido");
        Advance(now);
    }

    private bool IsAssigned(string dev, int axis) =>
        new[] { Result.Brake, Result.Throttle, Result.Clutch }.Any(a => a.IsSet && a.DeviceId == dev && a.Axis == axis);

    private void Advance(double now, bool keepTracks = false)
    {
        Current = Current + 1;
        if (Current == Step.Done) VerifyDirections();
        StartStep(now, keepTracks);
    }

    private void StartStep(double now, bool keepTracks = false)
    {
        _stepStart = now;
        // Conservamos el reposo aprendido de cada eje; sólo se reinician las pisadas en curso.
        foreach (var t in _tracks.Values)
        {
            t.InExcursion = false;
            t.HeldSince = double.NaN;
        }
        if (!keepTracks)
            foreach (var t in _tracks.Values) t.History.Clear();
        WaitingForRelease = false;
        Progress = 0;
    }
}
