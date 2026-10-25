using System;
using System.Collections.Generic;
using System.Linq;

namespace SimTelemetry.Tracks;

/// <summary>
/// Qué coche recomiendan fuentes externas, siempre con fuente y fecha (el mejor coche cambia con cada parche).
/// <paramref name="Community"/> = opinión de jugadores en foros, no un análisis medido. <paramref name="Tracks"/>: trozos del nombre
/// del circuito a los que se aplica (vacío = en general).
/// </summary>
public sealed record CarAdvice(string Profile, string Scope, string Cars, string Why, string Source, string Url, string Date,
                               bool Community = false, params string[] Tracks);

public static class CarAdviceBook
{
    private const string CoachDave = "https://coachdaveacademy.com/tutorials/what-is-the-fastest-gt3-car-in-acc/";
    private const string Ggrecon = "https://www.ggrecon.com/guides/forza-motorsport-fastest-cars-tier-list-ranked-class/";
    private const string PcGamer = "https://www.pcgamer.com/forza-motorsport-best-cars";
    private const string GameSkinny = "https://www.gameskinny.com/tips/forza-motorsport-best-cars-tier-list/";
    private const string DirtSteam = "https://steamcommunity.com/app/690790/discussions/0/2952595757895735333";
    private const string DirtGtp = "https://www.gtplanet.net/forum/threads/dirt-rally-2-0-general-discussion.385508/";
    private const string WrcSteam = "https://steamcommunity.com/app/1849250/discussions/0/3975051032841609579";

    // shortcut: datos copiados a mano el 24-10-2026; revisar las fuentes cuando salga un parche de BoP o un juego nuevo.
    public static readonly CarAdvice[] All =
    {
        new("acc", "GT3 · en general", "Ford Mustang GT3",
            "El más rápido en casi todos los circuitos, al sprint y en resistencia. Le siguen el Mercedes-AMG GT3 EVO, el McLaren 720S GT3 EVO, " +
            "el Ferrari 296 GT3 y el Porsche 992 GT3 R. ACC no espera más cambios de BoP, así que este orden debería mantenerse.",
            "Coach Dave Academy", CoachDave, "abril de 2026"),
        new("acc", "GT3 · circuitos lentos y de velocidad media", "Mercedes-AMG GT3 EVO",
            "Muy completo y fuerte al sprint; destaca en Laguna Seca, Zandvoort, Brands Hatch y Oulton Park.",
            "Coach Dave Academy", CoachDave, "abril de 2026", false, "laguna", "zandvoort", "brands", "oulton"),
        new("acc", "GT3 · circuitos técnicos de velocidad media", "Porsche 992 GT3 R",
            "Domina en circuitos concretos si te adaptas a su estilo: Suzuka, Kyalami, Imola y Barcelona.",
            "Coach Dave Academy", CoachDave, "abril de 2026", false, "suzuka", "kyalami", "imola", "barcelona"),
        new("acc", "GT3 · circuitos rápidos", "Audi R8 LMS GT3 EVO II",
            "Fuerte en Spa, Monza, Silverstone y Bathurst; pierde en los circuitos lentos.",
            "Coach Dave Academy", CoachDave, "abril de 2026", false, "spa", "monza", "silverstone", "bathurst", "mount_panorama"),
        new("acc", "GT3 · para empezar y para resistencia", "Ferrari 296 GT3",
            "El más fácil de llevar de los rápidos y muy bueno en carreras largas.",
            "Coach Dave Academy", CoachDave, "abril de 2026"),

        new("forza", "Clase E", "BMW 2002 Turbo (1973)", "La elección habitual en la clase más baja.", "GGRecon", Ggrecon, "2023"),
        new("forza", "Clase D", "Nissan Silvia Spec-R (2000) · Mercedes 190E 2.5-16 Evo II", "Los mejor valorados de la clase.", "GameSkinny", GameSkinny, "2023"),
        new("forza", "Clase B", "Lotus Emira (2023)", "La recomendación más repetida en la clase B.", "GameSkinny", GameSkinny, "2023"),
        new("forza", "Clase S", "Ford Zakspeed Capri Turbo (1981)", "Por delante del Sunoco T70 MkIIIB y del Nissan R390 GT1.", "GGRecon", Ggrecon, "2023"),
        new("forza", "Clase R", "Ferrari 599XX Evolution", "Alternativas: Radical RXC Turbo y Aston Martin Vulkan AMR.", "PC Gamer", PcGamer, "2023"),

        new("dirt", "Grupo B 4WD (la clase más rápida)", "Lancia Delta S4 · Peugeot 205 T16 Evo 2",
            "Muchos de los tiempos más rápidos se hacen con el Delta S4, que además es el más fácil de los Grupo B 4WD; el 205 T16 puede ser algo " +
            "más rápido pero cuesta mucho controlarlo.", "Foros de Steam y GTPlanet", DirtGtp, "2019", true),
        new("dirt", "Qué clase es más rápida", "Grupo B 4WD › 2000cc ≈ R5 › Grupo A",
            "Los 2000cc ganan en tramos de subida; los R5 tienen menos potencia pero más agarre. Si trompeas a menudo, mejor 2000cc o R5 que las clases de tracción trasera.",
            "Foro de Steam", DirtSteam, "2019", true),

        new("wrc", "Grupo B RWD", "Lancia 037 (tramos técnicos) · BMW M1 (tramos rápidos)",
            "Se reparten el primer puesto según el tramo.", "Foro de Steam", WrcSteam, "noviembre de 2023", true),
    };

    /// <summary>Clave del perfil de juego ("acc", "forza"…) de un nombre de juego tal como lo graba la app.</summary>
    private static string? Key(string game) => Telemetry.GameProfiles.For(game)?.Key;

    public static IEnumerable<CarAdvice> ForProfile(string profileKey) => All.Where(a => a.Profile == profileKey);

    /// <summary>Recomendaciones concretas para un circuito (las generales no se repiten aquí).</summary>
    public static IEnumerable<CarAdvice> ForTrack(string game, string track)
    {
        string key = Key(game) ?? "", t = track.ToLowerInvariant();
        return All.Where(a => a.Profile == key && a.Tracks.Any(x => t.Contains(x)));
    }
}
