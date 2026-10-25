using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SimTelemetry.Recording;
using SimTelemetry.Telemetry;

namespace SimTelemetry;

/// <summary>
/// Perfil de juego en la cabecera: con un perfil elegido (o detectado al abrir el juego), Análisis, Progreso y
/// Circuitos sólo muestran ese juego.
/// </summary>
public partial class MainWindow
{
    private HashSet<string> _profilesWithData = new();

    private void InitProfiles()
    {
        _loading = true;
        AutoProfileCheck.IsChecked = _app.Settings.AutoProfile;
        _loading = false;
        _profilesWithData = LapRecorder.ListLaps(_app.DataFolder).Select(l => GameProfiles.For(l.Game)?.Key).OfType<string>().ToHashSet();
        _app.ProfileChanged += () => Dispatcher.BeginInvoke(OnProfileChanged);
        RefreshProfileChips();
    }

    /// <summary>¿Se muestra esta vuelta con el perfil actual?</summary>
    private bool InProfile(LapInfo l) => _app.Profile?.Matches(l.Game) ?? true;

    private void RefreshProfileChips()
    {
        var current = _app.Profile;
        var shown = GameProfiles.All.Where(p => _profilesWithData.Contains(p.Key) || p == current).ToList();
        ProfileChips.Children.Clear();
        foreach (var p in shown.Prepend(null))
        {
            var chip = new RadioButton
            {
                Content = p?.Name ?? "Todos",
                GroupName = "Profile",
                IsChecked = p == current,
                Style = (Style)FindResource(typeof(System.Windows.Controls.Primitives.ToggleButton)),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 4, 0),
                FontSize = 12,
                ToolTip = p == null ? "Todos los juegos" : p.Rally ? $"{p.Name}: sólo sus tramos y sesiones" : $"{p.Name}: sólo sus circuitos y sesiones",
            };
            string key = p?.Key ?? "";
            chip.Checked += (_, _) => { if (!_loading) _app.SetProfile(key); };
            ProfileChips.Children.Add(chip);
        }
        ProfileNote.Text = current != null && _app.ProfileAutoSelected ? "detectado" : "";
    }

    private void OnProfileChanged()
    {
        _loading = true;
        RefreshProfileChips();
        _loading = false;
        _lapsDirty = _progressDirty = _tracksDirty = _carsDirty = true;
        if (Tabs.SelectedItem == TabAnalysis) RefreshLaps(null);
        else if (Tabs.SelectedItem == TabProgress) RefreshProgress();
        else if (Tabs.SelectedItem == TabTracks) RefreshTracks();
        else if (Tabs.SelectedItem == TabCars) RefreshCars();
    }

    private void OnAutoProfileChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.AutoProfile = AutoProfileCheck.IsChecked == true;
        SaveSoon();
    }

    /// <summary>Una vuelta nueva de otro juego hace que aparezca su perfil en la cabecera.</summary>
    private void NoteLapForProfiles(LapInfo info)
    {
        if (GameProfiles.For(info.Game)?.Key is string key && _profilesWithData.Add(key)) RefreshProfileChips();
    }
}
