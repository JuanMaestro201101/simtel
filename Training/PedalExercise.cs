using System;
using System.Collections.Generic;
using System.Linq;

namespace SimTelemetry.Training;

public enum PedalChannel { Brake, Throttle }

/// <summary>
/// Un ejercicio del entrenador: una curva objetivo que hay que seguir con un pedal.
/// Basado en ejercicios habituales de coaching (repeticiones de umbral, trail braking, modulación).
/// </summary>
public sealed class PedalExercise
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Why { get; init; } = "";
    public PedalChannel Channel { get; init; }
    /// <summary>Margen de error aceptado (fracción del recorrido del pedal).</summary>
    public double Tolerance { get; init; } = 0.05;
    public double Duration { get; init; }
    /// <summary>Objetivo (0..1) en el segundo t del ejercicio.</summary>
    public Func<double, double> Target { get; init; } = _ => 0;

    public string ChannelName => Channel == PedalChannel.Brake ? "Freno" : "Acelerador";

    public static readonly List<PedalExercise> All = Build();

    private static List<PedalExercise> Build()
    {
        var list = new List<PedalExercise>();

        // 1) Escalones de presión de freno.
        double[] brakeSteps = { 0.25, 0.50, 0.75, 1.00, 0.50, 0.25, 0.75, 0.90 };
        list.Add(new PedalExercise
        {
            Id = "brake-steps",
            Name = "Presión exacta de freno",
            Channel = PedalChannel.Brake,
            Tolerance = 0.05,
            Duration = brakeSteps.Length * 4,
            Target = t => Steps(t, brakeSteps, hold: 3, rest: 1),
            Description = "Mantén el freno en el porcentaje que marca la banda (25 %, 50 %, 75 %, 100 %…) durante 3 s y suéltalo entre escalón y escalón.",
            Why = "Te enseña a dosificar el pedal: saber cuánto es «un 50 %» sin mirar es la base de la modulación y del trail braking.",
        });

        // 2) Repeticiones de umbral: 80 % durante 1 s, 10 veces (ejercicio clásico de consistencia).
        list.Add(new PedalExercise
        {
            Id = "threshold",
            Name = "Frenada de umbral (80 % · 1 s × 10)",
            Channel = PedalChannel.Brake,
            Tolerance = 0.05,
            Duration = 10 * 2.6,
            Target = t =>
            {
                double c = t % 2.6;
                return c < 1.0 ? 0.80 : 0;
            },
            Description = "Pisa de golpe hasta el 80 %, aguanta 1 segundo exacto y suelta. Diez repeticiones. Intenta llegar a la banda en menos de 0,2 s y no pasarte.",
            Why = "Repetir la misma presión una y otra vez crea memoria muscular: así frenas igual en cada vuelta (consistencia) y atacas el pedal rápido.",
        });

        // 3) Trail braking: pico, mantener, bajar progresivamente a ~40 % (punto de giro), sostener hasta el vértice y soltar.
        list.Add(new PedalExercise
        {
            Id = "trail",
            Name = "Trail braking",
            Channel = PedalChannel.Brake,
            Tolerance = 0.07,
            Duration = 6 * 4.7,
            Target = t => Trail(t % 4.7),
            Description = "Golpe de freno al 95 %, mantenlo, baja suavemente hasta ~40 % (cuando girarías), sostenlo hasta el «vértice» y suelta poco a poco hasta 0.",
            Why = "Es la forma de frenar de los pilotos rápidos: el coche sigue cargado delante al girar y la suelta progresiva evita el subviraje y los bloqueos.",
        });

        // 4) Acelerador progresivo a la salida de curva.
        list.Add(new PedalExercise
        {
            Id = "throttle-ramp",
            Name = "Acelerador progresivo",
            Channel = PedalChannel.Throttle,
            Tolerance = 0.07,
            Duration = 6 * 4,
            Target = t =>
            {
                double c = t % 4;
                return c < 2 ? c / 2 : c < 3 ? 1 : 0;
            },
            Description = "Abre el acelerador de 0 a 100 % de forma lineal en 2 segundos, mantén a fondo 1 s y suelta. Seis repeticiones.",
            Why = "A la salida de curva, abrir gas de forma progresiva evita patinar (o que salte el control de tracción) y te da mejor salida a la recta.",
        });

        // 5) Gas parcial estable.
        double[] throttleSteps = { 0.30, 0.60, 0.45, 0.80, 0.20, 1.00, 0.50 };
        list.Add(new PedalExercise
        {
            Id = "throttle-partial",
            Name = "Acelerador parcial estable",
            Channel = PedalChannel.Throttle,
            Tolerance = 0.05,
            Duration = throttleSteps.Length * 3,
            Target = t => Steps(t, throttleSteps, hold: 3, rest: 0),
            Description = "Mantén el acelerador quieto en cada porcentaje (30 %, 60 %, 45 %…) durante 3 segundos.",
            Why = "En curvas largas necesitas gas parcial constante para equilibrar el coche; un pie nervioso desestabiliza el chasis.",
        });

        // 6) Reflejos: objetivos aleatorios (pero iguales cada vez, para poder comparar puntuaciones).
        var rnd = new Random(1234);
        var randomTargets = Enumerable.Range(0, 12).Select(_ => 0.2 + rnd.NextDouble() * 0.8).ToArray();
        list.Add(new PedalExercise
        {
            Id = "random",
            Name = "Reflejos de presión",
            Channel = PedalChannel.Brake,
            Tolerance = 0.06,
            Duration = randomTargets.Length * 2.6,
            Target = t => Steps(t, randomTargets, hold: 2, rest: 0.6),
            Description = "La presión objetivo cambia cada 2 segundos. Llega a la banda lo antes posible y mantente dentro.",
            Why = "Entrena la rapidez para encontrar una presión concreta, como cuando una frenada exige más o menos fuerza que la anterior.",
        });

        return list;
    }

    private static double Steps(double t, double[] steps, double hold, double rest)
    {
        double period = hold + rest;
        int i = (int)(t / period);
        if (i < 0 || i >= steps.Length) return 0;
        return t - i * period < hold ? steps[i] : 0;
    }

    private static double Trail(double c)
    {
        if (c < 0.15) return 0.95 * c / 0.15;
        if (c < 0.8) return 0.95;
        if (c < 1.6) return 0.95 - (0.95 - 0.40) * (c - 0.8) / 0.8;
        if (c < 2.4) return 0.40;
        if (c < 3.2) return 0.40 * (1 - (c - 2.4) / 0.8);
        return 0;
    }
}

/// <summary>Puntúa un intento del ejercicio a partir de las muestras tomadas.</summary>
public sealed class ExerciseScorer
{
    private readonly PedalExercise _ex;
    private double _inTolerance, _counted, _absError;
    private double _prevTarget;
    private double _stepTime = double.NaN, _stepTarget;
    private readonly List<double> _attacks = new();
    private double _overshoot;

    public ExerciseScorer(PedalExercise ex) => _ex = ex;

    public void Add(double t, double dt, double actual)
    {
        double target = _ex.Target(t);
        // Un salto grande del objetivo: medimos cuánto tardas en llegar (ataque) y te damos 0,3 s de margen.
        if (Math.Abs(target - _prevTarget) > 0.15)
        {
            _stepTime = t;
            _stepTarget = target;
        }
        _prevTarget = target;

        bool grace = !double.IsNaN(_stepTime) && t - _stepTime < 0.3;
        double err = Math.Abs(actual - target);

        if (!double.IsNaN(_stepTime) && _stepTarget > 0.15 && err <= _ex.Tolerance)
        {
            _attacks.Add(t - _stepTime);
            _stepTime = double.NaN;
        }
        if (target > 0.1 && double.IsNaN(_stepTime)) _overshoot = Math.Max(_overshoot, actual - target);

        if (grace) return;
        _counted += dt;
        _absError += err * dt;
        if (err <= _ex.Tolerance) _inTolerance += dt;
    }

    public double Accuracy => _counted > 0 ? _inTolerance / _counted : 0;
    public double MeanError => _counted > 0 ? _absError / _counted : 0;
    public double AverageAttack => _attacks.Count > 0 ? _attacks.Average() : double.NaN;
    public double MaxOvershoot => _overshoot;

    /// <summary>Puntuación 0..100: 70 % tiempo dentro de la banda, 30 % error medio.</summary>
    public int Score => (int)Math.Round(Accuracy * 70 + Math.Max(0, 1 - MeanError / 0.25) * 30);

    public string Summary()
    {
        var parts = new List<string>
        {
            $"Dentro de la banda el {Accuracy:P0} del tiempo",
            $"error medio {MeanError * 100:0.0} %",
        };
        if (!double.IsNaN(AverageAttack)) parts.Add($"llegas al objetivo en {AverageAttack:0.00} s de media");
        if (MaxOvershoot > _ex.Tolerance) parts.Add($"te pasas hasta {MaxOvershoot * 100:0} % por encima");
        return string.Join(" · ", parts);
    }

    public string Advice()
    {
        if (Score >= 85) return "¡Excelente control! Prueba ahora el ejercicio sin mirar la pantalla, sólo sintiendo el pedal.";
        if (!double.IsNaN(AverageAttack) && AverageAttack > 0.35)
            return "Llegas tarde al objetivo: pisa con más decisión al principio y corrige después.";
        if (MaxOvershoot > _ex.Tolerance * 2)
            return "Te pasas de presión al pisar: frena el pie justo antes de llegar al objetivo.";
        if (Accuracy < 0.5)
            return "Te cuesta mantener la presión estable: apoya bien el talón y mueve sólo el tobillo.";
        return "Buen trabajo. Repite el ejercicio hasta superar 85 puntos de forma constante.";
    }
}
