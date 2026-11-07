using System;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;

namespace DynamicIsland.Services;

/// <summary>
/// HP yang dipasangkan lewat Bluetooth (untuk bagian Phone Link di menu dock): nama, tersambung atau tidak,
/// dan sisa baterai kalau HP melaporkannya ke Windows. Isi pesan / foto Phone Link tidak bisa dibaca aplikasi lain.
/// </summary>
public static class PhoneStatus
{
    public sealed record Info(string Name, bool Connected, int? Battery);

    const string ClassicProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    const string ContainerKey = "System.Devices.Aep.ContainerId";
    const string MajorClassKey = "System.Devices.Aep.Bluetooth.Cod.Major";
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    /// <summary>HP pertama yang dipasangkan (yang tersambung didahulukan). Null kalau tidak ada.</summary>
    public static async Task<Info?> QueryAsync()
    {
        try
        {
            string aqs = $"System.Devices.Aep.ProtocolId:=\"{ClassicProtocol}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
            var list = await DeviceInformation.FindAllAsync(aqs, new[] { IsConnectedKey, ContainerKey, MajorClassKey },
                DeviceInformationKind.AssociationEndpoint);
            DeviceInformation? best = null;
            bool bestConnected = false;
            foreach (var d in list)
            {
                int major = -1;
                if (d.Properties.TryGetValue(MajorClassKey, out var m) && m != null) { try { major = Convert.ToInt32(m); } catch { } }
                string n = d.Name.ToLowerInvariant();
                bool phone = major == 2 || n.Contains("phone") || n.Contains("galaxy") || n.Contains("iphone") || n.Contains("redmi")
                             || n.Contains("pixel") || n.Contains("oppo") || n.Contains("vivo") || n.Contains("realme") || n.Contains("xiaomi");
                if (!phone) continue;
                bool connected = d.Properties.TryGetValue(IsConnectedKey, out var c) && c is bool b && b;
                if (best == null || (connected && !bestConnected)) { best = d; bestConnected = connected; }
            }
            if (best == null) return null;

            int? battery = null;
            if (best.Properties.TryGetValue(ContainerKey, out var g) && g is Guid container && container != Guid.Empty)
            {
                var devs = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"",
                    new[] { BatteryKey }, DeviceInformationKind.Device);
                foreach (var dev in devs)
                {
                    if (!dev.Properties.TryGetValue(BatteryKey, out var v) || v == null) continue;
                    try { int pct = Convert.ToInt32(v); if (pct is >= 0 and <= 100) { battery = pct; break; } } catch { }
                }
            }
            return new Info(best.Name, bestConnected, battery);
        }
        catch { return null; }
    }
}
