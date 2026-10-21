using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SimTelemetry.Diagnostics;
using SimTelemetry.UI;
using Vortice.DirectInput;

namespace SimTelemetry.Input;

/// <summary>Estado de un volante / pedalera en un instante.</summary>
public sealed class WheelDeviceState
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsWheel { get; init; }
    /// <summary>Nombre de cada uno de los 8 ejes (X, Y, Z, RX, RY, RZ, deslizador 1 y 2); null si el dispositivo no lo tiene.</summary>
    public string?[] AxisNames { get; init; } = new string?[WheelInput.AxisCount];
    public int[] Values { get; init; } = new int[WheelInput.AxisCount];
    /// <summary>Eje del volante (giro), -1 si no se identifica.</summary>
    public int SteeringAxis { get; init; } = -1;
}

/// <summary>
/// Lee volantes y pedales USB con DirectInput (la misma API que usan los juegos), también en segundo plano
/// y sin ningún juego abierto. Detecta conexiones y desconexiones automáticamente.
/// </summary>
public sealed class WheelInput : IDisposable
{
    public const int AxisCount = 8;
    public static readonly string[] AxisLabels = { "X", "Y", "Z", "RX", "RY", "RZ", "Desliz. 1", "Desliz. 2" };

    private sealed class Device
    {
        public required DeviceInstance Info;
        public required string Id;
        public required IDirectInputDevice8 Handle;
        public required string?[] AxisNames;
        public required int SteeringAxis;
        public required bool IsWheel;
        public int[] Values = new int[AxisCount];
        // Algunos volantes (G29) devuelven 32767 en cada eje hasta que se toca por primera vez.
        public bool[] Touched = new bool[AxisCount];
        public bool FirstPollDone;
        public int[] Initial = new int[AxisCount];
    }

    private readonly Func<PedalMapping> _mapping;
    private readonly PedalBuffer _buffer;
    private readonly Func<bool> _shouldFeed;
    private readonly List<Device> _devices = new();
    private IDirectInput8? _di;
    private IntPtr _hwnd;
    private Thread? _thread;
    private volatile bool _running;
    private volatile IReadOnlyList<WheelDeviceState> _snapshot = Array.Empty<WheelDeviceState>();
    private DateTime _nextEnumerate = DateTime.MinValue;
    private DateTime _lastChange = DateTime.UtcNow;
    private volatile bool _enumerateRequested = true;

    public WheelInput(Func<PedalMapping> mapping, PedalBuffer buffer, Func<bool> shouldFeed)
    {
        _mapping = mapping;
        _buffer = buffer;
        _shouldFeed = shouldFeed;
    }

    /// <summary>Dispositivos conectados con sus valores actuales.</summary>
    public IReadOnlyList<WheelDeviceState> Devices => _snapshot;

    public float Throttle { get; private set; }
    public float Brake { get; private set; }
    public float Clutch { get; private set; }
    public float Steering { get; private set; }

    /// <summary>Hay pedales calibrados y su dispositivo está conectado.</summary>
    public bool PedalsConnected { get; private set; }

    /// <summary>Nombre del volante o pedalera principal conectado (null si no hay ninguno).</summary>
    public string? MainDeviceName => _snapshot.OrderByDescending(d => d.IsWheel).FirstOrDefault()?.Name;

    public event Action? DevicesChanged;

    /// <summary>Windows avisó de que se conectó o desconectó un dispositivo (WM_DEVICECHANGE).</summary>
    public void RequestEnumerate() => _enumerateRequested = true;

    /// <summary>Arranca la lectura. <paramref name="hwnd"/> es una ventana de la app (DirectInput la necesita).</summary>
    public void Start(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Volante (DirectInput)" };
        _thread.Start();
    }

    private void Loop()
    {
        try
        {
            _di = DInput.DirectInput8Create();
        }
        catch (Exception ex)
        {
            Log.Error("DirectInput no disponible", ex);
            return;
        }

        while (_running)
        {
            try
            {
                // Buscar dispositivos es lento (~60 ms): sólo cuando Windows avisa de un cambio, y cada 30 s por si acaso.
                if (_enumerateRequested || DateTime.UtcNow >= _nextEnumerate)
                {
                    _enumerateRequested = false;
                    _nextEnumerate = DateTime.UtcNow.AddSeconds(30);
                    Enumerate();
                }
                PollAll();
            }
            catch (Exception ex)
            {
                Log.Error("Error leyendo el volante", ex);
                _enumerateRequested = true;
                Thread.Sleep(500);
            }
            // Sin tocar nada durante unos segundos basta con sondear a 20 Hz (ahorra CPU con la app en segundo plano).
            Thread.Sleep((DateTime.UtcNow - _lastChange).TotalSeconds < 5 ? 4 : 50);
        }

        foreach (var d in _devices) Release(d);
        _devices.Clear();
        _di?.Dispose();
    }

    /// <summary>Busca dispositivos nuevos y suelta los que se desconectaron.</summary>
    private void Enumerate()
    {
        var attached = _di!.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
        bool changed = false;

        foreach (var gone in _devices.Where(d => attached.All(a => a.InstanceGuid != d.Info.InstanceGuid)).ToList())
        {
            Log.Info($"Volante/pedales desconectado: {gone.Info.ProductName}");
            Release(gone);
            _devices.Remove(gone);
            changed = true;
        }

        foreach (var info in attached)
        {
            if (_devices.Any(d => d.Info.InstanceGuid == info.InstanceGuid)) continue;
            var device = Open(info);
            if (device == null) continue;
            _devices.Add(device);
            changed = true;
            Log.Info($"Volante/pedales detectado: {info.ProductName} ({info.Type}); ejes: " +
                     string.Join(", ", device.AxisNames.Select((n, i) => n == null ? null : $"{AxisLabels[i]}='{n}'").Where(s => s != null)));
        }

        if (changed)
        {
            PublishSnapshot();
            DevicesChanged?.Invoke();
        }
    }

    private Device? Open(DeviceInstance info)
    {
        IDirectInputDevice8? handle = null;
        try
        {
            handle = _di!.CreateDevice(info.InstanceGuid);
            handle.SetCooperativeLevel(_hwnd, CooperativeLevel.NonExclusive | CooperativeLevel.Background);
            handle.SetDataFormat<RawJoystickState>();
            handle.Acquire();

            var names = new string?[AxisCount];
            int sliders = 0;
            foreach (var obj in handle.GetObjects(DeviceObjectTypeFlags.Axis))
            {
                // Uso HID: 0x30 X, 0x31 Y, 0x32 Z, 0x33 RX, 0x34 RY, 0x35 RZ, 0x36 deslizador, 0x37 dial.
                int slot = obj.Usage switch
                {
                    0x30 => 0, 0x31 => 1, 0x32 => 2, 0x33 => 3, 0x34 => 4, 0x35 => 5,
                    0x36 or 0x37 => sliders < 2 ? 6 + sliders++ : -1,
                    _ => -1,
                };
                if (slot >= 0) names[slot] = string.IsNullOrWhiteSpace(obj.Name) ? AxisLabels[slot] : obj.Name.Trim();
            }

            bool isWheel = info.Type == DeviceType.Driving;
            int steering = Array.FindIndex(names, n => n != null && (n.Contains("wheel", StringComparison.OrdinalIgnoreCase)
                                                                     || n.Contains("steer", StringComparison.OrdinalIgnoreCase)
                                                                     || n.Contains("volante", StringComparison.OrdinalIgnoreCase)));
            if (steering < 0 && isWheel && names[0] != null) steering = 0;

            return new Device { Info = info, Id = info.InstanceGuid.ToString(), Handle = handle, AxisNames = names, SteeringAxis = steering, IsWheel = isWheel };
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo abrir {info.ProductName}", ex);
            handle?.Dispose();
            return null;
        }
    }

    private static void Release(Device d)
    {
        try { d.Handle.Unacquire(); } catch { }
        d.Handle.Dispose();
    }

    private void PollAll()
    {
        bool changed = false;
        foreach (var d in _devices)
        {
            try
            {
                if (d.Handle.Poll().Failure) d.Handle.Acquire();
                var s = d.Handle.GetCurrentJoystickState();
                changed |= Set(d, 0, s.X) | Set(d, 1, s.Y) | Set(d, 2, s.Z)
                         | Set(d, 3, s.RotationX) | Set(d, 4, s.RotationY) | Set(d, 5, s.RotationZ)
                         | Set(d, 6, s.Sliders[0]) | Set(d, 7, s.Sliders[1]);
                if (!d.FirstPollDone)
                {
                    d.FirstPollDone = true;
                    Array.Copy(d.Values, d.Initial, AxisCount);
                }
                for (int a = 0; a < AxisCount; a++)
                    if (d.Values[a] != d.Initial[a]) d.Touched[a] = true;
            }
            catch (Exception)
            {
                // Desconectado: lo retiramos en la próxima búsqueda.
                _enumerateRequested = true;
            }
        }
        if (changed)
        {
            _lastChange = DateTime.UtcNow;
            PublishSnapshot();
        }

        var map = _mapping();
        bool brakeOk = TryRead(map.Brake, out float brk);
        bool thrOk = TryRead(map.Throttle, out float thr);
        TryRead(map.Clutch, out float clu);
        Brake = brk;
        Throttle = thr;
        Clutch = clu;
        PedalsConnected = brakeOk && thrOk;

        var wheel = _snapshot.FirstOrDefault(x => x.SteeringAxis >= 0);
        Steering = wheel != null ? Math.Clamp((wheel.Values[wheel.SteeringAxis] - 32767.5f) / 32767.5f, -1, 1) : 0;

        if (PedalsConnected && _shouldFeed()) _buffer.Add(thr, brk, clu, 0, 0, vehicleData: false);
    }

    private static bool Set(Device d, int axis, int value)
    {
        if (d.Values[axis] == value) return false;
        d.Values[axis] = value;
        return true;
    }

    private void PublishSnapshot()
    {
        _snapshot = _devices.Select(d => new WheelDeviceState
        {
            Id = d.Id,
            Name = d.Info.ProductName.Trim(),
            IsWheel = d.IsWheel,
            AxisNames = (string?[])d.AxisNames.Clone(),
            Values = (int[])d.Values.Clone(),
            SteeringAxis = d.SteeringAxis,
        }).ToList();
    }

    /// <summary>Valor 0..1 de un pedal calibrado. Amplía el recorrido si el pedal llega más allá de lo calibrado.</summary>
    private bool TryRead(PedalAxis axis, out float value)
    {
        value = 0;
        if (!axis.IsSet) return false;
        Device? d = null;
        foreach (var x in _devices)
            if (x.Id == axis.DeviceId) { d = x; break; }
        if (d == null) return false;
        int raw = d.Values[axis.Axis];
        if (!d.Touched[axis.Axis] && raw == 32767) return true; // aún sin datos del pedal: lo damos por suelto
        if (Math.Abs(raw - axis.Released) > Math.Abs(axis.Pressed - axis.Released)
            && Math.Sign(raw - axis.Released) == Math.Sign(axis.Pressed - axis.Released))
            axis.Pressed = raw;
        float v = (raw - axis.Released) / (float)(axis.Pressed - axis.Released);
        // Pequeña zona muerta en los extremos para que el pedal suelto marque 0 y a fondo marque 100.
        value = Math.Clamp((v - 0.015f) / 0.97f, 0, 1);
        return true;
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
    }
}
