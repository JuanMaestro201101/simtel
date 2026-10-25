using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SimTelemetry.Diagnostics;
using SimTelemetry.Engineer;
using SimTelemetry.Input;
using SimTelemetry.Recording;
using SimTelemetry.Telemetry;
using SimTelemetry.UI;
using Forms = System.Windows.Forms;

namespace SimTelemetry;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private DispatcherTimer? _tick;
    private DispatcherTimer? _overlayTimer;
    private long _lastDrivingTicks;

    public AppSettings Settings { get; private set; } = new();
    public TelemetryHub Hub { get; private set; } = null!;
    public LapRecorder Recorder { get; private set; } = null!;
    public PedalBuffer Pedals { get; } = new();
    public OverlayWindow Overlay { get; private set; } = null!;
    public InfoOverlayWindow InfoCard { get; private set; } = null!;
    public Remote.PhoneServer Phone { get; private set; } = null!;
    public WheelInput Wheel { get; private set; } = null!;
    public WheelSessionRecorder WheelRecorder { get; private set; } = null!;
    public RaceEngineer Engineer { get; private set; } = null!;
    private RunningGame? _foregroundGame;
    private DispatcherTimer? _wheelRecordTimer;
    private System.Windows.Interop.HwndSource? _inputWindow;
    public MainWindow MainView { get; private set; } = null!;

    /// <summary>Carpeta de vueltas grabadas (se puede cambiar con --data &lt;carpeta&gt; sin tocar los ajustes).</summary>
    public string DataFolder { get; private set; } = "";

    public static new App Current => (App)Application.Current;

    /// <summary>Prueba oculta (--hidden): nada se dibuja en la pantalla del usuario ni le quita el foco.</summary>
    public bool Hidden { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if DEBUG
        int selfTest = Array.IndexOf(e.Args, "--selftest");
        if (selfTest >= 0)
        {
            Shutdown(SelfTest.Run(selfTest + 1 < e.Args.Length ? e.Args[selfTest + 1] : System.IO.Path.GetTempPath()));
            return;
        }
#endif

        // Las ejecuciones de prueba (--data <carpeta>) pueden convivir con la app normal.
        bool testRun = Array.IndexOf(e.Args, "--data") >= 0;
        bool hidden = Hidden = Array.IndexOf(e.Args, "--hidden") >= 0;
        _singleInstance = new Mutex(true, testRun ? "SimTelemetry.SingleInstance.Test" : "SimTelemetry.SingleInstance", out bool first);
        if (!first)
        {
            if (!hidden) MessageBox.Show("APEX ya está abierto (mira en la bandeja del sistema, junto al reloj).",
                "APEX", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log.Info($"APEX {typeof(App).Assembly.GetName().Version} arrancando");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Error no controlado", args.Exception);
            if (!hidden) MessageBox.Show("Error inesperado: " + args.Exception.Message, "APEX", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Con el juego en primer plano Windows baja la prioridad de las apps de fondo (y el Modo Juego aún más):
        // la app es ligera, así que pedimos no ser frenados para que el overlay siga fluido.
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; }
        catch (Exception ex) { Log.Error("No se pudo subir la prioridad", ex); }
        if (!Native.DisablePowerThrottling()) Log.Info("Windows no permitió desactivar el ahorro de energía para la app");
        Native.timeBeginPeriod(1);

        Settings = AppSettings.Load();
        Palette.Apply(Settings.Theme, Settings.ThemeColor); // antes de crear ventanas: sus XAML usan estos colores
        int dataArg = Array.IndexOf(e.Args, "--data");
        DataFolder = dataArg >= 0 && dataArg + 1 < e.Args.Length ? e.Args[dataArg + 1] : Settings.DataFolder;
        Recorder = new LapRecorder(DataFolder) { Enabled = Settings.RecordingEnabled };
        WheelRecorder = new WheelSessionRecorder(DataFolder);
        Hub = new TelemetryHub(Settings);
        Engineer = new RaceEngineer(Settings.Engineer, DataFolder);
        Recorder.LapSaved += Engineer.OnLapSaved;
        Hub.Sample += (source, s) =>
        {
            Engineer.OnSample(source, s);
            if (s.InSession && Wheel.PedalsConnected) LearnPedalFeel(source.GameName, s);
            if (s.InSession) Interlocked.Exchange(ref _lastDrivingTicks, DateTime.UtcNow.Ticks);
            if (PedalSource == PedalSourceKind.Game) Pedals.Add(s.Throttle, s.Brake, s.Clutch, s.Gear, s.SpeedKmh);
            Recorder.Add(source, s);
        };

        // Volante y pedales USB: se leen siempre, con o sin juego abierto.
        Wheel = new WheelInput(() => Settings.Pedals, Pedals, () => PedalSource == PedalSourceKind.Wheel);
        _inputWindow = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("SimTelemetry.Input")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, nunca se muestra
        });
        _inputWindow.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0219) Wheel.RequestEnumerate(); // WM_DEVICECHANGE: volante conectado o desconectado
            return IntPtr.Zero;
        });
        Wheel.Start(_inputWindow.Handle);

        InfoCard = new InfoOverlayWindow();
        Overlay = new OverlayWindow(Settings.Overlay, Pedals);
        Overlay.Pedals.Delta = () => Engineer.LiveDelta;
        Overlay.Pedals.Sector = () => Engineer.LastSector;
        Overlay.LayoutChanged += () => MainView?.OnOverlayLayoutChanged();
        if (hidden)
        {
            Settings.Overlay.Left = Settings.Overlay.Top = -5000;
            Overlay.ApplySettings();
        }
        UpdateOverlayVisibility();

        // En las pruebas ocultas, sólo en este PC: así Windows no muestra el aviso del cortafuegos.
        Phone = new Remote.PhoneServer(PhoneSnapshot, hidden ? System.Net.IPAddress.Loopback : null);
        if (Settings.Phone.Enabled) Phone.Start(Settings.Phone.Port);

        MainView = new MainWindow();
        // --hidden (pruebas mientras el usuario juega): la ventana se dibuja fuera de la pantalla, sin quitar el foco
        // al juego ni aparecer en la barra de tareas o en la bandeja.
        if (hidden)
        {
            MainView.WindowStartupLocation = WindowStartupLocation.Manual;
            MainView.Left = MainView.Top = -32000;
            MainView.ShowActivated = false;
            MainView.ShowInTaskbar = false;
        }
        MainView.Show();

        if (!hidden) CreateTray();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) =>
        {
            Recorder.Tick();
            // El tacto medido de los pedales se guarda cada medio minuto (por si la app se cierra de golpe).
            if (PedalFeel.Dirty && ++_feelSaveTicks % 30 == 0)
            {
                PedalFeel.Dirty = false;
                Settings.Save();
            }
        };
        _tick.Start();

        // El overlay sólo se muestra mientras conduces (si la opción está activa) o mientras lo colocas.
        _overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _overlayTimer.Tick += (_, _) => UpdateOverlayVisibility();
        _overlayTimer.Start();

        // Tecla de la tarjeta de información: se mira el estado del teclado (sin capturarlo, el juego también la recibe).
        _keyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _keyTimer.Tick += (_, _) => PollInfoKey();
        _keyTimer.Start();
        Recorder.LapSaved += info => Dispatcher.BeginInvoke(() =>
        {
            if ($"{info.Game}|{info.Track}|{info.Car}" == _adviceKey) RefreshAdvice(reshow: false);
        });

        Hub.Start();

        // Sesiones de pedales: jugando a un juego compatible que no envía telemetría, grabamos el volante.
        _wheelRecordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _wheelRecordTimer.Tick += (_, _) =>
        {
            bool active = Settings.RecordingEnabled && !TrainerRunning && _foregroundGame != null
                          && PedalSource == PedalSourceKind.Wheel && !Hub.HasLiveData;
            WheelRecorder.Update(active, _foregroundGame?.Name, Wheel.Throttle, Wheel.Brake, Wheel.Clutch, Wheel.Steering);
            // Sin grabar basta con mirar 4 veces por segundo.
            var interval = TimeSpan.FromMilliseconds(active ? 20 : 250);
            if (_wheelRecordTimer!.Interval != interval) _wheelRecordTimer.Interval = interval;
        };
        _wheelRecordTimer.Start();

#if DEBUG
        int snapArg = Array.IndexOf(e.Args, "--snapshot");
        if (snapArg >= 0 && snapArg + 2 < e.Args.Length)
            DebugSnapshot.Schedule(this, e.Args[snapArg + 1], double.Parse(e.Args[snapArg + 2], System.Globalization.CultureInfo.InvariantCulture));
#endif
    }

    private DateTime _lastWheelActivity = DateTime.MinValue;

    /// <summary>Lo que ve el teléfono, en JSON (se llama 20 veces por segundo desde el servidor).</summary>
    private string PhoneSnapshot()
    {
        var s = Hub.Latest;
        bool live = Hub.HasLiveData;
        var next = Engineer.NextCue;
        var sector = Engineer.LastSector;
        double delta = Engineer.LiveDelta;
        var data = new
        {
            t = Pedals.Now,
            thr = Pedals.Throttle,
            brk = Pedals.Brake,
            vehicle = live && Pedals.HasVehicleData,
            gear = s.Gear,
            kmh = s.SpeedKmh,
            rpm = s.Rpm,
            maxRpm = s.MaxRpm,
            lap = live ? s.LapTime : -1,
            last = live ? s.LastLapTime : -1,
            best = Engineer.Plan?.Reference.LapTime ?? -1,
            delta = double.IsFinite(delta) ? delta : (double?)null,
            sector = sector != null && (DateTime.UtcNow - sector.AtUtc).TotalSeconds < 4 ? new { c = sector.Corner, d = sector.Delta } : null,
            next = live && next != null && s.LapDistance >= 0
                ? new { c = next.Corner, text = $"{char.ToUpper(next.Intensity[0])}{next.Intensity[1..]} {Math.Round(next.Peak * 20) * 5:0}%" + (next.Gear > 0 ? $" · {next.Gear}ª" : ""), m = next.BrakeAt - s.LapDistance }
                : null,
            game = live ? Hub.ActiveSource?.GameName : null,
            track = live ? Advice?.Track : null,
            wheel = Wheel.PedalsConnected,
        };
        return System.Text.Json.JsonSerializer.Serialize(data);
    }

    // ---- Tarjeta de información: circuito, categoría y recomendación de la sesión actual ----
    private DispatcherTimer? _keyTimer;
    private string _adviceKey = "";
    private DateTime _infoAutoUntil = DateTime.MinValue, _infoPreviewUntil = DateTime.MinValue;
    private bool _infoExpanded, _infoKeyDown;

    /// <summary>Recomendación de la sesión actual (null si no hay juego enviando circuito y coche).</summary>
    public Analysis.SessionAdvice? Advice { get; private set; }
    public event Action? AdviceChanged;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    private void PollInfoKey()
    {
        var s = Settings.InfoCard;
        var key = Array.Find(InfoCardSettings.Keys, k => k.Name == s.Key);
        bool down = key.VirtualKey != 0 && (GetAsyncKeyState(key.VirtualKey) & 0x8000) != 0
                    && (key.Modifier == 0 || (GetAsyncKeyState(key.Modifier) & 0x8000) != 0);
        bool pressed = down && !_infoKeyDown;
        _infoKeyDown = down;
        if (!pressed || !s.Enabled || _foregroundGame == null) return; // sólo con un juego delante
        _infoExpanded = !(InfoCard.IsVisible && InfoCard.Expanded);
        if (!_infoExpanded) _infoAutoUntil = DateTime.MinValue;
        UpdateInfoCard();
    }

    /// <summary>Muestra la tarjeta unos segundos aunque no haya un juego delante (botón de prueba en la pestaña Overlay).</summary>
    public void PreviewInfoCard()
    {
        _infoPreviewUntil = DateTime.UtcNow.AddSeconds(8);
        UpdateInfoCard();
    }

    /// <summary>Al cambiar de circuito o coche (o tras guardar una vuelta) recalcula la recomendación.</summary>
    private void RefreshAdvice(bool reshow)
    {
        var src = Hub.ActiveSource;
        if (src == null || string.IsNullOrEmpty(src.Track)) return;
        string game = src.GameName, track = src.Track!, car = src.Car ?? "", key = _adviceKey;
        System.Threading.Tasks.Task.Run(() =>
        {
            var advice = Analysis.SessionAdvisor.Build(game, track, car, LapRecorder.ListLaps(DataFolder));
            Dispatcher.BeginInvoke(() =>
            {
                if (key != _adviceKey) return;
                Advice = advice;
                if (reshow) _infoAutoUntil = DateTime.UtcNow.AddSeconds(20);
                AdviceChanged?.Invoke();
                UpdateInfoCard();
            });
        });
    }

    private void UpdateInfoCard()
    {
        var s = Settings.InfoCard;
        var latest = Hub.Latest;
        bool live = Hub.HasLiveData;
        var advice = Advice;
        // Juego delante sin telemetría (p. ej. Forza sin Data Out): lo decimos en la tarjeta.
        if ((!live || advice == null) && _foregroundGame != null && (_infoExpanded || DateTime.UtcNow < _infoPreviewUntil))
            advice = new Analysis.SessionAdvice
            {
                Game = _foregroundGame.Name,
                Headline = $"{_foregroundGame.Name} no está enviando datos: no se puede reconocer el circuito ni el coche. {_foregroundGame.Hint}",
            };
        bool preview = DateTime.UtcNow < _infoPreviewUntil;
        // Botón de prueba sin ninguna sesión: una tarjeta de ejemplo para ver el tamaño y la posición.
        if (preview && advice == null)
            advice = new Analysis.SessionAdvice
            {
                Game = "Ejemplo", Track = "Monza (ejemplo)", Category = "GT3", Car = "Porsche 991 II GT3 R",
                Info = Tracks.TrackDatabase.ByName("Monza"),
                Headline = "Con el Ferrari 296 GT3 fuiste 0,4 s más rápido aquí (1:48.100 frente a 1:48.500).",
            };
        bool inPits = live && latest.InSession && latest.InPit;
        bool show = s.Enabled && advice != null && (preview || (_foregroundGame != null
                    && (_infoExpanded || DateTime.UtcNow < _infoAutoUntil || inPits)));
        if (!show)
        {
            if (InfoCard.IsVisible) InfoCard.Hide();
            if (_foregroundGame == null) _infoExpanded = false;
            return;
        }
        var plan = Engineer.Plan;
        string engineer = !Settings.Engineer.Enabled ? "Desactivado (pestaña Ingeniero de la app)."
            : plan != null ? $"Referencia {plan.Reference.TimeText} · {plan.Cues.Count} frenadas" + (plan.SameCar ? "" : " (de otro coche)")
            : "Sin referencia todavía: tras tu primera vuelta limpia te cantará las frenadas.";
        var area = Hidden ? new Rect(-5000, -5000, 1920, 1080) : Overlay.MonitorArea();
        InfoCard.Present(advice!, _infoExpanded || preview, s.Key, engineer, area, s.Corner);
    }
    private int _feelSaveTicks;
    private float _feelBrake = -1, _feelThrottle = -1;

    /// <summary>Compara la posición real del pedal (volante) con lo que envía el juego para medir su calibración.</summary>
    private void LearnPedalFeel(string game, in TelemetrySample s)
    {
        float brake = Wheel.Brake, throttle = Wheel.Throttle;
        var curve = Settings.PedalFeel.GetOrAdd(game, _ => new PedalFeelCurve());
        // Sólo con el pedal quieto (el juego lo lee con unas milésimas de retraso), y descartando muestras
        // en que el juego frena o acelera con el pedal suelto: entonces juegas con otro mando.
        if (Math.Abs(brake - _feelBrake) < 0.01f && !(brake < 0.03f && s.Brake > 0.1f)) PedalFeel.Learn(curve, true, brake, s.Brake);
        if (Math.Abs(throttle - _feelThrottle) < 0.01f && !(throttle < 0.03f && s.Throttle > 0.1f)) PedalFeel.Learn(curve, false, throttle, s.Throttle);
        _feelBrake = brake;
        _feelThrottle = throttle;
    }

    public enum PedalSourceKind { None, Game, Wheel }

    /// <summary>
    /// De dónde salen ahora los pedales del overlay, la vista en vivo y el entrenador.
    /// En automático: el juego mientras estás en pista; si no, el volante USB.
    /// </summary>
    public PedalSourceKind PedalSource
    {
        get
        {
            bool gameDriving = Hub != null && Hub.HasLiveData && Hub.Latest.InSession;
            bool wheel = Wheel != null && Wheel.PedalsConnected;
            return Settings.PedalInput switch
            {
                PedalInputModes.Game => Hub != null && Hub.HasLiveData ? PedalSourceKind.Game : PedalSourceKind.None,
                PedalInputModes.Wheel => wheel ? PedalSourceKind.Wheel : PedalSourceKind.None,
                _ => gameDriving ? PedalSourceKind.Game : wheel ? PedalSourceKind.Wheel
                     : Hub != null && Hub.HasLiveData ? PedalSourceKind.Game : PedalSourceKind.None,
            };
        }
    }

    /// <summary>El entrenador está en marcha (el overlay se muestra aunque no estés en pista).</summary>
    public bool TrainerRunning { get; set; }

    public bool IsDriving =>
        (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastDrivingTicks), DateTimeKind.Utc)).TotalSeconds < 5;

    public void UpdateOverlayVisibility()
    {
        if (Overlay == null) return;
        var o = Settings.Overlay;
        // Uso del volante: cualquier pedal pisado en los últimos 20 s.
        if (Wheel != null && Wheel.PedalsConnected && (Wheel.Throttle > 0.03f || Wheel.Brake > 0.03f))
            _lastWheelActivity = DateTime.UtcNow;
        bool wheelActive = (DateTime.UtcNow - _lastWheelActivity).TotalSeconds < 20;
        // También con un juego compatible en primer plano, aunque no envíe telemetría (p. ej. Forza sin Data Out).
        _foregroundGame = GameProcesses.Foreground();
        bool gameInFront = _foregroundGame != null;
        DetectProfile();
        var src = Hub?.ActiveSource;
        string adviceKey = Hub != null && Hub.HasLiveData && src != null && !string.IsNullOrEmpty(src.Track) ? $"{src.GameName}|{src.Track}|{src.Car}" : _adviceKey;
        if (adviceKey != _adviceKey)
        {
            _adviceKey = adviceKey;
            RefreshAdvice(reshow: true);
        }
        if (InfoCard != null) UpdateInfoCard();
        bool show = o.Enabled && (Overlay.EditMode || !o.AutoHide || IsDriving || TrainerRunning || wheelActive || gameInFront);
        if (show && !Overlay.IsVisible) Overlay.Show();
        else if (!show && Overlay.IsVisible) Overlay.Hide();
    }

    // ---- Perfil de juego: qué juego se muestra en la app ----

    /// <summary>Perfil actual (null = todos los juegos).</summary>
    public GameProfile? Profile => GameProfiles.ByKey(Settings.Profile);
    /// <summary>true si el perfil actual lo eligió la app al detectar el juego.</summary>
    public bool ProfileAutoSelected { get; private set; }
    public event Action? ProfileChanged;

    private string? _detectedProfile;
    private DateTime _nextRunningScan;
    private GameProfile? _runningProfile;

    public void SetProfile(string key, bool auto = false)
    {
        ProfileAutoSelected = auto;
        if (Settings.Profile == key) { ProfileChanged?.Invoke(); return; }
        Settings.Profile = key;
        Settings.Save();
        Log.Info($"Perfil: {(GameProfiles.ByKey(key)?.Name ?? "todos los juegos")}{(auto ? " (detectado)" : "")}");
        ProfileChanged?.Invoke();
    }

    /// <summary>
    /// Qué juego hay ahora: el que envía telemetría, si no el que está delante, y si no uno abierto (mirar los procesos
    /// cuesta algo: cada 5 s). Cambia de perfil sólo cuando cambia el juego detectado, así no pisa lo que elijas a mano.
    /// </summary>
    private void DetectProfile()
    {
        if (DateTime.UtcNow >= _nextRunningScan)
        {
            _nextRunningScan = DateTime.UtcNow.AddSeconds(5);
            _runningProfile = GameProcesses.Running().Select(g => GameProfiles.For(g.Name)).FirstOrDefault(p => p != null);
        }
        var detected = (Hub != null && Hub.HasLiveData ? GameProfiles.For(Hub.ActiveSource?.GameName) : null)
                       ?? GameProfiles.For(_foregroundGame?.Name)
                       ?? _runningProfile;
        if (detected?.Key == _detectedProfile) return;
        _detectedProfile = detected?.Key;
        if (detected != null && Settings.AutoProfile && Settings.Profile != detected.Key) SetProfile(detected.Key, auto: true);
    }

    public void SetOverlayVisible(bool visible)
    {
        Settings.Overlay.Enabled = visible;
        if (!visible) Overlay.EditMode = false;
        UpdateOverlayVisibility();
        Settings.Save();
    }

    private void CreateTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = AppIcon.CreateIcon(),
            Text = "APEX",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir APEX", null, (_, _) => ShowMain());
        menu.Items.Add("Mover / redimensionar overlay", null, (_, _) =>
        {
            ShowMain();
            MainView.BeginOverlayEdit();
        });
        menu.Items.Add("Activar / desactivar overlay", null, (_, _) =>
        {
            SetOverlayVisible(!Settings.Overlay.Enabled);
            MainView.SyncOverlayControls();
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    public void ShowTrayHint()
    {
        if (Settings.TrayHintShown || _tray == null) return;
        Settings.TrayHintShown = true;
        Settings.Save();
        _tray.ShowBalloonTip(4000, "APEX sigue funcionando",
            "La telemetría y el overlay siguen activos. Haz doble clic en este icono para volver a abrir la app.",
            Forms.ToolTipIcon.Info);
    }

    public void ShowMain()
    {
        MainView.Show();
        if (MainView.WindowState == WindowState.Minimized) MainView.WindowState = WindowState.Normal;
        MainView.Activate();
    }

    public void ExitApp()
    {
        Log.Info("APEX cerrándose");
        Recorder.Flush();
        WheelRecorder.Flush();
        Settings.Save();
        Hub.Dispose();
        Phone.Dispose();
        Engineer.Dispose();
        Wheel.Dispose();
        _inputWindow?.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        MainView.AllowClose = true;
        MainView.Close();
        Overlay.Close();
        InfoCard.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Native.timeEndPeriod(1);
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
