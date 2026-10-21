using System;

namespace SimTelemetry.Telemetry.Sources;

/// <summary>
/// DiRT Rally, DiRT Rally 2.0, DiRT 4 y otros Codemasters con el formato UDP "extradata=3"
/// (66 floats, 264 bytes).
/// </summary>
public sealed class CodemastersSource : UdpSourceBase
{
    private const int PacketSize = 264;

    public CodemastersSource(int port) : base(port) { GameName = "DiRT Rally / DiRT Rally 2.0"; }

    public override string Id => "codemasters";
    public override string DisplayName => "DiRT Rally 1/2, DiRT 4 (UDP)";

    protected override bool TryParse(byte[] d, out TelemetrySample s)
    {
        s = TelemetrySample.Unknown();
        if (d.Length < PacketSize)
        {
            Status = $"Paquete de {d.Length} bytes: activa extradata=\"3\" en hardware_settings_config.xml";
            return false;
        }

        float F(int i) => BitConverter.ToSingle(d, i * 4);

        float gear = F(33);
        float trackLength = F(61);

        s.Time = F(0);
        s.LapTime = F(1);
        s.LapDistance = F(2);
        s.PosX = F(4);
        s.PosZ = F(6);
        s.SpeedKmh = F(7) * 3.6f;
        s.Throttle = Clamp01(F(29));
        s.Steering = ClampSigned(F(30));
        s.Brake = Clamp01(F(31));
        s.Clutch = Clamp01(F(32));
        s.Gear = gear < -0.5f || gear > 9.5f ? -1 : (int)MathF.Round(gear);
        s.LatG = F(34);
        s.LonG = F(35);
        s.Lap = (int)F(36);
        s.Rpm = F(37) * 10f;
        s.LastLapTime = F(62) > 0 ? F(62) : -1;
        s.MaxRpm = F(63) * 10f;
        s.TrackLength = trackLength > 10 ? trackLength : 0;
        s.LapProgress = s.TrackLength > 0 ? Math.Clamp(s.LapDistance / s.TrackLength, 0, 1) : -1;
        s.InSession = true;

        Track = s.TrackLength > 0 ? $"Tramo de {s.TrackLength / 1000f:0.00} km" : null;
        Status = "Recibiendo datos";
        return true;
    }
}
