using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFi;
using Windows.Security.Credentials;

namespace DynamicIsland.Services;

public sealed record WifiItem(string Ssid, int Bars, bool Secured, bool Connected, WiFiAvailableNetwork Network);
public sealed record BluetoothItem(string Name, bool Connected, string Id);

/// <summary>Daftar jaringan Wi-Fi di sekitar dan perangkat Bluetooth yang sudah dipasangkan.</summary>
public sealed class NetworkListService
{
    WiFiAdapter? _adapter;

    public async Task<(List<WifiItem> items, string? error)> ScanWifiAsync()
    {
        try
        {
            var access = await WiFiAdapter.RequestAccessAsync();
            if (access != WiFiAccessStatus.Allowed)
                return (new(), "Windows menolak akses daftar Wi-Fi. Nyalakan Lokasi di Pengaturan → Privasi → Lokasi.");

            var adapters = await WiFiAdapter.FindAllAdaptersAsync();
            _adapter = adapters.FirstOrDefault();
            if (_adapter == null) return (new(), "Adapter Wi-Fi tidak ditemukan.");

            await _adapter.ScanAsync();
            string connected = "";
            try
            {
                var profile = await _adapter.NetworkAdapter.GetConnectedProfileAsync();
                connected = profile?.ProfileName ?? "";
            }
            catch { }

            var items = _adapter.NetworkReport.AvailableNetworks
                .Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
                .GroupBy(n => n.Ssid)
                .Select(g => g.OrderByDescending(n => n.SignalBars).First())
                .Select(n => new WifiItem(
                    n.Ssid,
                    n.SignalBars,
                    n.SecuritySettings.NetworkAuthenticationType != Windows.Networking.Connectivity.NetworkAuthenticationType.Open80211,
                    n.Ssid == connected,
                    n))
                .OrderByDescending(i => i.Connected)
                .ThenByDescending(i => i.Bars)
                .Take(12)
                .ToList();
            return (items, null);
        }
        catch (Exception ex)
        {
            return (new(), "Gagal memindai Wi-Fi: " + ex.Message);
        }
    }

    /// <summary>Sambungkan. Tanpa password dicoba memakai profil yang sudah tersimpan di Windows.</summary>
    public async Task<(bool ok, bool needPassword, string message)> ConnectAsync(WifiItem item, string? password)
    {
        if (_adapter == null) return (false, false, "Adapter Wi-Fi tidak ditemukan.");
        try
        {
            WiFiConnectionResult result = password == null
                ? await _adapter.ConnectAsync(item.Network, WiFiReconnectionKind.Automatic)
                : await _adapter.ConnectAsync(item.Network, WiFiReconnectionKind.Automatic, new PasswordCredential { Password = password });

            return result.ConnectionStatus switch
            {
                WiFiConnectionStatus.Success => (true, false, "Tersambung ke " + item.Ssid),
                WiFiConnectionStatus.InvalidCredential => (false, true, password == null ? "Masukkan password" : "Password salah"),
                WiFiConnectionStatus.Timeout => (false, false, "Waktu habis, coba lagi"),
                WiFiConnectionStatus.NetworkNotAvailable => (false, false, "Jaringan tidak tersedia"),
                WiFiConnectionStatus.AccessRevoked => (false, false, "Akses ditolak Windows"),
                _ => (false, item.Secured && password == null, item.Secured && password == null ? "Masukkan password" : "Gagal tersambung"),
            };
        }
        catch (Exception ex)
        {
            return (false, false, "Gagal: " + ex.Message);
        }
    }

    public void Disconnect()
    {
        try { _adapter?.Disconnect(); } catch { }
    }

    public async Task<(List<BluetoothItem> items, string? error)> PairedBluetoothAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            var list = new List<BluetoothItem>();
            foreach (var d in devices)
            {
                bool connected = false;
                try
                {
                    using var bd = await BluetoothDevice.FromIdAsync(d.Id);
                    connected = bd?.ConnectionStatus == BluetoothConnectionStatus.Connected;
                }
                catch { }
                list.Add(new BluetoothItem(string.IsNullOrWhiteSpace(d.Name) ? "Perangkat" : d.Name, connected, d.Id));
            }
            return (list.OrderByDescending(i => i.Connected).ThenBy(i => i.Name).ToList(), null);
        }
        catch (Exception ex)
        {
            return (new(), "Gagal membaca perangkat Bluetooth: " + ex.Message);
        }
    }
}
