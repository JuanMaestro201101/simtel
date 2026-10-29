using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SimTelemetry.Telemetry;

/// <summary>Juego compatible detectado por su proceso (aunque todavía no envíe telemetría).</summary>
public sealed record RunningGame(string Name, string SourceId, string Hint);

/// <summary>
/// Detecta qué juegos compatibles están abiertos y cuál está en primer plano, mirando los procesos de Windows.
/// Sirve para mostrar el overlay en cuanto entras al juego y para avisar si un juego abierto no envía datos.
/// </summary>
public static class GameProcesses
{
    private const string ForzaHint =
        "En Forza: Ajustes → Interfaz y juego (HUD and Gameplay) → Salida de datos (Data Out): ACTIVADO · IP 127.0.0.1 · Puerto 5300 · Formato «Aceleración de auto» (Car Dash; no «Trineo»). Después cierra y vuelve a abrir el juego.";

    private static readonly (Regex Pattern, string Name, string SourceId, string Hint)[] Known =
    {
        (new Regex("^forzamotorsport|^forza_", RegexOptions.IgnoreCase), "Forza Motorsport", "forza", ForzaHint),
        (new Regex("^forzahorizon", RegexOptions.IgnoreCase), "Forza Horizon", "forza", ForzaHint),
        (new Regex("^(dirtrally2|drt|dirt4)$", RegexOptions.IgnoreCase), "DiRT Rally", "codemasters",
            "Pulsa «Configurar DiRT Rally» en la pestaña Juegos con el juego cerrado y vuelve a abrirlo."),
        (new Regex("^wrc$", RegexOptions.IgnoreCase), "EA SPORTS WRC", "eawrc",
            "Pulsa «Configurar EA WRC» en la pestaña Juegos con el juego cerrado y vuelve a abrirlo."),
        (new Regex("^ac2-win64-shipping$", RegexOptions.IgnoreCase), "Assetto Corsa Competizione", "assetto", "Sal a pista: ACC sólo envía datos con el coche en pista."),
        (new Regex("^acs$", RegexOptions.IgnoreCase), "Assetto Corsa", "assetto", "Sal a pista: AC sólo envía datos con el coche en pista."),
        (new Regex("^iracingsim", RegexOptions.IgnoreCase), "iRacing", "iracing", "Sal a pista: iRacing sólo envía datos con el coche en pista."),
        (new Regex("^beamng", RegexOptions.IgnoreCase), "BeamNG.drive", "outgauge",
            "En BeamNG: Opciones → Otros → Protocolos: activa OutGauge y MotionSim, los dos con IP 127.0.0.1 y puerto 4444."),
    };

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static RunningGame? Match(string processName)
    {
        foreach (var k in Known)
            if (k.Pattern.IsMatch(processName)) return new RunningGame(k.Name, k.SourceId, k.Hint);
        return null;
    }

    private static uint _lastPid;
    private static RunningGame? _lastGame;

    /// <summary>Juego compatible que está ahora en primer plano (null si es otra ventana).</summary>
    public static RunningGame? Foreground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return null;
            // Se llama 4 veces por segundo: sólo consultamos el proceso cuando cambia la ventana de delante.
            if (pid == _lastPid) return _lastGame;
            using var p = Process.GetProcessById((int)pid);
            _lastGame = Match(p.ProcessName);
            _lastPid = pid;
            return _lastGame;
        }
        catch (Exception)
        {
            return null; // el proceso terminó o no se puede consultar
        }
    }

    /// <summary>Juegos compatibles abiertos ahora mismo.</summary>
    public static List<RunningGame> Running()
    {
        var result = new List<RunningGame>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var game = Match(p.ProcessName);
                if (game != null && result.All(r => r.Name != game.Name)) result.Add(game);
            }
        }
        return result;
    }
}
