using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicIsland.Services;

/// <summary>
/// Menyembunyikan pop-up volume/kecerahan bawaan Windows (cara yang sama dengan Bloom).
/// Pop-up itu adalah jendela "NativeHWNDHost" (berisi "DirectUIHWND") milik explorer.
/// Jendela itu di-minimize: Windows tetap mengubah kecerahan/volume, tapi pop-up-nya tidak terlihat.
/// Tidak ada kode yang disuntikkan ke explorer; cukup dimatikan/di-restore lagi kapan saja.
/// </summary>
public sealed class NativeOsdHider : IDisposable
{
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint evMin, uint evMax, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);

    const uint EVENT_OBJECT_SHOW = 0x8002;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    const int SW_HIDE = 0, SW_MINIMIZE = 6, SW_RESTORE = 9;

    readonly WinEventProc _proc; // simpan referensi supaya tidak di-GC
    readonly StringBuilder _cls = new(32);
    IntPtr _hook;
    IntPtr _osd;

    public NativeOsdHider() => _proc = OnWinEvent;

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    IntPtr _xamlOsd; // pop-up Windows 11 versi baru (XAML) yang terakhir ditemukan

    public bool IsActive => _hook != IntPtr.Zero;

    /// <summary>
    /// Windows 11 baru: pop-up volume/kecerahan = "XamlExplorerHostIslandWindow" kecil di bawah-tengah layar.
    /// Kelas yang sama dipakai juga oleh Alt+Tab dll, jadi dicek bentuk & posisinya.
    /// </summary>
    static bool IsXamlOsdShape(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) return false;
        var m = mi.rcMonitor;
        int mw = m.R - m.L, mh = m.B - m.T, w = r.R - r.L, h = r.B - r.T;
        if (w <= 0 || h <= 0 || w > mw * 0.4 || h > mh * 0.25) return false;          // kecil
        int cx = (r.L + r.R) / 2, mcx = (m.L + m.R) / 2;
        if (Math.Abs(cx - mcx) > mw * 0.08) return false;                              // di tengah
        return m.B - r.B < mh * 0.2;                                                   // menempel di bawah
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    uint _explorerPid;
    System.Windows.Threading.DispatcherTimer? _pidTimer;

    static uint ExplorerPid()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(tray, out uint pid);
        return pid;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        // hanya event "jendela muncul" dari explorer (pemilik pop-up), bukan dari semua aplikasi:
        // jauh lebih sedikit event yang harus diproses thread tampilan
        _explorerPid = ExplorerPid();
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, _explorerPid, 0,
                                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        // explorer restart → pid berubah → pasang ulang
        if (_pidTimer == null)
        {
            _pidTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            { Interval = TimeSpan.FromSeconds(10) };
            _pidTimer.Tick += (_, _) =>
            {
                if (_hook == IntPtr.Zero) return;
                uint pid = ExplorerPid();
                if (pid == 0 || pid == _explorerPid) return;
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
                _osd = _xamlOsd = IntPtr.Zero;
                Start();
            };
        }
        _pidTimer.Start();
        Enforce();
    }

    public void Stop()
    {
        _pidTimer?.Stop();
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        RestoreWindow(_osd != IntPtr.Zero && IsWindow(_osd) ? _osd : FindOsd());
        _osd = IntPtr.Zero;
        _xamlOsd = IntPtr.Zero; // versi XAML cukup dibiarkan: explorer memunculkannya sendiri nanti
    }

    /// <summary>Pastikan pop-up tetap tersembunyi (dipanggil juga saat volume/kecerahan berubah).</summary>
    public void Enforce()
    {
        if (_hook == IntPtr.Zero) return;
        if (_osd == IntPtr.Zero || !IsWindow(_osd)) _osd = FindOsd();
        if (_osd != IntPtr.Zero && !IsIconic(_osd)) ShowWindow(_osd, SW_MINIMIZE);
        if (_xamlOsd != IntPtr.Zero && IsWindow(_xamlOsd) && IsWindowVisible(_xamlOsd)) ShowWindow(_xamlOsd, SW_HIDE);
    }

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return; // hanya jendela utuh
        if (hwnd == _osd) { if (!IsIconic(hwnd)) ShowWindow(hwnd, SW_MINIMIZE); return; }
        _cls.Clear();
        if (GetClassName(hwnd, _cls, _cls.Capacity) == 0) return;
        string cls = _cls.ToString();
        if (cls == "XamlExplorerHostIslandWindow")
        {
            // langsung sembunyikan sebelum sempat tergambar; explorer memunculkannya lagi di penekanan berikutnya
            if (hwnd == _xamlOsd || IsXamlOsdShape(hwnd)) { _xamlOsd = hwnd; ShowWindow(hwnd, SW_HIDE); }
            return;
        }
        if (cls != "NativeHWNDHost") return;
        if (FindWindowEx(hwnd, IntPtr.Zero, "DirectUIHWND", null) == IntPtr.Zero) return;
        _osd = hwnd; // jendela OSD baru dibuat (misalnya pertama kali tombol ditekan / explorer restart)
        ShowWindow(hwnd, SW_MINIMIZE);
    }

    static IntPtr FindOsd()
    {
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "NativeHWNDHost", null)) != IntPtr.Zero)
            if (FindWindowEx(h, IntPtr.Zero, "DirectUIHWND", null) != IntPtr.Zero) return h;
        return IntPtr.Zero;
    }

    static void RestoreWindow(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsIconic(h)) return;
        ShowWindow(h, SW_RESTORE); // kembali normal...
        ShowWindow(h, SW_HIDE);    // ...tapi tetap tersembunyi sampai Windows memunculkannya sendiri
    }

    /// <summary>Darurat: kembalikan pop-up Windows tanpa perlu instance (dipakai --restore-taskbar / crash).</summary>
    public static void RestoreNative() => RestoreWindow(FindOsd());

    public void Dispose() => Stop();
}
