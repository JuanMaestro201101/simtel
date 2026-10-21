using System;
using System.Collections.Generic;
using System.Linq;
using SimTelemetry.Telemetry.Sources;

namespace SimTelemetry.Telemetry;

/// <summary>
/// Arranca todos los lectores a la vez y elige como "activo" el que esté recibiendo datos.
/// Los eventos se disparan en hilos de fondo.
/// </summary>
public sealed class TelemetryHub : IDisposable
{
    private readonly object _gate = new();
    private readonly List<ITelemetrySource> _sources = new();
    private TelemetrySample _latest;

    public TelemetryHub(AppSettings settings)
    {
        _sources.Add(new CodemastersSource(settings.CodemastersPort));
        _sources.Add(new EaWrcSource(settings.WrcPort));
        _sources.Add(new ForzaSource(settings.ForzaPort));
        _sources.Add(new AssettoCorsaSource());
        _sources.Add(new IRacingSource());
        _sources.Add(new OutGaugeSource(settings.OutGaugePort));

        foreach (var s in _sources) s.SampleReceived += OnSample;
    }

    public IReadOnlyList<ITelemetrySource> Sources => _sources;

    public ITelemetrySource? ActiveSource { get; private set; }

    /// <summary>Muestra del lector activo (en el hilo del lector).</summary>
    public event Action<ITelemetrySource, TelemetrySample>? Sample;

    public event Action<ITelemetrySource>? ActiveSourceChanged;

    public TelemetrySample Latest
    {
        get { lock (_gate) return _latest; }
    }

    public bool HasLiveData => ActiveSource?.IsReceiving == true;

    public void Start()
    {
        foreach (var s in _sources) s.Start();
    }

    public void ApplyPorts(AppSettings settings)
    {
        foreach (var s in _sources.OfType<UdpSourceBase>())
        {
            s.Port = s switch
            {
                CodemastersSource => settings.CodemastersPort,
                EaWrcSource => settings.WrcPort,
                ForzaSource => settings.ForzaPort,
                OutGaugeSource => settings.OutGaugePort,
                _ => s.Port,
            };
            s.Start();
        }
    }

    private void OnSample(ITelemetrySource source, TelemetrySample sample)
    {
        bool changed = false;
        lock (_gate)
        {
            if (ActiveSource != source)
            {
                // Sólo cambiamos de juego si el activo dejó de enviar datos, o si está en menús
                // y el nuevo está en pista (p. ej. ACC abierto de fondo mientras juegas a otro).
                bool activeBusy = ActiveSource != null && ActiveSource.IsReceiving && (_latest.InSession || !sample.InSession);
                if (activeBusy) return;
                ActiveSource = source;
                changed = true;
            }
            _latest = sample;
            if (changed) ActiveSourceChanged?.Invoke(source);
            Sample?.Invoke(source, sample);
        }
    }

    public void Dispose()
    {
        foreach (var s in _sources) s.Dispose();
    }
}
