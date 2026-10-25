#if DEBUG
using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SimTelemetry.UI;

/// <summary>Sólo en Debug: guarda capturas PNG de cada pestaña y del overlay (para pruebas sin juego).</summary>
internal static class DebugSnapshot
{
    public static void Schedule(App app, string folder, double delaySeconds)
    {
        Directory.CreateDirectory(folder);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delaySeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Capture(app, folder);
            app.ExitApp();
        };
        timer.Start();
    }

    private static void Capture(App app, string folder)
    {
        var main = app.MainView;
        var names = new System.Collections.Generic.List<string>();
        foreach (System.Windows.Controls.TabItem tab in main.Tabs.Items)
            names.Add($"{names.Count + 1}-{tab.Name.Replace("Tab", "").ToLowerInvariant()}");
        for (int i = 0; i < names.Count; i++)
        {
            main.Tabs.SelectedIndex = i;
            if (main.Tabs.SelectedItem == main.TabGames) Wait(2.5); // las comprobaciones tardan un poco
            if (main.Tabs.SelectedItem == main.TabAnalysis)
            {
                // Elige la sesión con más vueltas y su segunda vuelta, para que se vea una comparación.
                int bestSession = 0;
                for (int s = 0; s < main.SessionList.Items.Count; s++)
                {
                    main.SessionList.SelectedIndex = s;
                    Flush();
                    if (main.LapList.Items.Count > 1) { bestSession = s; break; }
                }
                main.SessionList.SelectedIndex = bestSession;
                Flush();
                main.SelectComparableLap();
                Flush();
                main.SelectFirstSegmentTip();
            }
            if (main.Tabs.SelectedItem == main.TabTracks) main.SelectBusiestTrack();
            if (main.Tabs.SelectedItem == main.TabProgress || main.Tabs.SelectedItem == main.TabTracks || main.Tabs.SelectedItem == main.TabCars) Wait(2.5); // se calculan en segundo plano
            Flush();
            Save((FrameworkElement)main.Content, Path.Combine(folder, names[i] + ".png"));

            if (main.Tabs.SelectedItem == main.TabAnalysis)
            {
                // Segunda captura con la lista de frenadas.
                main.AnalysisSideTabs.SelectedIndex = 1;
                Flush();
                Save((FrameworkElement)main.Content, Path.Combine(folder, names[i] + "-frenadas.png"));
                main.AnalysisSideTabs.SelectedIndex = 0;
            }
        }

        // Cambio de tema con la app abierta (SIMTELEMETRY_SNAPSHOT_THEME="light|#4DA3FF"), por los mismos botones de Apariencia.
        if (Environment.GetEnvironmentVariable("SIMTELEMETRY_SNAPSHOT_THEME") is { } theme)
        {
            var parts = theme.Split('|');
            (parts[0] == "light" ? main.ThemeLight : main.ThemeDark).IsChecked = true;
            int color = Array.FindIndex(Palette.Choices, c => c.Hex == (parts.Length > 1 ? parts[1] : ""));
            if (color >= 0) ((System.Windows.Controls.RadioButton)main.ThemeColors.Children[color]).IsChecked = true;
            foreach (var tab in new[] { main.TabAnalysis, main.TabProgress, main.TabTrainer, main.TabLive })
            {
                main.Tabs.SelectedItem = tab;
                if (tab == main.TabProgress) Wait(1.5);
                Flush();
                Save((FrameworkElement)main.Content, Path.Combine(folder, $"tema-{tab.Name.Replace("Tab", "").ToLowerInvariant()}.png"));
            }
        }

        app.Overlay.EditMode = false;
        app.Overlay.Show();
        Flush();
        Save((FrameworkElement)app.Overlay.Content, Path.Combine(folder, "overlay-juego.png"), opaqueBackground: true);
        app.PreviewInfoCard();
        Flush();
        if (app.InfoCard.IsVisible) Save((FrameworkElement)app.InfoCard.Content, Path.Combine(folder, "tarjeta-info.png"), opaqueBackground: true);
        if (app.Hidden) return; // el modo edición activa la ventana: le quitaría el foco al juego del usuario
        app.Overlay.EditMode = true;
        Flush();
        Save((FrameworkElement)app.Overlay.Content, Path.Combine(folder, "overlay-edicion.png"), opaqueBackground: true);
    }

    private static void Wait(double seconds)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Flush() =>
        Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Save(FrameworkElement element, string path, bool opaqueBackground = false)
    {
        int w = (int)Math.Ceiling(element.ActualWidth), h = (int)Math.Ceiling(element.ActualHeight);
        if (w <= 0 || h <= 0) return;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (opaqueBackground)
            {
                // Simula un fondo de juego para ver la transparencia.
                var bg = new LinearGradientBrush(Color.FromRgb(0x55, 0x6B, 0x7A), Color.FromRgb(0x8A, 0x7B, 0x5C), 0);
                dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
            }
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
#endif
