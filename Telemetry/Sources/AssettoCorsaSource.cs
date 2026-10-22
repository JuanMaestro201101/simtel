using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using SimTelemetry.Tracks;

namespace SimTelemetry.Telemetry.Sources;

/// <summary>
/// Assetto Corsa Competizione y Assetto Corsa (memoria compartida acpmf_physics / graphics / static).
/// No requiere configuración en el juego.
/// </summary>
public sealed class AssettoCorsaSource : SharedMemorySourceBase
{
    // SPageFilePhysics
    private const int PhysPacketId = 0, PhysGas = 4, PhysBrake = 8, PhysFuel = 12, PhysGear = 16, PhysRpm = 20,
        PhysSteer = 24, PhysSpeedKmh = 28, PhysAccGX = 44, PhysAccGZ = 52, PhysPressure = 88, PhysClutch = 364;

    // SPageFileGraphic (mismo inicio en AC y ACC; a partir de activeCars sólo ACC)
    private const int GfxStatus = 4, GfxCompletedLaps = 132, GfxCurrentTimeMs = 140, GfxLastTimeMs = 144,
        GfxDistanceTraveled = 156, GfxIsInPit = 160, GfxNormalizedPos = 248;
    private const int AccActiveCars = 252, AccCarCoordinates = 256, AccCarIds = 976, AccPlayerCarId = 1216,
        AccIsInPitLane = 1236, AccAbs = 1280, AccFuelPerLap = 1284, AccRainTyres = 1316, AccIsValidLap = 1408;

    // SPageFileStatic
    private const int StaCarModel = 68, StaTrack = 134, StaMaxRpm = 412, StaSplineLength = 520;

    private MemoryMappedFile? _physFile, _gfxFile, _staFile;
    private MemoryMappedViewAccessor? _phys, _gfx, _sta;
    // Reloj propio que nunca se reinicia: si el juego se reconecta, el tiempo no "retrocede".
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _lastPacketId = int.MinValue;
    private DateTime _lastChangeUtc;
    private float _maxRpm;
    private DateTime _nextStaticRead;
    private bool _isAcc;
    private DateTime _nextProcessCheck;
    private bool _fixedLength;

    // Medición de la longitud del circuito con el odómetro del juego.
    private double _trackLength;
    private float _prevPos = -1;
    private double _odoAtLine = double.NaN;
    private double _odoRef = double.NaN;
    private float _posRef = -1;
    private double _ownOdometer;
    private double _lastPollTime;

    public AssettoCorsaSource() { GameName = "Assetto Corsa Competizione / Assetto Corsa"; }

    public override string Id => "assetto";
    public override string DisplayName => "Assetto Corsa Competizione / AC (memoria compartida)";

    protected override bool IsConnected => _phys != null;

    protected override bool TryConnect()
    {
        // G HUB (y otras apps) dejan abierta la memoria de AC con el juego cerrado: sin el proceso, son datos viejos.
        if (!GameRunning()) return false;
        _phys = OpenView("Local\\acpmf_physics", out _physFile);
        if (_phys == null) return false;
        _gfx = OpenView("Local\\acpmf_graphics", out _gfxFile);
        _sta = OpenView("Local\\acpmf_static", out _staFile);
        if (_gfx == null || _sta == null)
        {
            Disconnect();
            return false;
        }

        _isAcc = Process.GetProcessesByName("AC2-Win64-Shipping").Length > 0;
#if DEBUG
        // Pruebas con tools/simulador.py --juego acc (no hay proceso de ACC).
        if (Environment.GetEnvironmentVariable("SIMTELEMETRY_FORCE_ACC") == "1") _isAcc = true;
#endif
        GameName = _isAcc ? "Assetto Corsa Competizione"
                 : Process.GetProcessesByName("acs").Length > 0 ? "Assetto Corsa"
                 : "Assetto Corsa / ACC";
        _lastChangeUtc = DateTime.UtcNow;
        _lastPacketId = int.MinValue;
        _nextStaticRead = DateTime.MinValue;
        ResetLengthMeasurement();
        return true;
    }

    private void ResetLengthMeasurement()
    {
        _prevPos = -1;
        _odoAtLine = double.NaN;
        _odoRef = double.NaN;
        _posRef = -1;
    }

    protected override bool Poll()
    {
        if (_phys == null || _gfx == null || _sta == null) return false;

        int status = _gfx.ReadInt32(GfxStatus); // 0 off, 1 repetición, 2 en vivo, 3 pausa
        int packetId = _phys.ReadInt32(PhysPacketId);

        if (packetId == _lastPacketId)
        {
            // Sin datos nuevos (menús, pausa o juego cerrado): sólo soltamos el mapeo si el proceso ya no está.
            if ((DateTime.UtcNow - _lastChangeUtc).TotalSeconds < 10) return true;
            if (DateTime.UtcNow < _nextProcessCheck) return true;
            _nextProcessCheck = DateTime.UtcNow.AddSeconds(10);
            return GameRunning();
        }
        _lastPacketId = packetId;
        _lastChangeUtc = DateTime.UtcNow;

        if (DateTime.UtcNow >= _nextStaticRead)
        {
            _nextStaticRead = DateTime.UtcNow.AddSeconds(5);
            string? track = NullIfEmpty(ReadWideString(_sta, StaTrack, 33));
            if (track != Track)
            {
                Track = track;
                ResetLengthMeasurement();
                _trackLength = 0;
                _fixedLength = false;
            }
            Car = NullIfEmpty(ReadWideString(_sta, StaCarModel, 33));
            int maxRpm = _sta.ReadInt32(StaMaxRpm);
            _maxRpm = maxRpm is > 1000 and < 30000 ? maxRpm : 0;

            // AC publica la longitud; ACC la deja a 0, así que usamos la que medimos nosotros.
            float spline = _sta.ReadSingle(StaSplineLength);
            if (spline is > 200 and < 40000) _trackLength = spline;
            else if (_trackLength <= 0 && Track != null)
            {
                _trackLength = TrackLengthStore.Get(GameName, Track);
                // Circuito de la biblioteca: longitud fija desde la primera vuelta, así se comparan bien todas las sesiones.
                if (_trackLength <= 0 && TrackDatabase.Find(Track) is { } known)
                {
                    _trackLength = known.LengthKm * 1000;
                    _fixedLength = true;
                }
            }
        }

        double now = _clock.Elapsed.TotalSeconds;
        float speedKmh = _phys.ReadSingle(PhysSpeedKmh);
        double dt = Math.Clamp(now - _lastPollTime, 0, 0.5);
        _lastPollTime = now;
        _ownOdometer += speedKmh / 3.6 * dt;

        float normPos = _gfx.ReadSingle(GfxNormalizedPos);
        float gameOdo = _gfx.ReadSingle(GfxDistanceTraveled);
        double odo = gameOdo > 0 ? gameOdo : _ownOdometer;
        int currentMs = _gfx.ReadInt32(GfxCurrentTimeMs);
        int lastMs = _gfx.ReadInt32(GfxLastTimeMs);
        bool live = status == 2;

        bool inPit = _gfx.ReadInt32(GfxIsInPit) != 0 || (_isAcc && _gfx.ReadInt32(AccIsInPitLane) == 1);
        if (live) MeasureTrackLength(normPos, odo, inPit);

        var s = TelemetrySample.Unknown();
        s.Time = now;
        s.Throttle = Clamp01(_phys.ReadSingle(PhysGas));
        s.Brake = Clamp01(_phys.ReadSingle(PhysBrake));
        // En AC/ACC "clutch" vale 1 con el embrague soltado: lo invertimos para mostrar el pedal.
        s.Clutch = Clamp01(1f - _phys.ReadSingle(PhysClutch));
        s.Steering = ClampSigned(_phys.ReadSingle(PhysSteer));
        s.Gear = _phys.ReadInt32(PhysGear) - 1; // 0 = R, 1 = N, 2 = 1ª
        s.Rpm = _phys.ReadInt32(PhysRpm);
        s.MaxRpm = _maxRpm;
        s.SpeedKmh = speedKmh;
        s.LatG = _phys.ReadSingle(PhysAccGX);
        s.LonG = _phys.ReadSingle(PhysAccGZ);
        s.Lap = _gfx.ReadInt32(GfxCompletedLaps);
        s.LapTime = currentMs >= 0 && currentMs < int.MaxValue ? currentMs / 1000f : -1;
        s.LastLapTime = lastMs > 0 && lastMs < int.MaxValue ? lastMs / 1000f : -1;
        s.LapProgress = normPos is >= 0 and <= 1 ? normPos : -1;
        s.TrackLength = (float)_trackLength;
        s.LapDistance = _trackLength > 0 && s.LapProgress >= 0 ? s.LapProgress * (float)_trackLength : -1;
        s.InPit = _gfx.ReadInt32(GfxIsInPit) != 0;
        s.InSession = live;
        s.PressureFL = _phys.ReadSingle(PhysPressure);
        s.PressureFR = _phys.ReadSingle(PhysPressure + 4);
        s.PressureRL = _phys.ReadSingle(PhysPressure + 8);
        s.PressureRR = _phys.ReadSingle(PhysPressure + 12);

        if (_isAcc) ReadAccExtras(ref s);

        Status = status switch
        {
            2 => "Recibiendo datos",
            3 => "En pausa",
            1 => "Repetición (no se graba)",
            _ => "Conectado (en menús)",
        };
        Publish(s);
        return true;
    }

    /// <summary>Al cruzar la línea (la posición pasa de ~1 a ~0) medimos la vuelta con el odómetro.</summary>
    private void MeasureTrackLength(float pos, double odo, bool inPit)
    {
        if (pos < 0 || pos > 1) return;
        // En el pit lane la posición en la vuelta no avanza como en pista: la estimación empieza al salir.
        if (inPit) _posRef = -1;
        if (_prevPos > 0.9f && pos < 0.1f)
        {
            if (!double.IsNaN(_odoAtLine) && Track != null && !_fixedLength)
            {
                double measured = odo - _odoAtLine;
                double stored = TrackLengthStore.Report(GameName, Track, measured);
                if (_trackLength <= 0 || Math.Abs(stored - _trackLength) > 1) _trackLength = stored;
            }
            _odoAtLine = odo;
        }
        else if (_trackLength <= 0 && !inPit)
        {
            // Mientras no hay una vuelta entera, estimamos con lo recorrido desde que nos conectamos.
            if (_posRef < 0 || pos < _posRef) { _posRef = pos; _odoRef = odo; }
            else if (pos - _posRef > 0.15f)
            {
                double estimate = (odo - _odoRef) / (pos - _posRef);
                if (estimate is > 500 and < 30000) _estimatedLength = estimate;
            }
        }
        _prevPos = pos;
    }

    private double _estimatedLength;

    private void ReadAccExtras(ref TelemetrySample s)
    {
        var gfx = _gfx!;
        if (s.TrackLength <= 0 && _estimatedLength > 0 && s.LapProgress >= 0)
        {
            // Hasta medir una vuelta entera usamos la estimación también como longitud: sin ella la grabación
            // no sabe que 5093 m justo al cruzar meta son "-2 m" (y las frenadas salen mal colocadas).
            s.TrackLength = (float)_estimatedLength;
            s.LapDistance = s.LapProgress * s.TrackLength;
        }

        int valid = gfx.ReadInt32(AccIsValidLap);
        if (valid is 0 or 1) s.LapValid = valid;
        if (gfx.ReadInt32(AccIsInPitLane) == 1) s.InPit = true;
        s.WetTyres = gfx.ReadInt32(AccRainTyres) == 1;
        s.Fuel = _phys!.ReadSingle(PhysFuel);
        float perLap = gfx.ReadSingle(AccFuelPerLap);
        s.FuelPerLap = perLap is > 0.1f and < 50 ? perLap : 0;
        int abs = gfx.ReadInt32(AccAbs);
        if (abs is >= 0 and <= 20) s.Abs = abs > 0 ? 1 : 0;

        // Posición del coche del jugador entre los coches activos.
        int active = gfx.ReadInt32(AccActiveCars);
        int player = gfx.ReadInt32(AccPlayerCarId);
        if (active is > 0 and <= 60)
        {
            for (int i = 0; i < active; i++)
            {
                if (gfx.ReadInt32(AccCarIds + i * 4) != player) continue;
                float x = gfx.ReadSingle(AccCarCoordinates + i * 12);
                float z = gfx.ReadSingle(AccCarCoordinates + i * 12 + 8);
                if (float.IsFinite(x) && float.IsFinite(z) && (x != 0 || z != 0))
                {
                    s.PosX = x;
                    s.PosZ = z;
                }
                break;
            }
        }
    }

    protected override void Disconnect()
    {
        _phys?.Dispose(); _gfx?.Dispose(); _sta?.Dispose();
        _physFile?.Dispose(); _gfxFile?.Dispose(); _staFile?.Dispose();
        _phys = _gfx = _sta = null;
        _physFile = _gfxFile = _staFile = null;
    }

    private static bool GameRunning()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("SIMTELEMETRY_FORCE_ACC") == "1") return true;
#endif
        foreach (var name in new[] { "AC2-Win64-Shipping", "acs" })
        {
            var found = Process.GetProcessesByName(name);
            foreach (var p in found) p.Dispose();
            if (found.Length > 0) return true;
        }
        return false;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
