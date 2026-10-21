using System.Diagnostics;

namespace SimTelemetry.UI;

/// <summary>Historial circular de pedales para la gráfica en vivo (escritura desde el hilo del lector).</summary>
public sealed class PedalBuffer
{
    public const int Capacity = 4096;

    private readonly object _gate = new();
    private readonly double[] _t = new double[Capacity];
    private readonly float[] _thr = new float[Capacity];
    private readonly float[] _brk = new float[Capacity];
    private readonly float[] _clu = new float[Capacity];
    private int _head;  // siguiente posición a escribir
    private int _count;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public double Now => _clock.Elapsed.TotalSeconds;

    public float Throttle { get; private set; }
    public float Brake { get; private set; }
    public float Clutch { get; private set; }
    public int Gear { get; private set; }
    public float SpeedKmh { get; private set; }
    public double LastWrite { get; private set; } = double.NegativeInfinity;
    public long Version { get; private set; }

    /// <summary>false si los datos vienen sólo de los pedales del volante (sin marcha ni velocidad del juego).</summary>
    public bool HasVehicleData { get; private set; }

    public void Add(float throttle, float brake, float clutch, int gear, float speedKmh, bool vehicleData = true)
    {
        lock (_gate)
        {
            HasVehicleData = vehicleData;
            double now = Now;
            // Como mucho ~120 muestras/s: suficiente para una línea suave.
            if (now - LastWrite < 1.0 / 120) { UpdateCurrent(throttle, brake, clutch, gear, speedKmh); return; }
            _t[_head] = now;
            _thr[_head] = throttle;
            _brk[_head] = brake;
            _clu[_head] = clutch;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
            LastWrite = now;
            UpdateCurrent(throttle, brake, clutch, gear, speedKmh);
        }
    }

    private void UpdateCurrent(float throttle, float brake, float clutch, int gear, float speedKmh)
    {
        Throttle = throttle;
        Brake = brake;
        Clutch = clutch;
        Gear = gear;
        SpeedKmh = speedKmh;
        Version++;
    }

    /// <summary>Copia las muestras con t &gt;= since, en orden cronológico. Devuelve cuántas copió.</summary>
    public int Snapshot(double since, double[] t, float[] thr, float[] brk, float[] clu)
    {
        lock (_gate)
        {
            // Desde la más reciente hacia atrás hasta salir de la ventana: no recorremos todo el historial.
            int keep = 0;
            while (keep < _count && _t[(_head - 1 - keep + Capacity) % Capacity] >= since) keep++;
            int n = 0;
            for (int i = _count - keep; i < _count && n < t.Length; i++)
            {
                int idx = (_head - _count + i + Capacity) % Capacity;
                t[n] = _t[idx];
                thr[n] = _thr[idx];
                brk[n] = _brk[idx];
                clu[n] = _clu[idx];
                n++;
            }
            return n;
        }
    }
}
