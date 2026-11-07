using System;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>Kondisi laptop: CPU, RAM, baterai. Hanya dibaca saat island terbuka.</summary>
public static class SystemStats
{
    static ulong _prevIdle, _prevKernel, _prevUser;

    /// <summary>Persentase CPU sejak pemanggilan sebelumnya.</summary>
    public static double CpuPercent()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        ulong dIdle = idle.Value - _prevIdle;
        ulong dKernel = kernel.Value - _prevKernel; // kernel time sudah termasuk idle
        ulong dUser = user.Value - _prevUser;
        _prevIdle = idle.Value; _prevKernel = kernel.Value; _prevUser = user.Value;

        ulong total = dKernel + dUser;
        if (total == 0) return 0;
        return (total - dIdle) * 100.0 / total;
    }

    public static int RamPercent()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        return Native.GlobalMemoryStatusEx(ref m) ? (int)m.dwMemoryLoad : 0;
    }

    public readonly record struct BatteryInfo(bool HasBattery, int Percent, bool Charging);

    public static BatteryInfo Battery()
    {
        if (!Native.GetSystemPowerStatus(out var s)) return new BatteryInfo(false, 0, false);
        bool has = (s.BatteryFlag & 128) == 0 && s.BatteryLifePercent != 255;
        return new BatteryInfo(has, s.BatteryLifePercent == 255 ? 100 : s.BatteryLifePercent, s.ACLineStatus == 1);
    }

    // ---------- disk ----------

    /// <summary>Pemakaian ruang disk sistem (C:) dalam persen, plus sisa ruang (GB).</summary>
    public static (int percent, double freeGb) Disk()
    {
        try
        {
            string root = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var d = new System.IO.DriveInfo(root);
            if (!d.IsReady || d.TotalSize <= 0) return (0, 0);
            double used = 1 - (double)d.AvailableFreeSpace / d.TotalSize;
            return ((int)Math.Round(used * 100), d.AvailableFreeSpace / 1073741824.0);
        }
        catch { return (0, 0); }
    }

    // ---------- GPU (penghitung kinerja Windows, sama seperti Task Manager) ----------

    [System.Runtime.InteropServices.DllImport("pdh.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [System.Runtime.InteropServices.DllImport("pdh.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [System.Runtime.InteropServices.DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);
    [System.Runtime.InteropServices.DllImport("pdh.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    const uint PDH_FMT_DOUBLE = 0x200, PDH_MORE_DATA = 0x800007D2;
    static IntPtr _gpuQuery, _gpuCounter;
    static bool _gpuFailed;

    /// <summary>Pemakaian GPU (mesin 3D) dalam persen; -1 kalau tidak tersedia. Dipanggil kira-kira tiap detik.</summary>
    public static double GpuPercent()
    {
        if (_gpuFailed) return -1;
        try
        {
            if (_gpuQuery == IntPtr.Zero)
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _gpuQuery) != 0 ||
                    PdhAddEnglishCounter(_gpuQuery, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0)
                { _gpuFailed = true; return -1; }
                PdhCollectQueryData(_gpuQuery); // butuh dua sampel
                return 0;
            }
            if (PdhCollectQueryData(_gpuQuery) != 0) return 0;
            uint size = 0;
            uint r = PdhGetFormattedCounterArray(_gpuCounter, PDH_FMT_DOUBLE, ref size, out uint count, IntPtr.Zero);
            if (r != PDH_MORE_DATA || size == 0) return 0;
            IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(_gpuCounter, PDH_FMT_DOUBLE, ref size, out count, buf) != 0) return 0;
                double sum = 0;
                const int itemSize = 24; // x64: nama (pointer 8) + status (4) + celah (4) + nilai double (8)
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = buf + i * itemSize;
                    int status = System.Runtime.InteropServices.Marshal.ReadInt32(item, 8);
                    if (status != 0) continue;
                    double v = BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(item, 16));
                    if (v > 0 && v <= 100) sum += v;
                }
                return Math.Min(100, sum);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
        }
        catch { _gpuFailed = true; return -1; }
    }

    // ---------- aktivitas disk (seperti "Active time" di Task Manager) ----------

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE { public uint CStatus; public uint pad; public double doubleValue; }
    [System.Runtime.InteropServices.DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE value);

    static IntPtr _diskQuery, _diskCounter;
    static bool _diskFailed;

    /// <summary>Persentase waktu disk sedang sibuk (0-100); -1 kalau tidak tersedia.</summary>
    public static double DiskActivePercent()
    {
        if (_diskFailed) return -1;
        try
        {
            if (_diskQuery == IntPtr.Zero)
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _diskQuery) != 0 ||
                    PdhAddEnglishCounter(_diskQuery, @"\PhysicalDisk(_Total)\% Idle Time", IntPtr.Zero, out _diskCounter) != 0)
                { _diskFailed = true; return -1; }
                PdhCollectQueryData(_diskQuery);
                return 0;
            }
            if (PdhCollectQueryData(_diskQuery) != 0) return 0;
            if (PdhGetFormattedCounterValue(_diskCounter, PDH_FMT_DOUBLE, IntPtr.Zero, out var v) != 0 || v.CStatus != 0) return 0;
            return Math.Clamp(100 - v.doubleValue, 0, 100);
        }
        catch { _diskFailed = true; return -1; }
    }

    // ---------- jaringan ----------

    static long _netPrevBytes = -1, _netPrevTick;

    /// <summary>Kecepatan jaringan total (unduh + unggah) dalam byte per detik sejak panggilan sebelumnya.</summary>
    public static (double down, double up) NetworkSpeed()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var st = ni.GetIPStatistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
        }
        catch { return (0, 0); }
        long now = Environment.TickCount64;
        double sec = (now - _netPrevTick) / 1000.0;
        (double, double) result = (0, 0);
        if (_netPrevBytes >= 0 && sec > 0.2)
            result = (Math.Max(0, (rx - _netRx) / sec), Math.Max(0, (tx - _netTx) / sec));
        _netRx = rx; _netTx = tx; _netPrevBytes = rx + tx; _netPrevTick = now;
        return result;
    }
    static long _netRx, _netTx;

    public static string FormatSpeed(double bytesPerSec)
    {
        double kb = bytesPerSec / 1024;
        return kb < 1000 ? $"{kb:0} KB/s" : $"{kb / 1024:0.0} MB/s";
    }

    /// <summary>RAM terpakai dan total (GB).</summary>
    public static (double usedGb, double totalGb) RamGb()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref m)) return (0, 0);
        double total = m.ullTotalPhys / 1073741824.0, avail = m.ullAvailPhys / 1073741824.0;
        return (total - avail, total);
    }
}
