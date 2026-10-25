using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using SimTelemetry.Analysis;
using SimTelemetry.Diagnostics;
using SimTelemetry.Recording;
using SimTelemetry.Telemetry;

namespace SimTelemetry.Engineer;

/// <summary>
/// Un punto de frenada de la vuelta de referencia. <paramref name="Potential"/>: metros que podrías retrasarlo
/// con la frenada máxima que tu coche ya ha demostrado; <paramref name="Spread"/>: tu variación (m) en otras vueltas;
/// <paramref name="Shift"/>: metros que el objetivo progresivo lo retrasa respecto a tu mejor vuelta.
/// </summary>
public sealed record BrakeCue(int Corner, double BrakeAt, double End, double Peak, int Gear, double EntrySpeed, double MinSpeed,
    double Potential = double.NaN, double Spread = double.NaN, double Shift = 0)
{
    // "Toque" sólo si además es una frenada ligera: "toque de freno, 85 %" confunde.
    public string Intensity => EntrySpeed - MinSpeed < 25 && Peak < 0.6 ? "toque" : Peak >= 0.8 ? "fuerte" : Peak >= 0.5 ? "media" : "suave";
    /// <summary>Donde frenaste en tu mejor vuelta.</summary>
    public double RefBrakeAt => BrakeAt - Shift;
}

/// <summary>Tiempo ganado (-) o perdido (+) en el tramo de una curva frente a la referencia.</summary>
public sealed record SectorResult(int Corner, double Delta, DateTime AtUtc);

/// <summary>Las frenadas de tu mejor vuelta en un circuito (o tramo), que el ingeniero te va cantando.</summary>
public sealed class BrakePlan
{
    public required LapInfo Reference { get; init; }
    public required IReadOnlyList<BrakeCue> Cues { get; init; }
    /// <summary>false si la referencia es de otro coche (los puntos de frenada pueden variar).</summary>
    public bool SameCar { get; init; }
    /// <summary>Distancia y tiempo de la vuelta de referencia (para la diferencia en vivo).</summary>
    public double[] RefDistance { get; init; } = Array.Empty<double>();
    public double[] RefTime { get; init; } = Array.Empty<double>();

    public static List<BrakeCue> CuesFrom(LapStats stats) => Build(stats, Array.Empty<LapStats>(), DecelModel.None, progressive: false);

    // Objetivo progresivo: como mucho la mitad de lo que permite el coche, y 15 m por paso.
    private const double MaxStep = 15, MaxSpreadForStep = 12;

    /// <summary>
    /// Frenadas de la vuelta de referencia con su potencial (según la deceleración medida del coche) y tu constancia
    /// en <paramref name="others"/>. Con <paramref name="progressive"/>, en las curvas donde eres constante el punto
    /// se retrasa un paso pequeño: así mejoras poco a poco y la siguiente mejor vuelta trae el siguiente paso.
    /// </summary>
    public static List<BrakeCue> Build(LapStats reference, IReadOnlyList<LapStats> others, DecelModel model, bool progressive) => reference.BrakeZones
        .Where(LapAnalyzer.IsCorner)
        .Select((z, i) =>
        {
            var points = others
                .Select(o => o.BrakeZones.MinBy(m => Math.Abs(m.StartDistance - z.StartDistance)))
                .Where(m => m != null && Math.Abs(m.StartDistance - z.StartDistance) < 80)
                .Select(m => m!.StartDistance)
                .Append(z.StartDistance)
                .ToList();
            double spread = points.Count >= 3 ? Math.Sqrt(points.Sum(p => (p - points.Average()) * (p - points.Average())) / (points.Count - 1)) : double.NaN;
            double potential = model.Potential(z);
            double shift = progressive && potential > 0 && spread <= MaxSpreadForStep ? Math.Round(Math.Min(MaxStep, potential / 2)) : 0;
            if (shift < 3) shift = 0; // menos de 3 m no se nota
            return new BrakeCue(i + 1, z.StartDistance + shift, z.EndDistance, z.PeakBrake, z.Gear, z.StartSpeed, z.MinSpeed, potential, spread, shift);
        })
        .ToList();
}

/// <summary>Rangos de presión en caliente recomendados.</summary>
public static class TyreTargets
{
    // ACC (desde la versión 1.8), neumático de seco de GT3/GT4.
    public const double AccDryMin = 26.0, AccDryMax = 27.0;
}

/// <summary>
/// Ingeniero de carrera por voz: canta las frenadas de tu vuelta de referencia (pitidos de cuenta atrás y
/// "fuerte, segunda"), resume cada vuelta y vigila las presiones de los neumáticos.
/// </summary>
public sealed class RaceEngineer : IDisposable
{
    /// <summary>Lo que tarda el sonido en llegar a tus oídos (Windows + auriculares): los avisos se adelantan eso.</summary>
    private double Lead => Math.Clamp(_settings.AudioDelayMs, 0, 500) / 1000.0;
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private readonly EngineerSettings _settings;
    private readonly string _dataFolder;
    private readonly EngineerAudio _audio;
    private readonly object _gate = new();
    private readonly LinkedList<string> _history = new();

    private string _key = "";
    private string _game = "";
    private volatile BrakePlan? _plan;
    private int _next;
    private byte[] _done = Array.Empty<byte>();
    private double _lastDistance = -1;

    // Neumáticos: media de la vuelta en curso.
    private readonly double[] _pressureSum = new double[4];
    private int _pressureCount;
    private int _completedLaps;
    private int _lapsSinceTyreCall = 99;
    private float _fuel, _fuelPerLap;
    private volatile int _focusCorner;
    private volatile string? _focusFix;

    public RaceEngineer(EngineerSettings settings, string dataFolder)
    {
        _settings = settings;
        _audio = new EngineerAudio(settings.Volume, settings.VoiceId, settings.VoiceRate, settings.VoicePitch);
        _audio.VoicesChanged += () => Changed?.Invoke();
        _dataFolder = dataFolder;
    }

    public BrakePlan? Plan => _plan;
    public string Game => _game;
    public BrakeCue? NextCue { get; private set; }

    /// <summary>Último tramo de curva terminado: cuánto ganaste (-) o perdiste (+) en él frente a la referencia.</summary>
    public SectorResult? LastSector { get; private set; }

    /// <summary>Modo práctica: sólo se avisa esta curva y al salir te dice cómo te fue (0 = desactivado).</summary>
    public int PracticeCorner
    {
        get => _practiceCorner;
        set
        {
            _practiceCorner = value;
            _practiceAttempts = 0;
            _practiceBest = double.NaN;
            Changed?.Invoke();
        }
    }
    private volatile int _practiceCorner;
    public int PracticeAttempts => _practiceAttempts;
    public double PracticeBest => _practiceBest;
    /// <summary>Diferencia (s) con tu vuelta de referencia en este punto de la pista; NaN si no se puede calcular.</summary>
    public double LiveDelta { get; private set; } = double.NaN;
    /// <summary>Presiones actuales (FL, FR, RL, RR); todo 0 si el juego no las da.</summary>
    public float[] Pressures { get; private set; } = new float[4];
    public bool WetTyres { get; private set; }
    public string VoiceName => _audio.VoiceName;
    public bool VoiceIsSpanish => _audio.VoiceIsSpanish;
    public IReadOnlyList<VoiceOption> Voices => _audio.Voices;
    public VoiceOption? CurrentVoice => _audio.Current;

    /// <summary>Aplica la voz, la velocidad y el tono de los ajustes (y vuelve a leer las voces instaladas).</summary>
    public void ConfigureVoice()
    {
        _audio.Configure(_settings.VoiceId, _settings.VoiceRate, _settings.VoicePitch);
        if (_plan is { } plan) PrepareCalls(plan.Cues);
    }

    /// <summary>Con voz moderna, los avisos de cada curva se generan al cargar la referencia: al llegar a la curva, sólo suenan.</summary>
    private void PrepareCalls(IEnumerable<BrakeCue> cues) =>
        _audio.Prepare(cues.SelectMany(c => new[] { CallText(c), RallyCallText(c), Capitalize(c.Intensity) }));

    /// <summary>Lo último que ha dicho el ingeniero (lo más nuevo primero).</summary>
    public IReadOnlyList<string> History
    {
        get { lock (_gate) return _history.ToList(); }
    }

    public event Action? Changed;

    /// <summary>Se llama con cada muestra del juego activo (hilo del lector: debe ser rápido).</summary>
    public void OnSample(ITelemetrySource source, in TelemetrySample s)
    {
        if (!_settings.Enabled) return;

        string key = $"{source.GameName}|{source.Track}|{source.Car}";
        if (key != _key && !string.IsNullOrEmpty(source.Track))
        {
            _key = key;
            _game = source.GameName;
            _plan = null;
            _practiceCorner = 0;
            LastSector = null;
            _completedLaps = 0;
            _lapsSinceTyreCall = 99;
            ResetPressures();
            LoadPlan(key, source.GameName, source.Track!, source.Car ?? "");
        }

        Pressures = new[] { s.PressureFL, s.PressureFR, s.PressureRL, s.PressureRR };
        _fuel = s.Fuel;
        _fuelPerLap = s.FuelPerLap;
        WetTyres = s.WetTyres;
        if (s.InSession && !s.InPit && s.SpeedKmh > 60 && s.PressureFL > 5)
        {
            _pressureSum[0] += s.PressureFL; _pressureSum[1] += s.PressureFR;
            _pressureSum[2] += s.PressureRL; _pressureSum[3] += s.PressureRR;
            _pressureCount++;
        }

        var plan = _plan;
        if (plan == null || !s.InSession || s.InPit || s.LapDistance < 0) { NextCue = null; LiveDelta = double.NaN; FlushDeferred(double.PositiveInfinity); return; }
        var cues = plan.Cues;
        if (_done.Length != cues.Count) _done = new byte[cues.Count];

        double d = s.LapDistance;
        // Diferencia en vivo con la referencia en el mismo punto de la pista (- = vas más rápido).
        double delta = plan.RefDistance.Length > 1 && s.LapTime >= 0 && d <= plan.RefDistance[^1]
            ? s.LapTime - LapAnalyzer.Interpolate(plan.RefDistance, plan.RefTime, d)
            : double.NaN;
        LiveDelta = Math.Abs(delta) < 30 ? delta : double.NaN; // vuelta de salida de boxes o reinicio: no es comparable
        // Vuelta nueva, reinicio de tramo o rebobinado: volvemos a la próxima frenada desde aquí.
        bool wrapped = d < _lastDistance - 30;
        if (wrapped)
        {
            _next = 0;
            Array.Clear(_done);
        }
        TrackSectors(cues, d, wrapped);
        _lastDistance = d;
        while (_next < cues.Count && cues[_next].BrakeAt < d - 3) _next++;
        if (_next >= cues.Count) { NextCue = null; FlushDeferred(double.PositiveInfinity); return; }

        var c = cues[_next];
        NextCue = c;
        double v = s.SpeedKmh / 3.6;
        if (v < 8) { FlushDeferred(double.PositiveInfinity); return; }
        double tt = (c.BrakeAt - d) / v; // s hasta el punto de frenada
        ref byte done = ref _done[_next];
        bool previousFinished = _next == 0 || d >= cues[_next - 1].End;
        // En rally el copiloto ya canta las notas: por defecto sólo pitidos (la voz se pisaría con él).
        bool voiceCalls = _settings.Voice && (!IsRally(_game) || _settings.RallyCalls);
        // El mensaje pendiente tiene que terminar antes de que empiece el aviso de la próxima curva (voz o pitidos).
        double nextCallLead = voiceCalls ? (c.Corner == _focusCorner && _focusFix != null ? 5.2 : 3.6) : 1.2;
        FlushDeferred(previousFinished ? tt - nextCallLead : 0); // en plena curva, tampoco

        double lead = Lead;
#if DEBUG
        if ((done & 8) == 0 && tt <= lead && _settings.Beeps) Log.Info($"Ingeniero[debug]: curva {c.Corner} frena en {c.BrakeAt:0} m, ahora {d:0} m ({tt:0.00} s)");
#endif
        // Modo práctica: sólo la curva elegida (las demás, en silencio).
        if (PracticeCorner > 0 && c.Corner != PracticeCorner) return;
        if (voiceCalls && (done & 1) == 0 && previousFinished && SpeakCall(c, tt, lead)) done |= 1;
        if (_settings.Beeps)
        {
            if ((done & 2) == 0 && tt <= 1.0 + lead)
            {
                done |= 2;
                if (previousFinished && tt > 0.6) _audio.Play(EngineerAudio.Beep.Short, _settings.Volume);
            }
            if ((done & 4) == 0 && tt <= 0.5 + lead)
            {
                done |= 4;
                if (previousFinished && tt > 0.2) _audio.Play(EngineerAudio.Beep.Short, _settings.Volume);
            }
            if ((done & 8) == 0 && tt <= lead)
            {
                done |= 8;
                if (tt > -0.3) _audio.Play(c.Peak >= 0.8 ? EngineerAudio.Beep.BrakeHard
                                        : c.Peak >= 0.5 ? EngineerAudio.Beep.BrakeMedium
                                        : EngineerAudio.Beep.BrakeLight, _settings.Volume);
            }
        }
    }

    /// <summary>
    /// Tiempo ganado o perdido en cada curva, en vivo: de su punto de frenada (en tu referencia) al de la siguiente,
    /// igual que en el análisis. Se cierra al pasar la siguiente frenada (o la meta) y se muestra unos segundos.
    /// </summary>
    private void TrackSectors(IReadOnlyList<BrakeCue> cues, double d, bool wrapped)
    {
        if (wrapped)
        {
            // Meta: cerramos la última curva con la diferencia de justo antes de cruzar (luego el cronómetro vuelve a 0).
            // Un rebobinado (vuelve atrás sin llegar a meta) invalida el tramo en curso.
            bool finishLine = _lastDistance > (cues.Count > 0 ? cues[^1].RefBrakeAt : 0) && d < 300;
            if (finishLine) CloseSector(_lastLiveDelta);
            _sectorCorner = 0;
        }
        int current = 0;
        foreach (var c in cues)
            if (c.RefBrakeAt <= d) current = c.Corner;
        double delta = LiveDelta;
        if (current != _sectorCorner && double.IsFinite(delta))
        {
            if (_sectorCorner > 0 && current == _sectorCorner + 1) CloseSector(delta);
            _sectorCorner = current;
            _sectorStartDelta = delta;
        }
        if (double.IsFinite(delta)) _lastLiveDelta = delta;
    }

    private int _sectorCorner;
    private double _sectorStartDelta, _lastLiveDelta = double.NaN;
    private int _practiceAttempts;
    private double _practiceBest = double.NaN;

    private void CloseSector(double endDelta)
    {
        if (_sectorCorner <= 0 || !double.IsFinite(endDelta) || !double.IsFinite(_sectorStartDelta)) return;
        double sector = endDelta - _sectorStartDelta;
        if (Math.Abs(sector) > 10) return; // no es un tramo comparable (salida de boxes, reinicio…)
        LastSector = new SectorResult(_sectorCorner, sector, DateTime.UtcNow);
#if DEBUG
        Log.Info($"Ingeniero[debug]: tramo curva {_sectorCorner}: {sector:+0.000;-0.000} s (diferencia acumulada {endDelta:+0.000;-0.000})");
#endif
        if (PracticeCorner == _sectorCorner)
        {
            _practiceAttempts++;
            bool best = double.IsNaN(_practiceBest) || sector < _practiceBest;
            if (best) _practiceBest = sector;
            Defer($"Curva {_sectorCorner}: {SpeakSigned(sector)}" + (best && _practiceAttempts > 1 ? ". Mejor intento." : "."));
        }
        Changed?.Invoke();
    }

    /// <summary>-0.12 → "una décima más rápido"; 0.34 → "3 décimas más lento".</summary>
    public static string SpeakSigned(double seconds) =>
        Math.Abs(seconds) < 0.05 ? "igual que tu referencia" : $"{SpeakDelta(seconds)} {(seconds < 0 ? "más rápido" : "más lento")}";

    /// <summary>
    /// Aviso de voz de una curva, sincronizado: cada frase empieza para terminar antes del primer pitido (1 s antes del
    /// punto de frenada). Si ya no da tiempo para la frase entera se dice una más corta, y si tampoco, nada: un aviso tarde confunde.
    /// Devuelve true cuando la curva ya está resuelta (dicha o descartada).
    /// </summary>
    private bool SpeakCall(BrakeCue c, double tt, double lead)
    {
        bool focus = c.Corner == _focusCorner && _focusFix != null;
        string call = IsRally(_game) ? RallyCallText(c) : CallText(c);
        var variants = focus
            ? new[] { $"Curva {c.Corner}: {_focusFix}. {call}", $"Curva {c.Corner}: {_focusFix}", call, Capitalize(c.Intensity) }
            : new[] { call, Capitalize(c.Intensity) };
        foreach (var text in variants)
        {
            double speech = _audio.SpeechSeconds(text);
            if (tt > speech + 1.1 + lead) return false;       // aún es pronto para la frase más completa: esperamos
            if (tt < speech + 0.3 + lead) continue;           // ya no cabe: probamos una más corta
            Say(text, urgent: true, log: focus);
#if DEBUG
            Log.Info($"Ingeniero[debug]: voz curva {c.Corner} a {tt:0.00} s del punto ({speech:0.0} s de frase): {text}");
#endif
            if (focus) _focusCorner = 0;                      // el recordatorio, una sola vez
            return true;
        }
        return true; // no cabe ninguna: mejor callar que avisar tarde
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    // Como hacen los ingenieros (y Crew Chief): lo que no es urgente no se dice a punto de frenar o en plena curva,
    // se guarda hasta que haya una recta con tiempo para decirlo entero. Si en 40 s no la hay, ya no vale y se descarta.
    private volatile string? _deferred;
    private DateTime _deferredAt;

    private void Defer(string text)
    {
        _deferredAt = DateTime.UtcNow;
        _deferred = text;
        Changed?.Invoke();
    }

    /// <summary>Dice el mensaje pendiente si cabe entero en los segundos libres antes del próximo aviso.</summary>
    private void FlushDeferred(double freeSeconds)
    {
        if (_deferred is not string text) return;
        if ((DateTime.UtcNow - _deferredAt).TotalSeconds > 40)
        {
            _deferred = null;
            Log.Info("Ingeniero: mensaje descartado (no hubo una recta para decirlo): " + text);
            return;
        }
        if (freeSeconds < _audio.SpeechSeconds(text) + 0.5) return;
        _deferred = null;
        Say(text);
    }

    /// <summary>Al guardarse una vuelta: tiempo, diferencia con tu mejor vuelta, dónde pierdes y neumáticos.</summary>
    public void OnLapSaved(LapInfo info)
    {
        if (!_settings.Enabled || $"{info.Game}|{info.Track}|{info.Car}" != _key) return;
        var avg = _pressureCount > 200 ? _pressureSum.Select(p => p / _pressureCount).ToArray() : null;
        ResetPressures();
        if (!info.Completed || info.TimeBased) return;
        _completedLaps++;
        _lapsSinceTyreCall++;
        var plan = _plan;
        bool tyres = avg != null && _game == "Assetto Corsa Competizione" && !WetTyres && _completedLaps >= 2 && _lapsSinceTyreCall >= 3;
        if (tyres) _lapsSinceTyreCall = 0;
        // Combustible: siempre que queden pocas vueltas y, si no, cada 5 vueltas.
        double fuelLaps = _fuelPerLap > 0 ? _fuel / _fuelPerLap : -1;
        string fuel = fuelLaps < 0 ? ""
            : fuelLaps < 1.5 ? "Combustible para menos de dos vueltas: entra a boxes"
            : fuelLaps < 3.5 || _completedLaps % 5 == 0 ? $"Combustible para {Math.Floor(fuelLaps):0} vueltas"
            : "";

        Task.Run(() =>
        {
            try
            {
                var parts = new List<string>();
                double best = plan?.Reference.LapTime ?? 0;
                parts.Add($"{(IsRally(info.Game) ? "Tramo" : "Vuelta")} {SpeakTime(info.LapTime)}");
                bool newBest = info.Valid && (best <= 0 || info.LapTime < best - 0.0005);
                if (!info.Valid) parts.Add(info.EndReason.Contains("no sirve de referencia") ? "no la uso como referencia" : "invalidada");
                else if (newBest) parts.Add(best > 0 ? $"tu mejor tiempo, {SpeakDelta(best - info.LapTime)} más rápido" : "tu mejor tiempo");
                else parts.Add($"{SpeakDelta(info.LapTime - best)} más lento que tu mejor");

                if (plan != null && !newBest) parts.Add(WhereYouLose(info, plan));
                if (tyres) parts.Add(TyreAdvice(avg!));
                parts.Add(fuel);
                Defer(string.Join(". ", parts.Where(p => p.Length > 0).Select(p => char.ToUpper(p[0]) + p[1..])) + ".");

                // La vuelta nueva pasa a ser la referencia de los avisos (y una válida con este coche sustituye a la de otro coche).
                if (newBest || (info.Valid && plan is { SameCar: false })) LoadPlan(_key, info.Game, info.Track, info.Car, announce: false);
            }
            catch (Exception ex)
            {
                Log.Error("Ingeniero: error al resumir la vuelta", ex);
            }
        });
    }

    /// <summary>Prueba de sonido desde la app.</summary>
    public void Test()
    {
        Say("Radio comprobada. Frena fuerte, 90 por ciento, segunda.");
        Task.Run(async () =>
        {
            await Task.Delay(2200);
            _audio.Play(EngineerAudio.Beep.Short, _settings.Volume);
            await Task.Delay(500);
            _audio.Play(EngineerAudio.Beep.Short, _settings.Volume);
            await Task.Delay(500);
            _audio.Play(EngineerAudio.Beep.BrakeHard, _settings.Volume);
        });
    }

    private void LoadPlan(string key, string game, string track, string car, bool announce = true)
    {
        Task.Run(() =>
        {
            try
            {
                var laps = LapRecorder.ListLaps(_dataFolder)
                    .Where(l => l.Game == game && l.Track == track && l.Completed && l.Valid && !l.TimeBased && l.LapTime > 0)
                    .ToList();
                var same = laps.Where(l => l.Car == car).MinBy(l => l.LapTime);
                var reference = same ?? laps.MinBy(l => l.LapTime);
                if (_key != key) return;
                if (reference == null)
                {
                    _plan = null;
                    if (announce) Say(IsRally(game)
                        ? "Sin referencia en este tramo todavía. Termínalo una vez y te cantaré las frenadas."
                        : "Sin referencia en este circuito todavía. Haz una vuelta limpia y te cantaré las frenadas.");
                    Changed?.Invoke();
                    return;
                }

                // Tus otras vueltas buenas con ese coche: para medir cuánto frena y lo constante que eres.
                var others = laps.Where(l => l.Car == reference.Car && l.FilePath != reference.FilePath)
                    .OrderBy(l => l.LapTime).Take(10)
                    .Select(l => { try { return LapFile.Read(l.FilePath); } catch (Exception) { return null; } })
                    .Where(d => d != null && d.Count > 1).Cast<LapData>().ToList();
                var refData = LapFile.Read(reference.FilePath);
                var cues = BrakePlan.Build(LapAnalyzer.Compute(refData), others.Select(LapAnalyzer.Compute).ToList(),
                    DecelModel.Build(others.Prepend(refData)), _settings.Progressive);
                if (_key != key) return;
                _plan = new BrakePlan
                {
                    Reference = reference, Cues = cues, SameCar = same != null,
                    RefDistance = refData.Distance, RefTime = refData.Time.Select(t => t - refData.Time[0]).ToArray(),
                };
                _done = new byte[cues.Count];
                _next = 0;
                PrepareCalls(cues);
                Log.Info($"Ingeniero: referencia {reference.TimeText} ({reference.FilePath}), {cues.Count} frenadas");
                int later = cues.Count(c => c.Shift > 0);
                if (announce)
                    Say($"Referencia {SpeakTime(reference.LapTime)}{(same == null ? ", de otro coche" : "")}. {cues.Count} frenadas" +
                        (later > 0 ? $"; en {later} te pediré frenar un poco más tarde." : "."));
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("Ingeniero: no se pudo cargar la referencia", ex);
            }
        });
    }

    /// <summary>La curva donde más tiempo se perdió frente a la referencia, con la causa probable.</summary>
    /// <summary>La curva donde más tiempo se perdió frente a la referencia, con la causa; y qué recordarte en la próxima vuelta.</summary>
    private string WhereYouLose(LapInfo info, BrakePlan plan)
    {
        var lap = LapFile.Read(info.FilePath);
        var reference = LapFile.Read(plan.Reference.FilePath);
        if (lap.Count < 2 || reference.Count < 2 || plan.Cues.Count == 0) return "";
        var worst = LapAnalyzer.CornerLosses(lap, LapAnalyzer.Compute(lap), reference, LapAnalyzer.Compute(reference)).MaxBy(c => c.Loss);
        if (worst == null || worst.Loss < 0.15) return "";
        // En la próxima vuelta, al llegar a esa curva, te lo recordamos (como un coach por radio).
        _focusCorner = worst.Fix != null ? worst.Corner : 0;
        _focusFix = worst.Fix;
        if (worst.Fix != null && plan.Cues.FirstOrDefault(c => c.Corner == worst.Corner) is { } cue)
        {
            string call = IsRally(info.Game) ? RallyCallText(cue) : CallText(cue);
            _audio.Prepare(new[] { $"Curva {cue.Corner}: {worst.Fix}. {call}", $"Curva {cue.Corner}: {worst.Fix}" });
        }
        string cause = worst.Causes.Count > 0 ? ": " + worst.Causes[0].Replace("km/h", "kilómetros por hora") : "";
        return $"Donde más pierdes: curva {worst.Corner}, {SpeakDelta(worst.Loss)}{cause}";
    }

    private static string TyreAdvice(double[] avg)
    {
        string Axle(string name, double p)
        {
            double mid = (TyreTargets.AccDryMin + TyreTargets.AccDryMax) / 2;
            if (p > TyreTargets.AccDryMax + 0.1) return $"{name} altas, {p.ToString("0.0", Es)}: baja {(p - mid).ToString("0.0", Es)} en frío";
            if (p < TyreTargets.AccDryMin - 0.1) return $"{name} bajas, {p.ToString("0.0", Es)}: sube {(mid - p).ToString("0.0", Es)} en frío";
            return "";
        }
        var parts = new[] { Axle("Delanteras", (avg[0] + avg[1]) / 2), Axle("Traseras", (avg[2] + avg[3]) / 2) }
            .Where(p => p.Length > 0).ToList();
        return parts.Count == 0 ? "Presiones en su punto" : "Presiones: " + string.Join("; ", parts);
    }

    private static bool IsRally(string game) => game.Contains("DiRT") || game.Contains("WRC");

    /// <summary>Rally: "Fuerte 90" (dos palabras, para no taparse con las notas del copiloto).</summary>
    internal static string RallyCallText(BrakeCue c) => $"{Capitalize(c.Intensity)} {(int)Math.Round(c.Peak * 20) * 5}";

    /// <summary>"Frena fuerte, 90 por ciento, segunda": intensidad, presión de freno (de 5 en 5) y marcha.</summary>
    internal static string CallText(BrakeCue c)
    {
        string gear = c.Gear switch
        {
            1 => "primera", 2 => "segunda", 3 => "tercera", 4 => "cuarta",
            5 => "quinta", 6 => "sexta", 7 => "séptima", 8 => "octava", _ => "",
        };
        int percent = (int)Math.Round(c.Peak * 20) * 5;
        string text = c.Intensity == "toque" ? $"Toque de freno, {percent} por ciento" : $"Frena {c.Intensity}, {percent} por ciento";
        return gear.Length > 0 ? $"{text}, {gear}" : text;
    }

    /// <summary>1:47.312 → "1, 47, 3" (como lo dicen los ingenieros: uno cuarenta y siete tres).</summary>
    public static string SpeakTime(double seconds)
    {
        int tenths = (int)Math.Round(seconds * 10);
        int m = tenths / 600, s = tenths / 10 % 60, t = tenths % 10;
        return m > 0 ? $"{m}, {s:00}, {t}" : $"{s}, {t}";
    }

    /// <summary>0.34 → "3 décimas"; 1.26 → "1,3 segundos".</summary>
    public static string SpeakDelta(double seconds)
    {
        seconds = Math.Abs(seconds);
        if (seconds >= 0.95) return $"{seconds.ToString("0.0", Es)} segundos";
        int tenths = (int)Math.Round(seconds * 10);
        return tenths == 0 ? "menos de una décima" : tenths == 1 ? "una décima" : $"{tenths} décimas";
    }

    private void Say(string text, bool urgent = false, bool log = true)
    {
        if (!_settings.Voice) return;
        _audio.Say(text, _settings.Volume, urgent);
        lock (_gate)
        {
            _history.AddFirst($"{DateTime.Now:HH:mm:ss}  {text}");
            while (_history.Count > 30) _history.RemoveLast();
        }
        if (log) Log.Info($"Ingeniero: {text}");
        Changed?.Invoke();
    }

    private void ResetPressures()
    {
        Array.Clear(_pressureSum);
        _pressureCount = 0;
    }

    public void Dispose() => _audio.Dispose();
}
