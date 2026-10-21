using System;
using System.IO;
using System.Text.Json;

namespace SimTelemetry;

/// <summary>Pantalla en el teléfono (página web en la red de casa).</summary>
public sealed class PhoneSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8090;
}

/// <summary>Tarjeta de información sobre el juego (circuito, categoría y recomendación).</summary>
public sealed class InfoCardSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>TopLeft, TopRight, BottomLeft o BottomRight (en el monitor del overlay de pedales).</summary>
    public string Corner { get; set; } = "TopLeft";
    private string _key = Keys[0].Name;
    /// <summary>Tecla o combinación para ampliarla u ocultarla (una de <see cref="Keys"/>). Una que ya no está, como «Tab» sola, pasa a Ctrl+Tab.</summary>
    public string Key { get => _key; set => _key = Array.Exists(Keys, k => k.Name == value) ? value : Keys[0].Name; }

    // El juego también recibe las teclas, así que sólo combinaciones o teclas que los juegos de carreras no suelen usar.
    // Mayús+Tab abre el overlay de Steam en los juegos de Steam.
    public static readonly (string Name, int Modifier, int VirtualKey)[] Keys =
    {
        ("Ctrl+Tab", 0x11, 0x09), ("Mayús+Tab", 0x10, 0x09), ("Bloq Despl", 0, 0x91),
        ("F8", 0, 0x77), ("F9", 0, 0x78), ("F10", 0, 0x79), ("Inicio", 0, 0x24),
    };
}

public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public double Left { get; set; } = 60;
    public double Top { get; set; } = 60;
    public double Width { get; set; } = 460;
    public double Height { get; set; } = 140;
    /// <summary>Opacidad del fondo del overlay (las líneas siempre se ven opacas).</summary>
    public double BackgroundOpacity { get; set; } = 0.55;
    /// <summary>Segundos de historial que muestra la gráfica.</summary>
    public double WindowSeconds { get; set; } = 6;
    public bool ShowTrace { get; set; } = true;
    public bool ShowBars { get; set; } = true;
    public bool ShowClutch { get; set; } = false;
    public bool ShowGearSpeed { get; set; } = true;
    /// <summary>Diferencia en vivo con tu mejor vuelta (verde = más rápido).</summary>
    public bool ShowDelta { get; set; } = true;
    /// <summary>Ocultar el overlay cuando no estás conduciendo (menús, escritorio…).</summary>
    public bool AutoHide { get; set; } = true;
}

/// <summary>Calibración de un pedal del volante leído directamente con DirectInput (sin juego).</summary>
public sealed class PedalAxis
{
    /// <summary>Identificador DirectInput del dispositivo (InstanceGuid).</summary>
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    /// <summary>Eje: 0 X, 1 Y, 2 Z, 3 RX, 4 RY, 5 RZ, 6-7 deslizadores.</summary>
    public int Axis { get; set; } = -1;
    public string AxisName { get; set; } = "";
    public int Released { get; set; }
    public int Pressed { get; set; } = 65535;
    public bool IsSet => DeviceId.Length > 0 && Axis >= 0 && Released != Pressed;
}

/// <summary>De dónde salen los pedales del overlay y del entrenador.</summary>
public static class PedalInputModes
{
    /// <summary>El juego si estás en pista; si no, el volante USB.</summary>
    public const string Auto = "auto";
    public const string Wheel = "usb";
    public const string Game = "game";
}

/// <summary>Ingeniero de carrera por voz.</summary>
public sealed class EngineerSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Pitidos de cuenta atrás hasta cada punto de frenada.</summary>
    public bool Beeps { get; set; } = true;
    /// <summary>Voz: intensidad y marcha de cada frenada, resumen de vuelta y neumáticos.</summary>
    public bool Voice { get; set; } = true;
    public int Volume { get; set; } = 80;
    /// <summary>Objetivo progresivo: en las curvas donde eres constante, el aviso llega unos metros más tarde que tu mejor vuelta.</summary>
    public bool Progressive { get; set; } = true;
    /// <summary>Voz elegida ("sapi:…" o "onecore:…"; "" = la primera en español).</summary>
    public string VoiceId { get; set; } = "";
    /// <summary>Velocidad de la voz en % (100 = normal; 125 es un poco más rápida, cómoda en pista).</summary>
    public int VoiceRate { get; set; } = 125;
    /// <summary>Tono de la voz en % (sólo voces modernas).</summary>
    public int VoicePitch { get; set; } = 100;
    /// <summary>Adelanto de los avisos (ms) para compensar el retraso del sonido: ~80 con cable o USB, ~250 con Bluetooth.</summary>
    public int AudioDelayMs { get; set; } = 80;
    /// <summary>En rally (DiRT, EA WRC), avisos de voz en las curvas además de los pitidos (por defecto no: el copiloto ya habla).</summary>
    public bool RallyCalls { get; set; }
    /// <summary>Ya probó el sonido alguna vez (para "Primeros pasos").</summary>
    public bool Tested { get; set; }
}

public sealed class PedalMapping
{
    public PedalAxis Throttle { get; set; } = new();
    public PedalAxis Brake { get; set; } = new();
    public PedalAxis Clutch { get; set; } = new();
}

public sealed class AppSettings
{
    public OverlaySettings Overlay { get; set; } = new();
    public InfoCardSettings InfoCard { get; set; } = new();
    public PhoneSettings Phone { get; set; } = new();
    public int CodemastersPort { get; set; } = 20777;
    public int WrcPort { get; set; } = 20790;
    public int ForzaPort { get; set; } = 5300;
    public int OutGaugePort { get; set; } = 4444;
    public bool RecordingEnabled { get; set; } = true;
    public string DataFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SimTelemetry");
    public bool TrayHintShown { get; set; }
    /// <summary>El usuario ocultó la tarjeta "Primeros pasos".</summary>
    public bool OnboardingHidden { get; set; }
    /// <summary>Perfil de juego que filtra lo que se muestra ("" = todos los juegos). Ver GameProfiles.</summary>
    public string Profile { get; set; } = "";
    /// <summary>Cambiar de perfil solo al abrir un juego (o cuando empieza a enviar datos).</summary>
    public bool AutoProfile { get; set; } = true;
    /// <summary>Tema de la ventana: "dark" o "light".</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>Color de Apariencia ("" = el original, o "#RRGGBB" de UI/Palette.Choices).</summary>
    public string ThemeColor { get; set; } = "";
    /// <summary>"Qué mejorar" en detalle (qué pasa, por qué, cómo y práctica) o en resumen (sólo qué hacer).</summary>
    public bool TipsDetailed { get; set; }
    /// <summary>null = preguntar; true = al cerrar la ventana la app sigue en la bandeja; false = se cierra del todo.</summary>
    public bool? CloseToTray { get; set; }
    public PedalMapping Pedals { get; set; } = new();
    public EngineerSettings Engineer { get; set; } = new();
    /// <summary>Cómo convierte cada juego tu pedal (se mide sola mientras conduces). Se escribe desde el hilo de la telemetría.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, SimTelemetry.Input.PedalFeelCurve> PedalFeel { get; set; } = new();
    /// <summary>Juego cuyo tacto de pedales usa el Entrenador ("" = el pedal tal cual).</summary>
    public string TrainerFeel { get; set; } = "";
    /// <summary>Circuitos que el juego no nombra (p. ej. Forza "Pista #42") asociados a uno de la biblioteca: "juego|pista" → nombre.</summary>
    public System.Collections.Generic.Dictionary<string, string> TrackLinks { get; set; } = new();
    public string PedalInput { get; set; } = PedalInputModes.Auto;
    /// <summary>Mejores puntuaciones del entrenador de pedales, por ejercicio.</summary>
    public System.Collections.Generic.Dictionary<string, int> TrainerBest { get; set; } = new();

    private static string SettingsPath =>
        // Pruebas con el simulador sin tocar los ajustes (ni los puertos) de la app que esté abierta.
        Environment.GetEnvironmentVariable("SIMTELEMETRY_SETTINGS") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimTelemetry", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Ajustes corruptos: empezamos con los valores por defecto.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException)
        {
        }
    }
}
