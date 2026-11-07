using System;
using System.Management;
using System.Threading.Tasks;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>
/// Kecerahan layar. Layar bawaan laptop lewat WMI; monitor eksternal lewat DDC/CI
/// (kalau monitornya mendukung). Perubahan dikirim di thread terpisah dan digabung,
/// jadi scroll cepat tidak bikin patah-patah.
/// </summary>
public sealed class BrightnessService : IDisposable
{
    readonly Dispatcher _ui;
    readonly object _lock = new();
    ManagementEventWatcher? _watcher;
    ManagementObject? _wmiMethods;
    Native.PHYSICAL_MONITOR[]? _physical;
    uint _ddcMin, _ddcMax;
    bool _useDdc;
    int? _pending;
    bool _applying;
    long _lastSetTick;

    public bool Available { get; private set; }
    public int Current { get; private set; } = 50;

    /// <summary>Kecerahan diubah dari luar (tombol Fn, pengaturan Windows).</summary>
    public event Action? ChangedExternally;
    public event Action? Ready;

    public BrightnessService(Dispatcher ui) => _ui = ui;

    /// <summary>Inisialisasi di background (WMI bisa lambat beberapa ratus ms).</summary>
    public void InitInBackground() => Task.Run(() =>
    {
        InitWmi();
        if (!Available) InitDdc();
        _ui.BeginInvoke(new Action(() => Ready?.Invoke()));
    });

    void InitWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in searcher.Get())
            {
                Current = Convert.ToInt32(o["CurrentBrightness"]);
                Available = true;
                break;
            }
            if (!Available) return;

            using var methods = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in methods.Get()) { _wmiMethods = o; break; }

            _watcher = new ManagementEventWatcher(new ManagementScope("root\\WMI"),
                new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _watcher.EventArrived += (_, e) =>
            {
                try
                {
                    int v = Convert.ToInt32(e.NewEvent["Brightness"]);
                    // abaikan event yang berasal dari scroll kita sendiri
                    if (Environment.TickCount64 - _lastSetTick < 800) return;
                    if (v == Current) return;
                    Current = v;
                    _ui.BeginInvoke(new Action(() => ChangedExternally?.Invoke()));
                }
                catch { }
            };
            _watcher.Start();
        }
        catch
        {
            Available = _wmiMethods != null;
        }
    }

    void InitDdc()
    {
        try
        {
            IntPtr hmon = Native.MonitorFromPoint(new Native.POINT(), Native.MONITOR_DEFAULTTOPRIMARY);
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint count) || count == 0) return;
            var arr = new Native.PHYSICAL_MONITOR[count];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(hmon, count, arr)) return;
            if (Native.GetMonitorBrightness(arr[0].hPhysicalMonitor, out _ddcMin, out uint cur, out _ddcMax) && _ddcMax > _ddcMin)
            {
                _physical = arr;
                _useDdc = true;
                Available = true;
                Current = (int)Math.Round((cur - _ddcMin) * 100.0 / (_ddcMax - _ddcMin));
            }
            else
            {
                Native.DestroyPhysicalMonitors(count, arr);
            }
        }
        catch { }
    }

    public void Set(int value)
    {
        if (!Available) return;
        value = Math.Clamp(value, 0, 100);
        Current = value;
        _lastSetTick = Environment.TickCount64;

        lock (_lock)
        {
            _pending = value;
            if (_applying) return;
            _applying = true;
        }
        Task.Run(ApplyLoop);
    }

    void ApplyLoop()
    {
        while (true)
        {
            int v;
            lock (_lock)
            {
                if (_pending == null) { _applying = false; return; }
                v = _pending.Value;
                _pending = null;
            }

            try
            {
                if (_useDdc && _physical != null)
                {
                    uint raw = (uint)Math.Round(_ddcMin + v * (_ddcMax - _ddcMin) / 100.0);
                    Native.SetMonitorBrightness(_physical[0].hPhysicalMonitor, raw);
                }
                else
                {
                    _wmiMethods?.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)v });
                }
            }
            catch { }
            _lastSetTick = Environment.TickCount64;
        }
    }

    public void Dispose()
    {
        try { _watcher?.Stop(); _watcher?.Dispose(); } catch { }
        try { _wmiMethods?.Dispose(); } catch { }
        try { if (_physical != null) Native.DestroyPhysicalMonitors((uint)_physical.Length, _physical); } catch { }
    }
}
