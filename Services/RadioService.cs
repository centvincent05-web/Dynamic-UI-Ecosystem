using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace DynamicIsland.Services;

/// <summary>
/// Wi-Fi dan Bluetooth lewat API resmi Windows (Windows.Devices.Radios),
/// plus nama jaringan yang sedang tersambung. Berbasis event.
/// </summary>
public sealed class RadioService
{
    readonly Dispatcher _ui;
    Radio? _wifi, _bluetooth;
    System.Collections.Generic.List<Radio> _all = new();

    public event Action? Changed;

    public bool HasWifi => _wifi != null;
    public bool HasBluetooth => _bluetooth != null;
    public bool WifiOn => _wifi?.State == RadioState.On;
    public bool BluetoothOn => _bluetooth?.State == RadioState.On;
    public string NetworkName { get; private set; } = "";

    public RadioService(Dispatcher ui) => _ui = ui;

    public async Task InitAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access == RadioAccessStatus.Allowed)
            {
                var radios = await Radio.GetRadiosAsync();
                _all = radios.ToList();
                foreach (var r in _all.Where(r => r.Kind != RadioKind.WiFi && r.Kind != RadioKind.Bluetooth))
                    r.StateChanged += (_, _) => Post(Raise);
                _wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
                _bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (_wifi != null) _wifi.StateChanged += (_, _) => Post(Raise);
                if (_bluetooth != null) _bluetooth.StateChanged += (_, _) => Post(Raise);
            }
        }
        catch { }

        try { NetworkInformation.NetworkStatusChanged += _ => Post(() => { RefreshNetworkName(); Raise(); }); } catch { }
        Post(() => { RefreshNetworkName(); Raise(); });
    }

    void RefreshNetworkName()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null) { NetworkName = ""; return; }
            if (profile.IsWlanConnectionProfile)
                NetworkName = profile.WlanConnectionProfileDetails?.GetConnectedSsid() ?? profile.ProfileName ?? "";
            else
                NetworkName = profile.ProfileName ?? "";
        }
        catch { NetworkName = ""; }
    }

    /// <summary>Mode pesawat versi aplikasi: semua radio (Wi-Fi, Bluetooth, seluler) dimatikan.</summary>
    public bool AirplaneOn => _all.Count > 0 && _all.All(r => r.State != RadioState.On);

    public async void ToggleAirplane()
    {
        if (_all.Count == 0) { OpenSettings("ms-settings:network-airplanemode"); return; }
        var target = AirplaneOn ? RadioState.On : RadioState.Off;
        foreach (var r in _all)
            try { await r.SetStateAsync(target); } catch { }
        Raise();
    }

    public async void ToggleWifi()
    {
        if (_wifi == null) { OpenSettings("ms-settings:network-wifi"); return; }
        try { await _wifi.SetStateAsync(WifiOn ? RadioState.Off : RadioState.On); } catch { }
        Raise();
    }

    public async void ToggleBluetooth()
    {
        if (_bluetooth == null) { OpenSettings("ms-settings:bluetooth"); return; }
        try { await _bluetooth.SetStateAsync(BluetoothOn ? RadioState.Off : RadioState.On); } catch { }
        Raise();
    }

    /// <summary>Buka halaman pengaturan Windows (ms-settings:...) atau pusat notifikasi.</summary>
    public static void OpenSettings(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    void Raise() => Changed?.Invoke();

    void Post(Action a)
    {
        if (_ui.CheckAccess()) a();
        else _ui.BeginInvoke(a);
    }
}
