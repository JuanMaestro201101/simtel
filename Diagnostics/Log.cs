using System;
using System.IO;
using System.Text;

namespace SimTelemetry.Diagnostics;

/// <summary>
/// Registro de diagnóstico en %APPDATA%\SimTelemetry\log.txt: conexiones, cortes de vuelta y errores.
/// Sirve para averiguar qué pasó cuando algo no se graba bien con un juego real.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2_000_000;

    public static string FilePath { get; } =
        Environment.GetEnvironmentVariable("SIMTELEMETRY_LOG") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimTelemetry", "log.txt");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    /// <summary>Último error al escribir el registro (para mostrarlo en la app).</summary>
    public static string? LastWriteError { get; private set; }

    private static readonly string FallbackPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimTelemetry", "log.txt");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    string old = FilePath + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(FilePath, old);
                }
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            // El registro nunca debe romper la app: probamos una ruta alternativa y guardamos el motivo.
            LastWriteError = $"{ex.GetType().Name}: {ex.Message}";
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath)!);
                    File.AppendAllText(FallbackPath, $"[no se pudo escribir en {FilePath}: {LastWriteError}] " + line, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }
    }
}
