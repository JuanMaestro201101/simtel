using System;
using System.Threading;
using SimTelemetry.Diagnostics;

namespace SimTelemetry.Telemetry;

/// <summary>Un lector de telemetría de un juego (o familia de juegos).</summary>
public interface ITelemetrySource : IDisposable
{
    string Id { get; }
    /// <summary>Nombre fijo del lector, p. ej. "Forza (UDP)".</summary>
    string DisplayName { get; }
    /// <summary>Juego detectado a partir de los datos, p. ej. "Forza Motorsport (2023)".</summary>
    string GameName { get; }
    string? Track { get; }
    string? Car { get; }
    string Status { get; }
    DateTime LastPacketUtc { get; }
    bool IsReceiving { get; }
    /// <summary>Paquetes recibidos desde que arrancó la app (también los que no se pudieron leer).</summary>
    long PacketCount { get; }

    void Start();
    void Stop();

    event Action<ITelemetrySource, TelemetrySample>? SampleReceived;
}

public abstract class TelemetrySourceBase : ITelemetrySource
{
    private long _lastPacketTicks;
    private long _packetCount;
    private string _status = "Detenido";
    private int _handlerErrors;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public virtual string GameName { get; protected set; } = "";
    public string? Track { get; protected set; }
    public string? Car { get; protected set; }

    public string Status
    {
        get => _status;
        protected set
        {
            if (_status == value) return;
            _status = value;
            if (!value.StartsWith("Recibiendo")) Log.Info($"{DisplayName}: {value}");
        }
    }

    public DateTime LastPacketUtc => new(Interlocked.Read(ref _lastPacketTicks), DateTimeKind.Utc);
    public bool IsReceiving => (DateTime.UtcNow - LastPacketUtc).TotalSeconds < 2;
    public long PacketCount => Interlocked.Read(ref _packetCount);

    public event Action<ITelemetrySource, TelemetrySample>? SampleReceived;

    public abstract void Start();
    public abstract void Stop();

    protected void Publish(in TelemetrySample sample)
    {
        Interlocked.Exchange(ref _lastPacketTicks, DateTime.UtcNow.Ticks);
        try
        {
            SampleReceived?.Invoke(this, sample);
        }
        catch (Exception ex)
        {
            // Un fallo al procesar una muestra (grabación, overlay…) no debe cortar la conexión con el juego.
            if (_handlerErrors++ < 20) Log.Error($"{DisplayName}: error procesando una muestra", ex);
        }
    }

    /// <summary>Cuenta un paquete recibido (válido o no) para el diagnóstico.</summary>
    protected void CountPacket() => Interlocked.Increment(ref _packetCount);

    /// <summary>Marca actividad sin publicar muestra (p. ej. paquetes válidos pero sin datos de pedales).</summary>
    protected void Touch() => Interlocked.Exchange(ref _lastPacketTicks, DateTime.UtcNow.Ticks);

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    protected static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    protected static float ClampSigned(float v) => v < -1 ? -1 : v > 1 ? 1 : v;
}
