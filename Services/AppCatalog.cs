using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DynamicIsland.Services;

/// <summary>
/// Daftar semua aplikasi (sama dengan "All apps" di Start: shell:AppsFolder, termasuk aplikasi Store)
/// beserta ikonnya, untuk menu dock. Dibaca di thread latar; ikon dimuat sekali lalu disimpan.
/// </summary>
public static class AppCatalog
{
    public sealed class App
    {
        public string Name = "";
        public string Id = "";           // path di shell:AppsFolder
        public BitmapSource? Icon;       // diisi belakangan
        public System.Windows.Media.Color Dominant = System.Windows.Media.Color.FromRgb(0x2C, 0x2C, 0x2E); // warna latar kotak ikon (gaya dock)
    }

    static List<App> _apps = new();
    static DateTime _loadedAt = DateTime.MinValue;
    static int _loading;

    /// <summary>Daftar terakhir (bisa kosong kalau belum pernah dimuat).</summary>
    public static IReadOnlyList<App> Apps => _apps;

    /// <summary>Dipanggil di thread UI saat daftar / ikon selesai dimuat.</summary>
    public static event Action? Updated;

    /// <summary>Muat ulang di latar kalau sudah lebih dari 10 menit (aplikasi baru dipasang / dihapus).</summary>
    public static void EnsureLoaded(Dispatcher ui, int iconSize)
    {
        if (DateTime.Now - _loadedAt < TimeSpan.FromMinutes(10) && _apps.Count > 0) return;
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        var t = new Thread(() =>
        {
            try
            {
                var fresh = Read();
                if (fresh.Count > 0)
                {
                    // ikon yang sudah ada dipakai ulang
                    var old = _apps.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                                   .ToDictionary(g => g.Key, g => g.First().Icon, StringComparer.OrdinalIgnoreCase);
                    foreach (var a in fresh)
                        if (old.TryGetValue(a.Id, out var ic) && ic != null) { a.Icon = ic; a.Dominant = IconLoader.DominantColor(ic); }
                    _apps = fresh;
                    _loadedAt = DateTime.Now;
                    ui.BeginInvoke(new Action(() => Updated?.Invoke()));
                    // ikon dimuat bertahap; tampilan diberi tahu tiap 24 ikon
                    int n = 0;
                    foreach (var a in fresh)
                    {
                        if (a.Icon != null) continue;
                        a.Icon = IconLoader.LoadIcon("shell:AppsFolder\\" + a.Id, iconSize);
                        if (a.Icon != null) a.Dominant = IconLoader.DominantColor(a.Icon);
                        if (++n % 24 == 0) ui.BeginInvoke(new Action(() => Updated?.Invoke()));
                    }
                    ui.BeginInvoke(new Action(() => Updated?.Invoke()));
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref _loading, 0); }
        }) { IsBackground = true, Name = "AppCatalog", Priority = ThreadPriority.BelowNormal };
        t.SetApartmentState(ApartmentState.STA); // objek shell Windows butuh STA
        t.Start();
    }

    static List<App> Read()
    {
        var list = new List<App>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return list;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string path = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new App { Name = name, Id = path });
            }
        }
        catch { }
        return list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                   .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void Launch(App a)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + a.Id) { UseShellExecute = true }); } catch { }
    }
}
