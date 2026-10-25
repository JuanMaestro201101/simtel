using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SimTelemetry.Analysis;
using SimTelemetry.Recording;
using SimTelemetry.Setup;
using SimTelemetry.Telemetry;
using SimTelemetry.UI;

namespace SimTelemetry;

public partial class MainWindow : Window
{
    // Colores de estado del tema actual (se leen cada vez: cambian al elegir otro tema). "Wait" es ámbar, no el color elegido.
    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    private static Brush GoodBrush => Res("Good");
    private static Brush WaitBrush => Res("Warn");
    private static Brush OffBrush => Res("Off");
    private static Brush BadBrush => Res("Bad");

    private readonly App _app;
    private readonly List<SourceRow> _sourceRows;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<string, LapData> _lapCache = new();
    private List<LapInfo> _allLaps = new();
    private LapInfo? _manualReference;
    private LapInfo? _currentLap;
    private bool _lapsDirty = true;
    private bool _progressDirty = true;
    private int _progressVersion;
    private List<SessionProgress> _progressSessions = new();
    private bool _loading;
    private DateTime _nextRunningCheck;
    private List<RunningGame> _runningGames = new();

    public MainWindow()
    {
        InitializeComponent();
        _app = App.Current;

        var logo = AppIcon.CreateImageSource();
        LogoImage.Source = logo;
        Icon = logo;

        LivePedals.Buffer = _app.Pedals;
        LivePedals.Settings = _app.Settings.Overlay;
        LivePedals.Delta = () => _app.Engineer.LiveDelta;
        LivePedals.Sector = () => _app.Engineer.LastSector;

        _sourceRows = _app.Hub.Sources.Select(s => new SourceRow(s)).ToList();
        SourceList.ItemsSource = _sourceRows;
        DiagnosticsList.ItemsSource = _sourceRows;
        LearnHost.Content = GuideContent.Build();
        Chart.CursorChanged += d => TrackMap.SetCursor(d);
        BuildMonitorButtons();
        InitTrainer();
        InitEngineer();
        InitInfoCard();
        InitPhone();
        InitProfiles();
        InitAppearance();
        _loading = true;
        (_app.Settings.TipsDetailed ? TipsDetailed : TipsSimple).IsChecked = true;
        _loading = false;

        _loading = true;
        RecordCheck.IsChecked = _app.Settings.RecordingEnabled;
        PortDirt.Text = _app.Settings.CodemastersPort.ToString();
        PortWrc.Text = _app.Settings.WrcPort.ToString();
        PortForza.Text = _app.Settings.ForzaPort.ToString();
        PortOutGauge.Text = _app.Settings.OutGaugePort.ToString();
        _loading = false;
        LoadOverlayControls();

        _app.Recorder.LapSaved += info => Dispatcher.BeginInvoke(() => OnLapSaved(info));
        _app.WheelRecorder.Saved += info => Dispatcher.BeginInvoke(() => OnLapSaved(info));

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => UpdateLive();
        _uiTimer.Start();

        ProgressChart.XPad = 0.5;
        ProgressChart.XLabel = v =>
        {
            int i = (int)Math.Round(v) - 1;
            return Math.Abs(v - Math.Round(v)) < 0.01 && i >= 0 && i < _progressSessions.Count
                ? $"#{i + 1} · {_progressSessions[i].Date:dd'/'MM}" : "";
        };

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _app.Settings.Save();
        };

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) EditToggle.IsChecked = false;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) EditToggle.IsChecked = false;
        };
    }

    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            if (_app.Settings.CloseToTray == null)
            {
                var answer = MessageBox.Show(this,
                    "¿Quieres que APEX siga funcionando en segundo plano al cerrar la ventana?\n\n" +
                    "• Sí: sigue grabando vueltas y mostrando el overlay mientras juegas (icono junto al reloj).\n" +
                    "• No: se cierra del todo, overlay incluido.\n\n" +
                    "Puedes cambiarlo más tarde en la pestaña Overlay.",
                    "Cerrar APEX", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                _app.Settings.CloseToTray = answer == MessageBoxResult.Yes;
                _app.Settings.Save();
            }
            if (_app.Settings.CloseToTray == false)
            {
                e.Cancel = true;
                Dispatcher.BeginInvoke(() => _app.ExitApp());
                return;
            }
            // Cerrar la ventana sólo la oculta: la telemetría y el overlay siguen funcionando.
            e.Cancel = true;
            EditToggle.IsChecked = false;
            StopTrainer(showResult: false);
            Hide();
            _app.ShowTrayHint();
            return;
        }
        _uiTimer.Stop();
        base.OnClosing(e);
    }

    private void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ======================= EN VIVO =======================

    private void UpdateLive()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        foreach (var row in _sourceRows) row.Refresh();
        if (Tabs.SelectedItem == TabEngineer) UpdateEngineerLive();
        if (Tabs.SelectedItem == TabLive) UpdateOnboarding();

        var hub = _app.Hub;
        var src = hub.ActiveSource;
        bool live = hub.HasLiveData && src != null;

        string? wheelName = _app.Wheel.MainDeviceName;
        StatusDot.Fill = live ? GoodBrush : wheelName != null ? WaitBrush : OffBrush;
        StatusText.Text = live ? $"{src!.GameName} · recibiendo telemetría"
            : wheelName != null ? $"🎮 {ShortName(wheelName)} conectado · esperando un juego" : "Esperando un juego…";
        LivePedalSource.Text = _app.PedalSource switch
        {
            App.PedalSourceKind.Wheel => $"Pedales: {ShortName(wheelName ?? "volante")} (sin juego)",
            App.PedalSourceKind.Game => "Pedales: telemetría del juego",
            _ => wheelName != null ? "Detecta tus pedales en la pestaña Entrenador" : "",
        };

        if (live)
        {
            var s = hub.Latest;
            LiveGame.Text = src!.GameName;
            var details = new[] { src.Track, src.Car }.Where(x => !string.IsNullOrEmpty(x)).ToArray();
            LiveTrack.Text = details.Length > 0 ? string.Join(" · ", details) : src.Status;
            LiveSpeed.Text = $"{s.SpeedKmh:0}";
            LiveGear.Text = s.Gear < 0 ? "R" : s.Gear == 0 ? "N" : s.Gear.ToString();
            LiveRpm.Text = $"{s.Rpm:0}";
            LiveLapTime.Text = s.LapTime >= 0 ? LapInfo.FormatLapTime(s.LapTime) : "--";
            LiveDistance.Text = s.LapDistance >= 0 ? $"{s.LapDistance:0} m" : "--";
        }
        else
        {
            // ¿Hay un juego compatible abierto que no envía datos? (mirar los procesos cuesta algo: cada 5 s)
            if (DateTime.UtcNow >= _nextRunningCheck)
            {
                _nextRunningCheck = DateTime.UtcNow.AddSeconds(5);
                _runningGames = GameProcesses.Running();
            }
            var game = _runningGames.FirstOrDefault();
            var gameSource = game == null ? null : hub.Sources.FirstOrDefault(x => x.Id == game.SourceId);
            LiveGame.Text = game != null ? $"{game.Name} está abierto, pero no llega telemetría" : "Ningún juego conectado";
            LiveTrack.Text = game == null
                ? "Abre un juego compatible y sal a pista. Algunos juegos necesitan activar la telemetría: mira la pestaña \"Juegos\"."
                : gameSource != null && gameSource.PacketCount > 0
                    ? $"Llegan datos del juego, pero no se pueden usar: {gameSource.Status}"
                    : game.Hint;
            LiveSpeed.Text = LiveGear.Text = LiveRpm.Text = LiveLapTime.Text = LiveDistance.Text = "--";
        }

        var rec = _app.Recorder;
        RecordStatus.Text = !rec.Enabled ? "Grabación desactivada."
            : rec.CurrentLapPoints > 0 ? $"Grabando la vuelta actual ({rec.CurrentLapPoints} muestras)…"
            : "Se grabará automáticamente cuando conduzcas.";
    }

    /// <summary>"Logitech G HUB G29 Driving Force Racing Wheel USB" → "Logitech G29 Driving Force Racing Wheel".</summary>
    private static string ShortName(string name) =>
        name.Replace("G HUB ", "").Replace(" USB", "").Trim();

    private void OnLapSaved(LapInfo info)
    {
        NoteLapForOnboarding(info);
        NoteLapForProfiles(info);
        LastLapText.Text = $"Última guardada: {info.Game} · vuelta {info.Number} · {info.TimeText}" +
                           (info.Completed ? "" : "  (incompleta)");
        _lapsDirty = true;
        _progressDirty = true;
        _tracksDirty = true;
        // Si estabas viendo esta sesión (o nada), Análisis salta a la vuelta nueva; la referencia sigue siendo la más rápida.
        if (info.Completed && (_currentLap == null || _currentLap.SessionFolder == info.SessionFolder)) _currentLap = info;
        if (Tabs.SelectedItem == TabAnalysis) RefreshLaps(_currentLap?.FilePath);
        else if (Tabs.SelectedItem == TabProgress) RefreshProgress();
    }

    private void OnRecordChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = RecordCheck.IsChecked == true;
        _app.Settings.RecordingEnabled = on;
        _app.Recorder.Enabled = on;
        if (!on) _app.Recorder.Flush();
        SaveSoon();
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        string folder = _currentLap?.SessionFolder is { Length: > 0 } f && Tabs.SelectedItem == TabAnalysis ? f : _app.DataFolder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void OnGoToAnalysis(object sender, RoutedEventArgs e) => Tabs.SelectedItem = TabAnalysis;

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        if (Tabs.SelectedItem == TabAnalysis && _lapsDirty) RefreshLaps(_currentLap?.FilePath);
        else if (Tabs.SelectedItem == TabProgress && _progressDirty) RefreshProgress();
        else if (Tabs.SelectedItem == TabGames) CheckGames();
        else if (Tabs.SelectedItem == TabTracks && _tracksDirty) RefreshTracks();
        if (Tabs.SelectedItem != TabTrainer) StopTrainer(showResult: false);
        _trainerTimer.IsEnabled = Tabs.SelectedItem == TabTrainer;
        if (Tabs.SelectedItem == TabTrainer) RefreshFeelCombo();
    }

    // ======================= ANÁLISIS =======================

    private void OnRefreshLaps(object sender, RoutedEventArgs e) => RefreshLaps(_currentLap?.FilePath);

    private void RefreshLaps(string? keepSelected)
    {
        _lapsDirty = false;
        _allLaps = LapRecorder.ListLaps(_app.DataFolder);
        BuildSessionFilters();

        var sessions = _allLaps
            .Where(MatchesFilter)
            .GroupBy(l => l.SessionFolder)
            .Select(g => new SessionItem(g.Key, g.OrderBy(l => l.Number).ToList()))
            .OrderByDescending(s => s.Date)
            .ToList();

        string? folder = keepSelected != null ? Path.GetDirectoryName(keepSelected) : (SessionList.SelectedItem as SessionItem)?.Folder;
        SessionList.ItemsSource = sessions;
        SessionList.SelectedItem = sessions.FirstOrDefault(s => s.Folder == folder) ?? sessions.FirstOrDefault();

        if (keepSelected != null && LapList.ItemsSource is IEnumerable<LapItem> laps)
            LapList.SelectedItem = laps.FirstOrDefault(l => l.Info.FilePath == keepSelected);

        if (sessions.Count == 0)
        {
            AnalysisTitle.Text = "TODAVÍA NO HAY VUELTAS GRABADAS";
            LapBigTime.Text = LapBigDelta.Text = RefLine.Text = "";
            RefChip.Visibility = Visibility.Collapsed;
            SectorRow.Children.Clear();
            AnalysisSubtitle.Text = "Conduce con la app abierta: cada vuelta (o tramo de rally) se guarda sola.";
        }
    }

    // ---- Filtros de sesiones: juego → circuito / tramo → categoría del coche ----
    private string _filterGame = "", _filterTrack = "", _filterCategory = "";

    private static string TrackLabel(LapInfo l) => Tracks.TrackDatabase.Find(l.Track)?.Name ?? (l.Track.Length > 0 ? l.Track : "Sin nombre");

    private bool MatchesFilter(LapInfo l) =>
        InProfile(l)
        && (_filterGame.Length == 0 || l.Game == _filterGame)
        && (_filterTrack.Length == 0 || TrackLabel(l) == _filterTrack)
        && (_filterCategory.Length == 0 || Tracks.CarCatalog.Category(l.Game, l.Car) == _filterCategory);

    private void BuildSessionFilters()
    {
        var inProfile = _allLaps.Where(InProfile).ToList();
        var games = _app.Profile != null ? new List<string>()
            : inProfile.GroupBy(l => l.Game).OrderByDescending(g => g.Max(l => l.Date)).Select(g => g.Key).ToList();
        if (!games.Contains(_filterGame)) _filterGame = "";
        var inGame = inProfile.Where(l => _filterGame.Length == 0 || l.Game == _filterGame).ToList();
        var tracks = _filterGame.Length == 0 && _app.Profile == null ? new List<string>()
            : inGame.GroupBy(TrackLabel).OrderByDescending(g => g.Max(l => l.Date)).Select(g => g.Key).ToList();
        if (!tracks.Contains(_filterTrack)) _filterTrack = "";
        var categories = inGame.Where(l => _filterTrack.Length == 0 || TrackLabel(l) == _filterTrack)
            .Select(l => Tracks.CarCatalog.Category(l.Game, l.Car)).Where(c => c.Length > 0).Distinct().OrderBy(c => c).ToList();
        if (!categories.Contains(_filterCategory)) _filterCategory = "";

        FillChips(GameChips, "Todos los juegos", games, _filterGame, v => _filterGame = v);
        FillChips(TrackChips, "Todos los circuitos", tracks, _filterTrack, v => _filterTrack = v);
        FillChips(CategoryChips, "Todas las categorías", categories.Count > 1 ? categories : new List<string>(), _filterCategory, v => _filterCategory = v);
    }

    /// <summary>Una fila de botones de filtro; vacía (oculta) si no hay nada que elegir.</summary>
    private void FillChips(WrapPanel panel, string allLabel, List<string> values, string selected, Action<string> select)
    {
        panel.Children.Clear();
        if (values.Count == 0) return;
        foreach (var v in values.Prepend(""))
        {
            var chip = new RadioButton
            {
                Content = v.Length == 0 ? allLabel : ShortGame(v),
                ToolTip = v.Length == 0 ? null : v,
                GroupName = panel.Name,
                IsChecked = v == selected,
                Style = (Style)FindResource(typeof(System.Windows.Controls.Primitives.ToggleButton)),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 4, 4),
                FontSize = 12,
            };
            chip.Checked += (_, _) =>
            {
                select(v);
                RefreshLaps(null);
            };
            panel.Children.Add(chip);
        }
    }

    /// <summary>Nombres de juego cortos para los botones ("DiRT Rally / DiRT Rally 2.0" → "DiRT Rally").</summary>
    private static string ShortGame(string s) => s switch
    {
        "Assetto Corsa Competizione" => "ACC",
        "DiRT Rally / DiRT Rally 2.0" => "DiRT Rally",
        _ => s.Length > 26 ? s[..24] + "…" : s,
    };

    private void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionItem session)
        {
            LapList.ItemsSource = null;
            return;
        }
        FillLapList(session, _currentLap?.FilePath);
        if (LapList.SelectedItem == null)
        {
            // Por defecto, la vuelta completa más rápida de la sesión.
            var items = (List<LapItem>)LapList.ItemsSource;
            LapList.SelectedItem = items.Where(i => i.Info.Completed).OrderBy(i => i.Info.LapTime).FirstOrDefault() ?? items.LastOrDefault();
        }
    }

    private void FillLapList(SessionItem session, string? select)
    {
        var best = session.Laps.Where(l => l.Completed && l.Valid && !l.TimeBased).MinBy(l => l.LapTime);
        var items = session.Laps.Select(l => new LapItem(l, best, _manualReference?.FilePath == l.FilePath)).ToList();
        LapList.ItemsSource = items;
        LapList.SelectedItem = items.FirstOrDefault(i => i.Info.FilePath == select);
    }

    private void OnLapSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LapList.SelectedItem is LapItem item) ShowLap(item.Info);
    }

    private void OnSetReference(object sender, RoutedEventArgs e)
    {
        if (LapList.SelectedItem is not LapItem item) return;
        _manualReference = item.Info;
        if (SessionList.SelectedItem is SessionItem s) FillLapList(s, item.Info.FilePath);
        AnalysisSubtitle.Text = $"Referencia fijada: {Describe(item.Info)}. Ahora elige otra vuelta para compararla.";
    }

    private void OnClearReference(object sender, RoutedEventArgs e)
    {
        _manualReference = null;
        if (SessionList.SelectedItem is SessionItem s) FillLapList(s, _currentLap?.FilePath);
        if (_currentLap != null) ShowLap(_currentLap);
    }

    private LapData Load(LapInfo info)
    {
        if (!_lapCache.TryGetValue(info.FilePath, out var data))
        {
            data = LapFile.Read(info.FilePath);
            if (_lapCache.Count > 40) _lapCache.Clear();
            _lapCache[info.FilePath] = data;
        }
        return data;
    }

    private (LapInfo? info, bool manual) ResolveReference(LapInfo lap)
    {
        if (lap.TimeBased) return (null, false); // sin distancia no se pueden alinear dos sesiones
        if (_manualReference != null && _manualReference.FilePath != lap.FilePath && _manualReference.Game == lap.Game)
            return (_manualReference, true);

        var auto = _allLaps
            .Where(l => l.Completed && l.Valid && l.FilePath != lap.FilePath && l.Game == lap.Game)
            .Where(l => lap.Track.Length > 0 ? l.Track == lap.Track : l.SessionFolder == lap.SessionFolder)
            .Where(l => lap.Length <= 0 || Math.Abs(l.Length - lap.Length) < lap.Length * 0.05)
            .OrderBy(l => l.Car == lap.Car ? 0 : 1) // mejor con el mismo coche: las curvas coinciden con las del Ingeniero
            .ThenBy(l => l.LapTime)
            .FirstOrDefault();
        return (auto, false);
    }

    private static string Describe(LapInfo l) =>
        $"vuelta {l.Number} · {l.TimeText} · {l.Date:dd/MM HH:mm}";

    private void ShowLap(LapInfo info)
    {
        _currentLap = info;
        LapData lap;
        try { lap = Load(info); }
        catch (IOException ex)
        {
            AnalysisTitle.Text = "No se pudo leer la vuelta";
            AnalysisSubtitle.Text = ex.Message;
            return;
        }

        var (refInfo, manual) = ResolveReference(info);
        LapData? reference = null;
        if (refInfo != null)
        {
            try { reference = Load(refInfo); } catch (IOException) { refInfo = null; }
        }

        var stats = LapAnalyzer.Compute(lap);
        var refStats = reference != null ? LapAnalyzer.Compute(reference) : null;
        var cmp = reference != null ? LapAnalyzer.Compare(lap, reference) : null;
        var tips = LapAnalyzer.BuildTips(lap, stats, reference, refStats, cmp);

        string where = string.Join(" · ", new[] { ShortGame(info.Game), info.Track.Length > 0 ? TrackLabel(info) : "", Tracks.CarCatalog.DisplayName(info.Car) }.Where(x => !string.IsNullOrEmpty(x)));
        AnalysisTitle.Text = (info.TimeBased ? $"Sesión de pedales {info.Number} · {info.Game}"
            : $"Vuelta {info.Number}{(info.Completed ? "" : " (incompleta)")}{(info.Valid ? "" : " (inválida)")} · {where}").ToUpper();
        LapBigTime.Text = info.TimeText;
        // Diferencia de la vuelta entera si las dos están completas; si no, lo acumulado hasta donde llega la comparación.
        double? delta = refInfo != null && info.Completed && refInfo.Completed && !info.TimeBased ? info.LapTime - refInfo.LapTime
            : cmp != null && cmp.Delta.Length > 0 ? cmp.Delta[^1] : null;
        LapBigDelta.Text = delta is double dd ? dd.ToString("+0.000;-0.000", CultureInfo.InvariantCulture) : "";
        LapBigDelta.Foreground = delta < 0 ? GoodBrush : Res("Warn");
        RefChip.Visibility = refInfo != null ? Visibility.Visible : Visibility.Collapsed;
        RefChipText.Text = manual ? "REFERENCIA FIJADA POR TI" : "REFERENCIA AUTOMÁTICA · TU MEJOR VUELTA";
        RefLine.Text = refInfo == null ? "" : $"vs vuelta {refInfo.Number} · {refInfo.TimeText}";
        const string notReference = "no sirve de referencia: ";
        AnalysisSubtitle.Text = info.EndReason.Contains(notReference)
            ? "Esta vuelta no se usa como referencia: " + info.EndReason[(info.EndReason.IndexOf(notReference) + notReference.Length)..] + "."
            : refInfo != null
            ? (refInfo.SessionFolder != info.SessionFolder ? $"La referencia es de otra sesión: {Describe(refInfo)}." : "")
            : info.TimeBased
                ? "Sesión de pedales grabada con el volante (el juego no enviaba telemetría): sin vueltas ni velocidad, pero con toda tu técnica de pedales. Mira la pestaña «Frenadas»."
                : "Sin referencia: graba más vueltas completas en este circuito/tramo para comparar.";
        AnalysisSubtitle.Visibility = AnalysisSubtitle.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FillSectors(info, lap, reference);

        Chart.XUnit = info.TimeBased ? "s" : "m";
        Chart.XLabel = info.TimeBased ? v => TimeSpan.FromSeconds(Math.Max(0, v)).ToString(@"m\:ss") : null;
        Chart.SetPanes(BuildPanes(lap, reference, cmp));
        FillStats(stats, refStats, LapAnalyzer.NoSpeed(lap));
        _tips = tips;
        TipList.ItemsSource = tips.Select(t => new TipItem(t, _app.Settings.TipsDetailed)).ToList();
        TrackMap.SetData(lap, reference, cmp, stats, refStats);
        BrakeList.ItemsSource = stats.BrakeZones.Select((z, i) => new BrakeItem(z, i + 1, lap.Info.Abs, LapAnalyzer.NoSpeed(lap))).ToList();
    }

    /// <summary>Tres sectores de igual longitud y la velocidad máxima, con la diferencia frente a la referencia (verde ganas, amarillo pierdes).</summary>
    private void FillSectors(LapInfo info, LapData lap, LapData? reference)
    {
        SectorRow.Children.Clear();
        if (info.TimeBased || !info.Completed || lap.Count < 10) return;
        double d0 = lap.Distance[0], d3 = lap.Distance[^1];
        double T(LapData l, double d) => LapAnalyzer.Interpolate(l.Distance, l.Time, d);
        for (int k = 0; k < 3; k++)
        {
            double a = d0 + (d3 - d0) * k / 3, b = d0 + (d3 - d0) * (k + 1) / 3;
            double mine = T(lap, b) - T(lap, a);
            double? diff = reference != null ? mine - (T(reference, b) - T(reference, a)) : null;
            AddSector($"SECTOR {k + 1}", mine.ToString("0.000", CultureInfo.InvariantCulture), diff?.ToString("+0.000;-0.000", CultureInfo.InvariantCulture) ?? "", diff < 0 ? GoodBrush : diff > 0 ? Res("Warn") : null);
        }
        if (!LapAnalyzer.NoSpeed(lap))
            AddSector("VEL. MÁXIMA", $"{lap.Speed.Max():0}", reference != null ? $"km/h · ref. {reference.Speed.Max():0}" : "km/h", null);
    }

    private void AddSector(string label, string value, string detail, Brush? mark)
    {
        var cell = new StackPanel();
        cell.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("H2"), Margin = new Thickness(0) });
        cell.Children.Add(new TextBlock { Text = value, Style = (Style)FindResource("Figure"), FontSize = 24 });
        cell.Children.Add(new TextBlock { Text = detail, FontFamily = (FontFamily)FindResource("Mono"), Foreground = mark ?? Res("Dim") });
        SectorRow.Children.Add(new Border
        {
            Child = cell,
            Background = Res("Panel"),
            BorderBrush = mark ?? Res("Line"),
            BorderThickness = new Thickness(0, 3, 0, 0),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 0, SectorRow.Children.Count < 3 ? 10 : 0, 0),
        });
    }

    private static IEnumerable<ChartPane> BuildPanes(LapData lap, LapData? reference, Comparison? cmp)
    {
        Color mine = Palette.LapMine, refColor = Palette.Reference;
        Color thr = Palette.Throttle, brk = Palette.Brake;
        static double[] Pct(double[] v) => v.Select(x => x * 100).ToArray();

        bool noSpeed = LapAnalyzer.NoSpeed(lap);
        var speed = new ChartPane { Title = "Velocidad (km/h)", Weight = 2.2 };
        speed.Series.Add(new ChartSeries { Name = "Esta", X = lap.Distance, Y = lap.Speed, Color = mine, Thickness = 1.6 });
        if (reference != null) speed.Series.Add(new ChartSeries { Name = "Ref", X = reference.Distance, Y = reference.Speed, Color = refColor, Thickness = 1.2 });
        if (!noSpeed) yield return speed;

        if (cmp != null && cmp.Delta.Length > 1)
        {
            var delta = new ChartPane { Title = "Diferencia con la referencia (s)  · arriba = pierdes tiempo", Weight = 1.1, ZeroLine = true };
            delta.Series.Add(new ChartSeries { Name = "Δ", X = cmp.GridDistance, Y = cmp.Delta, Color = Palette.Warn, Format = "+0.000;-0.000" });
            yield return delta;
        }

        var pedals = new ChartPane { Title = "Acelerador / Freno (%)", Weight = 1.6, Min = -3, Max = 103 };
        if (reference != null)
        {
            pedals.Series.Add(new ChartSeries { Name = "Acel. ref", X = reference.Distance, Y = Pct(reference.Throttle), Color = Color.FromArgb(0xA0, thr.R, thr.G, thr.B), Thickness = 1, Dashed = true });
            pedals.Series.Add(new ChartSeries { Name = "Freno ref", X = reference.Distance, Y = Pct(reference.Brake), Color = Color.FromArgb(0xA0, brk.R, brk.G, brk.B), Thickness = 1, Dashed = true });
        }
        pedals.Series.Add(new ChartSeries { Name = "Acelerador", X = lap.Distance, Y = Pct(lap.Throttle), Color = thr, Thickness = 1.6 });
        pedals.Series.Add(new ChartSeries { Name = "Freno", X = lap.Distance, Y = Pct(lap.Brake), Color = brk, Thickness = 1.6 });
        yield return pedals;

        var steer = new ChartPane { Title = "Volante (%)  · arriba = derecha", Weight = 1, Min = -105, Max = 105, ZeroLine = true };
        if (reference != null) steer.Series.Add(new ChartSeries { Name = "Ref", X = reference.Distance, Y = Pct(reference.Steering), Color = refColor, Thickness = 1 });
        steer.Series.Add(new ChartSeries { Name = "Esta", X = lap.Distance, Y = Pct(lap.Steering), Color = Palette.Steering, Thickness = 1.4 });
        yield return steer;

        var gear = new ChartPane { Title = "Marcha", Weight = 0.8 };
        if (reference != null) gear.Series.Add(new ChartSeries { Name = "Ref", X = reference.Distance, Y = reference.Gear, Color = refColor, Thickness = 1 });
        gear.Series.Add(new ChartSeries { Name = "Esta", X = lap.Distance, Y = lap.Gear, Color = Palette.Gear, Thickness = 1.4 });
        if (!noSpeed) yield return gear;

        var rpm = new ChartPane { Title = "RPM", Weight = 0.8 };
        if (reference != null) rpm.Series.Add(new ChartSeries { Name = "Ref", X = reference.Distance, Y = reference.Rpm, Color = refColor, Thickness = 1 });
        rpm.Series.Add(new ChartSeries { Name = "Esta", X = lap.Distance, Y = lap.Rpm, Color = Palette.Info, Thickness = 1.2 });
        if (!noSpeed) yield return rpm;
    }

    private void FillStats(LapStats s, LapStats? r, bool noSpeed = false)
    {
        StatsGrid.Children.Clear();
        StatsGrid.RowDefinitions.Clear();
        StatsGrid.ColumnDefinitions.Clear();
        StatsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        StatsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        StatsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });

        void Row(string label, string mine, string? reference, bool header = false)
        {
            int row = StatsGrid.RowDefinitions.Count;
            StatsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var brush = header ? (Brush)FindResource("Dim") : (Brush)FindResource("Text");
            var weight = header ? FontWeights.SemiBold : FontWeights.Normal;
            Add(new TextBlock { Text = label, Foreground = header ? brush : (Brush)FindResource("Dim"), FontWeight = weight, Margin = new Thickness(0, 1, 0, 1) }, row, 0);
            Add(new TextBlock { Text = mine, Foreground = brush, FontWeight = header ? weight : FontWeights.SemiBold, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 1, 0, 1) }, row, 1);
            Add(new TextBlock { Text = reference ?? "", Foreground = header ? brush : Res("Reference"), FontWeight = weight, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 1, 0, 1) }, row, 2);
        }
        void Add(UIElement el, int row, int col)
        {
            Grid.SetRow(el, row);
            Grid.SetColumn(el, col);
            StatsGrid.Children.Add(el);
        }
        string P(double v) => $"{v:0.0} %";
        string? RP(Func<LapStats, double> f, Func<double, string> fmt) => r != null ? fmt(f(r)) : null;

        Row("Métrica", "Esta vuelta", r != null ? "Referencia" : "", header: true);
        Row(noSpeed ? "Duración" : "Tiempo", LapInfo.FormatLapTime(s.LapTime), RP(x => x.LapTime, LapInfo.FormatLapTime));
        if (!noSpeed) Row("Velocidad punta", $"{s.TopSpeed:0} km/h", RP(x => x.TopSpeed, v => $"{v:0} km/h"));
        if (!noSpeed) Row("Velocidad media", $"{s.AvgSpeed:0} km/h", RP(x => x.AvgSpeed, v => $"{v:0} km/h"));
        Row("A fondo", P(s.FullThrottlePct), RP(x => x.FullThrottlePct, P));
        Row("Frenando", P(s.BrakingPct), RP(x => x.BrakingPct, P));
        if (!noSpeed) Row("Inercia (sin pedales)", P(s.CoastingPct), RP(x => x.CoastingPct, P));
        Row("Acelerador + freno", P(s.OverlapPct), RP(x => x.OverlapPct, P));
        Row("Presión freno máx.", $"{s.AvgPeakBrake:P0}", RP(x => x.AvgPeakBrake, v => $"{v:P0}"));
        Row("Ataque al freno", $"{s.AvgTimeToPeak:0.00} s", RP(x => x.AvgTimeToPeak, v => $"{v:0.00} s"));
        Row("Zonas de frenada", s.BrakeZones.Count.ToString(), RP(x => x.BrakeZones.Count, v => $"{v:0}"));
        if (!noSpeed) Row("En el corte", P(s.LimiterPct), RP(x => x.LimiterPct, P));
        if (!noSpeed) Row("Correcciones / km", $"{s.SteeringCorrectionsPerKm:0.0}", RP(x => x.SteeringCorrectionsPerKm, v => $"{v:0.0}"));
        if (!noSpeed) Row("Cambios de marcha", s.GearShifts.ToString(), RP(x => x.GearShifts, v => $"{v:0}"));
        Row("Nota media de frenadas", $"{s.AvgBrakeScore:0}/100", RP(x => x.AvgBrakeScore, v => $"{v:0}/100"));
        Row("Trail braking", $"{s.TrailBrakingZones}/{s.TurnZones} curvas", r != null ? $"{r.TrailBrakingZones}/{r.TurnZones}" : null);
        Row("Sueltas bruscas", s.AbruptReleases.ToString(), RP(x => x.AbruptReleases, v => $"{v:0}"));
    }

    private void OnTipSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TipList.SelectedItem is TipItem { Tip.Distance: double from } item)
        {
            Chart.ZoomTo(from, item.Tip.DistanceTo ?? from + 200);
            TrackMap.ZoomTo(from - 50, (item.Tip.DistanceTo ?? from + 200) + 50);
        }
    }

    private void OnBrakeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (BrakeList.SelectedItem is BrakeItem b)
        {
            Chart.ZoomTo(b.Zone.StartDistance - 50, Math.Max(b.Zone.EndDistance, b.Zone.MinSpeedDistance) + 80);
            TrackMap.ZoomTo(b.Zone.StartDistance - 80, Math.Max(b.Zone.EndDistance, b.Zone.MinSpeedDistance) + 120);
        }
    }

#if DEBUG
    /// <summary>Para las capturas: una vuelta completa que no sea la mejor (así hay comparación); si no, la segunda.</summary>
    internal void SelectComparableLap()
    {
        if (LapList.ItemsSource is not List<LapItem> items || items.Count == 0) return;
        var best = items.Where(i => i.Info.Completed && i.Info.Valid).MinBy(i => i.Info.LapTime);
        LapList.SelectedItem = items.FirstOrDefault(i => i.Info.Completed && i != best) ?? items.ElementAtOrDefault(1) ?? items[0];
    }
#endif

    private List<Tip> _tips = new();

    private void OnTipsModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.TipsDetailed = TipsDetailed.IsChecked == true;
        TipList.ItemsSource = _tips.Select(t => new TipItem(t, _app.Settings.TipsDetailed)).ToList();
        SaveSoon();
    }

    /// <summary>Botón "Practicar" de un consejo: abre ese ejercicio en el Entrenador.</summary>
    private void OnPracticeExercise(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id) return;
        Tabs.SelectedItem = TabTrainer;
        RefreshExerciseList(id);
    }

    internal void SelectFirstSegmentTip()
    {
        if (TipList.ItemsSource is List<TipItem> tips)
            TipList.SelectedItem = tips.FirstOrDefault(t => t.Tip.Distance != null);
    }

    // ======================= PROGRESO =======================

    private void RefreshProgress()
    {
        _progressDirty = false;
        _allLaps = LapRecorder.ListLaps(_app.DataFolder);
        string? keep = (ComboList.SelectedItem as ComboItem)?.Key;

        var combos = _allLaps
            .Where(InProfile)
            .GroupBy(ProgressAnalyzer.ComboKey)
            .Select(g => new ComboItem(g.Key, g.ToList()))
            .OrderByDescending(c => c.LastDate)
            .ToList();
        ComboList.ItemsSource = combos;
        ComboList.SelectedItem = combos.FirstOrDefault(c => c.Key == keep) ?? combos.FirstOrDefault();

        if (combos.Count == 0)
        {
            ProgressTitle.Text = "Todavía no hay vueltas grabadas";
            ProgressTips.ItemsSource = null;
            ProgressChart.SetPanes(Array.Empty<ChartPane>());
            ProgressGrid.Children.Clear();
        }
    }

    private async void OnComboSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ComboList.SelectedItem is not ComboItem combo) return;
        int version = ++_progressVersion;
        ProgressTitle.Text = combo.Title + " · calculando…";

        // Leer todas las vueltas puede tardar: lo hacemos fuera del hilo de la interfaz.
        var sessions = await Task.Run(() => ProgressAnalyzer.Build(combo.Laps));
        if (version != _progressVersion) return;

        _progressSessions = sessions;
        ProgressTitle.Text = $"{combo.Title}  —  {sessions.Count} sesión(es)";
        ProgressTips.ItemsSource = ProgressAnalyzer.Summarize(sessions, combo.TrackKnown).Select(t => new TipItem(t, detailed: true)).ToList();
        ProgressChart.SetPanes(BuildProgressPanes(sessions));
        FillProgressTable(sessions);
    }

    private static IEnumerable<ChartPane> BuildProgressPanes(List<SessionProgress> sessions)
    {
        ChartSeries Series(string name, Func<SessionProgress, double> value, Func<SessionProgress, bool> include, Color color, string format)
        {
            var points = sessions.Select((s, i) => (s, x: i + 1.0)).Where(p => include(p.s)).ToList();
            return new ChartSeries
            {
                Name = name,
                X = points.Select(p => p.x).ToArray(),
                Y = points.Select(p => value(p.s)).ToArray(),
                Color = color,
                Thickness = 1.8,
                Markers = true,
                Format = format,
            };
        }

        Color green = Palette.Throttle, red = Palette.Brake;
        Color dim = Palette.Dim, orange = Palette.Reference, yellow = Palette.Warn;

        var times = new ChartPane { Title = "Tiempo por sesión (s)  · abajo = más rápido", Weight = 2 };
        times.Series.Add(Series("Media", s => s.Average, s => s.Best > 0, dim, "0.000"));
        times.Series.Add(Series("Mejor", s => s.Best, s => s.Best > 0, green, "0.000"));
        yield return times;

        var consistency = new ChartPane { Title = "Variación entre vueltas (s)  · abajo = más constante", Weight = 1, Min = 0 };
        consistency.Series.Add(Series("Variación", s => s.StdDev, s => s.CompletedLaps > 1, yellow, "0.00"));
        yield return consistency;

        var technique = new ChartPane { Title = "Inercia y pedales a la vez (%)  · abajo = mejor", Weight = 1.2, Min = 0 };
        technique.Series.Add(Series("Inercia", s => s.Coasting, _ => true, orange, "0.0"));
        technique.Series.Add(Series("Ambos pedales", s => s.Overlap, _ => true, red, "0.0"));
        yield return technique;

        var throttle = new ChartPane { Title = "A fondo (%)", Weight = 1 };
        throttle.Series.Add(Series("A fondo", s => s.FullThrottle, _ => true, green, "0.0"));
        yield return throttle;

        var brake = new ChartPane { Title = "Presión máxima de freno (%)", Weight = 1 };
        brake.Series.Add(Series("Freno máx.", s => s.PeakBrake * 100, s => s.PeakBrake > 0, red, "0"));
        yield return brake;
    }

    private void FillProgressTable(List<SessionProgress> sessions)
    {
        ProgressGrid.Children.Clear();
        ProgressGrid.RowDefinitions.Clear();
        ProgressGrid.ColumnDefinitions.Clear();
        string[] headers = { "Sesión", "Vueltas", "Mejor", "Media", "Variación", "A fondo", "Inercia", "Ambos ped.", "Freno máx.", "Ataque freno" };
        foreach (var _ in headers)
            ProgressGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 56 });

        var dimBrush = (Brush)FindResource("Dim");
        var textBrush = (Brush)FindResource("Text");
        double record = sessions.Where(s => s.Best > 0).Select(s => s.Best).DefaultIfEmpty(0).Min();

        void Cell(string text, int row, int col, Brush brush, bool bold = false)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, 2, 12, 2),
                TextAlignment = col == 0 ? TextAlignment.Left : TextAlignment.Right,
            };
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            ProgressGrid.Children.Add(tb);
        }

        ProgressGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < headers.Length; c++) Cell(headers[c], 0, c, dimBrush, bold: true);

        // La sesión más reciente, arriba.
        int row = 1;
        foreach (var s in Enumerable.Reverse(sessions))
        {
            ProgressGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bool isRecord = s.Best > 0 && s.Best == record;
            Cell($"#{sessions.IndexOf(s) + 1} · {s.Date:dd'/'MM HH:mm}", row, 0, textBrush);
            Cell($"{s.CompletedLaps}/{s.Laps}", row, 1, textBrush);
            Cell(s.Best > 0 ? LapInfo.FormatLapTime(s.Best) + (isRecord ? " ★" : "") : "—", row, 2, isRecord ? GoodBrush : textBrush, bold: true);
            Cell(s.Best > 0 ? LapInfo.FormatLapTime(s.Average) : "—", row, 3, textBrush);
            Cell(s.CompletedLaps > 1 ? $"{s.StdDev:0.00} s" : "—", row, 4, textBrush);
            Cell($"{s.FullThrottle:0.0} %", row, 5, textBrush);
            Cell($"{s.Coasting:0.0} %", row, 6, textBrush);
            Cell($"{s.Overlap:0.0} %", row, 7, textBrush);
            Cell(s.PeakBrake > 0 ? $"{s.PeakBrake:P0}" : "—", row, 8, textBrush);
            Cell(s.PeakBrake > 0 ? $"{s.TimeToPeak:0.00} s" : "—", row, 9, textBrush);
            row++;
        }
    }

    // ======================= OVERLAY =======================

    private void LoadOverlayControls()
    {
        _loading = true;
        var o = _app.Settings.Overlay;
        OverlayEnabled.IsChecked = o.Enabled;
        AutoHideCheck.IsChecked = o.AutoHide;
        ShowTraceCheck.IsChecked = o.ShowTrace;
        ShowBarsCheck.IsChecked = o.ShowBars;
        ShowClutchCheck.IsChecked = o.ShowClutch;
        ShowGearCheck.IsChecked = o.ShowGearSpeed;
        ShowDeltaCheck.IsChecked = o.ShowDelta;
        WindowSecondsSlider.Value = o.WindowSeconds;
        OpacitySlider.Value = o.BackgroundOpacity;
        WidthSlider.Value = o.Width;
        HeightSlider.Value = o.Height;
        _loading = false;
        UpdateOverlayLabels();
    }

    public void SyncOverlayControls()
    {
        _loading = true;
        OverlayEnabled.IsChecked = _app.Settings.Overlay.Enabled;
        if (!_app.Settings.Overlay.Enabled) EditToggle.IsChecked = false;
        _loading = false;
    }

    /// <summary>Abre la pestaña Overlay y desbloquea el overlay para colocarlo (desde el menú de la bandeja).</summary>
    public void BeginOverlayEdit()
    {
        Tabs.SelectedItem = TabOverlay;
        if (!_app.Settings.Overlay.Enabled)
        {
            _app.SetOverlayVisible(true);
            SyncOverlayControls();
        }
        EditToggle.IsChecked = true;
    }

    private void UpdateOverlayLabels()
    {
        var o = _app.Settings.Overlay;
        WindowSecondsLabel.Text = $"Historial de la gráfica: {o.WindowSeconds:0} s";
        OpacityLabel.Text = $"Opacidad del fondo: {o.BackgroundOpacity:P0}";
        WidthLabel.Text = $"Ancho: {o.Width:0} px";
        HeightLabel.Text = $"Alto: {o.Height:0} px";
        OverlayPosText.Text = $"Posición actual: X {o.Left:0}, Y {o.Top:0} · tamaño {o.Width:0} × {o.Height:0}";
        if (!OverlayX.IsKeyboardFocused) OverlayX.Text = $"{o.Left:0}";
        if (!OverlayY.IsKeyboardFocused) OverlayY.Text = $"{o.Top:0}";
        if (!OverlayW.IsKeyboardFocused) OverlayW.Text = $"{o.Width:0}";
        if (!OverlayH.IsKeyboardFocused) OverlayH.Text = $"{o.Height:0}";
    }

    /// <summary>Lo llama el overlay cuando el usuario lo arrastra o redimensiona.</summary>
    public void OnOverlayLayoutChanged()
    {
        _loading = true;
        WidthSlider.Value = _app.Settings.Overlay.Width;
        HeightSlider.Value = _app.Settings.Overlay.Height;
        _loading = false;
        UpdateOverlayLabels();
        SaveSoon();
    }

    private void OnOverlayEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetOverlayVisible(OverlayEnabled.IsChecked == true);
        if (OverlayEnabled.IsChecked != true) EditToggle.IsChecked = false;
    }

    private void OnEditToggled(object sender, RoutedEventArgs e)
    {
        bool edit = EditToggle.IsChecked == true;
        if (edit && !_app.Settings.Overlay.Enabled)
        {
            _app.SetOverlayVisible(true);
            SyncOverlayControls();
        }
        _app.Overlay.EditMode = edit;
        _app.UpdateOverlayVisibility();
        EditHelp.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
        EditToggle.Content = edit ? "🔒  Bloquear overlay (listo)" : "🔓  Desbloquear para mover y redimensionar";
    }

    private void EnsureOverlayShown()
    {
        if (!_app.Settings.Overlay.Enabled)
        {
            _app.SetOverlayVisible(true);
            SyncOverlayControls();
        }
        // Al colocarlo lo mostramos aunque no estés conduciendo.
        if (!_app.Overlay.EditMode) EditToggle.IsChecked = true;
    }

    private void OnSnap(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var parts = tag.Split(',');
        var h = Enum.Parse<HorizontalAlignment>(parts[0]);
        var v = Enum.Parse<VerticalAlignment>(parts[1]);
        EnsureOverlayShown();
        _app.Overlay.SnapTo(h, v);
    }

    private void BuildMonitorButtons()
    {
        MonitorButtons.Children.Clear();
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length < 2) return;
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var b = new Button { Content = $"🖥  Llevar al monitor {i + 1}{(screen.Primary ? " (principal)" : "")}" };
            b.Click += (_, _) =>
            {
                EnsureOverlayShown();
                _app.Overlay.SnapTo(HorizontalAlignment.Left, VerticalAlignment.Top, screen);
            };
            MonitorButtons.Children.Add(b);
        }
    }

    private void OnOverlayApplyNumbers(object sender, RoutedEventArgs e)
    {
        static double? Num(string s) => double.TryParse(s, out var v) ? v : null;
        var o = _app.Settings.Overlay;
        o.Left = Num(OverlayX.Text) ?? o.Left;
        o.Top = Num(OverlayY.Text) ?? o.Top;
        o.Width = Math.Max(120, Num(OverlayW.Text) ?? o.Width);
        o.Height = Math.Max(50, Num(OverlayH.Text) ?? o.Height);
        EnsureOverlayShown();
        _app.Overlay.ApplySettings();
        OnOverlayLayoutChanged();
    }

    private void OnOverlayBigger(object sender, RoutedEventArgs e)
    {
        EnsureOverlayShown();
        _app.Overlay.Scale(1.15);
    }

    private void OnOverlaySmaller(object sender, RoutedEventArgs e)
    {
        EnsureOverlayShown();
        _app.Overlay.Scale(1 / 1.15);
    }

    private void OnOverlayOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var o = _app.Settings.Overlay;
        o.AutoHide = AutoHideCheck.IsChecked == true;
        o.ShowTrace = ShowTraceCheck.IsChecked == true;
        o.ShowBars = ShowBarsCheck.IsChecked == true;
        o.ShowClutch = ShowClutchCheck.IsChecked == true;
        o.ShowGearSpeed = ShowGearCheck.IsChecked == true;
        o.ShowDelta = ShowDeltaCheck.IsChecked == true;
        _app.Overlay.ApplySettings();
        _app.UpdateOverlayVisibility();
        LivePedals.InvalidateVisual();
        SaveSoon();
    }

    private void OnOverlaySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _app?.Overlay == null) return;
        var o = _app.Settings.Overlay;
        o.WindowSeconds = WindowSecondsSlider.Value;
        o.BackgroundOpacity = OpacitySlider.Value;
        o.Width = WidthSlider.Value;
        o.Height = HeightSlider.Value;
        _app.Overlay.ApplySettings();
        LivePedals.InvalidateVisual();
        UpdateOverlayLabels();
        SaveSoon();
    }

    private void OnOverlayReset(object sender, RoutedEventArgs e)
    {
        var defaults = new OverlaySettings();
        var o = _app.Settings.Overlay;
        o.Left = defaults.Left;
        o.Top = defaults.Top;
        o.Width = defaults.Width;
        o.Height = defaults.Height;
        _app.Overlay.ApplySettings();
        LoadOverlayControls();
        SaveSoon();
    }

    // ======================= JUEGOS =======================

    private void ShowConfigResult(string text)
    {
        ConfigResult.Visibility = Visibility.Visible;
        ConfigResult.Text = text;
        MessageBox.Show(this, text, "APEX", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnConfigureDirt(object sender, RoutedEventArgs e)
    {
        ShowConfigResult(GameConfigurator.ConfigureCodemasters(_app.Settings.CodemastersPort));
        CheckGames();
    }

    private void OnConfigureWrc(object sender, RoutedEventArgs e)
    {
        ShowConfigResult(GameConfigurator.ConfigureEaWrc(_app.Settings.WrcPort));
        _app.Hub.ApplyPorts(_app.Settings); // recarga los tipos de canal del juego
        CheckGames();
    }

    private void OnApplyPorts(object sender, RoutedEventArgs e)
    {
        static int? Parse(string s) => int.TryParse(s, out int v) && v is >= 1024 and <= 65535 ? v : null;
        var ports = new[] { Parse(PortDirt.Text), Parse(PortWrc.Text), Parse(PortForza.Text), Parse(PortOutGauge.Text) };
        if (ports.Any(p => p == null))
        {
            MessageBox.Show(this, "Los puertos deben ser números entre 1024 y 65535.", "APEX", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ports.Distinct().Count() != ports.Length)
        {
            MessageBox.Show(this, "Cada juego necesita un puerto distinto.", "APEX", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var s = _app.Settings;
        s.CodemastersPort = ports[0]!.Value;
        s.WrcPort = ports[1]!.Value;
        s.ForzaPort = ports[2]!.Value;
        s.OutGaugePort = ports[3]!.Value;
        s.Save();
        _app.Hub.ApplyPorts(s);
        ShowConfigResult("Puertos aplicados. Recuerda poner los mismos puertos en cada juego.");
    }

    // ======================= Modelos de las listas =======================

    private sealed class SourceRow : INotifyPropertyChanged
    {
        private readonly ITelemetrySource _source;
        public SourceRow(ITelemetrySource source)
        {
            _source = source;
            Refresh();
        }

        public string Name => _source.DisplayName;
        public string Packets { get; private set; } = "";
        public string Status { get; private set; } = "";
        public Brush DotBrush { get; private set; } = OffBrush;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Refresh()
        {
            string status = _source.Status;
            bool receiving = _source.IsReceiving;
            if (!receiving && status.StartsWith("Recibiendo"))
                status = (_source is UdpSourceBase udp ? $"Escuchando UDP {udp.Port}" : "Esperando al juego…") +
                         $" · última señal {_source.LastPacketUtc.ToLocalTime():HH:mm:ss}";
            if (receiving && !string.IsNullOrEmpty(_source.GameName) && !status.Contains(_source.GameName))
                status = $"{_source.GameName} — {status}";
            Brush dot = receiving ? GoodBrush
                : status.StartsWith("No se pudo") || status.StartsWith("Error") || status.StartsWith("Paquete") ? BadBrush
                : status.StartsWith("Conectado") ? WaitBrush
                : OffBrush;
            string packets = _source.PacketCount == 0 ? "sin paquetes recibidos"
                : $"{_source.PacketCount:N0} paquetes" + (_source is UdpSourceBase u && u.LastPacketSize > 0 ? $" · último de {u.LastPacketSize} bytes" : "");
            if (packets != Packets)
            {
                Packets = packets;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Packets)));
            }
            if (status != Status)
            {
                Status = status;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
            if (dot != DotBrush)
            {
                DotBrush = dot;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DotBrush)));
            }
        }
    }

    private sealed class SessionItem
    {
        public SessionItem(string folder, List<LapInfo> laps)
        {
            Folder = folder;
            Laps = laps;
            var first = laps[0];
            Date = laps.Min(l => l.Date);
            Title = string.IsNullOrEmpty(first.Track) ? first.Game : $"{ShortGame(first.Game)} · {TrackLabel(first)}";
            var best = laps.Where(l => l.Completed).OrderBy(l => l.LapTime).FirstOrDefault();
            string category = Tracks.CarCatalog.Category(first.Game, first.Car);
            string car = Tracks.CarCatalog.DisplayName(first.Car);
            Subtitle = $"{Date:dd/MM HH:mm}" + (category.Length > 0 ? $" · {category}" : "") + (car.Length > 0 ? $" · {car}" : "")
                       + $" · {laps.Count} vuelta(s)" + (best != null ? $" · mejor {best.TimeText}" : "");
        }

        public string Folder { get; }
        public List<LapInfo> Laps { get; }
        public DateTime Date { get; }
        public string Title { get; }
        public string Subtitle { get; }
    }

    private sealed class ComboItem
    {
        public ComboItem(string key, List<LapInfo> laps)
        {
            Key = key;
            Laps = laps;
            var first = laps[0];
            TrackKnown = !string.IsNullOrEmpty(first.Track);
            Title = TrackKnown ? $"{ShortGame(first.Game)} · {TrackLabel(first)}" : $"{ShortGame(first.Game)} · circuito sin identificar";
            LastDate = laps.Max(l => l.Date);
            int sessions = laps.Select(l => l.SessionFolder).Distinct().Count();
            var best = laps.Where(l => l.Completed).OrderBy(l => l.LapTime).FirstOrDefault();
            Subtitle = $"{sessions} sesión(es) · {laps.Count} vuelta(s)" + (best != null ? $" · récord {best.TimeText}" : "");
        }

        public string Key { get; }
        public List<LapInfo> Laps { get; }
        public bool TrackKnown { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public DateTime LastDate { get; }
    }

    private sealed class BrakeItem
    {
        public BrakeItem(BrakeZone z, int index, int abs, bool timeBased)
        {
            Zone = z;
            Title = timeBased
                ? $"Frenada {index} · minuto {TimeSpan.FromSeconds(z.StartDistance):m\\:ss} · {z.Duration:0.0} s frenando"
                : $"Frenada {index} · {z.StartDistance:0} m · {z.StartSpeed:0} → {z.MinSpeed:0} km/h";
            string trail = !z.HasTurn ? "en recta" : z.BrakeAtTurnIn >= BrakeIdeals.TrailMin ? $"giras con {z.BrakeAtTurnIn:P0} de freno" : "sin trail braking";
            Detail = $"Pico {z.PeakBrake:P0} (ideal ≥ {BrakeIdeals.MinPeak(abs):P0}) en {z.TimeToPeak:0.00} s · {trail} · {(z.AbruptRelease ? "suelta brusca" : "suelta progresiva")}";
            Notes = string.Join(" · ", z.Notes);
            ScoreBrush = z.Score >= 80 ? GoodBrush : z.Score >= 55 ? WaitBrush : BadBrush;
        }

        public BrakeZone Zone { get; }
        public int Score => Zone.Score;
        public string Title { get; }
        public string Detail { get; }
        public string Notes { get; }
        public Brush ScoreBrush { get; }
    }

    /// <summary>Fila de la torre de vueltas: número, tiempo y diferencia con la mejor (morado), como en una pantalla de tiempos.</summary>
    private sealed class LapItem
    {
        public LapItem(LapInfo info, LapInfo? best, bool reference)
        {
            Info = info;
            bool isBest = info == best;
            static Brush R(string key) => (Brush)Application.Current.Resources[key];
            Num = info.TimeBased ? $"P{info.Number}" : info.Number.ToString();
            Diff = info.TimeBased ? "" : reference ? "REF" : isBest ? "MEJOR" : !info.Valid ? "INVÁL." : !info.Completed ? "INCOMPL."
                 : best != null ? (info.LapTime - best.LapTime).ToString("+0.000", CultureInfo.InvariantCulture) : "";
            bool dim = !info.TimeBased && (!info.Valid || !info.Completed);
            NumBrush = isBest ? R("Reference") : R("Dim");
            TimeBrush = isBest ? R("Reference") : dim ? R("Dim") : R("Text");
            DiffBrush = isBest || reference ? R("Reference") : dim ? R("Dim") : R("Warn");
            TimeWeight = isBest ? FontWeights.Bold : FontWeights.Normal;
            Strike = !info.Valid && !info.TimeBased ? TextDecorations.Strikethrough : null;
        }

        public LapInfo Info { get; }
        public string Num { get; }
        public string Time => Info.TimeText;
        public string Diff { get; }
        public Brush NumBrush { get; }
        public Brush TimeBrush { get; }
        public Brush DiffBrush { get; }
        public FontWeight TimeWeight { get; }
        public TextDecorationCollection? Strike { get; }
        public string Note => Info.TimeBased ? $"sesión con el volante · {Info.Date:HH:mm:ss}" : (Info.Completed ? "" : "incompleta · ") + (Info.Valid ? "" : Info.EndReason.Contains("no sirve de referencia") ? "no sirve de referencia · " : "inválida · ") + $"{Info.Length / 1000:0.00} km · {Info.Date:HH:mm:ss}";
    }

    private sealed class TipItem
    {
        public TipItem(Tip tip, bool detailed)
        {
            Tip = tip;
            Detailed = detailed;
        }

        public Tip Tip { get; }
        public bool Detailed { get; }
        public string Icon => Tip.Icon;
        public string Title => Tip.Title;
        /// <summary>Qué pasa (con los números). En el resumen sólo se ve si no hay un "qué hacer".</summary>
        public string Detail => Tip.Detail + (Tip.Distance != null ? "  (clic para verlo en la gráfica)" : "");
        public Visibility DetailVisibility => Detailed || Tip.Advice == null ? Visibility.Visible : Visibility.Collapsed;
        public string Why => Tip.Advice is { } a ? "Por qué: " + a.Why : "";
        public string How => Tip.Advice is { } a ? "Qué hacer: " + a.How : "";
        public string Drill => Tip.Advice is { } a ? "En pista: " + a.Drill : "";
        public Visibility WhyVisibility => Detailed && Tip.Advice != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility HowVisibility => Tip.Advice != null ? Visibility.Visible : Visibility.Collapsed;
        public string? ExerciseId => Tip.Advice?.ExerciseId;
        public string ExerciseLabel => "🎯  Practicar: " + (Training.PedalExercise.All.FirstOrDefault(e => e.Id == ExerciseId)?.Name ?? "");
        public Visibility ExerciseVisibility => ExerciseId != null ? Visibility.Visible : Visibility.Collapsed;
        public Brush Brush => Tip.Level switch
        {
            TipLevel.Good => GoodBrush,
            TipLevel.Warning => WaitBrush,
            _ => Res("Info"),
        };
    }
}
