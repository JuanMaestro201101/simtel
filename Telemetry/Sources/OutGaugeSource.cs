using System;
using System.Diagnostics;
using System.Text;

namespace SimTelemetry.Telemetry.Sources;

/// <summary>Protocolo OutGauge: BeamNG.drive y Live for Speed. No incluye datos de vuelta.</summary>
public sealed class OutGaugeSource : UdpSourceBase
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public OutGaugeSource(int port) : base(port) { GameName = "BeamNG.drive / Live for Speed"; }

    public override string Id => "outgauge";
    public override string DisplayName => "BeamNG / LFS (OutGauge UDP)";

    protected override bool TryParse(byte[] d, out TelemetrySample s)
    {
        s = TelemetrySample.Unknown();
        if (d.Length < 92)
        {
            Status = $"Paquete OutGauge demasiado corto ({d.Length} bytes)";
            return false;
        }

        float F(int o) => BitConverter.ToSingle(d, o);

        s.Time = _clock.Elapsed.TotalSeconds;
        s.Gear = d[10] - 1; // 0 = R, 1 = N, 2 = 1ª
        s.SpeedKmh = F(12) * 3.6f;
        s.Rpm = F(16);
        s.Throttle = Clamp01(F(48));
        s.Brake = Clamp01(F(52));
        s.Clutch = Clamp01(F(56));
        s.InSession = true;

        Car = Encoding.ASCII.GetString(d, 4, 4).TrimEnd('\0');
        Status = "Recibiendo datos";
        return true;
    }
}
