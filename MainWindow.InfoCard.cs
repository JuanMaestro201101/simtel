using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SimTelemetry.Recording;

namespace SimTelemetry;

/// <summary>Tarjeta "Sesión actual" de la pestaña En vivo y ajustes de la tarjeta de información sobre el juego.</summary>
public partial class MainWindow
{
    private void InitInfoCard()
    {
        _loading = true;
        InfoCardEnabled.IsChecked = _app.Settings.InfoCard.Enabled;
        _loading = false;
        Options(InfoCornerChips, new[] { ("TopLeft", "↖ Arriba izq."), ("TopRight", "↗ Arriba der."), ("BottomLeft", "↙ Abajo izq."), ("BottomRight", "↘ Abajo der.") },
            _app.Settings.InfoCard.Corner, v => _app.Settings.InfoCard.Corner = v);
        Options(InfoKeyChips, InfoCardSettings.Keys.Select(k => (k.Name, k.Name)).ToArray(),
            _app.Settings.InfoCard.Key, v => _app.Settings.InfoCard.Key = v);
        _app.AdviceChanged += RefreshSessionCard;
        RefreshSessionCard();
    }

    /// <summary>Fila de opciones como botones (uno marcado).</summary>
    private void Options(WrapPanel panel, (string Value, string Label)[] options, string selected, Action<string> select)
    {
        panel.Children.Clear();
        foreach (var (value, label) in options)
        {
            var chip = new RadioButton
            {
                Content = label,
                GroupName = panel.Name,
                IsChecked = value == selected,
                Style = (Style)FindResource(typeof(System.Windows.Controls.Primitives.ToggleButton)),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 4, 4),
                FontSize = 12,
            };
            chip.Checked += (_, _) =>
            {
                select(value);
                SaveSoon();
            };
            panel.Children.Add(chip);
        }
    }

    private void OnInfoCardChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.InfoCard.Enabled = InfoCardEnabled.IsChecked == true;
        SaveSoon();
    }

    private void OnInfoCardPreview(object sender, RoutedEventArgs e) => _app.PreviewInfoCard();

    private void RefreshSessionCard()
    {
        var a = _app.Advice;
        SessionCard.Visibility = a == null ? Visibility.Collapsed : Visibility.Visible;
        if (a == null) return;
        SessionTitle.Text = $"{a.Game} · {a.Title}";
        SessionHeadline.Text = a.Headline;
        SessionRanking.Text = a.Ranking.Count > 1
            ? $"Tus {(a.Category.Length > 0 ? a.Category : "coches")} aquí: " +
              string.Join(" · ", a.Ranking.Take(4).Select((r, i) => $"{i + 1}. {Tracks.CarCatalog.DisplayName(r.Car)} {LapInfo.FormatLapTime(r.Best)}"))
            : a.Info != null ? $"Qué coche conviene: {a.Info.CarChoice}" : "";
    }

    private void OnOpenTrackGuide(object sender, RoutedEventArgs e)
    {
        var a = _app.Advice;
        Tabs.SelectedItem = TabTracks;
        if (_tracksDirty) RefreshTracks();
        if (a != null && TrackList.ItemsSource is List<TrackItem> items)
            TrackList.SelectedItem = items.FirstOrDefault(i => i.Game == a.Game && i.Title == a.Track) ?? TrackList.SelectedItem;
    }
}
