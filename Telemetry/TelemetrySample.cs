namespace SimTelemetry.Telemetry;

/// <summary>
/// Una muestra de telemetría normalizada, igual para todos los juegos.
/// Los valores desconocidos se marcan con -1 (o 0 para longitudes/rpm máximas, NaN para posiciones).
/// </summary>
public struct TelemetrySample
{
    /// <summary>Tiempo en segundos, monótono dentro de la sesión del juego.</summary>
    public double Time;

    public float Throttle;   // 0..1
    public float Brake;      // 0..1
    public float Clutch;     // 0..1 (1 = pedal pisado)
    public float Steering;   // -1 (izquierda) .. 1 (derecha)
    public float Handbrake;  // 0..1

    public float SpeedKmh;
    public float Rpm;
    public float MaxRpm;     // 0 si no se conoce
    public int Gear;         // -1 = R, 0 = N, 1.. = marchas

    public float LatG;
    public float LonG;

    public float LapDistance; // m desde la línea de salida / inicio del tramo, -1 si no se conoce
    public float LapTime;     // s de la vuelta actual, -1 si no se conoce
    public int Lap;           // número de vuelta (cualquier base), -1 si no se conoce
    public float LastLapTime; // s, -1 si no se conoce
    public float TrackLength; // m, 0 si no se conoce

    /// <summary>Posición en la vuelta de 0 a 1 (0 = línea de meta), -1 si no se conoce.</summary>
    public float LapProgress;

    /// <summary>Posición del coche en el mundo (plano horizontal), NaN si el juego no la da.</summary>
    public float PosX;
    public float PosZ;

    /// <summary>1 = vuelta válida, 0 = invalidada (límites de pista…), -1 = el juego no lo indica.</summary>
    public int LapValid;

    /// <summary>En el pit lane o en boxes.</summary>
    public bool InPit;

    /// <summary>1 = el coche lleva ABS activo, 0 = sin ABS, -1 = no se sabe.</summary>
    public int Abs;

    /// <summary>Presión de cada neumático en caliente (psi: delantero izq./der., trasero izq./der.); 0 si el juego no la da.</summary>
    public float PressureFL, PressureFR, PressureRL, PressureRR;

    /// <summary>Combustible (litros) y consumo medio por vuelta que calcula el juego; 0 si no lo da (sólo ACC).</summary>
    public float Fuel, FuelPerLap;

    /// <summary>Lleva neumáticos de lluvia (sólo lo indica ACC).</summary>
    public bool WetTyres;

    /// <summary>true si el coche está en pista conduciendo (no en menús, pausa o repetición).</summary>
    public bool InSession;

    public bool HasPosition => !float.IsNaN(PosX) && !float.IsNaN(PosZ);

    /// <summary>Muestra vacía con todos los campos "desconocidos".</summary>
    public static TelemetrySample Unknown() => new()
    {
        Handbrake = -1,
        LapDistance = -1,
        LapTime = -1,
        Lap = -1,
        LastLapTime = -1,
        LapProgress = -1,
        PosX = float.NaN,
        PosZ = float.NaN,
        LapValid = -1,
        Abs = -1,
    };
}
