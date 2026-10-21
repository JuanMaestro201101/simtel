using System;

namespace SimTelemetry.Telemetry.Sources;

/// <summary>
/// Forza Motorsport 7, Forza Motorsport (2023), Forza Horizon 4 y 5: "Data Out" en formato "Car Dash".
/// Tamaños: 232 = Sled (sin pedales), 311 = FM7 Dash, 324 = FH4/FH5, 331 = FM 2023.
/// </summary>
public sealed class ForzaSource : UdpSourceBase
{
    private int _lap = int.MinValue;
    private float _lapStartDistance;

    public ForzaSource(int port) : base(port) { GameName = "Forza"; }

    public override string Id => "forza";
    public override string DisplayName => "Forza Motorsport / Horizon (UDP)";

    protected override bool TryParse(byte[] d, out TelemetrySample s)
    {
        s = TelemetrySample.Unknown();
        int dash; // desplazamiento del bloque "Dash"
        switch (d.Length)
        {
            case 311: dash = 232; GameName = "Forza Motorsport 7"; break;
            case 331: dash = 232; GameName = "Forza Motorsport (2023)"; break;
            case 324: dash = 244; GameName = "Forza Horizon 4/5"; break;
            case 232:
                Touch();
                Status = "Llega el formato «Trineo» (Sled), sin pedales: en el juego elige «Aceleración de auto» (Car Dash) y reinícialo";
                return false;
            default:
                Status = $"Paquete Forza de tamaño desconocido ({d.Length} bytes)";
                return false;
        }

        float F(int o) => BitConverter.ToSingle(d, o);

        bool raceOn = BitConverter.ToInt32(d, 0) != 0;
        uint timestampMs = BitConverter.ToUInt32(d, 4);

        float distance = F(dash + 48);
        int lap = BitConverter.ToUInt16(d, dash + 68);
        if (lap != _lap)
        {
            // En la salida desde parrilla la distancia es negativa hasta cruzar la línea: la vuelta empieza en 0.
            _lapStartDistance = _lap == int.MinValue && distance < 0 ? 0 : distance;
            _lap = lap;
        }

        int gearRaw = d[dash + 75];
        s.Time = timestampMs / 1000.0;
        s.MaxRpm = F(8);
        s.Rpm = F(16);
        s.LatG = F(20) / 9.81f;
        s.LonG = F(28) / 9.81f;
        s.PosX = F(dash + 0);
        s.PosZ = F(dash + 8);
        s.SpeedKmh = F(dash + 12) * 3.6f;
        s.LastLapTime = F(dash + 56) > 0 ? F(dash + 56) : -1;
        s.LapTime = F(dash + 60);
        s.Lap = lap;
        s.LapDistance = Math.Max(0, distance - _lapStartDistance);
        s.Throttle = d[dash + 71] / 255f;
        s.Brake = d[dash + 72] / 255f;
        s.Clutch = d[dash + 73] / 255f;
        s.Handbrake = d[dash + 74] / 255f;
        s.Gear = gearRaw == 0 ? -1 : gearRaw >= 11 ? 0 : gearRaw;
        s.Steering = (sbyte)d[dash + 76] / 127f;
        s.InSession = raceOn;

        // Forza sólo da números: el coche (CarOrdinal) y su índice de rendimiento (IR/PI) para distinguir clases y mejoras.
        int pi = BitConverter.ToInt32(d, 220);
        Car = $"Coche #{BitConverter.ToInt32(d, 212)}" + (pi > 0 ? $" · IR {pi}" : "");
        Track = d.Length == 331 ? "Pista #" + BitConverter.ToInt32(d, 327) : null;
        Status = raceOn ? "Recibiendo datos" : "Conectado (fuera de carrera)";
        return true;
    }
}
