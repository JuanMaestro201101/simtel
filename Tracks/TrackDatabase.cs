using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SimTelemetry.Tracks;

public sealed record Corner(string Name, string Tip);

/// <summary>Lo que conviene saber de un circuito: carácter, curvas clave, adelantamientos, reglaje y coche.</summary>
public sealed record TrackInfo
{
    public required string Name { get; init; }
    public required string Country { get; init; }
    public double LengthKm { get; init; }
    public int Turns { get; init; }
    /// <summary>Nombres con que lo llaman los juegos (ACC "monza", AC "ks_monza", iRacing "monza full"…).</summary>
    public required string[] Aliases { get; init; }
    public required string Character { get; init; }
    public required Corner[] Corners { get; init; }
    public required string Overtaking { get; init; }
    public required string Setup { get; init; }
    public required string CarChoice { get; init; }
    /// <summary>Ritmo orientativo de un piloto rápido en ACC, GT3 en seco.</summary>
    public string? AccGt3Pace { get; init; }
}

/// <summary>
/// Información de los circuitos más habituales en ACC, Assetto Corsa, iRacing y Forza.
/// Los puntos de frenada exactos no están aquí: salen de tus propias vueltas (pestaña Circuitos e Ingeniero).
/// </summary>
public static class TrackDatabase
{
    /// <summary>Busca el circuito por el nombre que da el juego (null si no lo conocemos).</summary>
    public static TrackInfo? Find(string? track)
    {
        if (string.IsNullOrWhiteSpace(track)) return null;
        string t = Normalize(track);
        foreach (var info in All)
            foreach (var alias in info.Aliases)
            {
                string a = Normalize(alias);
                if (t == a || (a.Length >= 5 ? t.Contains(a) : t.StartsWith(a) || t.EndsWith(a))) return info;
            }
        return null;
    }

    public static TrackInfo? ByName(string? name) => All.FirstOrDefault(t => t.Name == name);

    // Circuitos oficiales de ACC y de Assetto Corsa (Kunos, con sus DLC). De los demás juegos no tenemos la lista
    // segura, así que se muestra toda la biblioteca; en rally, ninguna (no hay circuitos).
    private static readonly HashSet<string> InAcc = new()
    {
        "Spa-Francorchamps", "Monza", "Nürburgring GP", "Silverstone", "Brands Hatch", "Barcelona-Catalunya", "Hungaroring",
        "Zandvoort", "Paul Ricard", "Misano", "Zolder", "Kyalami", "Suzuka", "Laguna Seca", "Mount Panorama (Bathurst)", "Imola",
        "Oulton Park", "Donington Park", "Snetterton", "Circuit of the Americas", "Indianapolis (circuito)", "Watkins Glen",
        "Valencia (Ricardo Tormo)", "Nürburgring 24h (Nordschleife + GP)",
    };
    private static readonly HashSet<string> InAc = new()
    {
        "Spa-Francorchamps", "Monza", "Nürburgring GP", "Nürburgring Nordschleife", "Silverstone", "Brands Hatch",
        "Barcelona-Catalunya", "Zandvoort", "Imola", "Mugello", "Laguna Seca", "Red Bull Ring",
    };

    /// <summary>¿Tiene sentido mostrar este circuito de la biblioteca en el perfil de ese juego?</summary>
    public static bool InGame(TrackInfo track, Telemetry.GameProfile profile) =>
        profile.Rally ? false
        : profile.Key == "acc" ? InAcc.Contains(track.Name)
        : profile.Key == "ac" ? InAc.Contains(track.Name)
        : true;

    private const string Tarmac = "Frena más tarde y con más presión que en tierra; los cortes ensucian la carretera, úsalos con cuidado. Suspensión más dura y baja.";
    private const string Gravel = "Frena antes y más en línea recta y deja que el coche deslice. Suspensión blanda y alta. Si sales de los primeros, la grava suelta te frena.";
    private const string FastGravel = "Tierra muy rápida con saltos y crestas: aterriza con el coche recto, no frenes en el aire y fíate de las notas.";
    private const string RoughGravel = "Tierra muy rota y con piedras: cuida el coche, suspensión alta y blanda, y un ritmo constante vale más que atacar.";
    private const string Snow = "Nieve y hielo con neumáticos de clavos: movimientos muy suaves; puedes apoyarte en los muros de nieve en las curvas.";

    /// <summary>Superficie y consejo de los rallies de EA WRC (por el nombre del rally en el tramo).</summary>
    public static (string Surface, string Tip)? Rally(string? track)
    {
        string t = Normalize(track ?? "");
        (string Key, string Surface, string Tip)[] rallies =
        {
            ("mediterraneo", "Asfalto", Tarmac), ("croatia", "Asfalto bacheado", Tarmac), ("japan", "Asfalto estrecho, a menudo húmedo", Tarmac),
            ("centraleurope", "Asfalto", Tarmac), ("iberia", "Asfalto", Tarmac),
            ("montecarlo", "Asfalto con hielo y nieve en zonas", "Asfalto con placas de hielo: escucha las notas de hielo y frena antes y más suave en ellas."),
            ("sweden", "Nieve y hielo", Snow),
            ("portugal", "Tierra", Gravel), ("sardegna", "Tierra arenosa y estrecha", Gravel), ("mexico", "Tierra, en altura (el motor rinde menos)", Gravel),
            ("chile", "Tierra rápida y fluida", Gravel), ("estonia", "Tierra rápida con saltos", FastGravel), ("finland", "Tierra muy rápida con saltos", FastGravel),
            ("latvia", "Tierra rápida", FastGravel), ("poland", "Tierra rápida", FastGravel), ("oceania", "Tierra rápida", FastGravel),
            ("acropolis", "Tierra muy rota y con piedras", RoughGravel), ("kenya", "Tierra muy rota (fesh-fesh y piedras)", RoughGravel),
        };
        foreach (var r in rallies)
            if (t.Contains(r.Key)) return (r.Surface, r.Tip);
        return null;
    }

    /// <summary>"Nürburgring GP" → "nurburringgp": minúsculas, sin tildes ni símbolos.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static readonly Corner[] NordschleifeCorners = new[]
    {
        new Corner("Hatzenbach", "Serie de curvas enlazadas al principio: ritmo y suavidad, no ataques."),
        new Corner("Fuchsröhre", "Bajada muy rápida con compresión: coche recto y estable, sin tocar el freno en el fondo."),
        new Corner("Adenauer Forst", "Chicane lenta tras la bajada: frena en línea recta, es fácil pasarse."),
        new Corner("Karussell", "Curva peraltada de hormigón a la izquierda: entra por dentro, al peralte, y mantén el gas estable."),
        new Corner("Pflanzgarten", "Saltos: llega con el coche recto y sin frenar en el aire."),
        new Corner("Döttinger Höhe", "La recta larga final: la salida de la curva anterior (Galgenkopf) vale muchos segundos."),
    };

    // El orden importa: los alias más específicos (Nordschleife) antes que los genéricos (nurburgring).
    public static readonly IReadOnlyList<TrackInfo> All = new List<TrackInfo>
    {
        new()
        {
            Name = "Nürburgring 24h (Nordschleife + GP)", Country = "Alemania", LengthKm = 25.378, Turns = 170,
            Aliases = new[] { "nurburgring_24h", "nurburgring24h", "nurburgring combined" },
            Character = "El trazado de las 24 Horas: el circuito GP unido al «Infierno Verde». Más de 25 km de curvas ciegas, saltos y cambios de rasante. Aprende el circuito por tramos antes de buscar tiempo.",
            Corners = NordschleifeCorners,
            Overtaking = "Recta de meta del GP, Döttinger Höhe y las frenadas que la siguen.",
            Setup = "Altura algo mayor y suspensión más blanda de lo normal para los saltos y bordillos; aerodinámica media.",
            CarChoice = "Coches estables y fáciles en los baches. Más vale un coche predecible que el más rápido en recta.",
        },
        new()
        {
            Name = "Nürburgring Nordschleife", Country = "Alemania", LengthKm = 20.832, Turns = 154,
            Aliases = new[] { "nordschleife" },
            Character = "El «Infierno Verde»: 20 km de curvas ciegas, saltos y cambios de rasante entre guardarraíles. Aquí manda la memoria: aprende el circuito por tramos antes de buscar tiempo.",
            Corners = NordschleifeCorners,
            Overtaking = "Döttinger Höhe y las frenadas que la siguen; en el resto, sólo con el rival fuera de la trazada.",
            Setup = "Altura algo mayor y suspensión más blanda de lo normal para los saltos y bordillos; aerodinámica media.",
            CarChoice = "Coches estables y fáciles en los baches. Más vale un coche predecible que el más rápido en recta.",
        },
        new()
        {
            Name = "Spa-Francorchamps", Country = "Bélgica", LengthKm = 7.004, Turns = 19,
            Aliases = new[] { "spa", "spa_francorchamps", "spafrancorchamps" },
            Character = "Rápido y con mucho desnivel: rectas largas (Kemmel, la de Blanchimont) y curvas de alta velocidad. La velocidad punta y la valentía en las curvas rápidas marcan la vuelta.",
            Corners = new[]
            {
                new Corner("La Source", "Horquilla lenta tras la meta: frenada fuerte en bajada. Sacrifica la entrada: la salida decide toda la subida a Eau Rouge y Kemmel."),
                new Corner("Eau Rouge – Raidillon", "A fondo en GT3 en seco si sales bien de La Source. Entra un poco por la izquierda, no levantes en la compresión y deja que el coche se cargue."),
                new Corner("Les Combes", "Frenada fuerte al final de la recta de Kemmel, la mejor zona de adelantamiento. Derecha-izquierda: prioriza la segunda parte."),
                new Corner("Bruxelles (Rivage)", "Horquilla en bajada a la derecha: es fácil bloquear la rueda interior. Frena un poco antes y gira pronto."),
                new Corner("Pouhon", "Doble izquierda rápida en bajada: un toque de freno o sólo levantar. Mucha velocidad de paso, coche suave."),
                new Corner("Campus – Stavelot", "Chicane lenta y luego derecha larga: sal bien de Stavelot porque empieza la zona más rápida."),
                new Corner("Blanchimont", "A fondo (en seco) a mucha velocidad: no corrijas bruscamente."),
                new Corner("Bus Stop", "Chicane final con la frenada más fuerte de la vuelta. Usa poco piano en la segunda parte para salir recto a meta."),
            },
            Overtaking = "Les Combes (tras Kemmel), Bus Stop y La Source.",
            Setup = "Alerón bajo-medio (compromiso entre las rectas y Pouhon/Blanchimont); suspensión firme y altura suficiente para la compresión de Eau Rouge.",
            CarChoice = "Buena velocidad punta y estabilidad aerodinámica en curvas rápidas.",
            AccGt3Pace = "≈ 2:17–2:18",
        },
        new()
        {
            Name = "Monza", Country = "Italia", LengthKm = 5.793, Turns = 11,
            Aliases = new[] { "monza", "ks_monza" },
            Character = "El templo de la velocidad: rectas larguísimas, tres chicanes y frenadas muy fuertes. Ganas tiempo frenando tarde y recto y saliendo bien de las chicanes.",
            Corners = new[]
            {
                new Corner("Variante del Rettifilo (1ª chicane)", "La frenada más fuerte del circuito, desde más de 260 km/h en GT3. Frena recto y a fondo; no te comas el piano interior de la segunda parte."),
                new Corner("Curva Grande", "A fondo. Mantente por fuera para preparar la Roggia."),
                new Corner("Variante della Roggia", "Frenada fuerte en recta; izquierda-derecha. La salida importa más que la entrada."),
                new Corner("Lesmo 1 y 2", "Dos derechas medias. La segunda es más cerrada: entra algo más despacio para acelerar antes hacia la recta del puente."),
                new Corner("Variante Ascari", "Chicane rápida de tres partes. Un toque de freno, ritmo suave y salida limpia: viene una recta larga."),
                new Corner("Parabolica (Curva Alboreto)", "Frenada fuerte y una derecha larga que se abre. Entra tarde y abre gas pronto y progresivo: la salida define la recta de meta."),
            },
            Overtaking = "Rettifilo y Roggia, con rebufo en la recta.",
            Setup = "Alerón al mínimo y estabilidad en frenada (reparto algo hacia delante). Suspensión que trague los pianos de las chicanes.",
            CarChoice = "Velocidad punta y buena frenada; la carga aerodinámica sirve de poco aquí.",
            AccGt3Pace = "≈ 1:47–1:48",
        },
        new()
        {
            Name = "Nürburgring GP", Country = "Alemania", LengthKm = 5.148, Turns = 15,
            Aliases = new[] { "nurburgring", "nurburgring_gp", "ks_nurburgring" },
            Character = "Técnico, con curvas lentas y medias y frenadas en bajada. Premia la tracción y no cometer errores en las horquillas.",
            Corners = new[]
            {
                new Corner("Yokohama-S (curva 1)", "Frenada fuerte al final de la recta de meta y horquilla a la derecha. Buen sitio para adelantar; no te pases porque la salida es lenta."),
                new Corner("Mercedes-Arena", "Zona lenta y enrevesada: prioriza salir bien hacia la bajada."),
                new Corner("Dunlop-Kehre", "Horquilla en bajada al final de la recta: frena antes de lo que crees y gira pronto."),
                new Corner("Schumacher-S", "Izquierda-derecha rápida: un toque de freno o sólo levantar, y mucha velocidad de paso."),
                new Corner("NGK-Schikane", "Chicane final lenta: frena recto y usa el piano con moderación."),
                new Corner("Coca-Cola-Kurve", "Última curva antes de meta: sal a fondo, la recta es larga."),
            },
            Overtaking = "Curva 1, Dunlop-Kehre y la chicane final.",
            Setup = "Carga media-alta y buena tracción en las salidas lentas.",
            CarChoice = "Coches con buena tracción y frenada; la punta importa menos.",
            AccGt3Pace = "≈ 1:53–1:54",
        },
        new()
        {
            Name = "Silverstone", Country = "Reino Unido", LengthKm = 5.891, Turns = 18,
            Aliases = new[] { "silverstone", "ks_silverstone" },
            Character = "Muy rápido y fluido. Las curvas de alta velocidad (Copse, Maggots-Becketts) premian la confianza y la carga aerodinámica.",
            Corners = new[]
            {
                new Corner("Abbey", "Derecha muy rápida tras la meta: casi a fondo."),
                new Corner("Village y The Loop", "Zona lenta: frena fuerte para The Loop y sal bien hacia Aintree y la recta Wellington."),
                new Corner("Brooklands", "Frenada fuerte al final de Wellington: sitio clásico de adelantamiento."),
                new Corner("Luffield", "Derecha larga y lenta: paciencia, el gas llega tarde pero la salida a Woodcote es clave."),
                new Corner("Copse", "Derecha muy rápida: sólo un toque de freno o levantar."),
                new Corner("Maggots – Becketts – Chapel", "Las eses rápidas: ritmo y suavidad, sacrifica la primera para enlazar bien la última y salir a Hangar Straight."),
                new Corner("Stowe", "Frenada fuerte al final de Hangar Straight: adelantamiento."),
                new Corner("Vale y Club", "Chicane lenta y la última curva: tracción para la recta de meta."),
            },
            Overtaking = "Brooklands, Stowe y Village.",
            Setup = "Carga aerodinámica media-alta: gana mucho en Copse y Becketts.",
            CarChoice = "Coches con buena carga aerodinámica y estables en curva rápida.",
            AccGt3Pace = "≈ 1:57–1:58",
        },
        new()
        {
            Name = "Brands Hatch", Country = "Reino Unido", LengthKm = 3.908, Turns = 9,
            Aliases = new[] { "brands_hatch", "brandshatch", "ks_brands_hatch" },
            Character = "Corto, con mucho desnivel y curvas ciegas. Vuelta corta: cada décima cuenta y el tráfico es constante.",
            Corners = new[]
            {
                new Corner("Paddock Hill Bend", "Derecha en bajada y ciega tras la meta: frena poco y pronto, el coche se descarga en la bajada."),
                new Corner("Druids", "Horquilla en subida: frenada fuerte, entra lento y sal bien."),
                new Corner("Graham Hill Bend", "Izquierda que da a la recta Cooper: tracción."),
                new Corner("Hawthorn", "Derecha rápida tras la recta del bosque: frenada fuerte, adelantamiento."),
                new Corner("Sheene Curve (Dingle Dell)", "Bajada y compresión: coche estable."),
                new Corner("Clearways – Clark Curve", "Última derecha larga hacia meta: entra con paciencia y acelera pronto."),
            },
            Overtaking = "Paddock Hill (por dentro, con valor), Druids y Hawthorn.",
            Setup = "Carga alta y suspensión que aguante las compresiones.",
            CarChoice = "Coches ágiles con buena tracción.",
            AccGt3Pace = "≈ 1:22–1:23",
        },
        new()
        {
            Name = "Barcelona-Catalunya", Country = "España", LengthKm = 4.657, Turns = 14,
            Aliases = new[] { "barcelona", "catalunya", "ks_barcelona" },
            Character = "Equilibrado: recta larga y todo tipo de curvas. Muy exigente con los neumáticos delanteros (curva 3).",
            Corners = new[]
            {
                new Corner("Elf (curva 1)", "Frenada fuerte al final de la recta, derecha-izquierda: adelantamiento principal."),
                new Corner("Renault (curva 3)", "Derecha larguísima y rápida: aquí sufren los neumáticos. Gas estable."),
                new Corner("Repsol y Seat", "Curva 4 media y curva 5 lenta en bajada: tracción."),
                new Corner("Campsa (curva 9)", "Derecha rápida y ciega en subida: confianza."),
                new Corner("La Caixa (curva 10)", "Frenada fuerte tras la recta opuesta: segundo punto de adelantamiento."),
                new Corner("Sector final", "Curvas medias y rápidas: la salida de la última decide la recta de meta."),
            },
            Overtaking = "Curva 1 y La Caixa.",
            Setup = "Carga media-alta; cuida el desgaste delantero (menos caída si se calientan mucho).",
            CarChoice = "Coches equilibrados y suaves con los neumáticos.",
            AccGt3Pace = "≈ 1:42–1:43",
        },
        new()
        {
            Name = "Hungaroring", Country = "Hungría", LengthKm = 4.381, Turns = 14,
            Aliases = new[] { "hungaroring" },
            Character = "Lento y revirado, «un Mónaco sin muros». Pocas rectas, muchas curvas enlazadas: la tracción y el ritmo constante valen más que la punta.",
            Corners = new[]
            {
                new Corner("Curva 1", "Derecha cerrada en bajada tras la meta: la frenada más fuerte y el mejor sitio para adelantar."),
                new Corner("Curvas 2 y 3", "Izquierda larga en bajada y derecha: no entres pasado."),
                new Corner("Curva 4", "Izquierda rápida y ciega en una cresta: confianza y coche estable."),
                new Corner("Curvas 8 a 11", "Serie enlazada: sacrifica cada entrada para la siguiente. Aquí se gana o se pierde la vuelta."),
                new Corner("Curva 14 (última)", "Derecha larga hacia meta: acelera pronto y progresivo."),
            },
            Overtaking = "Casi sólo en la curva 1; un poco en la 2.",
            Setup = "Carga aerodinámica alta, buena tracción y frenos fríos (refrigeración).",
            CarChoice = "Coches ágiles y con buena tracción en curva lenta.",
            AccGt3Pace = "≈ 1:42–1:43",
        },
        new()
        {
            Name = "Zandvoort", Country = "Países Bajos", LengthKm = 4.259, Turns = 14,
            Aliases = new[] { "zandvoort", "ks_zandvoort" },
            Character = "Estrecho, entre dunas y con curvas rápidas y ciegas. Hay poco sitio para adelantar: la clasificación pesa mucho.",
            Corners = new[]
            {
                new Corner("Tarzanbocht (curva 1)", "Horquilla a la derecha al final de la recta: frenada fuerte y el mejor adelantamiento."),
                new Corner("Hugenholtzbocht", "Horquilla a la izquierda: tracción hacia la subida."),
                new Corner("Scheivlak", "Derecha rápida y ciega en bajada: velocidad de paso y valor."),
                new Corner("Arie Luyendykbocht (última)", "La salida a la recta de meta: acelera pronto."),
            },
            Overtaking = "Tarzanbocht; poco más.",
            Setup = "Carga alta: muchas curvas rápidas y poca recta.",
            CarChoice = "Coches con buena carga y cambios de dirección rápidos.",
            AccGt3Pace = "≈ 1:34–1:35",
        },
        new()
        {
            Name = "Paul Ricard", Country = "Francia", LengthKm = 5.770, Turns = 15,
            Aliases = new[] { "paul_ricard", "paulricard", "castellet" },
            Character = "Plano, con escapatorias enormes de colores y la larga recta de Mistral cortada por una chicane. Fácil de aprender, difícil de clavar.",
            Corners = new[]
            {
                new Corner("Primeras curvas", "Zona técnica tras la meta: precisión, las escapatorias perdonan pero invalidan la vuelta."),
                new Corner("Chicane de Mistral", "Frenada fuerte a mitad de la recta: adelantamiento."),
                new Corner("Signes", "Derecha muy rápida al final de Mistral: casi a fondo."),
                new Corner("Double droite du Beausset", "Doble derecha larga: velocidad constante, carga sobre los neumáticos izquierdos."),
            },
            Overtaking = "Chicane de Mistral y la curva 1.",
            Setup = "Carga media; cuida los neumáticos en el Beausset.",
            CarChoice = "Buen equilibrio entre punta y curva rápida.",
            AccGt3Pace = "≈ 1:53–1:54",
        },
        new()
        {
            Name = "Misano", Country = "Italia", LengthKm = 4.226, Turns = 16,
            Aliases = new[] { "misano" },
            Character = "Plano y de curvas lentas y medias, con alguna muy rápida. Tracción y frenada.",
            Corners = new[]
            {
                new Corner("Curvone", "Derecha muy rápida: a fondo o casi."),
                new Corner("Quercia", "Horquilla lenta: frenada fuerte y tracción."),
                new Corner("Carro", "Horquilla lenta del final: frenada fuerte, adelantamiento."),
            },
            Overtaking = "Quercia y Carro.",
            Setup = "Carga media-alta y buena tracción.",
            CarChoice = "Coches con buena tracción.",
            AccGt3Pace = "≈ 1:33–1:34",
        },
        new()
        {
            Name = "Zolder", Country = "Bélgica", LengthKm = 4.011, Turns = 10,
            Aliases = new[] { "zolder" },
            Character = "Estrecho, con chicanes y pianos altos. Muy exigente con los bordillos.",
            Corners = new[]
            {
                new Corner("Earste Links (curva 1)", "Izquierda rápida tras la meta."),
                new Corner("Chicanes", "Pianos altos: no los ataques con el coche cargado o saltará."),
                new Corner("Bolderberg", "Zona en subida y bajada: coche estable."),
                new Corner("Jochen Rindt-bocht", "Chicane final: frena recto y sal bien hacia meta."),
            },
            Overtaking = "Frenadas de las chicanes.",
            Setup = "Suspensión que trague pianos y carga media-alta.",
            CarChoice = "Coches estables sobre los bordillos.",
            AccGt3Pace = "≈ 1:27–1:28",
        },
        new()
        {
            Name = "Kyalami", Country = "Sudáfrica", LengthKm = 4.522, Turns = 16,
            Aliases = new[] { "kyalami" },
            Character = "Mucho desnivel y curvas de todo tipo, algunas ciegas. Exige al coche en curva rápida y en frenada en bajada.",
            Corners = new[]
            {
                new Corner("Crowthorne (curva 1)", "Frenada fuerte en bajada tras la recta: adelantamiento. Es fácil bloquear."),
                new Corner("Sunset", "Derecha rápida en bajada: confianza."),
                new Corner("Leeukop", "Curva lenta antes de meta: frenada fuerte y tracción para la recta."),
            },
            Overtaking = "Crowthorne y Leeukop.",
            Setup = "Carga media; frenada estable en bajada.",
            CarChoice = "Coches estables en frenada.",
            AccGt3Pace = "≈ 1:40–1:41",
        },
        new()
        {
            Name = "Suzuka", Country = "Japón", LengthKm = 5.807, Turns = 18,
            Aliases = new[] { "suzuka" },
            Character = "Clásico en forma de ocho, muy técnico. Las eses del primer sector y las curvas rápidas premian el ritmo.",
            Corners = new[]
            {
                new Corner("Curvas 1 y 2", "Derecha rápida en bajada y otra más lenta: frena antes de la 2, no en la 1."),
                new Corner("Las eses (S Curves)", "Ritmo y suavidad: sacrifica cada curva para la siguiente."),
                new Corner("Degner 1 y 2", "Dos derechas: la 2 es más lenta y engaña."),
                new Corner("Horquilla", "Frenada fuerte y lenta."),
                new Corner("Spoon", "Izquierda larga de doble vértice: la salida decide la recta opuesta."),
                new Corner("130R", "Izquierda muy rápida: casi a fondo."),
                new Corner("Chicane Casio", "Frenada fuerte antes de meta: adelantamiento."),
            },
            Overtaking = "Curva 1, chicane Casio y la horquilla.",
            Setup = "Carga media-alta: muchas curvas rápidas.",
            CarChoice = "Coches con buena carga y cambio de dirección.",
            AccGt3Pace = "≈ 1:58–1:59",
        },
        new()
        {
            Name = "Laguna Seca", Country = "EE. UU.", LengthKm = 3.602, Turns = 11,
            Aliases = new[] { "laguna_seca", "lagunaseca", "ks_laguna_seca", "weathertech raceway" },
            Character = "Corto, con mucho desnivel y el famoso Sacacorchos. Frenadas fuertes y una curva ciega única.",
            Corners = new[]
            {
                new Corner("Andretti Hairpin (curva 2)", "Frenada fuerte y horquilla de doble vértice: entra tarde y no te pases."),
                new Corner("Curvas 5 y 6", "La 6 sube: tracción hacia la recta Rahal."),
                new Corner("Corkscrew (8 y 8A)", "Izquierda-derecha ciega que cae varios pisos: frena antes de la cresta, apunta al árbol y deja caer el coche."),
                new Corner("Rainey Curve (curva 9)", "Izquierda rápida en bajada: coche estable."),
                new Corner("Curva 11 (última)", "Horquilla lenta: frena fuerte y sal bien a la recta de meta."),
            },
            Overtaking = "Curva 2, curva 11 y la frenada de la 5.",
            Setup = "Carga media-alta y frenos fuertes.",
            CarChoice = "Coches ágiles y buenos frenando.",
            AccGt3Pace = "≈ 1:21–1:22",
        },
        new()
        {
            Name = "Mount Panorama (Bathurst)", Country = "Australia", LengthKm = 6.213, Turns = 23,
            Aliases = new[] { "mount_panorama", "mountpanorama", "bathurst" },
            Character = "Un circuito urbano en una montaña: rectas enormes (Conrod) y una sección de montaña estrecha entre muros. Un error se paga contra el muro.",
            Corners = new[]
            {
                new Corner("Hell Corner (curva 1)", "Izquierda lenta tras meta: tracción para subir la Mountain Straight."),
                new Corner("Griffins Bend y The Cutting", "Subida lenta y empinada: frena en recto y acelera sin patinar."),
                new Corner("Skyline y The Esses", "Cresta ciega a la izquierda y bajada en eses: frena antes de la cresta."),
                new Corner("Forrest's Elbow", "Izquierda lenta que da a Conrod: la salida es la más importante de la vuelta."),
                new Corner("The Chase", "Chicane rápida al final de Conrod, desde muy alta velocidad: frenada fuerte."),
                new Corner("Murray's Corner", "Última izquierda lenta: frena fuerte y sal bien a meta."),
            },
            Overtaking = "The Chase y Hell Corner.",
            Setup = "Alerón bajo-medio por Conrod, pero coche estable en la montaña.",
            CarChoice = "Velocidad punta y frenada estable.",
            AccGt3Pace = "≈ 2:00–2:01",
        },
        new()
        {
            Name = "Imola", Country = "Italia", LengthKm = 4.909, Turns = 19,
            Aliases = new[] { "imola" },
            Character = "Clásico, estrecho y en sentido antihorario, con pianos agresivos y mucho desnivel.",
            Corners = new[]
            {
                new Corner("Tamburello", "Chicane tras la meta: frenada fuerte."),
                new Corner("Villeneuve y Tosa", "Chicane rápida y luego horquilla: Tosa es el mejor adelantamiento."),
                new Corner("Piratella", "Izquierda ciega en cresta: confianza."),
                new Corner("Acque Minerali", "Bajada y subida a la derecha: frena en la bajada y sal bien en la subida."),
                new Corner("Variante Alta", "Chicane con pianos altos: cuidado al saltarlos."),
                new Corner("Rivazza", "Doble izquierda en bajada: frenada fuerte."),
            },
            Overtaking = "Tosa, Tamburello y Rivazza.",
            Setup = "Suspensión que aguante pianos y carga media.",
            CarChoice = "Coches estables sobre bordillos y buenos frenando.",
            AccGt3Pace = "≈ 1:40–1:41",
        },
        new()
        {
            Name = "Oulton Park", Country = "Reino Unido", LengthKm = 4.307, Turns = 17,
            Aliases = new[] { "oulton_park", "oultonpark" },
            Character = "Revirado, estrecho y con desnivel: curvas ciegas sin apenas escapatoria.",
            Corners = new[]
            {
                new Corner("Old Hall", "Derecha tras la meta."),
                new Corner("Cascades", "Izquierda rápida en bajada: difícil de clavar."),
                new Corner("Shell Oils Hairpin", "Horquilla lenta: frenada fuerte."),
                new Corner("Knickerbrook", "Chicane: frena recto."),
                new Corner("Lodge", "Última curva con compresión: sal bien a meta."),
            },
            Overtaking = "Horquilla y Lodge.",
            Setup = "Carga alta y suspensión que trague baches.",
            CarChoice = "Coches ágiles y estables.",
            AccGt3Pace = "≈ 1:32–1:33",
        },
        new()
        {
            Name = "Donington Park", Country = "Reino Unido", LengthKm = 4.020, Turns = 12,
            Aliases = new[] { "donington" },
            Character = "Rápido y fluido en la primera mitad (Craner Curves) y lento con horquillas al final.",
            Corners = new[]
            {
                new Corner("Redgate", "Derecha tras la meta: frenada en bajada."),
                new Corner("Craner Curves", "Bajada rápida en eses: compromiso total."),
                new Corner("Coppice", "Derecha ciega que da a la recta: salida."),
                new Corner("Melbourne Hairpin", "Frenada muy fuerte: adelantamiento."),
                new Corner("Goddards", "Horquilla final: tracción hacia meta."),
            },
            Overtaking = "Melbourne Hairpin y Redgate.",
            Setup = "Carga media.",
            CarChoice = "Equilibrio entre frenada y tracción.",
            AccGt3Pace = "≈ 1:25–1:26",
        },
        new()
        {
            Name = "Snetterton", Country = "Reino Unido", LengthKm = 4.779, Turns = 12,
            Aliases = new[] { "snetterton" },
            Character = "Plano y ventoso, con dos rectas largas y una horquilla clave.",
            Corners = new[]
            {
                new Corner("Riches", "Derecha rápida tras la meta."),
                new Corner("Montreal", "Horquilla: frenada fuerte, adelantamiento."),
                new Corner("Brundle y Nelson", "Curvas enlazadas: ritmo."),
                new Corner("Bombhole", "Rápida y con compresión: coche estable."),
                new Corner("Coram y Murrays", "Final de vuelta: tracción hacia meta."),
            },
            Overtaking = "Montreal y Brundle.",
            Setup = "Carga media.",
            CarChoice = "Buena tracción y punta.",
            AccGt3Pace = "≈ 1:46–1:47",
        },
        new()
        {
            Name = "Circuit of the Americas", Country = "EE. UU.", LengthKm = 5.513, Turns = 20,
            Aliases = new[] { "cota", "americas", "austin" },
            Character = "Moderno y completo: subida a la curva 1, eses rápidas, una recta muy larga y zona lenta al final.",
            Corners = new[]
            {
                new Corner("Curva 1", "Horquilla en subida: puedes frenar tarde porque la cuesta ayuda. Entrada abierta."),
                new Corner("Curvas 2 a 6 (eses)", "Muy rápidas y enlazadas: ritmo y suavidad."),
                new Corner("Curva 11", "Horquilla lenta antes de la recta larga: la salida lo es todo."),
                new Corner("Curva 12", "Frenada muy fuerte al final de la recta: adelantamiento."),
                new Corner("Curvas 16 a 18", "Derecha larga de varios vértices: carga constante en los neumáticos."),
            },
            Overtaking = "Curvas 12, 1 y 11.",
            Setup = "Carga media; compromiso entre las eses y la recta.",
            CarChoice = "Coches equilibrados.",
            AccGt3Pace = "≈ 2:04–2:05",
        },
        new()
        {
            Name = "Indianapolis (circuito)", Country = "EE. UU.", LengthKm = 4.167, Turns = 14,
            Aliases = new[] { "indianapolis" },
            Character = "Zona interior lenta y técnica y un tramo del óvalo peraltado como recta de meta.",
            Corners = new[]
            {
                new Corner("Curva 1", "Frenada fuerte al final de la recta del óvalo: adelantamiento."),
                new Corner("Zona interior", "Curvas lentas: tracción y frenadas en recta."),
                new Corner("Últimas curvas", "La salida al peralte del óvalo decide la recta más larga."),
            },
            Overtaking = "Curva 1 y la frenada de la zona interior.",
            Setup = "Carga media-baja y buena tracción.",
            CarChoice = "Punta y tracción.",
            AccGt3Pace = "≈ 1:33–1:34",
        },
        new()
        {
            Name = "Watkins Glen", Country = "EE. UU.", LengthKm = 5.552, Turns = 11,
            Aliases = new[] { "watkins_glen", "watkinsglen", "the glen" },
            Character = "Rápido y fluido, con guardarraíles cerca y la sección de la «bota».",
            Corners = new[]
            {
                new Corner("Curva 1 (The 90)", "Derecha en bajada al final de la recta: frenada fuerte y adelantamiento."),
                new Corner("Las eses", "Subida rápida: casi a fondo con un coche estable."),
                new Corner("Bus Stop (Inner Loop)", "Chicane al final de la recta opuesta: frenada fuerte."),
                new Corner("La bota (Toe y Heel)", "Zona más lenta: tracción."),
                new Corner("Curvas 10 y 11", "Rápidas hacia meta: salida."),
            },
            Overtaking = "Curva 1 y Bus Stop.",
            Setup = "Carga media, estabilidad en curva rápida.",
            CarChoice = "Coches estables en alta velocidad.",
            AccGt3Pace = "≈ 1:43–1:44",
        },
        new()
        {
            Name = "Valencia (Ricardo Tormo)", Country = "España", LengthKm = 4.005, Turns = 14,
            Aliases = new[] { "valencia", "ricardo_tormo", "cheste" },
            Character = "Estadio con curvas lentas y medias y una recta de meta larga. Tracción y frenada.",
            Corners = new[]
            {
                new Corner("Curva 1", "Frenada fuerte al final de la recta: adelantamiento."),
                new Corner("Zona central", "Curvas lentas enlazadas: ritmo."),
                new Corner("Última curva", "Horquilla a la izquierda: la salida decide la recta de meta."),
            },
            Overtaking = "Curva 1 y la última curva.",
            Setup = "Carga alta y tracción.",
            CarChoice = "Coches ágiles y con tracción.",
            AccGt3Pace = "≈ 1:30–1:31",
        },
        new()
        {
            Name = "Red Bull Ring", Country = "Austria", LengthKm = 4.318, Turns = 10,
            Aliases = new[] { "red_bull_ring", "redbullring", "spielberg", "ks_red_bull_ring" },
            Character = "Corto, con mucho desnivel y tres zonas de frenada fuerte en subida. Pocas curvas: cada error se nota.",
            Corners = new[]
            {
                new Corner("Curva 1 (Niki Lauda)", "Derecha en subida: frena tarde, la cuesta ayuda."),
                new Corner("Curva 3 (Remus)", "Horquilla en subida tras la recta: la mejor frenada para adelantar."),
                new Corner("Curva 4 (Schlossgold)", "Derecha en bajada: es fácil pasarse."),
                new Corner("Curvas 6 y 7", "Rápidas en bajada: velocidad de paso."),
                new Corner("Curvas 9 y 10", "Rápidas hacia meta: la 10 en bajada engaña."),
            },
            Overtaking = "Curvas 3, 1 y 4.",
            Setup = "Carga media-baja y buena frenada.",
            CarChoice = "Punta y frenada.",
        },
        new()
        {
            Name = "Mugello", Country = "Italia", LengthKm = 5.245, Turns = 15,
            Aliases = new[] { "mugello" },
            Character = "Rápido y con mucho desnivel, una recta muy larga y curvas rápidas enlazadas.",
            Corners = new[]
            {
                new Corner("San Donato (curva 1)", "Frenada muy fuerte al final de la recta: adelantamiento."),
                new Corner("Arrabbiata 1 y 2", "Derechas muy rápidas: compromiso y coche estable."),
                new Corner("Bucine (última)", "Izquierda larga hacia la recta: la salida es clave."),
            },
            Overtaking = "San Donato.",
            Setup = "Carga media-alta para las curvas rápidas.",
            CarChoice = "Coches con buena carga aerodinámica.",
        },
        new()
        {
            Name = "Road Atlanta", Country = "EE. UU.", LengthKm = 4.088, Turns = 12,
            Aliases = new[] { "road_atlanta", "roadatlanta" },
            Character = "Rápido, con desnivel y las eses ciegas; la última chicane y curva en bajada son delicadas.",
            Corners = new[]
            {
                new Corner("Curva 1", "Derecha en subida tras meta."),
                new Corner("Eses (3 a 5)", "Bajada rápida y ciega: ritmo."),
                new Corner("Curva 10 (chicane)", "Frenada fuerte al final de la recta: adelantamiento."),
                new Corner("Curva 12", "Bajada rápida bajo el puente hacia meta."),
            },
            Overtaking = "Chicane de la curva 10 y curva 1.",
            Setup = "Carga media.",
            CarChoice = "Coches estables.",
        },
        new()
        {
            Name = "Le Mans (La Sarthe)", Country = "Francia", LengthKm = 13.626, Turns = 38,
            Aliases = new[] { "lemans", "le_mans", "sarthe" },
            Character = "Rectas larguísimas (Hunaudières) con chicanes y curvas rápidas. Velocidad punta y frenadas desde muy alta velocidad.",
            Corners = new[]
            {
                new Corner("Dunlop", "Chicane tras la meta: frenada fuerte."),
                new Corner("Chicanes de Hunaudières", "Frenadas desde muy alta velocidad: frena recto."),
                new Corner("Mulsanne", "Derecha lenta al final de la recta: adelantamiento."),
                new Corner("Indianapolis y Arnage", "Lentas: tracción hacia las rectas."),
                new Corner("Porsche Curves", "Rápidas y enlazadas: confianza."),
            },
            Overtaking = "Chicanes, Mulsanne y Arnage.",
            Setup = "Alerón bajo.",
            CarChoice = "Velocidad punta.",
        },
    };
}
