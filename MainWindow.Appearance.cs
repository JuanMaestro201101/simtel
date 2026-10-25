using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using SimTelemetry.UI;

namespace SimTelemetry;

/// <summary>Apariencia: tema oscuro o claro y el color de la ventana (los colores están en UI/Palette.cs).</summary>
public partial class MainWindow
{
    private void InitAppearance()
    {
        _loading = true;
        (_app.Settings.Theme == "light" ? ThemeLight : ThemeDark).IsChecked = true;
        _loading = false;
        RefreshColorChips();
        SourceInitialized += (_, _) => ApplyTitleBar();
    }

    private void RefreshColorChips()
    {
        ThemeColors.Children.Clear();
        foreach (var (name, hex) in Palette.Choices)
        {
            var swatch = new Ellipse
            {
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(hex.Length > 0 ? (Color)ColorConverter.ConvertFromString(hex) : Palette.OverlayAccent),
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(swatch);
            content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var chip = new RadioButton
            {
                Content = content,
                GroupName = "ThemeColor",
                IsChecked = hex == _app.Settings.ThemeColor,
                Style = (Style)FindResource(typeof(System.Windows.Controls.Primitives.ToggleButton)),
            };
            chip.Checked += (_, _) =>
            {
                _app.Settings.ThemeColor = hex;
                ApplyTheme();
            };
            ThemeColors.Children.Add(chip);
        }
    }

    private void OnThemeModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _app == null) return;
        _app.Settings.Theme = ThemeLight.IsChecked == true ? "light" : "dark";
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        Palette.Apply(_app.Settings.Theme, _app.Settings.ThemeColor);
        SaveSoon();
        ApplyTitleBar();
        // Lo que se construye en código con los colores del momento se rehace: gráficas, tablas, mapa y guía.
        LearnHost.Content = GuideContent.Build();
        _lapsDirty = _progressDirty = _tracksDirty = _carsDirty = true;
        foreach (var c in new UIElement[] { Chart, ProgressChart, TrackMap, Trainer, AxisMonitor }) c.InvalidateVisual();
    }

    private void ApplyTitleBar() =>
        Native.SetTitleBar(new WindowInteropHelper(this).Handle, !Palette.Light, Palette.Panel, Palette.Text);
}
