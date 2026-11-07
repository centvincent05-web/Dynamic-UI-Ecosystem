using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using DynamicIsland.Core;
using Microsoft.Win32;

namespace DynamicIsland.Services;

public enum EdgeSide { Left, Right, Top, Bottom }

/// <summary>
/// Mendeteksi pointer di tepi kiri/kanan layar tanpa menaruh jendela di sana
/// (jadi scrollbar dan klik di tepi layar tidak terhalang).
/// Jeda munculnya tergantung kondisi:
///   - desktop         : EdgeDelayDesktopMs (default langsung)
///   - jendela biasa   : EdgeWindowDelayMs (default 0,3 detik)
///   - fullscreen      : EdgeDelayFullscreenMs (default 1,5 detik)
/// </summary>
public sealed class EdgeWatcher : IDisposable
{
    readonly DispatcherTimer _timer;
    readonly Dispatcher _ui;
    readonly HookThread _hook; // hook mouse di thread sendiri: tidak pernah dicabut Windows walau tampilan sibuk
    int _px, _py, _pending;
    Native.RECT _monitor;
    EdgeSide? _zone;
    int _zoneY;

    /// <summary>Pointer sudah cukup lama di tepi. Parameter: sisi dan posisi Y (piksel fisik).</summary>
    public event Action<EdgeSide, int>? Triggered;

    public EdgeWatcher()
    {
        _ui = Dispatcher.CurrentDispatcher;
        _hook = new HookThread(Native.WH_MOUSE_LL, HookProc);
        _timer = new DispatcherTimer(DispatcherPriority.Input);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (_zone is EdgeSide side) Fire(side, _zoneY);
        };

        RefreshMonitor();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        _hook.Start();
    }

    void OnDisplayChanged(object? sender, EventArgs e) => RefreshMonitor();
    void RefreshMonitor() => _monitor = Native.PrimaryMonitorRect();

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)Native.WM_MOUSEMOVE)
        {
            // di thread hook: cukup catat posisi terakhir, pengecekan dikerjakan di thread tampilan
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            Volatile.Write(ref _px, data.pt.X);
            Volatile.Write(ref _py, data.pt.Y);
            Volatile.Write(ref _hookTick, Environment.TickCount64);
            if (Interlocked.Exchange(ref _pending, 1) == 0)
                _ui.BeginInvoke(DispatcherPriority.Send, new Action(ProcessMove)); // didahulukan: panel tidak telat walau animasi sedang jalan
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    long _hookTick;

    /// <summary>Folder catatan diagnosa (diisi App): kapan pointer masuk tepi & berapa lama sampai diproses.</summary>
    public static string? LogFolder;
    static int _logLines;

    public static void Log(string line)
    {
        if (LogFolder == null || _logLines > 150) return;
        _logLines++;
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(LogFolder, "edge.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {line}\r\n"); } catch { }
    }

    void ProcessMove()
    {
        Interlocked.Exchange(ref _pending, 0);
        if (_hook.IsRunning) Check(Volatile.Read(ref _px), Volatile.Read(ref _py));
    }

    /// <summary>Dicek oleh island: zona tengah tepi atas hanya aktif saat notch sedang tersembunyi.</summary>
    public Func<bool>? TopZoneActive;
    /// <summary>Dicek oleh dock: zona tengah tepi bawah aktif saat dock tersembunyi. Nilai = lebar dock (DIP).</summary>
    public Func<double>? BottomZoneWidth;

    void Check(int x, int y)
    {
        var s = Settings.Current;
        EdgeSide? now = null;
        int width = _monitor.Right - _monitor.Left;
        int height = _monitor.Bottom - _monitor.Top;

        if (s.EnableEdgePanels)
        {
            double zoneH = height * s.EdgeZonePercent / 100.0;
            double top = _monitor.Top + (height - zoneH) / 2;
            bool inY = y >= top && y <= top + zoneH;

            // beberapa piksel dari tepi monitor utama (tidak perlu "mentok")
            int tol = s.EdgeTolerancePx;
            if (inY && x >= _monitor.Left && x <= _monitor.Left + tol) now = EdgeSide.Left;
            else if (inY && x <= _monitor.Right - 1 && x >= _monitor.Right - 1 - tol) now = EdgeSide.Right;
        }

        if (now == null && y >= _monitor.Top && y <= _monitor.Top + s.EdgeTolerancePx
            && s.RevealOnTopEdge && TopZoneActive?.Invoke() == true)
        {
            double half = s.TopRevealFullWidth ? double.MaxValue : s.TopEdgeZoneWidth * App.DpiScale / 2;
            double cx = _monitor.Left + width / 2.0;
            if (Math.Abs(x - cx) <= half) now = EdgeSide.Top;
        }

        if (now == null && y <= _monitor.Bottom - 1 && y >= _monitor.Bottom - 1 - s.EdgeTolerancePx && BottomZoneWidth != null)
        {
            double dockW = BottomZoneWidth();
            if (dockW > 0)
            {
                double half = s.DockRevealFullWidth ? double.MaxValue : Math.Max(dockW, 200) * App.DpiScale / 2;
                double cx = _monitor.Left + width / 2.0;
                if (Math.Abs(x - cx) <= half) now = EdgeSide.Bottom;
            }
        }

        // Ada jeda muncul (mis. "Jeda muncul" tepi atas > 0): pointer harus menunggu di tepi. Dulu pointer
        // yang turun 1–2 piksel saja langsung membatalkan hitungan jeda, sehingga notch/dock/panel sering
        // tidak muncul sama sekali. Sekarang selama jeda berjalan, pointer boleh sedikit menjauh dari tepi.
        if (now == null && _zone is EdgeSide waiting && !_fired && _timer.IsEnabled && StillNear(waiting, x, y, s))
            now = waiting;

        if (now == _zone)
        {
            _zoneY = y;
            // masih di tepi dan panel sempat hilang: munculkan lagi saat pointer bergerak (dibatasi 300 ms)
            if (now != null && _fired && Environment.TickCount64 - _lastFire > 300) Fire(now.Value, y);
            return;
        }
        _zone = now;
        _zoneY = y;
        _fired = false;
        _timer.Stop();
        if (now == null) return;
        // diagnosa: jeda dari gerakan mouse sampai dibaca, dan kondisi jendela aktif
        long lag = Environment.TickCount64 - Volatile.Read(ref _hookTick);
        IntPtr fgLog = Native.GetForegroundWindow();
        Log($"masuk tepi {now} x={x} y={y} | baca telat {lag} ms | jendela: {ScreenState.ProcessNameOf(fgLog)} ({Native.ClassNameOf(fgLog)}) " +
            $"desktop={ScreenState.IsDesktop(fgLog)} fullscreen={ScreenState.IsFullscreen(fgLog)} maximize={ScreenState.IsMaximized(fgLog)}");

        int delay;
        if (now == EdgeSide.Top || now == EdgeSide.Bottom)
        {
            IntPtr fg = Native.GetForegroundWindow();
            // fullscreen (F11, video, game): notch tidak muncul, kecuali aplikasinya ada di daftar pengecualian
            if (ScreenState.IsFullscreen(fg) && !ScreenState.IsRevealAllowedApp(fg)) { Log("  → diabaikan: dianggap fullscreen"); return; }
            delay = s.TopRevealDelayMs;
        }
        else
        {
            delay = DelayForCurrentContext();
        }
        Log($"  → jeda {delay} ms");
        if (delay <= 0)
        {
            Fire(now.Value, y);
        }
        else
        {
            _timer.Interval = TimeSpan.FromMilliseconds(delay);
            _timer.Start();
        }
    }

    bool _fired;
    long _lastFire;

    /// <summary>Masih dekat tepi yang sedang ditunggu (toleransi lebih longgar selama jeda berjalan).</summary>
    bool StillNear(EdgeSide side, int x, int y, Settings s)
    {
        int band = s.EdgeTolerancePx + (int)Math.Round(24 * App.DpiScale);
        return side switch
        {
            EdgeSide.Top => y <= _monitor.Top + band,
            EdgeSide.Bottom => y >= _monitor.Bottom - 1 - band,
            EdgeSide.Left => x <= _monitor.Left + band,
            _ => x >= _monitor.Right - 1 - band,
        };
    }

    void Fire(EdgeSide side, int y)
    {
        _fired = true;
        _lastFire = Environment.TickCount64;
        Triggered?.Invoke(side, y);
    }

    static int DelayForCurrentContext()
    {
        var s = Settings.Current;
        IntPtr fg = Native.GetForegroundWindow();
        if (ScreenState.IsDesktop(fg)) return s.EdgeDelayDesktopMs;
        if (ScreenState.IsFullscreen(fg)) return s.EdgeDelayFullscreenMs;
        return s.EdgeWindowDelayMs;
    }

    /// <summary>Mode game: lepas hook mouse supaya tidak ada pemrosesan di setiap gerakan mouse.</summary>
    public void Pause()
    {
        _timer.Stop();
        _zone = null;
        _hook.Stop();
    }

    public void Resume()
    {
        if (_hook.IsRunning) return;
        RefreshMonitor();
        _hook.Start();
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _hook.Stop();
    }
}

/// <summary>Cek kondisi layar: sedang di desktop atau ada aplikasi fullscreen.</summary>
public static class ScreenState
{
    public static bool IsDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return true;
        string cls = Native.ClassNameOf(hwnd);
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>
    /// Permukaan shell Windows yang muncul sebentar di atas semuanya (Start, Search, pusat notifikasi,
    /// Quick Settings, kalender, overflow tray, Alt+Tab, Task View). Ukurannya bisa selebar layar,
    /// tapi bukan aplikasi fullscreen → jangan memicu mode game / "tertutup jendela penuh".
    /// </summary>
    public static bool IsShellSurface(IntPtr hwnd)
    {
        if (IsDesktop(hwnd)) return true;
        string cls = Native.ClassNameOf(hwnd);
        switch (cls)
        {
            case "XamlExplorerHostIslandWindow":
            case "TopLevelWindowForOverflowXamlIsland":
            case "NotifyIconOverflowWindow":
            case "MultitaskingViewFrame":
            case "TaskSwitcherWnd":
            case "XamlWindowHost":
            case "ForegroundStaging":
            case "Shell_LightDismissOverlay":
                return true;
            case "Windows.UI.Core.CoreWindow":
                // CoreWindow juga dipakai aplikasi UWP fullscreen → hanya proses shell yang dikecualikan
                return ProcessNameOf(hwnd) is "startmenuexperiencehost" or "searchhost" or "searchapp" or "searchui"
                    or "shellexperiencehost" or "shellhost" or "explorer" or "textinputhost" or "lockapp" or "widgets";
        }
        return false;
    }

    /// <summary>Jendela aktif sedang di-maximize di monitor utama.</summary>
    public static bool IsMaximized(IntPtr hwnd)
    {
        if (IsShellSurface(hwnd) || !IsRealWindow(hwnd) || !Native.IsZoomed(hwnd)) return false;
        var primary = Native.MonitorFromPoint(new Native.POINT(), Native.MONITOR_DEFAULTTOPRIMARY);
        return Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST) == primary;
    }

    static readonly uint OwnPid = (uint)Environment.ProcessId;

    /// <summary>Nama proses pemilik jendela (tanpa .exe), huruf kecil.</summary>
    public static string ProcessNameOf(IntPtr hwnd)
    {
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName.ToLowerInvariant();
        }
        catch { return ""; }
    }

    /// <summary>
    /// Fullscreen yang boleh memunculkan notch (browser, pemutar video). Game fullscreen eksklusif
    /// (D3D) dan aplikasi lain yang tidak ada di daftar dianggap game, jadi notch tidak muncul.
    /// </summary>
    public static bool IsRevealAllowedApp(IntPtr hwnd)
    {
        if (Native.SHQueryUserNotificationState(out int state) == 0 && state == 3) return false; // game D3D
        string name = ProcessNameOf(hwnd);
        if (name.Length == 0) return false;
        foreach (var part in Settings.Current.FullscreenRevealList.Split(',', ';'))
        {
            string app = part.Trim().ToLowerInvariant();
            if (app.EndsWith(".exe")) app = app[..^4];
            if (app.Length > 0 && app == name) return true;
        }
        return false;
    }

    /// <summary>Jendela nyata yang terlihat pengguna (bukan jendela tersembunyi/overlay atau milik aplikasi ini).</summary>
    static bool IsRealWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindowVisible(hwnd)) return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == OwnPid) return false;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;
        return true;
    }

    public static bool IsFullscreen(IntPtr hwnd)
    {
        if (IsShellSurface(hwnd) || !IsRealWindow(hwnd)) return false;

        // 3 = game D3D fullscreen, 4 = mode presentasi
        if (Native.SHQueryUserNotificationState(out int state) == 0 && state is 3 or 4)
            return true;

        // Jendela maximize biasa BUKAN fullscreen walaupun menutupi seluruh layar
        // (misalnya saat taskbar disembunyikan oleh Bloom / auto-hide). Fullscreen asli
        // (F11, video, game) tidak berstatus maximize, atau tidak punya title bar.
        if (!Native.GetWindowRect(hwnd, out var w)) return false;
        var m = Native.MonitorRect(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST));

        if (Native.IsZoomed(hwnd))
        {
            long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
            bool hasCaption = (style & Native.WS_CAPTION) == Native.WS_CAPTION;
            // jendela maximize biasa punya bingkai yang "melewati" tepi layar beberapa piksel
            bool overhangs = w.Left < m.Left || w.Top < m.Top || w.Right > m.Right;
            if (hasCaption || overhangs) return false;
        }
        return w.Left <= m.Left && w.Top <= m.Top && w.Right >= m.Right && w.Bottom >= m.Bottom;
    }
}
