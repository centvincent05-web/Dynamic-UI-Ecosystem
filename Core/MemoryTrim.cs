using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DynamicIsland.Core;

/// <summary>
/// Merapikan memori secara berkala: membuang sisa objek .NET yang sudah tidak dipakai lalu
/// mengembalikan halaman memori yang menganggur ke Windows. Hanya saat tidak ada animasi,
/// supaya tidak terasa patah-patah.
/// </summary>
public static class MemoryTrim
{
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    static DispatcherTimer? _timer;
    static long _lastTrimmedWs;
    static long _lastTrimTick;
    /// <summary>True kalau island sedang terbuka / dipakai (diisi oleh App).</summary>
    public static Func<bool>? BusyCheck;

    public static void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = TimeSpan.FromSeconds(45);
            long ws = Environment.WorkingSet;
            bool first = _lastTrimTick == 0;
            bool grew = ws > _lastTrimmedWs + 12L * 1024 * 1024;              // naik > 12 MB sejak terakhir dirapikan
            bool stale = Environment.TickCount64 - _lastTrimTick > 5 * 60_000; // atau sudah 5 menit
            if (!first && !grew && !stale) return;
            if (BusyCheck?.Invoke() ?? false) return; // island sedang terbuka / notifikasi: jangan ganggu
            // bar musik di notch kecil membuat animasi terus berjalan selama lagu diputar. Dulu perapian
            // memori dilewati sepanjang lagu, sehingga memori terus menumpuk (120–140 MB). Sekarang saat itu
            // dipakai cara ringan: hanya mengembalikan halaman memori yang menganggur ke Windows, tanpa
            // menjalankan GC penuh (yang bisa membuat bar musik patah sesaat).
            if (FrameLoop.IsActive) { TrimLight(); return; }
            Trim();
        };
        _timer.Start();
    }

    public static void Trim()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }
        catch { }
        _lastTrimmedWs = Environment.WorkingSet;
        _lastTrimTick = Environment.TickCount64;
    }

    static DispatcherTimer? _soon;

    /// <summary>
    /// Island baru ditutup: rapikan sebentar lagi (bukan menunggu jadwal berkala 45 detik), supaya memori
    /// yang dipakai saat island terbuka (gambar halaman, ikon, permukaan jendela) cepat kembali ke Windows.
    /// </summary>
    public static void Soon()
    {
        _soon ??= CreateSoon();
        _soon.Stop();
        _soon.Start();
    }

    static DispatcherTimer CreateSoon()
    {
        var t = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(3) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (BusyCheck?.Invoke() ?? false) return; // dibuka lagi: tunggu sampai ditutup berikutnya
            if (FrameLoop.IsActive) TrimLight(); else Trim();
        };
        return t;
    }

    /// <summary>Tanpa GC paksa: hanya halaman memori yang menganggur dikembalikan ke Windows (sangat cepat).</summary>
    public static void TrimLight()
    {
        try
        {
            GC.Collect(0, GCCollectionMode.Optimized, blocking: false); // generasi 0 saja, tidak menghentikan animasi
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }
        catch { }
        _lastTrimmedWs = Environment.WorkingSet;
        _lastTrimTick = Environment.TickCount64;
    }

    public static void Stop() => _timer?.Stop();
}
