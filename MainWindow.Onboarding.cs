using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SimTelemetry.Recording;

namespace SimTelemetry;

/// <summary>Tarjeta "Primeros pasos" de En vivo: qué falta para sacarle partido a la app, con un botón para cada cosa.</summary>
public partial class MainWindow
{
    private sealed record OnboardingStep(bool Done, string Title, string Detail, string Action, string Target)
    {
        public string Icon => Done ? "✔" : "○";
        public Brush IconBrush => Done ? GoodBrush : WaitBrush;
        public Visibility ActionVisibility => Done || Action.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private DateTime _nextOnboardingCheck;

    /// <summary>Se llama desde el temporizador de la ventana (cada pocos segundos basta).</summary>
    private void UpdateOnboarding()
    {
        if (_app.Settings.OnboardingHidden || DateTime.UtcNow < _nextOnboardingCheck) return;
        _nextOnboardingCheck = DateTime.UtcNow.AddSeconds(3);

        if (!_onboardingLapsRead)
        {
            // Una sola vez: luego lo actualiza cada vuelta guardada (NoteLapForOnboarding).
            var laps = LapRecorder.ListLaps(_app.DataFolder);
            _hasLaps = laps.Count > 0;
            _hasCompletedLap = laps.Any(l => l.Completed && l.Valid && !l.TimeBased);
            _onboardingLapsRead = true;
        }
        bool anyData = _hasLaps || _app.Hub.Sources.Any(s => s.PacketCount > 0);
        bool hasWheel = _app.Wheel.Devices.Count > 0;
        bool completed = _hasCompletedLap;
        var steps = new List<OnboardingStep>
        {
            new(anyData, "Conecta un juego",
                anyData ? "Ya llega (o ha llegado) telemetría." : "Abre un juego compatible y sal a pista. DiRT Rally, EA WRC y Forza necesitan activar la telemetría.",
                "Configurar juegos", "games"),
            new(!hasWheel || PedalsCalibrated, "Pedales del volante",
                !hasWheel ? "Sin volante conectado: se usan los pedales que envía el juego." : PedalsCalibrated ? "Pedales detectados." : "Pisa cada pedal cuando te lo pida (sin tocar el ratón).",
                "Detectar pedales", "trainer"),
            new(completed, "Tu primera vuelta completa",
                completed ? "Ya tienes referencia: el Ingeniero te canta las frenadas." : "Haz una vuelta limpia empezando en la línea de meta (o termina un tramo). Se graba sola.",
                "", ""),
            new(_app.Settings.Engineer.Tested, "Escucha al ingeniero",
                "Comprueba el volumen de la voz y los pitidos en tus auriculares.",
                "Probar sonido", "engineer"),
        };
        bool allDone = steps.All(s => s.Done);
        OnboardingCard.Visibility = allDone ? Visibility.Collapsed : Visibility.Visible;
        if (!allDone) OnboardingSteps.ItemsSource = steps;
    }

    private bool _onboardingLapsRead, _hasLaps, _hasCompletedLap;

    private void NoteLapForOnboarding(LapInfo info)
    {
        _hasLaps = true;
        if (info.Completed && info.Valid && !info.TimeBased) _hasCompletedLap = true;
        _nextOnboardingCheck = DateTime.MinValue;
    }

    private void OnOnboardingAction(object sender, RoutedEventArgs e)
    {
        switch ((sender as Button)?.Tag as string)
        {
            case "games": Tabs.SelectedItem = TabGames; break;
            case "trainer": Tabs.SelectedItem = TabTrainer; break;
            case "engineer":
                OnEngineerTest(sender, e);
                _nextOnboardingCheck = DateTime.MinValue;
                break;
        }
    }

    private void OnOnboardingHide(object sender, RoutedEventArgs e)
    {
        _app.Settings.OnboardingHidden = true;
        OnboardingCard.Visibility = Visibility.Collapsed;
        SaveSoon();
    }
}
