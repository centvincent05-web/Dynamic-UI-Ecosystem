using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.UI;

/// <summary>
/// Fase 2 — widget desktop. Satu jendela kecil per widget (lebih hemat daripada satu jendela transparan selebar layar,
/// karena yang digambar ulang hanya widget yang datanya berubah). Jendela selalu di bawah semua aplikasi
/// (terlihat di desktop dan saat Win+D), tidak mencuri fokus, dan berhenti memperbarui diri saat tertutup jendela maximize.
/// Bawaan mati: selama WidgetsEnabled = false tidak ada jendela sama sekali.
/// </summary>
public static class WidgetManager
{
    static Dispatcher? _ui;
    static MediaService? _media;
    static WeatherService? _weather;
    static readonly Dictionary<string, WidgetWindow> _windows = new();
    static DispatcherTimer? _sync;
    static bool _suspended, _edit, _raised;
    static WinEventDelegate? _fgProc; // disimpan supaya tidak dibersihkan GC
    static IntPtr _fgHook;

    public static MediaService? Media => _media;
    public static WeatherService? Weather => _weather;
    public static bool EditMode => _edit;
    /// <summary>Widget ditampilkan di atas jendela lain sementara (Win+D / desktop sedang di depan).</summary>
    public static bool Raised => _raised || _edit;
    public static event Action? EditModeChanged;

    public static void Start(Dispatcher ui, MediaService? media, WeatherService? weather)
    {
        if (_ui != null) return;
        _ui = ui;
        _media = media;
        _weather = weather;
        WeatherService.ExtraDemand = () =>
            Settings.Current.WidgetsEnabled && (Settings.Current.Widgets?.Any(w => w.Visible && w.Kind == 1) ?? false);
        WallpaperService.ColorDemand = () =>
            Settings.Current.WidgetsEnabled && (Settings.Current.Widgets?.Any(w => w.Visible && (w.ColorMode == 1 || w.BgMode == 1)) ?? false);
        _sync = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(300) };
        _sync.Tick += (_, _) => { _sync.Stop(); Sync(); };
        Settings.Changed += () => { _sync.Stop(); _sync.Start(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            ui.BeginInvoke(new Action(() => { if (_windows.Count > 0) LayoutAll(); })); // monitor/resolusi berubah: rapikan ulang
        Sync();
    }

    /// <summary>Mode game: semua widget disembunyikan dan berhenti total.</summary>
    public static void SetSuspended(bool on)
    {
        _suspended = on;
        foreach (var w in _windows.Values)
        {
            if (on) w.Hide(); else { w.Show(); w.Reposition(); }
            w.SetPaused(on);
        }
    }

    public static void SetEditMode(bool on)
    {
        if (_edit == on) return;
        _edit = on;
        foreach (var w in _windows.Values) w.OnEditModeChanged();
        ApplyZOrder();
        EditModeChanged?.Invoke();
    }

    /// <summary>Samakan jendela dengan daftar widget di Pengaturan.</summary>
    public static void Sync()
    {
        var s = Settings.Current;
        if (s.WidgetsEnabled && s.Widgets == null) s.Widgets = Defaults();
        var wanted = s.WidgetsEnabled ? (s.Widgets ?? new()).Where(w => w.Visible).ToList() : new List<WidgetConfig>();

        foreach (var id in _windows.Keys.ToList())
        {
            if (wanted.Any(w => w.Id == id)) continue;
            try { _windows[id].Close(); } catch { }
            _windows.Remove(id);
        }

        if (wanted.Count > 0)
        {
            int n = Monitors.All().Count;
            for (int m = 0; m < n; m++) Layout(m);
        }
        foreach (var cfg in wanted)
        {
            if (_windows.TryGetValue(cfg.Id, out var win)) win.Update(cfg);
            else
            {
                win = new WidgetWindow(cfg);
                _windows[cfg.Id] = win;
                if (!_suspended) win.Show();
                win.Reposition();
            }
        }

        if (wanted.Count > 0 && _fgHook == IntPtr.Zero)
        {
            _fgProc = OnForeground;
            _fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgProc, 0, 0, 0);
        }
        else if (wanted.Count == 0 && _fgHook != IntPtr.Zero)
        {
            UnhookWinEvent(_fgHook);
            _fgHook = IntPtr.Zero;
            _fgProc = null;
            if (_edit) SetEditMode(false);
        }
        if (wanted.Any(w => w.Kind == 1) && _weather != null && _weather.Temperature == null) _weather.Refresh();
    }

    public static List<WidgetConfig> Defaults() => new()
    {
        new WidgetConfig { Kind = 0, Size = 1 }, // jam (sedang)
        new WidgetConfig { Kind = 1, Size = 0 }, // cuaca (kecil)
        new WidgetConfig { Kind = 3, Size = 0 }, // performa (kecil)
        new WidgetConfig { Kind = 2, Size = 1 }, // musik (sedang)
    };

    // ---------------------------------------------------------------- grid ala macOS

    /// <summary>Skala isi widget (ukuran sel / 158). Teks, gambar, sudut, dan jarak ikut membesar/mengecil.</summary>
    public static double K => Math.Clamp(Settings.Current.WidgetCellSize, 100, 260) / WidgetWindow.Cell;

    /// <summary>Lebar × tinggi widget dalam sel: kecil 1×1, sedang 2×1, besar 2×2, melebar 3×1.</summary>
    public static (int c, int r) Span(WidgetConfig w) => w.Size switch { 1 => (2, 1), 2 => (2, 2), 3 => (3, 1), _ => (1, 1) };

    /// <summary>Ukuran grid di monitor (DIP): jumlah kolom/baris, langkah per sel, dan margin tepi.</summary>
    internal static (int cols, int rows, double step, double left, double top) Grid(Monitors.Info mon)
    {
        var s = Settings.Current;
        double k = K, cell = WidgetWindow.Cell * k, gap = WidgetWindow.Gap * k;
        // grid tidak sampai ujung layar: ada jarak di semua sisi, dan area dock di bawah layar utama dikosongkan
        double margin = Math.Clamp(s.WidgetScreenMargin, 0, 240);
        double bottom = margin + (mon.Primary && s.DockEnabled ? DockReserve : 0);
        int cols = Math.Max(1, (int)Math.Floor((mon.WorkWidthDip - 2 * margin + gap) / (cell + gap)));
        int rows = Math.Max(1, (int)Math.Floor((mon.WorkHeightDip - margin - bottom + gap) / (cell + gap)));
        // sisa ruang dibagi rata kiri-kanan supaya grid berada di tengah, bukan mepet ke satu sisi
        double used = cols * (cell + gap) - gap;
        double side = Math.Max(margin, (mon.WorkWidthDip - used) / 2);
        return (cols, rows, cell + gap, side, margin);
    }

    /// <summary>Tinggi area dock yang tidak boleh ditempati widget (DIP).</summary>
    const double DockReserve = 96;

    /// <summary>
    /// Rapikan semua widget di satu monitor ke grid tanpa tumpang tindih.
    /// Widget "priority" (yang sedang digeser) mendapat tempatnya dulu; widget lain yang tertabrak menyingkir ke sel kosong terdekat.
    /// Widget tanpa posisi diletakkan mulai dari kanan atas (sisi kiri biasanya berisi ikon desktop).
    /// </summary>
    public static void Layout(int monitor, WidgetConfig? priority = null)
    {
        var all = Settings.Current.Widgets;
        if (all == null) return;
        int monCount = Monitors.All().Count;
        foreach (var w in all) if (w.Monitor >= monCount || w.Monitor < 0) w.Monitor = 0; // monitornya sudah dicabut
        var list = all.Where(w => w.Visible && w.Monitor == monitor).ToList();
        if (list.Count == 0) return;
        var (cols, rows, _, _, _) = Grid(Monitors.Get(monitor));
        var occ = new bool[cols, rows];

        bool Free(int c, int r, int sc, int sr)
        {
            if (c < 0 || r < 0 || c + sc > cols || r + sr > rows) return false;
            for (int x = c; x < c + sc; x++) for (int y = r; y < r + sr; y++) if (occ[x, y]) return false;
            return true;
        }
        void Mark(int c, int r, int sc, int sr)
        {
            for (int x = Math.Max(0, c); x < Math.Min(cols, c + sc); x++)
                for (int y = Math.Max(0, r); y < Math.Min(rows, r + sr); y++) occ[x, y] = true;
        }
        (int c, int r)? Nearest(int dc, int dr, int sc, int sr)
        {
            (int, int)? best = null;
            double bestD = double.MaxValue;
            for (int c = 0; c + sc <= cols; c++)
                for (int r = 0; r + sr <= rows; r++)
                {
                    if (!Free(c, r, sc, sr)) continue;
                    double d = (c - dc) * (c - dc) + (r - dr) * (r - dr) * 1.1; // sedikit lebih suka bergeser ke samping
                    if (d < bestD) { bestD = d; best = (c, r); }
                }
            return best;
        }
        (int c, int r)? FirstFromRight(int sc, int sr)
        {
            for (int c = cols - sc; c >= 0; c--)
                for (int r = 0; r + sr <= rows; r++)
                    if (Free(c, r, sc, sr)) return (c, r);
            return null;
        }

        var order = list.OrderBy(w => w == priority ? 0 : w.Col >= 0 ? 1 : 2).ToList();
        foreach (var w in order)
        {
            var (sc, sr) = Span(w);
            sc = Math.Min(sc, cols);
            sr = Math.Min(sr, rows);
            (int c, int r)? spot;
            if (w.Col >= 0 && w.Row >= 0)
            {
                int c0 = Math.Clamp(w.Col, 0, cols - sc), r0 = Math.Clamp(w.Row, 0, rows - sr);
                spot = Free(c0, r0, sc, sr) ? (c0, r0) : Nearest(c0, r0, sc, sr);
            }
            else spot = FirstFromRight(sc, sr);
            var (pc, pr) = spot ?? (Math.Clamp(w.Col, 0, cols - sc), Math.Clamp(w.Row, 0, rows - sr)); // grid penuh: biarkan menumpuk
            w.Col = pc;
            w.Row = pr;
            Mark(pc, pr, sc, sr);
        }
    }

    /// <summary>Rapikan semua monitor lalu pindahkan jendela (kecuali yang sedang digeser).</summary>
    public static void LayoutAll(WidgetWindow? except = null)
    {
        int n = Monitors.All().Count;
        for (int m = 0; m < n; m++) Layout(m);
        Settings.Save();
        foreach (var w in _windows.Values) if (w != except) w.Reposition();
    }

    /// <summary>Susun ulang semua widget dari awal (tombol "Susun ulang otomatis" di Pengaturan).</summary>
    public static void ResetLayout()
    {
        foreach (var w in Settings.Current.Widgets ?? new()) { w.Col = -1; w.Row = -1; }
        LayoutAll();
        Settings.NotifyChanged();
    }

    /// <summary>Saat menggeser: widget pindah ke sel (col,row) di monitor itu, yang lain langsung menyingkir (seperti macOS).</summary>
    public static void DragTo(WidgetWindow dragged, WidgetConfig cfg, int monitor, int col, int row)
    {
        int oldMon = cfg.Monitor;
        cfg.Monitor = monitor;
        cfg.Col = col;
        cfg.Row = row;
        Layout(monitor, cfg);
        if (oldMon != monitor) Layout(oldMon);
        foreach (var w in _windows.Values) if (w != dragged) w.Reposition();
    }

    // ---------------------------------------------------------------- urutan z

    /// <summary>
    /// Win+D / "show desktop" membuat desktop ke depan: widget ikut ke depan supaya tetap terlihat.
    /// Begitu aplikasi lain aktif, widget kembali ke paling bawah.
    /// </summary>
    static void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        if (_windows.Values.Any(w => w.Handle == hwnd)) return;
        string cls = Native.ClassNameOf(hwnd);
        bool desktop = cls == "WorkerW" || cls == "Progman";
        if (desktop == _raised) { if (!desktop) ResumeIfUncovered(); return; }
        _raised = desktop;
        ApplyZOrder();
        ResumeIfUncovered();
    }

    static void ApplyZOrder()
    {
        foreach (var w in _windows.Values) w.ApplyZOrder();
    }

    static void ResumeIfUncovered()
    {
        foreach (var w in _windows.Values) w.CheckCovered();
    }

    /// <summary>Monitor ini sedang tertutup penuh oleh jendela maximize / fullscreen? (widget tidak perlu diperbarui)</summary>
    public static bool IsMonitorCovered(IntPtr monitor)
    {
        if (Raised) return false;
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || _windows.Values.Any(w => w.Handle == fg)) return false;
        if (ScreenState.IsShellSurface(fg)) return false; // Start, Search, pusat notifikasi, dll.
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        if (Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST) != monitor) return false;
        if (Native.IsZoomed(fg)) return true;
        if (!Native.GetWindowRect(fg, out var r)) return false;
        var m = Native.MonitorRect(monitor);
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    // ---------------------------------------------------------------- native

    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
}

/// <summary>Daftar monitor: indeks 0 = layar utama, sisanya urut dari kiri. Ukuran dalam piksel fisik + skala DPI.</summary>
internal static class Monitors
{
    public sealed record Info(IntPtr Handle, Native.RECT Work, double Scale, bool Primary)
    {
        public double WorkWidthDip => (Work.Right - Work.Left) / Scale;
        public double WorkHeightDip => (Work.Bottom - Work.Top) / Scale;
    }

    public static List<Info> All()
    {
        var list = new List<Info>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref Native.RECT r, IntPtr data) =>
        {
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfo(h, ref mi))
            {
                double scale = 1;
                try { if (GetDpiForMonitor(h, 0, out uint dx, out _) == 0 && dx > 0) scale = dx / 96.0; } catch { }
                list.Add(new Info(h, mi.rcWork, scale, (mi.dwFlags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Work.Left).ThenBy(m => m.Work.Top).ToList();
    }

    /// <summary>Monitor ke-i; kalau sudah tidak ada (dicabut), kembali ke layar utama.</summary>
    public static Info Get(int index)
    {
        var all = All();
        if (all.Count == 0) return new Info(IntPtr.Zero, new Native.RECT { Right = 1920, Bottom = 1080 }, 1, true);
        return index >= 0 && index < all.Count ? all[index] : all[0];
    }

    public static int IndexOf(IntPtr monitor)
    {
        var all = All();
        int i = all.FindIndex(m => m.Handle == monitor);
        return i < 0 ? 0 : i;
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Native.RECT rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
}
