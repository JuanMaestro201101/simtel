using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SimTelemetry.Input;
using SimTelemetry.Training;

namespace SimTelemetry;

/// <summary>
/// Pestaña "Entrenador": ejercicios de precisión de pedales. Funciona con el juego o sólo con el volante
/// conectado (DirectInput), sin ningún juego abierto.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _trainerTimer = new() { Interval = TimeSpan.FromMilliseconds(8) };
    private readonly Stopwatch _trainerClock = new();
    private readonly Stopwatch _uiClock = Stopwatch.StartNew();
    private PedalExercise? _exercise;
    private ExerciseScorer? _scorer;
    private bool _trainerRunning;
    private double _lastTrainerTime;
    private double _lastWheelUiUpdate;
    private bool _autoDetectOffered;
    private PedalDetector? _detector;
    private bool _startAfterDetection;
    private const double Countdown = 3;

    private sealed class ExerciseItem
    {
        public ExerciseItem(PedalExercise ex, int best)
        {
            Exercise = ex;
            Best = best > 0 ? $"★ {best}" : "";
        }

        public PedalExercise Exercise { get; }
        public string Name => Exercise.Name;
        public string Subtitle => $"{Exercise.ChannelName} · {Exercise.Duration:0} s · margen ±{Exercise.Tolerance:P0}";
        public string Best { get; }
    }

    private void InitTrainer()
    {
        RefreshExerciseList(PedalExercise.All[0].Id);
        _loading = true;
        (_app.Settings.PedalInput switch
        {
            PedalInputModes.Wheel => InputWheel,
            PedalInputModes.Game => InputGame,
            _ => InputAuto,
        }).IsChecked = true;
        _loading = false;
        _app.Wheel.DevicesChanged += () => Dispatcher.BeginInvoke(UpdateWheelUi);
        _trainerTimer.Tick += OnTrainerTick; // se activa sólo con la pestaña Entrenador a la vista
        IsVisibleChanged += (_, _) => _trainerTimer.IsEnabled = IsVisible && Tabs.SelectedItem == TabTrainer;
        UpdateWheelUi();
        RefreshFeelCombo();
    }

    private void RefreshExerciseList(string? selectId)
    {
        var items = PedalExercise.All
            .Select(e => new ExerciseItem(e, _app.Settings.TrainerBest.TryGetValue(e.Id, out var b) ? b : 0))
            .ToList();
        ExerciseList.ItemsSource = items;
        ExerciseList.SelectedItem = items.FirstOrDefault(i => i.Exercise.Id == selectId) ?? items[0];
    }

    private void OnExerciseSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ExerciseList.SelectedItem is not ExerciseItem item) return;
        if (_trainerRunning) StopTrainer(showResult: false);
        _exercise = item.Exercise;
        ExerciseTitle.Text = $"{_exercise.Name}  ·  {_exercise.ChannelName}";
        ExerciseDescription.Text = _exercise.Description;
        ExerciseWhy.Text = "Por qué: " + _exercise.Why;
        Trainer.Exercise = _exercise;
        Trainer.Time = 0;
        Trainer.Reset();
        int best = _app.Settings.TrainerBest.TryGetValue(_exercise.Id, out var b) ? b : 0;
        TrainerScore.Text = "--";
        TrainerResultTitle.Text = best > 0 ? $"Tu mejor marca: {best} puntos" : "Todavía no lo has hecho";
        TrainerSummary.Text = "La banda amarilla es el objetivo; tu pedal es la línea de color. Lo que viene aparece por la derecha.";
        TrainerAdvice.Text = "";
    }

    private void OnTrainerInputChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.PedalInput = InputWheel.IsChecked == true ? PedalInputModes.Wheel
            : InputGame.IsChecked == true ? PedalInputModes.Game
            : PedalInputModes.Auto;
        SaveSoon();
        UpdateWheelUi();
    }

    private float ReadPedal(PedalChannel channel)
    {
        var source = _app.PedalSource;
        if (source == App.PedalSourceKind.None) return 0;
        bool brake = channel == PedalChannel.Brake;
        float value = brake ? _app.Pedals.Brake : _app.Pedals.Throttle;
        // Con el volante, aplicamos la calibración medida del juego elegido: el mismo pedal, el mismo porcentaje.
        if (source == App.PedalSourceKind.Wheel && _app.Settings.PedalFeel.TryGetValue(_app.Settings.TrainerFeel, out var curve)
            && PedalFeel.Ready(curve, brake))
            value = PedalFeel.Map(curve, brake, value);
        return value;
    }

    /// <summary>Juegos con el tacto ya medido, para elegir con cuál practicar.</summary>
    private void RefreshFeelCombo()
    {
        var games = new List<(string Game, string Label)> { ("", "Pedal tal cual (sin la calibración de ningún juego)") };
        games.AddRange(_app.Settings.PedalFeel
            .Where(kv => PedalFeel.Ready(kv.Value, true) || PedalFeel.Ready(kv.Value, false))
            .OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Key)));
        if (games.All(g => g.Game != _app.Settings.TrainerFeel)) _app.Settings.TrainerFeel = "";
        FeelOptions.Children.Clear();
        foreach (var (game, label) in games)
        {
            var radio = new RadioButton
            {
                GroupName = "TrainerFeel",
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                Margin = new Thickness(0, 0, 0, 6),
                IsChecked = game == _app.Settings.TrainerFeel,
            };
            radio.Checked += (_, _) =>
            {
                _app.Settings.TrainerFeel = game;
                SaveSoon();
                UpdateFeelInfo();
            };
            FeelOptions.Children.Add(radio);
        }
        UpdateFeelInfo();
    }

    private void UpdateFeelInfo()
    {
        string game = _app.Settings.TrainerFeel;
        if (FeelOptions.Children.Count <= 1)
        {
            FeelInfo.Text = "Cada juego convierte tu pedal a su manera (saturación, curva, zona muerta). Conduce unos minutos con el volante en un juego que envíe telemetría (ACC, DiRT, WRC…): la app medirá su tacto y podrás practicar aquí con el mismo.";
            return;
        }
        if (game.Length == 0 || !_app.Settings.PedalFeel.TryGetValue(game, out var curve))
        {
            FeelInfo.Text = "El 100 % aquí es el pedal a fondo del todo. Elige un juego para practicar con su calibración.";
            return;
        }
        string Describe(bool brake, string name) => !PedalFeel.Ready(curve, brake) ? $"el {name} aún no está medido (se usa tal cual)"
            : PedalFeel.FullAt(curve, brake) >= 0.97f ? $"el {name} va igual que el pedal"
            : $"el {name} llega al 100 % con el {PedalFeel.FullAt(curve, brake):P0} del recorrido";
        FeelInfo.Text = $"En {game}, {Describe(true, "freno")} y {Describe(false, "acelerador")}. Los ejercicios usan ese mismo tacto.";
    }

    private void OnTrainerTick(object? sender, EventArgs e)
    {
        if (!IsVisible || Tabs.SelectedItem != TabTrainer) return;
        double now = _uiClock.Elapsed.TotalSeconds;

        if (_detector != null) UpdateDetection(now);
        else if (!_autoDetectOffered && _app.Wheel.Devices.Count > 0 && !PedalsCalibrated)
        {
            // Primera vez que abres el Entrenador con un volante sin calibrar: empezamos a detectar los pedales.
            _autoDetectOffered = true;
            StartDetection();
        }

        if (now - _lastWheelUiUpdate > 0.066)
        {
            _lastWheelUiUpdate = now;
            AxisMonitor.Update(_app.Wheel.Devices, _app.Settings.Pedals);
            UpdatePedalSourceText();
        }

        var ex = _exercise;
        if (ex == null) return;
        float value = ReadPedal(ex.Channel);
        Trainer.Current = value;
        if (_trainerRunning)
        {
            double t = _trainerClock.Elapsed.TotalSeconds - Countdown;
            double dt = Math.Clamp(t - _lastTrainerTime, 0, 0.1);
            _lastTrainerTime = t;
            Trainer.Time = t;
            if (t >= 0)
            {
                Trainer.AddSample(t, value);
                _scorer!.Add(t, dt, value);
                int left = (int)Math.Ceiling(ex.Duration - t);
                TrainerResultTitle.Text = $"En curso… {left} s · dentro de la banda {_scorer.Accuracy:P0}";
            }
            if (t > ex.Duration) StopTrainer(showResult: true);
        }
        Trainer.InvalidateVisual();
    }

    private bool PedalsCalibrated => _app.Settings.Pedals.Brake.IsSet && _app.Settings.Pedals.Throttle.IsSet;

    private void OnTrainerStart(object sender, RoutedEventArgs e)
    {
        if (_exercise == null) return;
        if (_app.PedalSource == App.PedalSourceKind.None)
        {
            if (_app.Wheel.Devices.Count > 0 && !PedalsCalibrated)
            {
                // Hay volante pero aún no sabemos qué eje es cada pedal: lo detectamos y luego empezamos.
                _startAfterDetection = true;
                StartDetection();
                return;
            }
            MessageBox.Show(this,
                _app.Wheel.Devices.Count == 0
                    ? "No se detecta ningún volante y no llega telemetría de ningún juego.\n\nConecta el volante (y ábrelo en Logitech G HUB / el software de tu marca si hace falta) o abre un juego y quédate en boxes."
                    : "El modo elegido no tiene pedales disponibles ahora. Prueba con «Automático».",
                "Entrenador", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _scorer = new ExerciseScorer(_exercise);
        Trainer.Reset();
        _lastTrainerTime = -Countdown;
        _trainerClock.Restart();
        _trainerRunning = true;
        _app.TrainerRunning = true;
        _app.Recorder.Paused = true; // en boxes no queremos grabar "vueltas" vacías
        _app.UpdateOverlayVisibility();
        TrainerStart.IsEnabled = false;
        TrainerStop.IsEnabled = true;
        TrainerScore.Text = "--";
        TrainerSummary.Text = "Prepárate…";
        TrainerAdvice.Text = "";
    }

    private void OnTrainerStop(object sender, RoutedEventArgs e) => StopTrainer(showResult: true);

    private void StopTrainer(bool showResult)
    {
        if (!_trainerRunning) return;
        _trainerRunning = false;
        _app.TrainerRunning = false;
        _app.Recorder.Paused = false;
        _app.UpdateOverlayVisibility();
        TrainerStart.IsEnabled = true;
        TrainerStop.IsEnabled = false;

        var ex = _exercise;
        var sc = _scorer;
        bool finished = ex != null && _lastTrainerTime >= ex.Duration;
        if (!showResult || ex == null || sc == null || _lastTrainerTime < 2)
        {
            TrainerResultTitle.Text = "Ejercicio detenido";
            return;
        }

        int score = sc.Score;
        TrainerScore.Text = score.ToString();
        TrainerSummary.Text = sc.Summary();
        TrainerAdvice.Text = sc.Advice();
        int best = _app.Settings.TrainerBest.TryGetValue(ex.Id, out var b) ? b : 0;
        if (finished && score > best)
        {
            _app.Settings.TrainerBest[ex.Id] = score;
            _app.Settings.Save();
            TrainerResultTitle.Text = best > 0 ? $"¡Nuevo récord! {score} puntos (antes {best})" : $"Primera marca: {score} puntos";
            RefreshExerciseList(ex.Id);
        }
        else
        {
            TrainerResultTitle.Text = finished ? $"{score} puntos · tu récord: {Math.Max(best, score)}" : $"{score} puntos (ejercicio incompleto)";
        }
    }

    // ---------------- Volante: estado y detección de pedales ----------------

    private void UpdateWheelUi()
    {
        var devices = _app.Wheel.Devices;
        var map = _app.Settings.Pedals;
        if (devices.Count == 0)
            WheelStatus.Text = "No se detecta ningún volante. Conéctalo por USB: aparecerá aquí solo.";
        else if (!PedalsCalibrated)
            WheelStatus.Text = $"🎮 {ShortName(_app.Wheel.MainDeviceName!)} conectado · falta detectar los pedales";
        else
            WheelStatus.Text = $"🎮 {ShortName(_app.Wheel.MainDeviceName!)} conectado ✔\nFreno «{map.Brake.AxisName}» · Acelerador «{map.Throttle.AxisName}»" +
                               (map.Clutch.IsSet ? $" · Embrague «{map.Clutch.AxisName}»" : "");
        CalibrateButton.Content = PedalsCalibrated ? "🎯  Volver a detectar pedales" : "🎯  Detectar pedales";
        InvertPanel.Visibility = PedalsCalibrated && devices.Count > 0 && _detector == null ? Visibility.Visible : Visibility.Collapsed;
        InvertClutchButton.Visibility = map.Clutch.IsSet ? Visibility.Visible : Visibility.Collapsed;
        CalibrateButton.IsEnabled = devices.Count > 0 && _detector == null;
        AxisMonitor.Update(devices, map);
        UpdatePedalSourceText();
    }

    private void UpdatePedalSourceText()
    {
        PedalSourceNow.Text = _app.PedalSource switch
        {
            App.PedalSourceKind.Wheel => "Usando ahora: los pedales del volante (no hace falta ningún juego).",
            App.PedalSourceKind.Game => $"Usando ahora: los pedales que envía {_app.Hub.ActiveSource?.GameName ?? "el juego"}.",
            _ => _app.Wheel.Devices.Count > 0 && !PedalsCalibrated
                ? "Pulsa «Detectar pedales» y pisa cada pedal cuando te lo pida."
                : "Sin pedales ahora mismo: conecta el volante o abre un juego.",
        };
    }

    private void OnCalibrateStart(object sender, RoutedEventArgs e) => StartDetection();

    /// <summary>Arreglo manual de un clic: intercambia las posiciones "suelto" y "a fondo" de un pedal.</summary>
    private void OnInvertPedal(object sender, RoutedEventArgs e)
    {
        var map = _app.Settings.Pedals;
        var axis = ((sender as Button)?.Tag as string) switch
        {
            "brake" => map.Brake,
            "throttle" => map.Throttle,
            "clutch" => map.Clutch,
            _ => null,
        };
        if (axis == null || !axis.IsSet) return;
        (axis.Released, axis.Pressed) = (axis.Pressed, axis.Released);
        _app.Settings.Save();
        Diagnostics.Log.Info($"Pedal «{axis.AxisName}» invertido a mano");
        UpdateWheelUi();
    }

    private void StartDetection()
    {
        if (_app.Wheel.Devices.Count == 0) return;
        if (_trainerRunning) StopTrainer(showResult: false);
        _detector = new PedalDetector();
        CalibrationPanel.Visibility = Visibility.Visible;
        CalibrateButton.IsEnabled = false;
        UpdateDetection(_uiClock.Elapsed.TotalSeconds);
    }

    private void UpdateDetection(double now)
    {
        var d = _detector!;
        d.Update(_app.Wheel.Devices, now);
        CalibrationText.Text = d.Prompt;
        CalibrationProgress.Value = d.Progress;
        CalibrationDone.Text = string.Join("\n", d.Detected);
        CalibrationSkip.Visibility = d.Current == PedalDetector.Step.Clutch ? Visibility.Visible : Visibility.Collapsed;
        if (d.Current != PedalDetector.Step.Done) return;

        _app.Settings.Pedals = d.Result;
        _app.Settings.Save();
        _detector = null;
        CalibrationPanel.Visibility = Visibility.Collapsed;
        UpdateWheelUi();
        if (_startAfterDetection)
        {
            _startAfterDetection = false;
            OnTrainerStart(this, new RoutedEventArgs());
        }
    }

    private void OnCalibrationSkip(object sender, RoutedEventArgs e) =>
        _detector?.SkipClutch(_app.Wheel.Devices, _uiClock.Elapsed.TotalSeconds);

    private void OnCalibrationCancel(object sender, RoutedEventArgs e)
    {
        _detector = null;
        _startAfterDetection = false;
        CalibrationPanel.Visibility = Visibility.Collapsed;
        UpdateWheelUi();
    }
}
