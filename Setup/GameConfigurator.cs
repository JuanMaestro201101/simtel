using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SimTelemetry.Telemetry.Sources;

namespace SimTelemetry.Setup;

/// <summary>Activa la telemetría UDP en los archivos de configuración de los juegos que lo necesitan.</summary>
public static class GameConfigurator
{
    private static string MyGames => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games");

    /// <summary>Carpetas de juegos Codemasters con el mismo formato de hardware_settings_config.xml.</summary>
    public static readonly string[] CodemastersGames = { "DiRT Rally 2.0", "DiRT Rally", "DiRT 4" };

    /// <summary>Activa UDP extradata=3 en DiRT Rally / DiRT Rally 2.0 / DiRT 4. Devuelve un resumen legible.</summary>
    public static string ConfigureCodemasters(int port)
    {
        var lines = new System.Collections.Generic.List<string>();
        if (IsRunning("dirtrally2", "drt", "dirt4"))
            lines.Add("⚠ El juego está ABIERTO: ciérralo del todo y vuelve a abrirlo, sólo lee esta configuración al arrancar.");
        foreach (var game in CodemastersGames)
        {
            string path = Path.Combine(MyGames, game, "hardwaresettings", "hardware_settings_config.xml");
            if (!File.Exists(path))
            {
                lines.Add($"{game}: no encontrado (si lo tienes, ábrelo una vez y vuelve a intentarlo).");
                continue;
            }
            try
            {
                Backup(path);
                var doc = XDocument.Load(path);
                var platform = doc.Root?.Element("motion_platform");
                if (platform == null)
                {
                    platform = new XElement("motion_platform");
                    doc.Root?.Add(platform);
                }

                var udps = platform.Elements("udp").ToList();
                var udp = udps.FirstOrDefault(u => (string?)u.Attribute("port") == port.ToString())
                          ?? udps.FirstOrDefault(u => (string?)u.Attribute("enabled") != "true");
                if (udp == null)
                {
                    udp = new XElement("udp");
                    platform.Add(udp);
                }
                udp.SetAttributeValue("enabled", "true");
                udp.SetAttributeValue("extradata", "3");
                udp.SetAttributeValue("ip", "127.0.0.1");
                udp.SetAttributeValue("port", port.ToString());
                udp.SetAttributeValue("delay", "1");

                new FileInfo(path).IsReadOnly = false;
                doc.Save(path);
                lines.Add($"{game}: UDP activado en el puerto {port} ✔");
            }
            catch (Exception ex)
            {
                lines.Add($"{game}: error al modificar el archivo ({ex.Message}).");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Instala la estructura de paquete "simtelemetry" en EA SPORTS WRC y la añade a telemetry\config.json.
    /// </summary>
    public static string ConfigureEaWrc(int port)
    {
        string folder = EaWrcSource.TelemetryFolder;
        string config = Path.Combine(folder, "config.json");
        if (!File.Exists(config))
            return "EA SPORTS WRC: no se encontró Documentos\\My Games\\WRC\\telemetry\\config.json. " +
                   "Abre el juego una vez (hasta el menú principal) para que lo cree y vuelve a intentarlo.";

        try
        {
            // 1) Estructura con exactamente los canales que lee SimTelemetry.
            var versions = ReadWrcVersions(folder);
            var structure = new JsonObject
            {
                ["versions"] = versions,
                ["id"] = EaWrcSource.StructureId,
                ["header"] = new JsonObject { ["channels"] = new JsonArray() },
                ["packets"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "session_update",
                        ["channels"] = new JsonArray(EaWrcSource.Channels.Select(c => (JsonNode)JsonValue.Create(c.Id)!).ToArray()),
                    },
                },
            };
            string udpFolder = Path.Combine(folder, "udp");
            Directory.CreateDirectory(udpFolder);
            File.WriteAllText(Path.Combine(udpFolder, EaWrcSource.StructureId + ".json"),
                structure.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // 2) Registrar el paquete en config.json.
            Backup(config);
            var root = JsonNode.Parse(File.ReadAllText(config))?.AsObject()
                       ?? throw new InvalidDataException("config.json vacío");
            if (root["udp"] is not JsonObject udp)
            {
                udp = new JsonObject();
                root["udp"] = udp;
            }
            if (udp["packets"] is not JsonArray packets)
            {
                packets = new JsonArray();
                udp["packets"] = packets;
            }

            var entry = packets.OfType<JsonObject>()
                .FirstOrDefault(p => p["structure"]?.GetValue<string>() == EaWrcSource.StructureId);
            if (entry == null)
            {
                entry = new JsonObject();
                packets.Add(entry);
            }
            entry["structure"] = EaWrcSource.StructureId;
            entry["packet"] = "session_update";
            entry["ip"] = "127.0.0.1";
            entry["port"] = port;
            entry["frequencyHz"] = -1;
            entry["bEnabled"] = true;

            File.WriteAllText(config, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            string msg = $"EA SPORTS WRC: telemetría activada en el puerto {port} ✔";
            return IsRunning("WRC")
                ? msg + Environment.NewLine + "⚠ El juego está ABIERTO: ciérralo del todo y vuelve a abrirlo, sólo lee esta configuración al arrancar."
                : msg + " Abre el juego y sal a un tramo.";
        }
        catch (Exception ex)
        {
            return "EA SPORTS WRC: error al modificar la configuración (" + ex.Message + ").";
        }
    }

    public static bool IsRunning(params string[] processNames) =>
        processNames.Any(n => Process.GetProcessesByName(n).Length > 0);

    /// <summary>Comprueba si DiRT Rally / DiRT 4 tienen la telemetría UDP activada con el puerto de la app.</summary>
    public static string CodemastersStatus(int port)
    {
        var lines = new List<string>();
        foreach (var game in CodemastersGames)
        {
            string path = Path.Combine(MyGames, game, "hardwaresettings", "hardware_settings_config.xml");
            if (!File.Exists(path)) continue;
            try
            {
                var udp = XDocument.Load(path).Root?.Element("motion_platform")?.Elements("udp")
                    .FirstOrDefault(u => (string?)u.Attribute("port") == port.ToString());
                bool ok = udp != null && (string?)udp.Attribute("enabled") == "true" && (string?)udp.Attribute("extradata") == "3";
                lines.Add(ok ? $"{game}: configuración correcta ✔" : $"{game}: telemetría NO activada → pulsa \"Configurar DiRT Rally\"");
            }
            catch (Exception ex)
            {
                lines.Add($"{game}: no se pudo leer la configuración ({ex.Message})");
            }
        }
        if (lines.Count == 0) lines.Add("No se encontró DiRT Rally / DiRT 4 en Documentos\\My Games.");
        if (IsRunning("dirtrally2", "drt", "dirt4")) lines.Add("El juego está abierto ahora mismo.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Comprueba en telemetry\log.txt si EA WRC cargó la estructura de SimTelemetry.</summary>
    public static string EaWrcStatus()
    {
        string folder = EaWrcSource.TelemetryFolder;
        string config = Path.Combine(folder, "config.json");
        string log = Path.Combine(folder, "log.txt");
        string structure = Path.Combine(folder, "udp", EaWrcSource.StructureId + ".json");
        if (!File.Exists(config)) return "No se encontró la carpeta de telemetría de EA WRC (abre el juego una vez).";
        if (!File.Exists(structure)) return "Telemetría NO configurada → pulsa \"Configurar EA WRC\".";
        if (!File.Exists(log)) return "Configurado. Abre el juego para que lo cargue.";

        var logTime = File.GetLastWriteTime(log);
        string text = File.ReadAllText(log);
        bool loaded = text.Contains("'" + EaWrcSource.StructureId + "'");
        bool stale = logTime < File.GetLastWriteTime(structure);
        var errors = text.Split('\n').Where(l => l.Contains("rror") || l.Contains("ail")).Select(l => l.Trim()).Take(3).ToList();

        try
        {
            var installed = JsonNode.Parse(File.ReadAllText(structure))?["packets"]?[0]?["channels"]?.AsArray().Count ?? 0;
            if (installed != EaWrcSource.Channels.Length)
                return "Hay una versión antigua del formato de SimTelemetry: pulsa «Configurar EA WRC» (con el juego cerrado) para ver el mapa y el nombre de cada tramo y coche.";
        }
        catch (Exception)
        {
            return "El archivo udp\\simtelemetry.json está dañado: pulsa «Configurar EA WRC».";
        }

        if (stale)
            return $"Configurado, pero el juego no se ha abierto desde entonces (último arranque {logTime:dd/MM HH:mm}). " +
                   "Cierra EA WRC del todo y vuelve a abrirlo.";
        if (errors.Count > 0) return "EA WRC dio errores al cargar la telemetría: " + string.Join(" | ", errors);
        return loaded ? $"EA WRC cargó el formato de SimTelemetry ✔ (arranque {logTime:dd/MM HH:mm})"
                      : "EA WRC arrancó pero no cargó el formato de SimTelemetry: vuelve a pulsar \"Configurar EA WRC\".";
    }

    // ---------------- Forza (Microsoft Store / Game Pass) ----------------

    /// <summary>Paquetes de Forza instalados desde la Microsoft Store / Game Pass y si tienen el permiso de red local.</summary>
    public static (List<string> Packages, List<string> Missing) ForzaLoopbackStatus()
    {
        var packages = RunHidden("powershell.exe",
                "-NoProfile -Command \"Get-AppxPackage *Forza* | ForEach-Object { $_.PackageFamilyName }\"")
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string exempt = RunHidden("CheckNetIsolation.exe", "LoopbackExempt -s");
        var missing = packages.Where(p => exempt.IndexOf(p, StringComparison.OrdinalIgnoreCase) < 0).ToList();
        return (packages, missing);
    }

    /// <summary>
    /// Abre (con permiso de administrador, Windows pedirá confirmación) el comando que permite a Forza
    /// enviar la telemetría a este mismo PC.
    /// </summary>
    public static bool RequestForzaLoopback(IEnumerable<string> packageFamilies)
    {
        string commands = string.Join("; ", packageFamilies.Select(p => $"CheckNetIsolation.exe LoopbackExempt -a -n='{p}'"));
        if (commands.Length == 0) return false;
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{commands}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            return p?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // el usuario canceló el aviso de Windows
        }
    }

    private static string RunHidden(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return output;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static JsonNode ReadWrcVersions(string folder)
    {
        try
        {
            string channels = Path.Combine(folder, "readme", "channels.json");
            if (File.Exists(channels) && JsonNode.Parse(File.ReadAllText(channels))?["versions"] is JsonNode v)
                return v.DeepClone();
        }
        catch
        {
            // Usamos la versión por defecto.
        }
        return new JsonObject { ["schema"] = 1, ["data"] = 3 };
    }

    private static void Backup(string path)
    {
        string backup = path + ".simtelemetry.bak";
        if (!File.Exists(backup)) File.Copy(path, backup);
    }
}
