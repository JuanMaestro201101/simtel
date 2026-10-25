using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using SimTelemetry.Diagnostics;
using SimTelemetry.Setup;

namespace SimTelemetry;

/// <summary>Pestaña "Juegos": comprobaciones de estado y permiso de red para Forza.</summary>
public partial class MainWindow
{
    private List<string>? _forzaMissing;
    private bool _checkingGames;

    private void OnCheckGames(object sender, RoutedEventArgs e) => CheckGames();

    private async void CheckGames()
    {
        if (_checkingGames) return;
        _checkingGames = true;
        try
        {
            DirtStatus.Text = GameConfigurator.CodemastersStatus(_app.Settings.CodemastersPort);
            WrcStatus.Text = GameConfigurator.EaWrcStatus();
            ForzaStatus.Text = "Comprobando…";

            var (packages, missing) = await Task.Run(GameConfigurator.ForzaLoopbackStatus);
            _forzaMissing = missing;
            ForzaStatus.Text = packages.Count == 0
                ? "No hay ningún Forza de Microsoft Store / Game Pass instalado (la versión de Steam no necesita este permiso)."
                : missing.Count == 0
                    ? "Permiso de red concedido ✔ — si no llegan datos, revisa Data Out en el juego (IP 127.0.0.1, puerto 5300, formato «Aceleración de auto» / Car Dash, no «Trineo»)."
                    : $"⚠ Falta el permiso de red para: {string.Join(", ", missing)}. Pulsa «Dar permiso de red a Forza».";
            ForzaPermissionButton.IsEnabled = missing.Count > 0;
        }
        finally
        {
            _checkingGames = false;
        }
    }

    private async void OnForzaPermission(object sender, RoutedEventArgs e)
    {
        var missing = _forzaMissing ?? (await Task.Run(GameConfigurator.ForzaLoopbackStatus)).Missing;
        if (missing.Count == 0)
        {
            CheckGames();
            return;
        }
        ForzaStatus.Text = "Esperando la confirmación de Windows…";
        bool ok = await Task.Run(() => GameConfigurator.RequestForzaLoopback(missing));
        Log.Info($"Permiso de red para Forza: {(ok ? "concedido" : "no aplicado")}");
        _forzaMissing = null;
        CheckGames();
        MessageBox.Show(this,
            ok ? "Permiso concedido. Si Forza estaba abierto, ciérralo y vuelve a abrirlo."
               : "No se aplicó el permiso (¿cancelaste el aviso de Windows?). Puedes volver a intentarlo.",
            "Forza", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(Log.FilePath)) Log.Info("Registro creado");
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Log.FilePath}\"") { UseShellExecute = true });
    }
}
