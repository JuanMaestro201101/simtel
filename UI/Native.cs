using System;
using System.Runtime.InteropServices;

namespace SimTelemetry.UI;

internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    public delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version, ControlMask, StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// Pide a Windows que no frene la app (EcoQoS) cuando el juego está en primer plano:
    /// si no, con el juego enfocado el overlay y la lectura del volante van a tirones.
    /// </summary>
    public static bool DisablePowerThrottling()
    {
        // EXECUTION_SPEED: no bajar la CPU de la app; IGNORE_TIMER_RESOLUTION: respetar el reloj de 1 ms aunque la ventana no se vea.
        var state = new PowerThrottlingState { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
        return SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref state, Marshal.SizeOf<PowerThrottlingState>());
    }

    /// <summary>Reloj del sistema a 1 ms: las lecturas del juego y los avisos no se retrasan hasta 15,6 ms por espera.</summary>
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Barra de título del tema: oscura o clara y, en Windows 11, del mismo color que la cabecera.</summary>
    public static void SetTitleBar(IntPtr hwnd, bool dark, System.Windows.Media.Color caption, System.Windows.Media.Color text)
    {
        static int ColorRef(System.Windows.Media.Color c) => c.R | c.G << 8 | c.B << 16;
        int on = dark ? 1 : 0, back = ColorRef(caption), fore = ColorRef(text);
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 35, ref back, sizeof(int)); // DWMWA_CAPTION_COLOR (Windows 11; en Windows 10 no hace nada)
        DwmSetWindowAttribute(hwnd, 36, ref fore, sizeof(int)); // DWMWA_TEXT_COLOR
    }

    public static void SetExStyle(IntPtr hwnd, int add, int remove)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style = (style | (uint)add) & ~(long)(uint)remove;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }
}
