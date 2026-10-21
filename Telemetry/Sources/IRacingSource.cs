using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;

namespace SimTelemetry.Telemetry.Sources;

/// <summary>iRacing mediante el SDK oficial (memoria compartida "IRSDKMemMapFileName"). No requiere configuración.</summary>
public sealed class IRacingSource : SharedMemorySourceBase
{
    private const int VarHeaderSize = 144;
    private const int StatusConnected = 1;

    private enum VarType { Char = 0, Bool = 1, Int = 2, BitField = 3, Float = 4, Double = 5 }

    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _view;
    private readonly Dictionary<string, (VarType Type, int Offset)> _vars = new();
    private int _lastTick = -1;
    private int _sessionInfoUpdate = -1;
    private int _numVars = -1;
    private float _trackLength;

    public IRacingSource() { GameName = "iRacing"; }

    public override string Id => "iracing";
    public override string DisplayName => "iRacing (SDK)";

    protected override bool IsConnected => _view != null;

    protected override bool TryConnect()
    {
        _view = OpenView("Local\\IRSDKMemMapFileName", out _file);
        _vars.Clear();
        _numVars = -1;
        _lastTick = -1;
        _sessionInfoUpdate = -1;
        _trackLength = 0;
        return _view != null;
    }

    protected override void Disconnect()
    {
        _view?.Dispose();
        _file?.Dispose();
        _view = null;
        _file = null;
    }

    protected override bool Poll()
    {
        var v = _view!;
        int status = v.ReadInt32(4);
        if ((status & StatusConnected) == 0)
        {
            Status = "Esperando a iRacing…";
            return true;
        }

        int numVars = v.ReadInt32(24);
        if (numVars != _numVars) ReadVarHeaders(v, numVars);

        int sessionInfoUpdate = v.ReadInt32(12);
        if (sessionInfoUpdate != _sessionInfoUpdate)
        {
            _sessionInfoUpdate = sessionInfoUpdate;
            ReadSessionInfo(v);
        }

        // Elegimos el buffer con el tick más reciente.
        int numBuf = Math.Clamp(v.ReadInt32(32), 1, 4);
        int bestTick = -1, bestOffset = 0;
        for (int i = 0; i < numBuf; i++)
        {
            int tick = v.ReadInt32(48 + i * 16);
            if (tick > bestTick)
            {
                bestTick = tick;
                bestOffset = v.ReadInt32(48 + i * 16 + 4);
            }
        }
        if (bestTick == _lastTick) return true;
        _lastTick = bestTick;

        float lapDist = F(v, bestOffset, "LapDist", -1);
        float lapPct = F(v, bestOffset, "LapDistPct", -1);
        if (lapPct > 0.2f && lapDist > 100) _trackLength = lapDist / lapPct;

        float wheel = F(v, bestOffset, "SteeringWheelAngle", 0);
        float wheelMax = F(v, bestOffset, "SteeringWheelAngleMax", 0);
        bool onTrack = B(v, bestOffset, "IsOnTrack");
        bool replay = B(v, bestOffset, "IsReplayPlaying");

        var s = TelemetrySample.Unknown() with
        {
            Time = D(v, bestOffset, "SessionTime"),
            Throttle = Clamp01(F(v, bestOffset, "Throttle", 0)),
            Brake = Clamp01(F(v, bestOffset, "Brake", 0)),
            // En iRacing "Clutch" vale 1 con el embrague soltado: lo invertimos para mostrar el pedal.
            Clutch = Clamp01(1f - F(v, bestOffset, "Clutch", 1)),
            Steering = wheelMax > 0.1f ? ClampSigned(-wheel / wheelMax) : ClampSigned(-wheel / 7.85f),
            Handbrake = -1,
            Gear = I(v, bestOffset, "Gear", 0),
            Rpm = F(v, bestOffset, "RPM", 0),
            MaxRpm = F(v, bestOffset, "PlayerCarSLShiftRPM", 0),
            SpeedKmh = F(v, bestOffset, "Speed", 0) * 3.6f,
            LatG = F(v, bestOffset, "LatAccel", 0) / 9.81f,
            LonG = F(v, bestOffset, "LongAccel", 0) / 9.81f,
            Lap = I(v, bestOffset, "Lap", -1),
            LapTime = F(v, bestOffset, "LapCurrentLapTime", -1),
            LastLapTime = F(v, bestOffset, "LapLastLapTime", -1),
            LapDistance = lapDist,
            TrackLength = _trackLength,
            LapProgress = lapPct is >= 0 and <= 1 ? lapPct : -1,
            InPit = B(v, bestOffset, "OnPitRoad"),
            InSession = onTrack && !replay,
        };

        Status = s.InSession ? "Recibiendo datos" : replay ? "Repetición (no se graba)" : "Conectado (fuera de pista)";
        Publish(s);
        return true;
    }

    private void ReadVarHeaders(MemoryMappedViewAccessor v, int numVars)
    {
        _vars.Clear();
        _numVars = numVars;
        int headerOffset = v.ReadInt32(28);
        var name = new byte[32];
        for (int i = 0; i < numVars; i++)
        {
            long h = headerOffset + (long)i * VarHeaderSize;
            var type = (VarType)v.ReadInt32(h);
            int offset = v.ReadInt32(h + 4);
            v.ReadArray(h + 16, name, 0, 32);
            int len = Array.IndexOf(name, (byte)0);
            string key = Encoding.ASCII.GetString(name, 0, len < 0 ? 32 : len);
            _vars[key] = (type, offset);
        }
    }

    private void ReadSessionInfo(MemoryMappedViewAccessor v)
    {
        try
        {
            int len = v.ReadInt32(16);
            int offset = v.ReadInt32(20);
            if (len <= 0 || len > 4_000_000) return;
            var bytes = new byte[len];
            v.ReadArray(offset, bytes, 0, len);
            string yaml = Encoding.Latin1.GetString(bytes);

            var track = Regex.Match(yaml, @"TrackDisplayName:\s*(.+)");
            if (track.Success) Track = track.Groups[1].Value.Trim();

            var carIdx = Regex.Match(yaml, @"DriverCarIdx:\s*(\d+)");
            if (carIdx.Success)
            {
                var entry = Regex.Match(yaml, @"- CarIdx:\s*" + carIdx.Groups[1].Value + @"\s*\n");
                if (entry.Success)
                {
                    var car = Regex.Match(yaml.Substring(entry.Index), @"CarScreenName:\s*(.+)");
                    if (car.Success) Car = car.Groups[1].Value.Trim();
                }
            }
        }
        catch
        {
            // La info de sesión es opcional.
        }
    }

    private float F(MemoryMappedViewAccessor v, int buf, string name, float fallback)
    {
        if (!_vars.TryGetValue(name, out var var)) return fallback;
        return var.Type switch
        {
            VarType.Float => v.ReadSingle(buf + var.Offset),
            VarType.Double => (float)v.ReadDouble(buf + var.Offset),
            VarType.Int or VarType.BitField => v.ReadInt32(buf + var.Offset),
            _ => fallback,
        };
    }

    private double D(MemoryMappedViewAccessor v, int buf, string name)
    {
        if (!_vars.TryGetValue(name, out var var)) return 0;
        return var.Type == VarType.Double ? v.ReadDouble(buf + var.Offset) : F(v, buf, name, 0);
    }

    private int I(MemoryMappedViewAccessor v, int buf, string name, int fallback)
    {
        if (!_vars.TryGetValue(name, out var var)) return fallback;
        return var.Type is VarType.Int or VarType.BitField ? v.ReadInt32(buf + var.Offset) : (int)F(v, buf, name, fallback);
    }

    private bool B(MemoryMappedViewAccessor v, int buf, string name)
    {
        if (!_vars.TryGetValue(name, out var var)) return false;
        return v.ReadByte(buf + var.Offset) != 0;
    }
}
