using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SimTelemetry.Diagnostics;

namespace SimTelemetry.Remote;

/// <summary>
/// Servidor web mínimo para usar el teléfono como pantalla: sirve una página y un flujo de datos en vivo
/// (Server-Sent Events, 20 veces por segundo) por la red de casa. Sólo lectura, y sólo con el código secreto
/// de la dirección: sin él, la respuesta es 404.
/// </summary>
public sealed class PhoneServer : IDisposable
{
    private const int MaxClients = 5;
    private readonly Func<string> _snapshot;
    private readonly IPAddress _bind;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _clients;

    /// <param name="snapshot">Datos en vivo en JSON (se llama desde un hilo del servidor).</param>
    /// <param name="bind">IPAddress.Any para el teléfono; Loopback en las pruebas (sin aviso del cortafuegos).</param>
    public PhoneServer(Func<string> snapshot, IPAddress? bind = null)
    {
        _snapshot = snapshot;
        _bind = bind ?? IPAddress.Any;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
    }

    /// <summary>Código secreto que va en la dirección.</summary>
    public string Token { get; }
    public int Port { get; private set; }
    public int Clients => _clients;
    public string? Error { get; private set; }
    public bool Running => _listener != null;

    /// <summary>Dirección que hay que abrir en el teléfono (null si no está en marcha).</summary>
    public string? Url => Running ? $"http://{(_bind.Equals(IPAddress.Loopback) ? "127.0.0.1" : LanAddress())}:{Port}/{Token}" : null;

    public event Action? Changed;

    public void Start(int firstPort = 8090)
    {
        Stop();
        for (int port = firstPort; port < firstPort + 10; port++)
        {
            try
            {
                var listener = new TcpListener(_bind, port);
                listener.Start();
                _listener = listener;
                Port = port;
                Error = null;
                break;
            }
            catch (SocketException ex)
            {
                Error = $"No se pudo abrir el puerto {port}: {ex.Message}";
            }
        }
        if (_listener == null) { Changed?.Invoke(); return; }
        var cts = new CancellationTokenSource();
        _cts = cts;
        Log.Info($"Pantalla del teléfono: escuchando en el puerto {Port}");
        _ = Task.Run(() => AcceptLoop(_listener, cts.Token));
        Changed?.Invoke();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cts = null;
        Port = 0;
        Changed?.Invoke();
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception) { break; } // detenido
            _ = Task.Run(() => Handle(client, ct));
        }
    }

    private async Task Handle(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                string? path = await ReadRequestPath(stream, ct).ConfigureAwait(false);
                string root = "/" + Token;
                if (path == root || path == root + "/")
                    await Respond(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(PhonePage.Html), ct).ConfigureAwait(false);
                else if (path == root + "/stream")
                    await Stream(stream, ct).ConfigureAwait(false);
                else
                    await Respond(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("No encontrado"), ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // El teléfono cerró la página o se fue la Wi-Fi: no pasa nada.
            }
        }
    }

    /// <summary>Lee la petición (como mucho 8 KB y 5 s) y devuelve la ruta de un GET; null si no es válida.</summary>
    private static async Task<string?> ReadRequestPath(NetworkStream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var buffer = new byte[8192];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
            if (read == 0) return null;
            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n")) break;
        }
        string firstLine = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n")[0];
        var parts = firstLine.Split(' ');
        return parts.Length == 3 && parts[0] == "GET" ? parts[1].Split('?')[0] : null;
    }

    private static async Task Respond(NetworkStream stream, string status, string type, byte[] body, CancellationToken ct)
    {
        string head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    /// <summary>Datos en vivo: un evento cada 50 ms hasta que el teléfono cierra la página.</summary>
    private async Task Stream(NetworkStream stream, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _clients) > MaxClients)
        {
            Interlocked.Decrement(ref _clients);
            await Respond(stream, "503 Service Unavailable", "text/plain", Encoding.UTF8.GetBytes("Demasiadas pantallas"), ct).ConfigureAwait(false);
            return;
        }
        Changed?.Invoke();
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: keep-alive\r\n\r\n"), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("data: " + _snapshot() + "\n\n"), ct).ConfigureAwait(false);
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _clients);
            Changed?.Invoke();
        }
    }

    /// <summary>IP del PC en la red de casa (la de la interfaz con puerta de enlace).</summary>
    public static string LanAddress()
    {
        try
        {
            var ip = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                .SelectMany(p => p.UnicastAddresses)
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return ip?.ToString() ?? "127.0.0.1";
        }
        catch (Exception)
        {
            return "127.0.0.1";
        }
    }

    public void Dispose() => Stop();
}
