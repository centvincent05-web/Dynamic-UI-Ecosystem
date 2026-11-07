using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Enumeration;

namespace DynamicIsland.Services;

/// <summary>
/// Memberi tahu saat perangkat Bluetooth yang sudah dipasangkan (earbuds, mouse, keyboard) tersambung,
/// beserta sisa baterainya kalau Windows mengetahuinya (sama seperti yang tampil di Pengaturan → Bluetooth).
/// Berbasis event dari Windows, tanpa polling.
/// </summary>
public sealed class BluetoothWatcher
{
    const string ClassicProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    const string LeProtocol = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";
    const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    const string ContainerKey = "System.Devices.Aep.ContainerId";
    const string MajorClassKey = "System.Devices.Aep.Bluetooth.Cod.Major";
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    static readonly string[] Props = { IsConnectedKey, ContainerKey, MajorClassKey };

    sealed class Device
    {
        public string Name = "";
        public Guid Container;
        public int Major = -1;
        public bool Connected;
        public long DisconnectedAt;
    }

    readonly Dispatcher _dispatcher;
    readonly object _lock = new();
    readonly Dictionary<string, Device> _devices = new();
    readonly Dictionary<Guid, long> _announced = new(); // perangkat dual-mode muncul 2x (classic + LE): umumkan sekali
    readonly List<DeviceWatcher> _watchers = new();
    int _enumerating;

    /// <summary>(nama, baterai %, ikon) — di thread UI.</summary>
    public event Action<string, int?, string>? Connected;

    public BluetoothWatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool Running => _watchers.Count > 0;

    public void Start()
    {
        if (_watchers.Count > 0) return;
        try
        {
            foreach (var proto in new[] { ClassicProtocol, LeProtocol })
            {
                string aqs = $"System.Devices.Aep.ProtocolId:=\"{proto}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
                var w = DeviceInformation.CreateWatcher(aqs, Props, DeviceInformationKind.AssociationEndpoint);
                w.Added += OnAdded;
                w.Updated += OnUpdated;
                w.Removed += (_, u) => { lock (_lock) _devices.Remove(u.Id); };
                w.EnumerationCompleted += (_, _) => { lock (_lock) _enumerating--; };
                _watchers.Add(w);
            }
            lock (_lock) _enumerating = _watchers.Count;
            foreach (var w in _watchers) w.Start();
        }
        catch { Stop(); }
    }

    public void Stop()
    {
        foreach (var w in _watchers)
        {
            try
            {
                w.Added -= OnAdded;
                w.Updated -= OnUpdated;
                if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop();
            }
            catch { }
        }
        _watchers.Clear();
        lock (_lock) _devices.Clear();
    }

    void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        var d = new Device { Name = info.Name };
        Read(d, info.Properties);
        bool announce;
        lock (_lock)
        {
            _devices[info.Id] = d;
            announce = _enumerating <= 0 && d.Connected; // baru dipasangkan & langsung tersambung
        }
        if (announce) Announce(d);
    }

    void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate u)
    {
        Device? d;
        bool announce = false;
        lock (_lock)
        {
            if (!_devices.TryGetValue(u.Id, out d)) return;
            bool was = d.Connected;
            Read(d, u.Properties);
            long now = Environment.TickCount64;
            if (was && !d.Connected) d.DisconnectedAt = now;
            // mouse/keyboard BLE kadang putus-sambung sendiri saat diam: abaikan yang < 15 detik
            if (!was && d.Connected && _enumerating <= 0 && (d.DisconnectedAt == 0 || now - d.DisconnectedAt > 15000))
                announce = true;
        }
        if (announce) Announce(d);
    }

    static void Read(Device d, IReadOnlyDictionary<string, object> p)
    {
        if (p.TryGetValue(IsConnectedKey, out var c) && c is bool b) d.Connected = b;
        if (p.TryGetValue(ContainerKey, out var g) && g is Guid guid) d.Container = guid;
        if (p.TryGetValue(MajorClassKey, out var m) && m != null) { try { d.Major = Convert.ToInt32(m); } catch { } }
    }

    async void Announce(Device d)
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (d.Container != Guid.Empty)
            {
                if (_announced.TryGetValue(d.Container, out var at) && now - at < 15000) return;
                _announced[d.Container] = now;
            }
        }
        int? battery = null;
        try
        {
            // persen baterai baru diisi Windows beberapa detik setelah tersambung
            await Task.Delay(1500);
            battery = await ReadBattery(d.Container);
            if (battery == null) { await Task.Delay(2500); battery = await ReadBattery(d.Container); }
        }
        catch { }
        string name = d.Name, glyph = GlyphOf(d);
        _ = _dispatcher.BeginInvoke(new Action(() => Connected?.Invoke(name, battery, glyph)));
    }

    static async Task<int?> ReadBattery(Guid container)
    {
        if (container == Guid.Empty) return null;
        var list = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"",
            new[] { BatteryKey }, DeviceInformationKind.Device);
        foreach (var dev in list)
        {
            if (!dev.Properties.TryGetValue(BatteryKey, out var v) || v == null) continue;
            try
            {
                int pct = Convert.ToInt32(v);
                if (pct is >= 0 and <= 100) return pct;
            }
            catch { }
        }
        return null;
    }

    static string GlyphOf(Device d)
    {
        string n = d.Name.ToLowerInvariant();
        bool Has(params string[] words) => Array.Exists(words, w => n.Contains(w));
        if (d.Major == 4 || Has("bud", "pod", "head", "ear", "wh-", "wf-", "speaker", "jbl", "tws")) return ""; // headphone
        if (Has("keyboard", "keys")) return "";
        if (Has("mouse", "mx ", "trackpad")) return "";
        if (d.Major == 2 || Has("phone", "galaxy", "iphone", "redmi", "pixel")) return "";
        if (Has("controller", "gamepad", "xbox", "dualsense")) return "";
        if (d.Major == 5) return "";
        return ""; // bluetooth
    }
}
