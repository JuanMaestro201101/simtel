using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SimTelemetry.UI;

/// <summary>
/// Ventana del overlay: siempre encima, transparente y "click-through" (los clics pasan al juego).
/// Sólo es interactiva en modo edición, que se activa desde la app.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double Edge = 12; // px sensibles a redimensionar en cada borde

    [Flags]
    private enum Side { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    private readonly OverlaySettings _settings;
    private readonly DispatcherTimer _topmostTimer;
    private readonly Native.WinEventDelegate _foregroundHook;
    private IntPtr _hookHandle;
    private IntPtr _hwnd;
    private bool _editMode;

    // Estado del arrastre / redimensionado manual.
    private Side _dragSide;
    private Point _dragStartScreen;
    private Rect _dragStartRect;

    public OverlayWindow(OverlaySettings settings, PedalBuffer buffer)
    {
        InitializeComponent();
        _settings = settings;
        Pedals.Settings = settings;
        Pedals.Buffer = buffer;
        ApplySettings();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // Ventana transparente: con la GPU, WPF tiene que copiar cada fotograma de vuelta a memoria y esa copia
            // espera a que el juego suelte la tarjeta gráfica (tirones). Dibujar con la CPU algo tan pequeño cuesta poco.
            if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is { } target) target.RenderMode = RenderMode.SoftwareOnly;
            ApplyClickThrough();
        };
        SizeChanged += (_, _) => DrawHandles();

        // Algunos juegos se ponen "encima de todo" al enfocarse: reafirmamos el overlay al instante
        // cuando cambia la ventana activa, y además cada segundo por si acaso.
        _foregroundHook = (_, _, _, _, _, _, _) => BringToTop();
        _hookHandle = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundHook, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _topmostTimer.Tick += (_, _) => BringToTop();
        _topmostTimer.Start();
    }

    /// <summary>Se dispara cuando el usuario mueve o redimensiona el overlay.</summary>
    public event Action? LayoutChanged;

    public bool EditMode
    {
        get => _editMode;
        set
        {
            if (_editMode == value) return;
            _editMode = value;
            EditFrame.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Pedals.DemoWhenIdle = value;
            ApplyClickThrough();
            if (value)
            {
                Show();
                Activate();
                Focus();
            }
            else
            {
                Cursor = null;
                SavePosition();
            }
        }
    }

    public void ApplySettings()
    {
        Left = _settings.Left;
        Top = _settings.Top;
        Width = Math.Max(MinWidth, _settings.Width);
        Height = Math.Max(MinHeight, _settings.Height);
        Pedals.InvalidateVisual();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        const int always = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW;
        if (_editMode)
            Native.SetExStyle(_hwnd, always, Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE);
        else
            Native.SetExStyle(_hwnd, always | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE, 0);
        BringToTop();
    }

    private void BringToTop()
    {
        if (_hwnd != IntPtr.Zero && IsVisible)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // ---------------- Edición: mover, redimensionar, escalar ----------------

    private Side HitTest(Point p)
    {
        var side = Side.None;
        if (p.X <= Edge) side |= Side.Left;
        else if (p.X >= ActualWidth - Edge) side |= Side.Right;
        if (p.Y <= Edge) side |= Side.Top;
        else if (p.Y >= ActualHeight - Edge) side |= Side.Bottom;
        return side;
    }

    private static Cursor CursorFor(Side s) => s switch
    {
        Side.Left or Side.Right => Cursors.SizeWE,
        Side.Top or Side.Bottom => Cursors.SizeNS,
        Side.Left | Side.Top or Side.Right | Side.Bottom => Cursors.SizeNWSE,
        Side.Right | Side.Top or Side.Left | Side.Bottom => Cursors.SizeNESW,
        _ => Cursors.SizeAll,
    };

    /// <summary>Posición del ratón en pantalla, en unidades WPF (independientes de los PPP).</summary>
    private Point ScreenPoint(MouseEventArgs e)
    {
        var device = PointToScreen(e.GetPosition(this));
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice.Transform(device) : device;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_editMode) return;
        Activate();
        Focus();
        _dragSide = HitTest(e.GetPosition(this));
        if (_dragSide == Side.None)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
            SavePosition();
            return;
        }
        _dragStartScreen = ScreenPoint(e);
        _dragStartRect = new Rect(Left, Top, Width, Height);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_editMode) return;
        if (!IsMouseCaptured)
        {
            Cursor = CursorFor(HitTest(e.GetPosition(this)));
            return;
        }

        var p = ScreenPoint(e);
        double dx = p.X - _dragStartScreen.X, dy = p.Y - _dragStartScreen.Y;
        var r = _dragStartRect;
        double left = r.Left, top = r.Top, width = r.Width, height = r.Height;
        if (_dragSide.HasFlag(Side.Right)) width = Math.Max(MinWidth, r.Width + dx);
        if (_dragSide.HasFlag(Side.Bottom)) height = Math.Max(MinHeight, r.Height + dy);
        if (_dragSide.HasFlag(Side.Left))
        {
            width = Math.Max(MinWidth, r.Width - dx);
            left = r.Right - width;
        }
        if (_dragSide.HasFlag(Side.Top))
        {
            height = Math.Max(MinHeight, r.Height - dy);
            top = r.Bottom - height;
        }
        Left = left; Top = top; Width = width; Height = height;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
            SavePosition();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_editMode) return;
        Scale(e.Delta > 0 ? 1.1 : 1 / 1.1);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_editMode) return;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left: Left -= step; break;
            case Key.Right: Left += step; break;
            case Key.Up: Top -= step; break;
            case Key.Down: Top += step; break;
            case Key.OemPlus or Key.Add: Scale(1.1); break;
            case Key.OemMinus or Key.Subtract: Scale(1 / 1.1); break;
            default: return;
        }
        SavePosition();
        e.Handled = true;
    }

    /// <summary>Agranda o achica el overlay manteniendo su centro.</summary>
    public void Scale(double factor)
    {
        double w = Math.Max(MinWidth, Width * factor), h = Math.Max(MinHeight, Height * factor);
        Left -= (w - Width) / 2;
        Top -= (h - Height) / 2;
        Width = w;
        Height = h;
        SavePosition();
    }

    private void DrawHandles()
    {
        Handles.Children.Clear();
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        foreach (var (x, y) in new[] { (0.0, 0.0), (w / 2, 0), (w, 0), (0, h / 2), (w, h / 2), (0, h), (w / 2, h), (w, h) })
        {
            var r = new Rectangle { Width = 10, Height = 10, Fill = new SolidColorBrush(Palette.OverlayAccent), RadiusX = 2, RadiusY = 2 };
            Canvas.SetLeft(r, Math.Max(0, Math.Min(x - 5, w - 10)));
            Canvas.SetTop(r, Math.Max(0, Math.Min(y - 5, h - 10)));
            Handles.Children.Add(r);
        }
    }

    private void SavePosition()
    {
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Width = Width;
        _settings.Height = Height;
        LayoutChanged?.Invoke();
    }

    /// <summary>Área de trabajo (en unidades WPF) del monitor indicado, o del monitor donde está el overlay.</summary>
    public Rect MonitorArea(System.Windows.Forms.Screen? screen = null)
    {
        screen ??= System.Windows.Forms.Screen.FromHandle(_hwnd);
        var area = screen.WorkingArea;
        var source = PresentationSource.FromVisual(this);
        double sx = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        double sy = source?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        return new Rect(area.Left / sx, area.Top / sy, area.Width / sx, area.Height / sy);
    }

    /// <summary>Coloca el overlay en una posición predefinida de un monitor (por defecto, el actual).</summary>
    public void SnapTo(HorizontalAlignment h, VerticalAlignment v, System.Windows.Forms.Screen? screen = null)
    {
        var a = MonitorArea(screen);
        const double margin = 16;
        if (Width > a.Width - margin * 2) Width = a.Width - margin * 2;
        if (Height > a.Height - margin * 2) Height = a.Height - margin * 2;

        Left = h switch
        {
            HorizontalAlignment.Left => a.Left + margin,
            HorizontalAlignment.Right => a.Right - Width - margin,
            _ => a.Left + (a.Width - Width) / 2,
        };
        Top = v switch
        {
            VerticalAlignment.Top => a.Top + margin,
            VerticalAlignment.Bottom => a.Bottom - Height - margin,
            _ => a.Top + (a.Height - Height) / 2,
        };
        SavePosition();
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        if (_hookHandle != IntPtr.Zero) Native.UnhookWinEvent(_hookHandle);
        base.OnClosed(e);
    }
}
