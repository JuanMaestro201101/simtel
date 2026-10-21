using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using SimTelemetry.Analysis;
using SimTelemetry.Recording;

namespace SimTelemetry.UI;

/// <summary>
/// Tarjeta pequeña sobre el juego (como la de Steam) con el circuito, la categoría y la recomendación.
/// Con la tecla de detalles se agranda con la información del circuito. No se puede tocar (los clics pasan al juego).
/// </summary>
public sealed class InfoOverlayWindow : Window
{
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _headline = new() { FontSize = 13, Foreground = new SolidColorBrush(Palette.OverlayText), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _details = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _hint = new() { FontSize = 11, Foreground = new SolidColorBrush(Palette.OverlayAccent), Margin = new Thickness(0, 8, 0, 0) };
    private static readonly Brush Dim = new SolidColorBrush(Palette.InfoDim);

    public InfoOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "SimTelemetry Info";
        var panel = new StackPanel();
        panel.Children.Add(_title);
        panel.Children.Add(_headline);
        panel.Children.Add(_details);
        panel.Children.Add(_hint);
        Content = new Border
        {
            Background = new SolidColorBrush(Palette.InfoBg),
            BorderBrush = new SolidColorBrush(Palette.InfoBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Child = panel,
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // Igual que el overlay de pedales: transparente a los clics y dibujado con la CPU (no compite con el juego).
            Native.SetExStyle(hwnd, Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE, 0);
            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.RenderMode = RenderMode.SoftwareOnly;
        };
    }

    public bool Expanded { get; private set; }

    /// <summary>Muestra la tarjeta (pequeña o ampliada) en una esquina de <paramref name="area"/>.</summary>
    public void Present(SessionAdvice advice, bool expanded, string keyName, string engineerLine, Rect area, string corner)
    {
        Expanded = expanded;
        _title.Text = advice.Title.Length > 0 ? advice.Title : advice.Game;
        _headline.Text = advice.Headline;
        _hint.Text = expanded ? $"{keyName}: ocultar" : $"{keyName}: ver más detalles";
        _details.Children.Clear();
        _details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        double width = expanded ? Math.Min(520, area.Width - 48) : 380;
        _title.MaxWidth = _headline.MaxWidth = width;
        if (expanded) FillDetails(advice, engineerLine, width);

        if (!IsVisible) Show();
        UpdateLayout();
        const double margin = 24;
        Left = corner.EndsWith("Left") ? area.Left + margin : area.Right - ActualWidth - margin;
        Top = corner.StartsWith("Top") ? area.Top + margin : area.Bottom - ActualHeight - margin;
    }

    private void FillDetails(SessionAdvice a, string engineerLine, double width)
    {
        void Section(string title, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _details.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 1) });
            _details.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, MaxWidth = width });
        }

        if (a.Ranking.Count > 1)
            Section($"Tus {(a.Category.Length > 0 ? a.Category : "coches")} aquí",
                string.Join("\n", a.Ranking.Take(4).Select((r, i) => $"{i + 1}. {Tracks.CarCatalog.DisplayName(r.Car)} — {LapInfo.FormatLapTime(r.Best)}")));
        Section("Ingeniero", engineerLine);
        if (a.IsRally)
        {
            if (a.Rally is { } r) Section($"Superficie: {r.Surface}", r.Tip);
            Section("En rally", "Las notas del copiloto mandan: los avisos del ingeniero sólo te recuerdan dónde frenaste la otra vez.");
            return;
        }
        var info = a.Info;
        if (info == null)
        {
            Section("Circuito", "No está en la biblioteca. En la pestaña Circuitos de la app puedes asociarlo a uno conocido.");
            return;
        }
        Section("Cómo es", info.Character);
        Section("Curvas clave", string.Join("\n", info.Corners.Take(5).Select(c => $"• {c.Name}: {c.Tip}")));
        Section("Qué coche conviene", info.CarChoice);
        Section("Reglaje", info.Setup);
        Section("Dónde adelantar", info.Overtaking);
        if (info.AccGt3Pace != null && a.Game.Contains("Competizione"))
            Section("Ritmo rápido (GT3, seco)", info.AccGt3Pace);
    }
}
