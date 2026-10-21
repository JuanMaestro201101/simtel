using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace SimTelemetry.Telemetry;

/// <summary>Base para juegos que exponen la telemetría en memoria compartida (ACC, AC, iRacing).</summary>
public abstract class SharedMemorySourceBase : TelemetrySourceBase
{
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>Intervalo de sondeo (~100 Hz, con el reloj del sistema a 1 ms): los avisos van sincronizados con el juego.</summary>
    protected virtual int PollIntervalMs => 10;

    public override void Start()
    {
        Stop();
        _running = true;
        Status = "Esperando al juego…";
        _thread = new Thread(Loop) { IsBackground = true, Name = DisplayName };
        _thread.Start();
    }

    public override void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
        Disconnect();
        Status = "Detenido";
    }

    private void Loop()
    {
        DateTime nextConnectAttempt = DateTime.MinValue;
        while (_running)
        {
            try
            {
                if (!IsConnected)
                {
                    if (DateTime.UtcNow >= nextConnectAttempt)
                    {
                        nextConnectAttempt = DateTime.UtcNow.AddSeconds(2);
                        if (TryConnect()) Status = "Conectado";
                    }
                    Thread.Sleep(200);
                    continue;
                }

                if (!Poll())
                {
                    Disconnect();
                    Status = "Esperando al juego…";
                }
            }
            catch (Exception ex)
            {
                Disconnect();
                Status = "Error: " + ex.Message;
            }
            Thread.Sleep(PollIntervalMs);
        }
    }

    protected abstract bool IsConnected { get; }

    /// <summary>Intenta abrir la memoria compartida. Devuelve false si el juego no está abierto.</summary>
    protected abstract bool TryConnect();

    /// <summary>Lee una muestra y la publica. Devuelve false si se perdió la conexión.</summary>
    protected abstract bool Poll();

    protected abstract void Disconnect();

    protected static MemoryMappedViewAccessor? OpenView(string name, out MemoryMappedFile? file)
    {
        file = null;
        try
        {
            file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            return file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        }
        catch
        {
            file?.Dispose();
            file = null;
            return null;
        }
    }

    protected static string ReadWideString(MemoryMappedViewAccessor view, long offset, int chars)
    {
        var buffer = new char[chars];
        view.ReadArray(offset, buffer, 0, chars);
        int len = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, len < 0 ? chars : len).Trim();
    }
}
