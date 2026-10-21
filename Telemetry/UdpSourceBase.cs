using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SimTelemetry.Telemetry;

/// <summary>Base para los juegos que envían telemetría por UDP.</summary>
public abstract class UdpSourceBase : TelemetrySourceBase
{
    private UdpClient? _client;
    private CancellationTokenSource? _cts;

    protected UdpSourceBase(int port) => Port = port;

    public int Port { get; set; }

    /// <summary>Tamaño del último paquete recibido (diagnóstico de formato).</summary>
    public int LastPacketSize { get; private set; }

    public override void Start()
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        Bind(cts);
    }

    private void Bind(CancellationTokenSource cts)
    {
        if (cts.IsCancellationRequested) return;
        UdpClient client;
        try
        {
            // Sin ReuseAddress: si otro programa (SimHub, etc.) ya usa el puerto, preferimos avisar
            // en vez de compartirlo y no recibir nada.
            client = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
        }
        catch (SocketException ex)
        {
            Status = $"No se pudo abrir el puerto UDP {Port} (¿lo usa otro programa, como SimHub?): {ex.Message}. Se reintenta cada 10 s.";
            // Si el otro programa se cierra, empezamos a escuchar solos.
            Task.Delay(10000, cts.Token).ContinueWith(t => { if (!t.IsCanceled) Bind(cts); }, TaskScheduler.Default);
            return;
        }

        _client = client;
        Status = $"Escuchando UDP {Port}";
        OnStarted();

        Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { continue; }

                CountPacket();
                LastPacketSize = result.Buffer.Length;
                try
                {
                    if (TryParse(result.Buffer, out var sample))
                        Publish(sample);
                }
                catch (Exception ex)
                {
                    Status = "Paquete inválido: " + ex.Message;
                }
            }
        });
    }

    public override void Stop()
    {
        _cts?.Cancel();
        _client?.Dispose();
        _client = null;
        _cts = null;
        Status = "Detenido";
    }

    protected virtual void OnStarted() { }

    protected abstract bool TryParse(byte[] data, out TelemetrySample sample);
}
