using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace SimTelemetry.Telemetry.Sources;

/// <summary>
/// Protocolo OutGauge (BeamNG.drive y Live for Speed): pedales, marcha, RPM y velocidad. No trae vueltas ni posición.
/// BeamNG puede mandar además MotionSim («BNG1», posición y aceleraciones) al mismo puerto: con eso se dibuja el mapa,
/// se miden la distancia y las G y se detectan las vueltas al volver al punto de salida (<see cref="LoopLaps"/>).
/// Formatos: https://documentation.beamng.com/modding/protocols/
/// </summary>
public sealed class OutGaugeSource : UdpSourceBase
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly LoopLaps _laps = new();
    private double _lastTime = -1;
    private double _motionAt = double.NegativeInfinity;
    private float _x, _y, _latG, _lonG;

    public OutGaugeSource(int port) : base(port) { GameName = "BeamNG.drive / Live for Speed"; }

    public override string Id => "outgauge";
    public override string DisplayName => "BeamNG / LFS (OutGauge UDP)";

    protected override bool TryParse(byte[] d, out TelemetrySample s)
    {
        s = TelemetrySample.Unknown();
        float F(int o) => BitConverter.ToSingle(d, o);
        double now = _clock.Elapsed.TotalSeconds;

        if (d.Length >= 88 && d[0] == 'B' && d[1] == 'N' && d[2] == 'G' && d[3] == '1')
        {
            // MotionSim: posición en el mundo (z hacia arriba) y aceleración; se guarda para el siguiente paquete OutGauge.
            _x = F(4);
            _y = F(8);
            float vx = F(16), vy = F(20), ax = F(28), ay = F(32);
            double v = Math.Sqrt(vx * vx + vy * vy);
            if (v > 1)
            {
                double fx = vx / v, fy = vy / v; // hacia delante en el plano
                _lonG = (float)((ax * fx + ay * fy) / 9.81);
                _latG = (float)((ax * fy - ay * fx) / 9.81); // + = hacia la derecha
            }
            _motionAt = now;
            return false;
        }

        if (d.Length < 92)
        {
            Status = $"Paquete OutGauge demasiado corto ({d.Length} bytes)";
            return false;
        }

        s.Time = now;
        s.Gear = d[10] - 1; // 0 = R, 1 = N, 2 = 1ª
        s.SpeedKmh = F(12) * 3.6f;
        s.Rpm = F(16);
        s.Throttle = Clamp01(F(48));
        s.Brake = Clamp01(F(52));
        s.Clutch = Clamp01(F(56));
        s.InSession = true;
        Car = Encoding.ASCII.GetString(d, 4, 4).TrimEnd('\0');
        // BeamNG siempre manda "beam" (no dice qué coche); LFS manda el modelo (XFG, FXR…).
        GameName = Car == "beam" ? "BeamNG.drive" : "Live for Speed";
        if (Car == "beam") Car = null;

        bool motion = now - _motionAt < 1;
        if (motion)
        {
            s.PosX = _x;
            s.PosZ = _y;
            s.LatG = _latG;
            s.LonG = _lonG;
            double dt = _lastTime < 0 ? 0 : Math.Clamp(now - _lastTime, 0, 0.5);
            _laps.Update(now, _x, _y, s.SpeedKmh / 3.6 * dt);
            s.Lap = _laps.Lap;
            s.LapDistance = (float)_laps.LapDistance;
            s.LapTime = _laps.Started ? (float)(now - _laps.LapStart) : -1;
            s.LastLapTime = (float)_laps.LastLapTime;
            Track = _laps.Name;
        }
        _lastTime = now;
        Status = motion ? "Recibiendo datos (con posición: mapa y vueltas)" : "Recibiendo datos (activa MotionSim en BeamNG para el mapa y las vueltas)";
        return true;
    }
}

/// <summary>
/// Vueltas para juegos que no las envían: la vuelta se cierra al volver a pasar por el punto de salida en el mismo sentido.
/// Si arrancaste fuera del circuito (boxes, aparcamiento), el punto de salida se fija la primera vez que cierras un bucle.
/// </summary>
public sealed class LoopLaps
{
    private const double CloseRadius = 15;   // m: lo cerca que hay que pasar del punto de salida
    private const double MinLap = 500;       // m: una vuelta más corta no cuenta (dar la vuelta en una explanada)
    private readonly List<(double X, double Y, double Odo)> _trail = new();
    private double _odo, _startOdo, _sx, _sy, _hx, _hy, _px, _py, _prevGap = double.MaxValue;
    private bool _confirmed;

    public int Lap { get; private set; }
    public bool Started { get; private set; }
    public double LapStart { get; private set; }
    public double LastLapTime { get; private set; } = -1;
    public double LapDistance => Started ? _odo - _startOdo : -1;
    /// <summary>Nombre del recorrido (por la posición del punto de salida): así se juntan las sesiones en el mismo circuito.</summary>
    public string? Name { get; private set; }

    public void Update(double time, double x, double y, double moved)
    {
        // Teletransporte (reinicio del coche): la vuelta en curso ya no vale, empieza otra en el punto nuevo si no hay circuito fijado.
        if (Started && Math.Abs(x - _px) + Math.Abs(y - _py) > 60 && moved < 5)
        {
            if (!_confirmed) Begin(time, x, y);
            _trail.Clear();
        }
        double hx = x - _px, hy = y - _py;
        _px = x; _py = y;
        if (moved <= 0) return;
        _odo += moved;

        if (!Started)
        {
            Begin(time, x, y); // primera vez que se mueve
            return;
        }
        if (_hx == 0 && _hy == 0 && LapDistance > 20) (_hx, _hy) = (x - _sx, y - _sy); // sentido de la marcha al salir

        double gap = Math.Sqrt((x - _sx) * (x - _sx) + (y - _sy) * (y - _sy));
        bool sameWay = hx * _hx + hy * _hy > 0;
        // Pasamos por el punto más cercano a la salida: la distancia deja de bajar.
        if (LapDistance > MinLap && _prevGap < CloseRadius && gap > _prevGap && sameWay)
        {
            _confirmed = true;
            Name ??= $"Recorrido {Math.Round(_sx / 25) * 25:0}/{Math.Round(_sy / 25) * 25:0}";
            LastLapTime = time - LapStart;
            Lap++;
            LapStart = time;
            _startOdo = _odo;
            _trail.Clear();
            _prevGap = double.MaxValue;
            return;
        }
        _prevGap = gap;

        if (_confirmed) return;
        // Bucle cerrado en otro punto (saliste de boxes): ese punto pasa a ser la línea de salida.
        if (_trail.Count == 0 || _odo - _trail[^1].Odo >= 10) _trail.Add((x, y, _odo));
        foreach (var p in _trail)
        {
            if (_odo - p.Odo < 800) break;
            if ((p.X - _sx) * (p.X - _sx) + (p.Y - _sy) * (p.Y - _sy) < 4 * CloseRadius * CloseRadius) continue; // ahí lo cierra el punto de salida
            if ((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y) < CloseRadius * CloseRadius)
            {
                _confirmed = true;
                Begin(time, x, y);
                (_hx, _hy) = (hx, hy);
                Name = $"Recorrido {Math.Round(x / 25) * 25:0}/{Math.Round(y / 25) * 25:0}";
                _trail.Clear();
                return;
            }
        }
    }

    private void Begin(double time, double x, double y)
    {
        Started = true;
        LapStart = time;
        _startOdo = _odo;
        (_sx, _sy) = (x, y);
        (_hx, _hy) = (0, 0);
        _prevGap = double.MaxValue;
    }
}
