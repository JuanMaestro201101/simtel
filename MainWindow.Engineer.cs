using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SimTelemetry.Engineer;

namespace SimTelemetry;

/// <summary>Pestaña "Ingeniero": ajustes del ingeniero de carrera por voz, su plan de frenadas y los neumáticos.</summary>
public partial class MainWindow
{
    private sealed record CueItem(int Corner, string Left, string Right, bool Practicing)
    {
        public string PracticeLabel => Practicing ? "✓ Practicando" : "Practicar";
    }

    private BrakePlan? _shownPlan;

    private void InitEngineer()
    {
        var e = _app.Settings.Engineer;
        _loading = true;
        EngEnabled.IsChecked = e.Enabled;
        EngBeeps.IsChecked = e.Beeps;
        EngVoice.IsChecked = e.Voice;
        EngProgressive.IsChecked = e.Progressive;
        EngRallyCalls.IsChecked = e.RallyCalls;
        EngVolume.Value = e.Volume;
        EngDelay.Value = e.AudioDelayMs;
        EngRate.Value = e.VoiceRate;
        EngPitch.Value = e.VoicePitch;
        _loading = false;
        UpdateEngineerVolumeLabel();
        _app.Engineer.Changed += () => Dispatcher.BeginInvoke(RefreshEngineer);
        _voiceApply.Tick += (_, _) =>
        {
            _voiceApply.Stop();
            _app.Engineer.ConfigureVoice();
        };
        RefreshEngineer();
    }

    private void OnEngineerOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _app.Settings.Engineer;
        s.Enabled = EngEnabled.IsChecked == true;
        s.Beeps = EngBeeps.IsChecked == true;
        s.Voice = EngVoice.IsChecked == true;
        s.Progressive = EngProgressive.IsChecked == true;
        s.RallyCalls = EngRallyCalls.IsChecked == true;
        SaveSoon();
    }

    private void OnEngineerVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _app == null) return;
        _app.Settings.Engineer.Volume = (int)EngVolume.Value;
        UpdateEngineerVolumeLabel();
        SaveSoon();
    }

    private void UpdateEngineerVolumeLabel()
    {
        EngVolumeLabel.Text = $"Volumen: {EngVolume.Value:0} %";
        EngDelayLabel.Text = $"Adelanto de los avisos: {EngDelay.Value:0} ms";
        EngRateLabel.Text = $"Velocidad de la voz: {EngRate.Value:0} %";
        bool modern = _app?.Engineer.CurrentVoice?.IsModern == true;
        EngPitchLabel.Text = modern ? $"Tono de la voz: {EngPitch.Value:0} %" : "Tono de la voz (sólo voces modernas)";
        EngPitch.IsEnabled = modern;
    }

    private string _shownVoices = "";

    /// <summary>Botones con las voces instaladas (la elegida marcada).</summary>
    private void RefreshVoiceChips()
    {
        var eng = _app.Engineer;
        string signature = string.Join("|", eng.Voices.Select(v => v.Id)) + "#" + eng.CurrentVoice?.Id;
        if (signature == _shownVoices) return;
        _shownVoices = signature;
        EngVoices.Children.Clear();
        foreach (var v in eng.Voices)
        {
            var chip = new RadioButton
            {
                Content = v.Spanish ? v.Label : v.Label + " (no habla español)",
                GroupName = "EngVoice",
                IsChecked = v.Id == eng.CurrentVoice?.Id,
                Style = (Style)FindResource(typeof(System.Windows.Controls.Primitives.ToggleButton)),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 4, 4),
                FontSize = 12,
            };
            string id = v.Id;
            chip.Checked += (_, _) =>
            {
                _app.Settings.Engineer.VoiceId = id;
                _app.Engineer.ConfigureVoice();
                SaveSoon();
            };
            EngVoices.Children.Add(chip);
        }
        UpdateEngineerVolumeLabel();
    }

    private void OnEngineerVoiceSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _app == null) return;
        _app.Settings.Engineer.VoiceRate = (int)EngRate.Value;
        _app.Settings.Engineer.VoicePitch = (int)EngPitch.Value;
        UpdateEngineerVolumeLabel();
        _voiceApply.Stop();
        _voiceApply.Start(); // aplicar al soltar el control (no en cada paso del deslizador)
        SaveSoon();
    }

    private readonly System.Windows.Threading.DispatcherTimer _voiceApply = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private void OnInstallVoices(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:speech") { UseShellExecute = true }); }
        catch (Exception) { MessageBox.Show(this, "Abre Configuración de Windows → Hora e idioma → Voz → Agregar voces.", "SimTelemetry"); }
    }

    private void OnRefreshVoices(object sender, RoutedEventArgs e) => _app.Engineer.ConfigureVoice();

    private void OnEngineerDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _app == null) return;
        _app.Settings.Engineer.AudioDelayMs = (int)EngDelay.Value;
        UpdateEngineerVolumeLabel();
        SaveSoon();
    }

    private void OnEngineerDelayPreset(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string ms && int.TryParse(ms, out int value)) EngDelay.Value = value;
    }

    private void OnEngineerTest(object sender, RoutedEventArgs e)
    {
        if (!_app.Settings.Engineer.Voice) EngVoice.IsChecked = true;
        _app.Engineer.Test();
        _app.Settings.Engineer.Tested = true;
        SaveSoon();
    }

    /// <summary>Plan, historial y voz (cuando el ingeniero avisa de un cambio).</summary>
    private void RefreshEngineer()
    {
        var eng = _app.Engineer;
        // La voz elegida ya se ve marcada en «Voz del ingeniero»; aquí sólo los problemas.
        EngVoiceInfo.Text = eng.VoiceName.Length == 0 ? "Windows no tiene ninguna voz instalada: sólo sonarán los pitidos."
            : !eng.Voices.Any(v => v.Spanish) ? "No hay voz en español: instálala en Configuración de Windows → Hora e idioma → Voz."
            : "";
        EngVoiceInfo.Visibility = EngVoiceInfo.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EngHistory.ItemsSource = eng.History.Take(12).ToList();
        RefreshVoiceChips();

        UpdatePractice();
        var plan = eng.Plan;
        if (plan == _shownPlan && _shownPractice == eng.PracticeCorner) return;
        _shownPlan = plan;
        _shownPractice = eng.PracticeCorner;
        if (plan == null)
        {
            EngPlanTitle.Text = "Sin referencia: conduce y el ingeniero usará tu mejor vuelta.";
            EngCues.ItemsSource = null;
            return;
        }
        var r = plan.Reference;
        EngPlanTitle.Text = $"{r.Game} · {r.Track}{(r.Car.Length > 0 ? " · " + r.Car : "")}\n" +
                            $"Referencia: vuelta {r.Number} ({r.TimeText}) del {r.Date:dd/MM HH:mm}" +
                            (plan.SameCar ? "" : " — con otro coche: los puntos pueden variar");
        EngCues.ItemsSource = plan.Cues.Select(c => new CueItem(c.Corner,
            $"Curva {c.Corner}: frena a los {c.BrakeAt:0} m · {c.Intensity} ({c.Peak:P0})" + (c.Shift > 0 ? $" · objetivo +{c.Shift:0} m" : ""),
            $"{c.EntrySpeed:0} → {c.MinSpeed:0} km/h{(c.Gear > 0 ? $" · {c.Gear}ª" : "")}" + (c.Potential >= 5 ? $" · margen ~{c.Potential:0} m" : ""),
            c.Corner == eng.PracticeCorner)).ToList();
    }

    private int _shownPractice;

    /// <summary>Modo práctica (como el de Trophi): sólo se avisa una curva y al salir de ella te dice cuánto ganaste o perdiste.</summary>
    private void OnPracticeCorner(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not int corner) return;
        _app.Engineer.PracticeCorner = _app.Engineer.PracticeCorner == corner ? 0 : corner;
        RefreshEngineer();
    }

    private void OnPracticeStop(object sender, RoutedEventArgs e)
    {
        _app.Engineer.PracticeCorner = 0;
        RefreshEngineer();
    }

    private void UpdatePractice()
    {
        var eng = _app.Engineer;
        EngPracticeBox.Visibility = eng.PracticeCorner > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (eng.PracticeCorner == 0) return;
        EngPracticeText.Text = $"Modo práctica: curva {eng.PracticeCorner}. Sólo te aviso de esa curva y al salir te digo cuánto ganas o pierdes frente a tu referencia." +
            (eng.PracticeAttempts > 0 ? $"\nIntentos: {eng.PracticeAttempts} · mejor: {eng.PracticeBest:+0.00;-0.00} s" : "");
    }

    /// <summary>Próxima frenada y presiones en directo (desde el temporizador de la ventana).</summary>
    private void UpdateEngineerLive()
    {
        var eng = _app.Engineer;
        var next = eng.NextCue;
        EngNext.Text = next == null ? "" : $"Próxima: curva {next.Corner}, {next.Intensity}, a los {next.BrakeAt:0} m";

        var p = eng.Pressures;
        bool acc = eng.Game == "Assetto Corsa Competizione" && !eng.WetTyres;
        var cells = new[] { TyreFL, TyreFR, TyreRL, TyreRR };
        for (int i = 0; i < 4; i++)
        {
            cells[i].Text = p[i] > 5 ? p[i].ToString("0.0") : "--";
            cells[i].Foreground = !acc || p[i] <= 5 ? (Brush)FindResource("Text")
                : p[i] > TyreTargets.AccDryMax ? BadBrush
                : p[i] < TyreTargets.AccDryMin ? WaitBrush
                : GoodBrush;
        }
    }
}
