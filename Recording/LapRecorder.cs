using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SimTelemetry.Diagnostics;
using SimTelemetry.Telemetry;

namespace SimTelemetry.Recording;

/// <summary>
/// Corta la telemetría en vueltas (o tramos de rally) y guarda cada una en
/// Documentos\SimTelemetry\&lt;sesión&gt;\lap_NNN.csv.
/// </summary>
public sealed class LapRecorder
{
    private readonly object _gate = new();
    private readonly List<RecordedPoint> _points = new();

    private string? _sessionFolder;
    private string? _sessionSourceId;
    private string _game = "", _track = "", _car = "";
    private int _savedLaps;
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private DateTime _lastDrivingUtc = DateTime.MinValue;

    private TelemetrySample _prev;
    private bool _hasPrev;
    private double _lapStartTime;
    private double _lastBoundaryTime = -1e9;
    private float _maxRpm;
    private float _trackLength;
    private bool _startedAtLine;
    private bool _lapInvalid;
    private int _abs = -1;
    private double _integratedDistance;
    private bool _waitForStageReset;
    private bool _startsAtLine; // la próxima vuelta empieza justo al cruzar meta
    private bool _beforeWrap;   // la distancia aún es la del final de la vuelta anterior

    // Vuelta recién terminada que espera unos instantes el tiempo oficial del juego (iLastTime, LapLastLapTime…),
    // que en algunos juegos llega un poco después de que detectemos la meta.
    private RecordedPoint[]? _pendingPoints;
    private LapInfo? _pendingInfo;
    private double _pendingSince;
    private float _pendingLastLapBefore;

    public LapRecorder(string rootFolder) => RootFolder = rootFolder;

    public string RootFolder { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Pausa temporal (p. ej. mientras se usa el entrenador de pedales en boxes).</summary>
    public bool Paused { get; set; }

    public int CurrentLapPoints { get { lock (_gate) return _points.Count; } }
    public string? SessionFolder { get { lock (_gate) return _sessionFolder; } }

    /// <summary>Se dispara (en un hilo de fondo) cuando se guarda una vuelta.</summary>
    public event Action<LapInfo>? LapSaved;

    public void Add(ITelemetrySource source, in TelemetrySample s)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            if (Paused)
            {
                _points.Clear();
                _hasPrev = false;
                return;
            }

            var now = DateTime.UtcNow;
            bool newSession = _sessionSourceId != source.Id
                              || (now - _lastSampleUtc).TotalSeconds > 120
                              || (!string.IsNullOrEmpty(source.Track) && _track.Length > 0 && source.Track != _track);
            _lastSampleUtc = now;

            if (newSession)
            {
                FinishLap(completed: false, lapTime: null, "nueva sesión");
                StartSession(source);
            }
            if (string.IsNullOrEmpty(_track) && !string.IsNullOrEmpty(source.Track)) _track = source.Track!;
            if (string.IsNullOrEmpty(_car) && !string.IsNullOrEmpty(source.Car)) _car = source.Car!;
            _game = source.GameName;

            if (!s.InSession) return;
            if (_hasPrev && s.Time == _prev.Time) return; // pausa o paquete repetido
            _lastDrivingUtc = now;

            if (_hasPrev) DetectBoundary(s);
            ResolvePending(s);

            if (_waitForStageReset)
            {
                _prev = s;
                _hasPrev = true;
                return;
            }

            Append(s);

            // Rally: el tramo termina al cruzar la meta (la distancia no se reinicia sola).
            if (s.Lap <= 0 && s.TrackLength > 0 && s.LapDistance >= s.TrackLength && _points.Count > 0)
            {
                FinishLap(completed: true, lapTime: s.LapTime > 0 ? s.LapTime : null, "meta del tramo");
                _waitForStageReset = true;
            }

            _prev = s;
            _hasPrev = true;
        }
    }

    private void DetectBoundary(in TelemetrySample s)
    {
        bool recentBoundary = s.Time - _lastBoundaryTime < 3;
        bool lapIncremented = s.Lap >= 0 && _prev.Lap >= 0 && s.Lap > _prev.Lap;
        bool distanceReset = _prev.LapDistance > 200 && s.LapDistance >= 0 && s.LapDistance < _prev.LapDistance - 200;
        bool lapTimeReset = _prev.LapTime > 5 && s.LapTime >= 0 && s.LapTime < _prev.LapTime - 2;

        // Cruce de meta: la posición en la vuelta pasa de ~1 a ~0 (lo más fiable cuando el juego la da).
        bool progressWrap = _prev.LapProgress > 0.9f && s.LapProgress >= 0 && s.LapProgress < 0.1f;
        bool nearLine = s.LapProgress < 0 || s.LapProgress < 0.05f || s.LapProgress > 0.95f;
        bool crossedLine = progressWrap
                           || distanceReset && s.TrackLength > 0 && _prev.LapDistance > s.TrackLength * 0.9 && s.LapDistance < 200
                           || lapTimeReset && s.LapProgress >= 0 && nearLine && s.SpeedKmh > 30 && !s.InPit;

        if (s.Time < _prev.Time - 0.5)
        {
            FinishLap(completed: false, lapTime: null, "el reloj del juego se reinició");
            _waitForStageReset = false;
        }
        else if (recentBoundary)
        {
            // La vuelta ya se cerró hace un instante por otra señal (contador, posición o cronómetro).
        }
        else if (lapIncremented || crossedLine)
        {
            double? time = lapIncremented && s.LastLapTime > 0 ? s.LastLapTime : _prev.LapTime > 0 ? _prev.LapTime : null;
            FinishLap(completed: true, lapTime: time, lapIncremented ? "contador de vueltas" : "cruce de meta");
            _lastBoundaryTime = s.Time;
            _startsAtLine = true;
        }
        else if (distanceReset || lapTimeReset)
        {
            // Distancia o cronómetro reiniciados sin cruzar meta (reinicio de tramo, vuelta a boxes…).
            FinishLap(completed: false, lapTime: null, distanceReset ? "la distancia se reinició" : "el cronómetro se reinició");
            _waitForStageReset = false;
            _lastBoundaryTime = s.Time;
        }
    }

    private void ResolvePending(in TelemetrySample s)
    {
        if (_pendingInfo == null) return;
        bool officialArrived = s.LastLapTime > 0 && Math.Abs(s.LastLapTime - _pendingLastLapBefore) > 0.0005
                               && Math.Abs(s.LastLapTime - _pendingInfo.LapTime) < 3;
        if (officialArrived)
        {
            _pendingInfo = _pendingInfo with { LapTime = s.LastLapTime };
            WritePending("tiempo oficial del juego");
        }
        else if (s.Time - _pendingSince > 3)
        {
            WritePending("sin tiempo oficial");
        }
    }

    private void WritePending(string note)
    {
        if (_pendingInfo == null || _pendingPoints == null) return;
        var info = _pendingInfo;
        var points = _pendingPoints;
        _pendingInfo = null;
        _pendingPoints = null;
        Save(info, points, note);
    }

    /// <summary>Llamar periódicamente: cierra la vuelta si el juego dejó de enviar datos de conducción.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (_pendingInfo != null && (DateTime.UtcNow - _lastDrivingUtc).TotalSeconds > 3) WritePending("sin más datos");
            if (_points.Count > 0 && (DateTime.UtcNow - _lastDrivingUtc).TotalSeconds > 8)
            {
                bool completed = _trackLength > 0 && _prev.Lap <= 0 && _points[^1].Distance >= _trackLength * 0.98;
                FinishLap(completed, completed && _prev.LapTime > 0 ? _prev.LapTime : null, "sin datos de conducción");
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            FinishLap(completed: false, lapTime: null, "grabación detenida");
            WritePending("grabación detenida");
        }
    }

    private void StartSession(ITelemetrySource source)
    {
        WritePending("nueva sesión");
        _sessionSourceId = source.Id;
        _game = source.GameName;
        _track = source.Track ?? "";
        _car = source.Car ?? "";
        _savedLaps = 0;
        _hasPrev = false;
        _waitForStageReset = false;
        _lastBoundaryTime = -1e9;
        _sessionFolder = null; // se crea al guardar la primera vuelta
        Log.Info($"Nueva sesión: {_game} {_track} {_car}");
    }

    private void Append(in TelemetrySample s)
    {
        float distance = s.LapDistance;
        bool lineStart = _startsAtLine;
        _startsAtLine = false;
        if (_points.Count == 0)
        {
            // Si acabamos de detectar la meta pero el cronómetro del juego aún no se ha puesto a cero
            // (llega unas centésimas más tarde), la vuelta empieza ahora.
            bool lapTimeUsable = s.LapTime >= 0 && (!lineStart || s.LapTime < 3);
            _lapStartTime = lapTimeUsable ? s.Time - s.LapTime : s.Time;
            _pauseOffset = 0;
            _maxRpm = 0;
            _integratedDistance = 0;
            _lapInvalid = false;
        }

        // Justo tras cruzar meta algunos juegos aún dan la distancia del final de la vuelta anterior:
        // hasta que la distancia "da la vuelta", la contamos como negativa (metros antes de la línea).
        if (_points.Count == 0) _beforeWrap = s.TrackLength > 0 && distance > s.TrackLength * 0.5;
        if (_beforeWrap)
        {
            if (distance > s.TrackLength * 0.5) distance -= s.TrackLength;
            else _beforeWrap = false;
        }

        if (_points.Count == 0)
        {
            // Si empezamos a grabar a mitad de vuelta (app abierta tarde, salida de boxes…) no es una vuelta válida.
            bool progressAtLine = s.LapProgress < 0 || s.LapProgress < 0.05f || s.LapProgress > 0.95f;
            _startedAtLine = lineStart
                ? progressAtLine && !s.InPit
                : distance < 60 && s.LapTime < 3 && progressAtLine && !s.InPit;
        }
        else
        {
            var last = _points[^1];
            // Pausa a mitad de vuelta: el reloj sigue (ACC usa el nuestro) pero el coche no se movió. Descontamos
            // ese hueco para que los tiempos por curva y la comparación con la referencia sigan cuadrando.
            double gap = s.Time - _lapStartTime - _pauseOffset - last.Time;
            double moved = Math.Abs((s.LapDistance >= 0 ? distance : _integratedDistance) - last.Distance);
            if (gap > 0.5 && moved < 5 + s.SpeedKmh / 3.6 * 0.5) _pauseOffset += gap - 1.0 / 60;
            double dt = Math.Clamp(s.Time - _lapStartTime - _pauseOffset - last.Time, 0, 0.5);
            _integratedDistance += s.SpeedKmh / 3.6 * dt;
        }

        _maxRpm = Math.Max(_maxRpm, s.MaxRpm > 0 ? s.MaxRpm : 0);
        if (s.TrackLength > 0) _trackLength = s.TrackLength;
        if (s.LapValid == 0) _lapInvalid = true;
        if (s.Abs >= 0) _abs = s.Abs;

        _points.Add(new RecordedPoint
        {
            Time = s.Time - _lapStartTime - _pauseOffset,
            Distance = s.LapDistance >= 0 ? distance : _integratedDistance,
            Speed = s.SpeedKmh,
            Throttle = s.Throttle,
            Brake = s.Brake,
            Clutch = s.Clutch,
            Steering = s.Steering,
            Handbrake = Math.Max(0, s.Handbrake),
            Gear = s.Gear,
            Rpm = s.Rpm,
            LatG = s.LatG,
            LonG = s.LonG,
            PosX = s.PosX,
            PosZ = s.PosZ,
        });
    }

    private double _pauseOffset;

    private void FinishLap(bool completed, double? lapTime, string reason)
    {
        if (_points.Count == 0) return;
        var points = _points.ToArray();
        _points.Clear();

        double duration = points[^1].Time - points[0].Time;
        double travelled = points.Max(p => p.Distance) - points.Min(p => p.Distance);
        float topSpeed = points.Max(p => p.Speed);
        // Fragmentos sin interés: muy cortos, o parado en boxes / garaje.
        if (duration < 5 || points.Length < 50 || travelled < 300 || topSpeed < 30)
        {
            if (duration >= 5) Log.Info($"Descartado fragmento de {duration:0.0} s / {travelled:0} m ({reason})");
            return;
        }

        completed &= _startedAtLine;
        // Rebobinados, saltos, cortes o distancias imposibles: la vuelta se guarda, pero no sirve de referencia.
        var issues = LapQuality.Check(points, stage: _game.Contains("DiRT") || _game.Contains("WRC"));
        if (issues.Count > 0)
        {
            _lapInvalid = true;
            reason += " · no sirve de referencia: " + string.Join("; ", issues);
        }
        double time = lapTime ?? points[^1].Time;

        _savedLaps++;
        _sessionFolder ??= CreateSessionFolder();
        var info = new LapInfo
        {
            FilePath = Path.Combine(_sessionFolder, $"lap_{_savedLaps:000}.csv"),
            Game = _game,
            Track = _track,
            Car = _car,
            Number = _savedLaps,
            LapTime = time,
            Completed = completed,
            Valid = !_lapInvalid,
            Abs = _abs,
            EndReason = reason,
            Date = DateTime.Now,
            Length = points[^1].Distance,
            MaxRpm = _maxRpm,
        };
        Log.Info($"Vuelta {info.Number}: {info.TimeText}, completa={completed} (empezó en meta={_startedAtLine}), " +
                 $"válida={info.Valid}, {travelled:0} m, fin: {reason}");

        WritePending("llegó otra vuelta");
        if (completed && _hasPrev)
        {
            // Esperamos un momento por si el juego publica su tiempo oficial de la vuelta.
            _pendingInfo = info;
            _pendingPoints = points;
            _pendingSince = _prev.Time;
            _pendingLastLapBefore = _prev.LastLapTime;
            return;
        }
        Save(info, points, "");
    }

    private void Save(LapInfo info, RecordedPoint[] points, string note)
    {
        if (note.Length > 0) Log.Info($"Vuelta {info.Number} guardada: {info.TimeText} ({note})");
        Task.Run(() =>
        {
            try
            {
                LapFile.Write(info.FilePath, info, points);
                LapSaved?.Invoke(info);
            }
            catch (Exception ex)
            {
                // Disco lleno o carpeta bloqueada: no detenemos la telemetría por ello.
                Log.Error("No se pudo guardar la vuelta", ex);
            }
        });
    }

    private string CreateSessionFolder()
    {
        string folder = SessionPath(RootFolder, _game, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}" + (_track.Length > 0 ? " - " + _track : ""));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Carpeta de una sesión nueva: una subcarpeta por juego (Documentos\SimTelemetry\&lt;juego&gt;\&lt;fecha - pista&gt;).</summary>
    public static string SessionPath(string root, string game, string name)
    {
        static string Safe(string s, int max)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim().TrimEnd('.');
            return s.Length > max ? s[..max] : s;
        }
        return Path.Combine(root, Safe(game.Length > 0 ? game : "Otros", 60), Safe(name, 100));
    }

    /// <summary>Lista todas las vueltas guardadas, de la más reciente a la más antigua.</summary>
    public static List<LapInfo> ListLaps(string root)
    {
        var result = new List<LapInfo>();
        if (!Directory.Exists(root)) return result;
        foreach (var file in Directory.EnumerateFiles(root, "lap_*.csv", SearchOption.AllDirectories))
        {
            try { result.Add(LapFile.ReadInfo(file)); }
            catch (IOException) { }
        }
        return result.OrderByDescending(l => l.Date).ToList();
    }
}
