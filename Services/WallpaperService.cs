using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>
/// Fase 1 — pengaturan wallpaper statis. Wallpaper dipasang lewat fitur Windows sendiri (IDesktopWallpaper),
/// jadi tidak ada jendela yang terus hidup dan CPU nol di antara pergantian.
/// - Mode 0: satu gambar (opsional gambar lain untuk monitor lain)
/// - Mode 1: folder, berganti tiap X menit (urut/acak, opsional beda tiap monitor)
/// - Mode 2: siang/malam (ikut matahari di kota cuaca, atau jam tetap)
/// Tambahan: efek fade, lock screen, warna aksen & ambient dari wallpaper.
/// Semua bawaan MATI: selama WallpaperEnabled = false, wallpaper pengguna tidak disentuh sama sekali.
/// </summary>
public static class WallpaperService
{
    static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp" };
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly Random Rng = new();

    static Dispatcher? _ui;
    static DispatcherTimer? _timer;
    static DispatcherTimer? _debounce;
    static bool _busy, _pendingForce;
    static bool _lastAccentOpt;
    static string _geoBusyCity = "";

    /// <summary>Warna utama wallpaper monitor utama (null = belum diketahui / fitur mati). Dipakai ambient light island.</summary>
    public static Color? WallColor { get; private set; }
    /// <summary>Dipanggil (di thread UI) saat wallpaper / warnanya berubah.</summary>
    public static event Action? Changed;
    /// <summary>Pemakai warna wallpaper lain (mis. widget "ikut wallpaper"). True = tetap hitung warna wallpaper.</summary>
    public static Func<bool>? ColorDemand { get; set; }

    static bool WantColor(Settings s) => s.AccentFromWallpaper || s.AmbientFromWallpaper || ColorDemand?.Invoke() == true;

    /// <summary>Keterangan singkat untuk Pengaturan (mis. hasil lock screen, error).</summary>
    public static string Status { get; private set; } = "";
    public static string LockStatus { get; private set; } = "";

    public static void Start(Dispatcher ui)
    {
        if (_ui != null) return;
        _ui = ui;
        _lastAccentOpt = Settings.Current.AccentFromWallpaper;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui);
        _timer.Tick += (_, _) => { _timer.Stop(); Evaluate(false); };
        _debounce = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Evaluate(false); };
        Settings.Changed += () => { _debounce.Stop(); _debounce.Start(); };
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) ui.BeginInvoke(new Action(() => Evaluate(false)));
        };
        // wallpaper diganti dari luar (Pengaturan Windows, dll.): hitung ulang warnanya
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.Desktop) return;
            ui.BeginInvoke(new Action(() =>
            {
                var st = Settings.Current;
                if (!_busy && WantColor(st)) _ = RefreshColorAsync();
            }));
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            ui.BeginInvoke(new Action(() => { if (Settings.Current.WallpaperEnabled) Evaluate(true); }));
        // jangan berebut CPU dengan startup app
        var first = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(3) };
        first.Tick += (_, _) => { first.Stop(); Evaluate(false); };
        first.Start();
    }

    /// <summary>Mode folder: langsung ganti ke gambar berikutnya.</summary>
    public static void Next()
    {
        Settings.Current.WallpaperLastChange = DateTime.MinValue;
        Evaluate(false);
    }

    /// <summary>Pasang ulang sekarang (mis. setelah memilih gambar yang sama lagi).</summary>
    public static void ApplyNow() => Evaluate(true);

    // =====================================================================
    // Jadwal
    // =====================================================================

    static void Evaluate(bool force)
    {
        if (_ui == null) return;
        var s = Settings.Current;
        HandleAccentToggle(s);
        if (!s.WallpaperEnabled)
        {
            // fitur wallpaper mati: tidak memasang apa pun, tapi warna aksen/ambient tetap bisa ikut wallpaper Windows yang sekarang
            _timer?.Stop();
            if (WantColor(s)) { if (WallColor == null) _ = RefreshColorAsync(); }
            else if (WallColor != null) { WallColor = null; Changed?.Invoke(); }
            return;
        }
        if (_busy) { _pendingForce |= force; _timer!.Interval = TimeSpan.FromSeconds(2); _timer.Start(); return; }

        DateTime now = DateTime.Now;
        TimeSpan next = TimeSpan.FromMinutes(15); // cek ulang paling lama tiap 15 menit (aman setelah sleep, jam diubah, dll.)
        string[]? perMonitor = null; // null = tidak ada yang perlu dipasang
        string key = "";
        int fit = Math.Clamp(s.WallpaperFit, 0, 5);

        switch (s.WallpaperMode)
        {
            case 1:
            {
                var files = ListFolder(s.WallpaperFolder);
                if (files.Count == 0) { SetStatus(s.WallpaperFolder.Length == 0 ? "Pilih folder dulu." : "Folder ini tidak berisi gambar."); break; }
                int interval = Math.Clamp(s.WallpaperIntervalMin, 1, 7 * 24 * 60);
                key = $"1|{s.WallpaperFolder}|{s.WallpaperPerMonitor}|{fit}";
                DateTime due = s.WallpaperLastChange == DateTime.MinValue ? now : s.WallpaperLastChange.AddMinutes(interval);
                bool settingsChanged = !s.WallpaperLastApplied.StartsWith(key + "|", StringComparison.Ordinal);
                if (force || settingsChanged || now >= due || due > now.AddMinutes(interval + 1))
                {
                    // force/perubahan pengaturan saja (bukan waktunya ganti) → pasang gambar yang sama lagi
                    bool advance = !force && (now >= due || settingsChanged && s.WallpaperIndex < 0);
                    perMonitor = PickFromFolder(files, s, advance);
                    key += "|" + string.Join("|", perMonitor);
                    next = Min(next, TimeSpan.FromMinutes(interval));
                }
                else next = Min(next, due - now);
                break;
            }
            case 2:
            {
                bool day = IsDay(now, s, out DateTime boundary);
                string img = day ? s.WallpaperDayImage : s.WallpaperNightImage;
                next = Min(next, boundary - now);
                if (!File.Exists(img)) { SetStatus(day ? "Pilih gambar siang dulu." : "Pilih gambar malam dulu."); break; }
                key = $"2|{img}|{fit}";
                if (force || key != s.WallpaperLastApplied) perMonitor = new[] { img };
                break;
            }
            default:
            {
                string img = s.WallpaperImage, other = s.WallpaperImageOther;
                if (!File.Exists(img)) { SetStatus("Pilih gambar dulu."); break; }
                if (!File.Exists(other)) other = img;
                key = $"0|{img}|{other}|{fit}";
                if (force || key != s.WallpaperLastApplied) perMonitor = new[] { img, other };
                next = TimeSpan.FromHours(6);
                break;
            }
        }

        if (perMonitor != null) _ = ApplyAsync(perMonitor, fit, key);
        else if (WallColor == null && WantColor(s)) _ = RefreshColorAsync();

        if (next < TimeSpan.FromSeconds(5)) next = TimeSpan.FromSeconds(5);
        _timer!.Stop();
        _timer.Interval = next;
        _timer.Start();
    }

    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    static void SetStatus(string text) { Status = L.T(text); }

    public static List<string> ListFolder(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return new();
            return Directory.EnumerateFiles(folder)
                .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new(); }
    }

    /// <summary>Gambar untuk tiap monitor (indeks 0 = monitor utama). Panjang array 1 = semua monitor sama.</summary>
    static string[] PickFromFolder(List<string> files, Settings s, bool advance)
    {
        int count = s.WallpaperPerMonitor ? Math.Max(1, MonitorCountQuick()) : 1;
        int idx = s.WallpaperIndex;
        if (advance || idx < 0 || idx >= files.Count)
        {
            if (s.WallpaperShuffle && files.Count > 1)
            {
                int pick;
                do pick = Rng.Next(files.Count); while (pick == idx);
                idx = pick;
            }
            else idx = idx < 0 ? 0 : (idx + count) % files.Count;
        }
        s.WallpaperIndex = idx;
        var result = new string[count];
        for (int i = 0; i < count; i++)
            result[i] = files[(idx + (s.WallpaperShuffle && i > 0 ? Rng.Next(1, files.Count) : i)) % files.Count];
        return result;
    }

    static int MonitorCountQuick()
    {
        try { return GetSystemMetrics(80); } catch { return 1; } // SM_CMONITORS
    }

    // =====================================================================
    // Siang / malam
    // =====================================================================

    /// <summary>Sekarang siang? boundary = waktu pergantian berikutnya.</summary>
    static bool IsDay(DateTime now, Settings s, out DateTime boundary)
    {
        TimeSpan rise = TimeSpan.FromHours(Math.Clamp(s.WallpaperDayHour, 0, 23));
        TimeSpan set = TimeSpan.FromHours(Math.Clamp(s.WallpaperNightHour, 0, 23));
        if (s.WallpaperBySun)
        {
            EnsureGeo(s);
            if (s.WallpaperLat is double lat && s.WallpaperLon is double lon &&
                SunTimes(now.Date, lat, lon, out TimeSpan r, out TimeSpan st))
            {
                rise = r;
                set = st;
            }
        }
        DateTime today = now.Date;
        DateTime r0 = today + rise, s0 = today + set;
        bool day = rise < set ? now >= r0 && now < s0 : now >= r0 || now < s0; // jam tetap bisa "terbalik"
        if (day) boundary = now < s0 ? s0 : s0.AddDays(1);
        else boundary = now < r0 ? r0 : r0.AddDays(1);
        return day;
    }

    /// <summary>Cari koordinat kota (dari Pengaturan → Kalender, cuaca) sekali saja, lalu simpan.</summary>
    static void EnsureGeo(Settings s)
    {
        string city = (s.WeatherCity ?? "").Trim();
        if (city.Length == 0)
        {
            SetStatus("Isi kota di Pengaturan → Kalender, cuaca & timer supaya jam matahari terbit/terbenam tepat. Sementara memakai jam tetap.");
            return;
        }
        if (city.Equals(s.WallpaperGeoCity, StringComparison.OrdinalIgnoreCase) && s.WallpaperLat != null) return;
        if (_geoBusyCity == city) return;
        _geoBusyCity = city;
        Task.Run(async () =>
        {
            try
            {
                string g = await Http.GetStringAsync(
                    $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=id&name={Uri.EscapeDataString(city)}").ConfigureAwait(false);
                using var gd = JsonDocument.Parse(g);
                if (!gd.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0)
                {
                    _ui?.BeginInvoke(new Action(() => SetStatus("Kota tidak ditemukan. Memakai jam tetap.")));
                    return;
                }
                double lat = res[0].GetProperty("latitude").GetDouble();
                double lon = res[0].GetProperty("longitude").GetDouble();
                _ui?.BeginInvoke(new Action(() =>
                {
                    s.WallpaperLat = lat;
                    s.WallpaperLon = lon;
                    s.WallpaperGeoCity = city;
                    Settings.Save();
                    Evaluate(false);
                }));
            }
            catch { }
            finally { _geoBusyCity = ""; }
        });
    }

    /// <summary>Matahari terbit & terbenam (waktu lokal) — rumus NOAA ringkas, akurat ±2 menit.</summary>
    public static bool SunTimes(DateTime date, double lat, double lon, out TimeSpan rise, out TimeSpan set)
    {
        rise = set = default;
        double? r = SunEvent(date, lat, lon, true), s = SunEvent(date, lat, lon, false);
        if (r == null || s == null) return false;
        double offset = TimeZoneInfo.Local.GetUtcOffset(date).TotalHours;
        rise = TimeSpan.FromHours(((r.Value + offset) % 24 + 24) % 24);
        set = TimeSpan.FromHours(((s.Value + offset) % 24 + 24) % 24);
        return true;
    }

    static double? SunEvent(DateTime date, double lat, double lon, bool rising)
    {
        const double Deg = Math.PI / 180;
        int n = date.DayOfYear;
        double lngHour = lon / 15;
        double t = n + ((rising ? 6 : 18) - lngHour) / 24;
        double m = 0.9856 * t - 3.289;
        double l = m + 1.916 * Math.Sin(m * Deg) + 0.020 * Math.Sin(2 * m * Deg) + 282.634;
        l = (l % 360 + 360) % 360;
        double ra = Math.Atan(0.91764 * Math.Tan(l * Deg)) / Deg;
        ra = (ra % 360 + 360) % 360;
        ra += Math.Floor(l / 90) * 90 - Math.Floor(ra / 90) * 90;
        ra /= 15;
        double sinDec = 0.39782 * Math.Sin(l * Deg);
        double cosDec = Math.Cos(Math.Asin(sinDec));
        double cosH = (Math.Cos(90.833 * Deg) - sinDec * Math.Sin(lat * Deg)) / (cosDec * Math.Cos(lat * Deg));
        if (cosH > 1 || cosH < -1) return null; // matahari tidak terbit / tidak terbenam hari ini
        double h = rising ? 360 - Math.Acos(cosH) / Deg : Math.Acos(cosH) / Deg;
        h /= 15;
        double localMean = h + ra - 0.06571 * t - 6.622;
        double ut = localMean - lngHour;
        return (ut % 24 + 24) % 24;
    }

    // =====================================================================
    // Memasang wallpaper
    // =====================================================================

    sealed record MonitorInfo(string Id, RECT Rect, bool Primary, string Current);

    static async Task ApplyAsync(string[] perMonitor, int fit, string key)
    {
        _busy = true;
        var s = Settings.Current;
        var overlays = new List<Window>();
        try
        {
            var monitors = await Sta(ListMonitors).ConfigureAwait(true);
            // monitor utama dulu, sisanya urut dari kiri
            monitors = monitors.OrderByDescending(m => m.Primary).ThenBy(m => m.Rect.Left).ToList();
            var target = new Dictionary<string, string>();
            for (int i = 0; i < monitors.Count; i++)
                target[monitors[i].Id] = perMonitor[Math.Min(i, perMonitor.Length - 1)];
            string primaryImg = perMonitor[0];

            // fade: tutupi tiap monitor dengan wallpaper lamanya, pasang yang baru di bawahnya, lalu pudarkan
            if (s.WallpaperFade && fit != 5)
            {
                foreach (var m in monitors)
                {
                    if (!target.TryGetValue(m.Id, out var img) || SamePath(m.Current, img) || !File.Exists(m.Current)) continue;
                    var bmp = await Task.Run(() => Decode(m.Current, m.Rect.Right - m.Rect.Left)).ConfigureAwait(true);
                    if (bmp == null) continue;
                    var w = CreateOverlay(m.Rect, bmp, fit);
                    if (w != null) overlays.Add(w);
                }
                if (overlays.Count > 0) await Task.Delay(120).ConfigureAwait(true); // pastikan sudah tergambar
            }

            await Sta(() =>
            {
                var dw = (IDesktopWallpaper)new DesktopWallpaperClass();
                try
                {
                    dw.SetPosition(FitToPosition(fit));
                    if (fit == 5 || monitors.Count == 0 || target.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                        dw.SetWallpaper(null, primaryImg);
                    else
                        foreach (var (id, img) in target) dw.SetWallpaper(id, img);
                }
                finally { Marshal.ReleaseComObject(dw); }
                return true;
            }).ConfigureAwait(true);

            s.WallpaperLastApplied = key;
            s.WallpaperLastChange = DateTime.Now;
            Settings.Save();
            Status = L.T("Terpasang: ") + Path.GetFileName(primaryImg) + (perMonitor.Length > 1 && perMonitor.Skip(1).Any(p => !SamePath(p, primaryImg)) ? " +" : "");

            if (overlays.Count > 0)
            {
                await Task.Delay(350).ConfigureAwait(true); // beri waktu Windows menggambar wallpaper baru
                FadeOutAndClose(overlays);
                overlays.Clear();
            }

            if (s.WallpaperLockScreen) await SetLockScreenAsync(primaryImg).ConfigureAwait(true);
            await UpdateColorsAsync(primaryImg).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = L.T("Gagal memasang wallpaper: ") + ex.Message;
            CrashGuard.Log(ex, "wallpaper");
            // cadangan: cara lama Windows (tanpa per-monitor)
            try { SystemParametersInfo(0x0014, 0, perMonitor[0], 0x01 | 0x02); } catch { }
        }
        finally
        {
            foreach (var w in overlays) { try { w.Close(); } catch { } }
            _busy = false;
            Changed?.Invoke();
            if (_pendingForce) { _pendingForce = false; Evaluate(true); }
        }
    }

    static bool SamePath(string a, string b)
    {
        try { return a.Length > 0 && b.Length > 0 && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    static int FitToPosition(int fit) => fit switch
    {
        1 => 3, // Fit
        2 => 2, // Stretch
        3 => 0, // Center
        4 => 1, // Tile
        5 => 5, // Span
        _ => 4, // Fill
    };

    static List<MonitorInfo> ListMonitors()
    {
        var list = new List<MonitorInfo>();
        var dw = (IDesktopWallpaper)new DesktopWallpaperClass();
        try
        {
            uint n = dw.GetMonitorDevicePathCount();
            for (uint i = 0; i < n; i++)
            {
                try
                {
                    string id = dw.GetMonitorDevicePathAt(i);
                    RECT r = dw.GetMonitorRECT(id); // gagal untuk monitor yang sudah dicabut
                    if (r.Right <= r.Left) continue;
                    string cur = "";
                    try { cur = dw.GetWallpaper(id) ?? ""; } catch { }
                    list.Add(new MonitorInfo(id, r, r.Left <= 0 && r.Right > 0 && r.Top <= 0 && r.Bottom > 0, cur));
                }
                catch { }
            }
        }
        finally { Marshal.ReleaseComObject(dw); }
        return list;
    }

    /// <summary>COM IDesktopWallpaper harus di thread STA; dijalankan di thread pendek supaya UI tidak tersendat.</summary>
    static Task<T> Sta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var th = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "Wallpaper" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        return tcs.Task;
    }

    // ---------------------------------------------------------------- fade

    static Window? CreateOverlay(RECT r, BitmapSource bmp, int fit)
    {
        try
        {
            var img = new Image
            {
                Source = bmp,
                Stretch = fit switch { 1 => Stretch.Uniform, 2 => Stretch.Fill, 3 => Stretch.None, _ => Stretch.UniformToFill },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, ShowActivated = false, Topmost = false, Focusable = false,
                Background = Brushes.Black, Content = new Grid { ClipToBounds = true, Children = { img } },
                Left = -32000, Top = -32000, Width = 10, Height = 10, Title = "DynamicIsland.WallpaperFade",
            };
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            long ex = Native.GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            Native.SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            w.Show();
            // tepat di atas desktop (di bawah semua jendela aplikasi), seukuran monitor (piksel fisik)
            SetWindowPos(hwnd, AboveDesktop(hwnd), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            return w;
        }
        catch { return null; }
    }

    /// <summary>
    /// Jendela terlihat yang berada tepat DI ATAS desktop (Progman/WorkerW) dalam urutan z.
    /// Overlay diletakkan di bawahnya: menutupi wallpaper (dan ikon desktop sesaat), tapi tetap di bawah semua aplikasi.
    /// </summary>
    static IntPtr AboveDesktop(IntPtr self)
    {
        var sb = new StringBuilder(64);
        IntPtr prev = IntPtr.Zero;
        for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = Native.GetWindow(h, GW_HWNDNEXT))
        {
            if (h == self || !IsWindowVisible(h)) continue;
            sb.Clear();
            GetClassName(h, sb, sb.Capacity);
            string c = sb.ToString();
            if (c == "Progman" || c == "WorkerW") return prev == IntPtr.Zero ? HWND_TOP : prev;
            prev = h;
        }
        return HWND_BOTTOM;
    }

    static void FadeOutAndClose(List<Window> overlays)
    {
        foreach (var w in overlays.ToList())
        {
            var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(750)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            anim.Completed += (_, _) => { try { w.Close(); } catch { } };
            w.BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }

    // ---------------------------------------------------------------- lock screen

    static async Task SetLockScreenAsync(string path)
    {
        try
        {
            var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            await global::Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
            LockStatus = L.T("Lock screen ikut diganti.");
        }
        catch (Exception ex)
        {
            LockStatus = L.T("Lock screen tidak bisa diganti oleh app ini: ") + ex.Message;
            CrashGuard.LogLine("Lock screen gagal: " + ex.GetType().Name + " — " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- warna

    static bool _colorBusy;

    static async Task RefreshColorAsync()
    {
        if (_busy || _colorBusy) return;
        _colorBusy = true;
        try
        {
            var mons = await Sta(ListMonitors).ConfigureAwait(true);
            var p = mons.FirstOrDefault(m => m.Primary) ?? mons.FirstOrDefault();
            if (p != null && File.Exists(p.Current)) await UpdateColorsAsync(p.Current).ConfigureAwait(true);
        }
        catch { }
        finally { _colorBusy = false; }
    }

    static async Task UpdateColorsAsync(string path)
    {
        var s = Settings.Current;
        if (!WantColor(s)) { WallColor = null; return; }
        var small = await Task.Run(() => Decode(path, 96)).ConfigureAwait(true);
        if (small == null) return;
        var c = VividColor(small);
        WallColor = c;
        if (s.AccentFromWallpaper)
        {
            string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            if (!string.Equals(s.AccentColor, hex, StringComparison.OrdinalIgnoreCase))
            {
                s.AccentColor = hex;
                Settings.NotifyChanged();
                Settings.Save();
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Simpan warna aksen lama saat "ikut wallpaper" dinyalakan; kembalikan saat dimatikan.</summary>
    static void HandleAccentToggle(Settings s)
    {
        if (s.AccentFromWallpaper == _lastAccentOpt) return;
        _lastAccentOpt = s.AccentFromWallpaper;
        if (s.AccentFromWallpaper)
        {
            s.AccentBeforeWallpaper = s.AccentColor;
            WallColor = null; // hitung ulang
        }
        else if (s.AccentBeforeWallpaper.Length > 0)
        {
            s.AccentColor = s.AccentBeforeWallpaper;
            s.AccentBeforeWallpaper = "";
            Settings.NotifyChanged();
        }
        Settings.Save();
    }

    /// <summary>Warna paling "hidup" di gambar: histogram hue berbobot saturasi, lalu dibuat cukup terang untuk aksen.</summary>
    public static Color VividColor(BitmapSource src)
    {
        try
        {
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            const int Bins = 18;
            var weight = new double[Bins];
            var sumR = new double[Bins]; var sumG = new double[Bins]; var sumB = new double[Bins];
            double gray = 0, gR = 0, gG = 0, gB = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                double sat = max <= 0 ? 0 : (max - min) / max;
                if (sat < 0.18 || max < 0.15)
                {
                    gray += 1; gR += r; gG += g; gB += b;
                    continue;
                }
                double hue = Hue(r, g, b, max, min);
                int bin = Math.Min(Bins - 1, (int)(hue / 360 * Bins));
                double wgt = sat * max;
                weight[bin] += wgt; sumR[bin] += r * wgt; sumG[bin] += g * wgt; sumB[bin] += b * wgt;
            }
            int best = Array.IndexOf(weight, weight.Max());
            double total = weight.Sum();
            double cr, cg, cb;
            if (total < 0.02 * (px.Length / 4) || weight[best] <= 0)
            {
                // gambar hampir tanpa warna (hitam-putih): pakai abu-abu terang
                return Color.FromRgb(0xC7, 0xC7, 0xCC);
            }
            cr = sumR[best] / weight[best]; cg = sumG[best] / weight[best]; cb = sumB[best] / weight[best];
            // normalisasi: saturasi minimal 0.45, kecerahan ±0.9 supaya terlihat di island gelap
            double mx = Math.Max(cr, Math.Max(cg, cb)), mn = Math.Min(cr, Math.Min(cg, cb));
            double hh = Hue(cr, cg, cb, mx, mn);
            double ss = Math.Clamp(mx <= 0 ? 0 : (mx - mn) / mx, 0.45, 0.85);
            return FromHsv(hh, ss, 0.92);
        }
        catch { return Color.FromRgb(0x5D, 0xCA, 0xA5); }
    }

    static double Hue(double r, double g, double b, double max, double min)
    {
        double d = max - min;
        if (d <= 0) return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Baca gambar dengan lebar kecil (hemat RAM). Aman di thread mana saja (hasil di-freeze).</summary>
    public static BitmapSource? Decode(string path, int width)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width > 0) bi.DecodePixelWidth = Math.Min(width, 3840);
            bi.StreamSource = fs;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    // =====================================================================
    // Native
    // =====================================================================

    const int GWL_EXSTYLE = -20;
    const uint GW_HWNDNEXT = 2;
    const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_BOTTOM = (IntPtr)1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(uint action, uint param, string value, uint winIni);

    [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
    class DesktopWallpaperClass { }

    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
        uint GetMonitorDevicePathCount();
        RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
        void SetSlideshow(IntPtr items);
        IntPtr GetSlideshow();
        void SetSlideshowOptions(int options, uint slideshowTick);
        void GetSlideshowOptions(out int options, out uint slideshowTick);
        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, int direction);
        int GetStatus();
        void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }
}
