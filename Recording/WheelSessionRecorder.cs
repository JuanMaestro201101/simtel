using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SimTelemetry.Diagnostics;

namespace SimTelemetry.Recording;

/// <summary>
/// Graba "sesiones de pedales" con el volante USB cuando estás jugando a un juego que no envía telemetría
/// (p. ej. Forza sin Data Out). Sin datos del juego no hay vueltas, circuito ni velocidad, pero sí se puede
/// analizar la técnica de pedales: presión y ataque al freno, trail braking (con el giro del volante),
/// sueltas bruscas y pisar los dos pedales a la vez. Los datos se guardan por tiempo, no por distancia.
/// </summary>
public sealed class WheelSessionRecorder
{
    private const double ChunkSeconds = 600;   // un archivo cada 10 minutos de juego
    private const double IdleSeconds = 20;     // fuera del juego más de 20 s → se cierra el tramo

    private readonly List<RecordedPoint> _points = new();
    private readonly Stopwatch _clock = new();
    private string? _game;
    private string? _folder;
    private string? _folderGame;
    private int _count;
    private DateTime _lastActiveUtc = DateTime.MinValue;

    public WheelSessionRecorder(string rootFolder) => RootFolder = rootFolder;

    public string RootFolder { get; set; }

    public bool Recording => _points.Count > 0;

    public event Action<LapInfo>? Saved;

    /// <summary>Llamar ~50 veces por segundo.</summary>
    public void Update(bool active, string? game, float throttle, float brake, float clutch, float steering)
    {
        var now = DateTime.UtcNow;
        if (active && game != null)
        {
            if (_points.Count > 0 && game != _game) Finish("cambio de juego");
            if (_points.Count == 0)
            {
                _game = game;
                _clock.Restart();
            }
            double t = _clock.Elapsed.TotalSeconds;
            _points.Add(new RecordedPoint
            {
                Time = t,
                Distance = t, // sesiones por tiempo: el eje X es el tiempo
                Throttle = throttle,
                Brake = brake,
                Clutch = clutch,
                Steering = steering,
                PosX = float.NaN,
                PosZ = float.NaN,
            });
            _lastActiveUtc = now;
            if (t >= ChunkSeconds) Finish("tramo de 10 minutos");
        }
        else if (_points.Count > 0 && (now - _lastActiveUtc).TotalSeconds > IdleSeconds)
        {
            Finish("dejaste el juego");
        }
    }

    public void Flush() => Finish("app cerrada");

    private void Finish(string reason)
    {
        if (_points.Count == 0) return;
        var points = _points.ToArray();
        _points.Clear();

        double duration = points[^1].Time;
        int brakes = 0;
        for (int i = 1; i < points.Length; i++)
            if (points[i].Brake >= 0.2f && points[i - 1].Brake < 0.2f) brakes++;
        if (duration < 30 || brakes < 3)
        {
            Log.Info($"Sesión de pedales descartada ({duration:0} s, {brakes} frenadas): {reason}");
            return;
        }

        if (_folder == null || _folderGame != _game)
        {
            _folder = LapRecorder.SessionPath(RootFolder, _game ?? "", $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss} - pedales");
            _folderGame = _game;
            _count = 0;
            Directory.CreateDirectory(_folder);
        }

        _count++;
        var info = new LapInfo
        {
            FilePath = Path.Combine(_folder, $"lap_{_count:000}.csv"),
            Game = _game ?? "",
            Track = "Sesión de pedales (volante)",
            Car = "",
            Number = _count,
            LapTime = duration,
            Completed = false,
            Valid = true,
            EndReason = reason,
            Date = DateTime.Now,
            Length = 0,
            TimeBased = true,
        };
        Log.Info($"Sesión de pedales guardada: {_game}, {duration:0} s, {brakes} frenadas ({reason})");

        Task.Run(() =>
        {
            try
            {
                LapFile.Write(info.FilePath, info, points);
                Saved?.Invoke(info);
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo guardar la sesión de pedales", ex);
            }
        });
    }
}
