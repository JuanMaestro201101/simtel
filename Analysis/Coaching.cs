namespace SimTelemetry.Analysis;

/// <summary>Los problemas que la app sabe detectar en tus datos.</summary>
public enum Problem
{
    BrakeEarly, BrakeLate, LowPressure, SlowAttack, SlowApex, SlowExit, LateThrottle,
    Coasting, Overlap, NoTrail, AbruptRelease, Limiter, Corrections,
}

/// <summary>Para cada problema: por qué ocurre, cómo se soluciona, qué ejercicio del Entrenador lo trabaja y qué practicar en pista.</summary>
public sealed record Advice(string Why, string How, string? ExerciseId, string Drill);

public static class Coaching
{
    public static Advice For(Problem p) => p switch
    {
        Problem.BrakeEarly => new(
            "Frenas antes de lo que necesitas. Suele ser falta de confianza en el punto de frenada, o una frenada que empieza suave y obliga a darse margen.",
            "Frena fuerte desde el primer instante y retrasa el punto unos metros cada vuelta, con una referencia visual fija (un cartel, una marca). El Ingeniero, con el objetivo progresivo, te va marcando el punto.",
            "threshold",
            "En esta curva: 3 tandas de 5 vueltas retrasando la frenada poco a poco hasta que empieces a pasarte; entonces vuelve un paso atrás."),
        Problem.BrakeLate => new(
            "Frenas más tarde de lo que el coche puede frenar y entras pasado: pierdes el vértice y sales más lento.",
            "Adelanta la frenada unos metros y céntrate en salir bien. Una entrada algo más lenta suele dar una vuelta más rápida.",
            "threshold",
            "En esta curva, hasta conseguir 5 entradas iguales seguidas, sin pasarte del vértice."),
        Problem.LowPressure => new(
            "No llegas a la presión máxima de freno, así que frenar te cuesta más metros de los necesarios.",
            "Pisa más fuerte al principio: 90–100 % con ABS y ~85 % sin ABS (justo antes de bloquear). Luego suéltalo poco a poco al girar.",
            "threshold",
            "En el Entrenador, «Frenada de umbral» hasta mantener el 80 % sin salirte de ±5 %. Después, en pista, frenadas fuertes en recta."),
        Problem.SlowAttack => new(
            "Tardas en llegar a la presión máxima: los primeros metros de la frenada apenas frenas.",
            "Pisa el freno de forma rápida y decidida: de 0 a la presión máxima en menos de 0,2 s.",
            "random",
            "En el Entrenador, «Reflejos de presión» para llegar rápido al valor pedido. En pista, piensa en «pisotón» y luego modula."),
        Problem.SlowApex => new(
            "Llegas al vértice más lento que en tu referencia: frenas de más, o sueltas el freno demasiado pronto y el coche no gira.",
            "Suelta el freno poco a poco mientras giras (trail braking) para llevar más velocidad al vértice sin pasarte.",
            "trail",
            "En esta curva, sube 2–3 km/h la velocidad en el vértice en cada tanda, soltando el freno más tarde y más suave."),
        Problem.SlowExit => new(
            "Sales de la curva más lento: abres gas tarde o el coche no apunta todavía a la salida.",
            "Busca un vértice algo más tardío y empieza a acelerar, de forma progresiva, en cuanto el coche apunta a la salida.",
            "throttle-ramp",
            "En esta curva, prioriza la salida: empieza a acelerar un poco antes en cada vuelta y mira la velocidad al final de la recta."),
        Problem.LateThrottle => new(
            "En la recta tardas en ir a fondo.",
            "Acelera a fondo en cuanto el volante esté recto. Si el coche patina, abre más progresivo o sube el control de tracción.",
            "throttle-ramp",
            "En las salidas lentas, cuenta «uno» desde el vértice y ya debes estar a fondo."),
        Problem.Coasting => new(
            "Pasas tiempo sin acelerar ni frenar: el coche se descarga y pierdes velocidad.",
            "Enlaza la suelta del freno con el inicio del gas, aunque sea parcial (10–30 %) para mantener el coche asentado.",
            "throttle-partial",
            "En el Entrenador, «Acelerador parcial estable». En pista, busca no tener nunca los dos pies levantados."),
        Problem.Overlap => new(
            "Pisas acelerador y freno a la vez: frenas peor y desgastas más.",
            "Suelta el acelerador del todo antes de frenar, salvo que frenes con el pie izquierdo a propósito (rally).",
            "brake-steps",
            "Fíjate en las primeras frenadas de cada vuelta: el acelerador debe estar a 0 cuando el freno empieza."),
        Problem.NoTrail => new(
            "Sueltas el freno del todo antes de girar: el morro se descarga y el coche no quiere entrar en la curva.",
            "Llega al punto de giro con un 20–40 % de freno y suéltalo poco a poco hasta cerca del vértice.",
            "trail",
            "En el Entrenador, «Trail braking». En pista, una curva lenta: alarga la suelta del freno un poco más en cada vuelta."),
        Problem.AbruptRelease => new(
            "Sueltas el freno de golpe: la suspensión delantera rebota y el coche subvira justo al entrar.",
            "Suelta el freno en rampa (unos 0,5 s) mientras metes volante.",
            "trail",
            "En el Entrenador, «Trail braking» hasta que la bajada sea una rampa suave y sin escalones."),
        Problem.Limiter => new(
            "Tocas el corte de revoluciones: en el corte no aceleras.",
            "Cambia un poco antes, cuando se enciendan las últimas luces de cambio.",
            null,
            "En una tanda, céntrate sólo en los cambios: ninguno en el corte."),
        Problem.Corrections => new(
            "Corriges mucho el volante: el coche va nervioso (sobreviraje) o las entradas son bruscas.",
            "Gira más suave y progresivo. Si se repite en las mismas curvas, revisa el reglaje (Aprender → Reglaje).",
            null,
            "Una tanda a un 90 % de ritmo buscando mover el volante lo menos posible."),
        _ => new("", "", null, ""),
    };
}
