using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DynamicIsland.Core;

/// <summary>
/// Pengukur performa untuk pengembangan. MATI kecuali "perf-on.txt" di folder aplikasi/proyek berisi "on".
/// Saat aktif: tiap 0,5 detik mencatat CPU (% seperti Task Manager), RAM, dan apakah animasi sedang berjalan
/// ke perf.csv. Label fase dibaca dari perf-label.txt.
/// Memakai GetProcessTimes / GetProcessMemoryInfo untuk proses ini saja (murah) — bukan
/// System.Diagnostics.Process, yang memindai semua proses di komputer setiap kali dibaca.
/// </summary>
public static class PerfProbe
{
    [StructLayout(LayoutKind.Sequential)]
    struct MemCounters
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                       QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
    [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr h, out MemCounters c, int size);

    static Timer? _timer;
    static long _lastCpu;
    static long _lastAt;
    static bool _on;

    static string _csv = "perf.csv";

    /// <param name="name">"main" → perf.csv (seperti dulu); proses lain → perf-&lt;name&gt;.csv.</param>
    public static void Start(string name = "main")
    {
        _csv = name == "main" ? "perf.csv" : $"perf-{name}.csv";
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
    }

    static long CpuTicks()
    {
        GetProcessTimes(GetCurrentProcess(), out _, out _, out long k, out long u);
        return k + u; // satuan 100 ns
    }

    static void Tick()
    {
        try
        {
            string dir = CrashGuard.BaseFolder();
            string flag = Path.Combine(dir, "perf-on.txt");
            bool want = File.Exists(flag) && File.ReadAllText(flag).Trim().StartsWith("on", StringComparison.OrdinalIgnoreCase);
            if (want != _on)
            {
                _on = want;
                var iv = want ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(5);
                _timer?.Change(iv, iv);
                if (want)
                {
                    _lastCpu = CpuTicks();
                    _lastAt = DateTime.UtcNow.Ticks;
                    string csv = Path.Combine(dir, _csv);
                    if (!File.Exists(csv)) File.WriteAllText(csv, "time,label,cpu_pct,working_set_mb,private_mb,gc_heap_mb,animating,threads\r\n");
                }
                return;
            }
            if (!_on) return;

            long cpu = CpuTicks(), now = DateTime.UtcNow.Ticks;
            double pct = now > _lastAt ? (cpu - _lastCpu) / (double)(now - _lastAt) / Environment.ProcessorCount * 100 : 0;
            _lastCpu = cpu;
            _lastAt = now;
            GetProcessMemoryInfo(GetCurrentProcess(), out var mem, Marshal.SizeOf<MemCounters>());
            string label = "";
            try
            {
                string lf = Path.Combine(dir, "perf-label.txt");
                if (File.Exists(lf)) label = File.ReadAllText(lf).Trim().Replace(",", " ");
            }
            catch { }
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string line = string.Join(",",
                DateTime.Now.ToString("HH:mm:ss.f"), label,
                pct.ToString("0.00", ci),
                (mem.WorkingSetSize.ToUInt64() / 1048576.0).ToString("0.0", ci),
                (mem.PrivateUsage.ToUInt64() / 1048576.0).ToString("0.0", ci),
                (GC.GetTotalMemory(false) / 1048576.0).ToString("0.0", ci),
                FrameLoop.IsActive ? 1 : 0, 0);
            File.AppendAllText(Path.Combine(dir, _csv), line + "\r\n");
        }
        catch { }
    }
}
