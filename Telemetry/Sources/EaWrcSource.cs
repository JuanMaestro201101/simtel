using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace SimTelemetry.Telemetry.Sources;

/// <summary>
/// EA SPORTS WRC. El juego permite definir estructuras de paquete propias; SimTelemetry instala
/// "simtelemetry.json" (ver <see cref="Setup.GameConfigurator"/>) con exactamente los canales de
/// <see cref="Channels"/>. Los tipos se leen de readme/channels.json del juego si existe.
/// </summary>
public sealed class EaWrcSource : UdpSourceBase
{
    public const string StructureId = "simtelemetry";

    /// <summary>Canales del paquete, en orden, con el tipo esperado por defecto.</summary>
    public static readonly (string Id, string Type)[] Channels =
    {
        ("vehicle_gear_index", "uint8"),
        ("vehicle_gear_index_reverse", "uint8"),
        ("vehicle_engine_rpm_current", "float32"),
        ("vehicle_engine_rpm_max", "float32"),
        ("vehicle_speed", "float32"),
        ("vehicle_throttle", "float32"),
        ("vehicle_brake", "float32"),
        ("vehicle_clutch", "float32"),
        ("vehicle_steering", "float32"),
        ("vehicle_handbrake", "float32"),
        ("vehicle_acceleration_x", "float32"),
        ("vehicle_acceleration_y", "float32"),
        ("vehicle_acceleration_z", "float32"),
        ("vehicle_forward_direction_x", "float32"),
        ("vehicle_forward_direction_y", "float32"),
        ("vehicle_forward_direction_z", "float32"),
        ("vehicle_left_direction_x", "float32"),
        ("vehicle_left_direction_y", "float32"),
        ("vehicle_left_direction_z", "float32"),
        ("stage_current_time", "float32"),
        ("stage_current_distance", "float64"),
        ("stage_length", "float64"),
        // Añadidos en la versión 2 de la estructura (mapa del tramo).
        ("vehicle_position_x", "float32"),
        ("vehicle_position_z", "float32"),
        // Versión 3: nombres del tramo y del coche (ver readme/ids.json).
        ("location_id", "uint16"),
        ("route_id", "uint16"),
        ("vehicle_id", "uint16"),
        ("vehicle_class_id", "uint16"),
    };

    /// <summary>Canales de las versiones anteriores de la estructura: 1 sin posición, 2 sin nombres.</summary>
    private const int LegacyChannelCount = 22, V2ChannelCount = 24;

    private readonly Dictionary<string, (int Offset, string Type)> _layout = new();
    private int _packetSize;
    private int _legacySize;
    private int _v2Size;
    private Dictionary<string, Dictionary<int, string>>? _names;
    private DateTime _namesReadAt;

    public EaWrcSource(int port) : base(port) { GameName = "EA SPORTS WRC"; }

    public override string Id => "eawrc";
    public override string DisplayName => "EA SPORTS WRC (UDP)";

    public static string TelemetryFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "WRC", "telemetry");

    protected override void OnStarted() => BuildLayout();

    private void BuildLayout()
    {
        var types = ReadChannelTypes();
        _layout.Clear();
        int offset = 0;
        for (int i = 0; i < Channels.Length; i++)
        {
            var (id, defaultType) = Channels[i];
            if (i == LegacyChannelCount) _legacySize = offset;
            if (i == V2ChannelCount) _v2Size = offset;
            string type = types.TryGetValue(id, out var t) ? t : defaultType;
            _layout[id] = (offset, type);
            offset += SizeOf(type);
        }
        _packetSize = offset;
    }

    /// <summary>Lee los tipos reales de cada canal desde readme/channels.json (lo regenera el juego al arrancar).</summary>
    public static Dictionary<string, string> ReadChannelTypes()
    {
        var result = new Dictionary<string, string>();
        try
        {
            string path = Path.Combine(TelemetryFolder, "readme", "channels.json");
            if (!File.Exists(path)) return result;
            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root?["channels"] is not JsonArray channels) return result;
            foreach (var ch in channels)
            {
                string? id = ch?["id"]?.GetValue<string>();
                string? type = ch?["type"]?.GetValue<string>();
                if (id != null && type != null) result[id] = type;
            }
        }
        catch
        {
            // Si el archivo no se puede leer usamos los tipos por defecto.
        }
        return result;
    }

    private static int SizeOf(string type) => type switch
    {
        "boolean" or "bool" or "uint8" or "int8" => 1,
        "uint16" or "int16" => 2,
        "uint32" or "int32" or "float32" => 4,
        "uint64" or "int64" or "float64" => 8,
        _ => 4,
    };

    private double Read(byte[] d, string id)
    {
        var (o, type) = _layout[id];
        return type switch
        {
            "boolean" or "bool" or "uint8" => d[o],
            "int8" => (sbyte)d[o],
            "uint16" => BitConverter.ToUInt16(d, o),
            "int16" => BitConverter.ToInt16(d, o),
            "uint32" => BitConverter.ToUInt32(d, o),
            "int32" => BitConverter.ToInt32(d, o),
            "uint64" => BitConverter.ToUInt64(d, o),
            "int64" => BitConverter.ToInt64(d, o),
            "float64" => BitConverter.ToDouble(d, o),
            _ => BitConverter.ToSingle(d, o),
        };
    }

    protected override bool TryParse(byte[] d, out TelemetrySample s)
    {
        s = TelemetrySample.Unknown();
        if (_packetSize == 0) BuildLayout();
        bool legacy = d.Length == _legacySize;
        bool v2 = d.Length == _v2Size;
        if (d.Length != _packetSize && !legacy && !v2)
        {
            Status = $"Paquete de {d.Length} bytes (se esperaban {_packetSize}). " +
                     "Usa \"Configurar EA WRC\" en la pestaña Juegos y comprueba que el puerto no lo use otra estructura.";
            return false;
        }

        int gear = (int)Read(d, "vehicle_gear_index");
        int reverse = (int)Read(d, "vehicle_gear_index_reverse");

        float ax = (float)Read(d, "vehicle_acceleration_x"), ay = (float)Read(d, "vehicle_acceleration_y"), az = (float)Read(d, "vehicle_acceleration_z");
        float fx = (float)Read(d, "vehicle_forward_direction_x"), fy = (float)Read(d, "vehicle_forward_direction_y"), fz = (float)Read(d, "vehicle_forward_direction_z");
        float lx = (float)Read(d, "vehicle_left_direction_x"), ly = (float)Read(d, "vehicle_left_direction_y"), lz = (float)Read(d, "vehicle_left_direction_z");

        s.Time = Read(d, "stage_current_time");
        s.LapTime = (float)s.Time;
        s.LapDistance = (float)Read(d, "stage_current_distance");
        s.TrackLength = (float)Read(d, "stage_length");
        s.Lap = 0;
        s.LastLapTime = -1;
        s.SpeedKmh = (float)Read(d, "vehicle_speed") * 3.6f;
        s.Rpm = (float)Read(d, "vehicle_engine_rpm_current");
        s.MaxRpm = (float)Read(d, "vehicle_engine_rpm_max");
        s.Gear = gear == reverse ? -1 : gear;
        s.Throttle = Clamp01((float)Read(d, "vehicle_throttle"));
        s.Brake = Clamp01((float)Read(d, "vehicle_brake"));
        s.Clutch = Clamp01((float)Read(d, "vehicle_clutch"));
        s.Steering = ClampSigned((float)Read(d, "vehicle_steering"));
        s.Handbrake = Clamp01((float)Read(d, "vehicle_handbrake"));
        // Aceleración proyectada sobre los ejes del coche (positivo = hacia delante / hacia la derecha).
        s.LonG = (ax * fx + ay * fy + az * fz) / 9.81f;
        s.LatG = -(ax * lx + ay * ly + az * lz) / 9.81f;
        s.LapProgress = s.TrackLength > 0 ? Math.Clamp(s.LapDistance / s.TrackLength, 0, 1) : -1;
        if (!legacy)
        {
            // En WRC el eje X es positivo hacia la izquierda: lo invertimos para el mapa visto desde arriba.
            s.PosX = -(float)Read(d, "vehicle_position_x");
            s.PosZ = (float)Read(d, "vehicle_position_z");
        }
        s.InSession = true;

        string? route = null, location = null;
        if (!legacy && !v2)
        {
            route = Name("routes", (int)Read(d, "route_id"));
            location = Name("locations", (int)Read(d, "location_id"));
            string? vehicle = Name("vehicles", (int)Read(d, "vehicle_id"));
            string? cls = Name("vehicle_classes", (int)Read(d, "vehicle_class_id"));
            Car = vehicle == null ? null : cls == null ? vehicle : $"{vehicle} ({cls})";
        }
        Track = route != null ? (location != null ? $"{route} · {TitleCase(location)}" : route)
              : s.TrackLength > 0 ? $"Tramo de {s.TrackLength / 1000f:0.00} km" : null;
        Status = legacy || v2 ? "Recibiendo datos (pulsa de nuevo \"Configurar EA WRC\" para ver el nombre del tramo y el mapa)" : "Recibiendo datos";
        return true;
    }

    /// <summary>Nombre de un identificador de readme/ids.json (null si no está o es 0).</summary>
    private string? Name(string list, int id)
    {
        // ids.json lo crea el juego al arrancar: si aún no existía, volvemos a mirar cada 30 s.
        if (_names == null || (_names.Count == 0 && DateTime.UtcNow > _namesReadAt.AddSeconds(30)))
        {
            _names = ReadIds();
            _namesReadAt = DateTime.UtcNow;
        }
        return id > 0 && _names.TryGetValue(list, out var map) && map.TryGetValue(id, out var name) ? name : null;
    }

    /// <summary>readme/ids.json (lo regenera el juego al arrancar, en UTF-16): listas de { id, name }.</summary>
    private static Dictionary<string, Dictionary<int, string>> ReadIds()
    {
        var result = new Dictionary<string, Dictionary<int, string>>();
        try
        {
            string path = Path.Combine(TelemetryFolder, "readme", "ids.json");
            if (!File.Exists(path)) return result;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return result;
            foreach (var (key, node) in root)
            {
                if (node is not JsonArray items) continue;
                var map = new Dictionary<int, string>();
                foreach (var item in items)
                    if (item?["id"] is JsonValue idv && idv.TryGetValue(out int id) && item["name"]?.GetValue<string>() is string name)
                        map[id] = name.Trim();
                result[key] = map;
            }
        }
        catch (Exception)
        {
            // Sin nombres: seguimos con "Tramo de X km".
        }
        return result;
    }

    /// <summary>"RALLY MEDITERRANEO" → "Rally Mediterraneo".</summary>
    private static string TitleCase(string s) =>
        s.Any(char.IsLower) ? s : System.Globalization.CultureInfo.GetCultureInfo("es-ES").TextInfo.ToTitleCase(s.ToLowerInvariant());
}
