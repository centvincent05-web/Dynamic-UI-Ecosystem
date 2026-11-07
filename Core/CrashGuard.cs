using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DynamicIsland.Services;

namespace DynamicIsland.Core;

/// <summary>
/// Pemulihan saat crash + log error.
/// - <see cref="Log"/>: error dicatat ke crash.log (versi app, versi Windows, detail error).
///   Error yang sama berulang-ulang tidak membanjiri file, dan file dipotong kalau sudah besar.
/// - Penjaga (watchdog): proses kecil "DynamicIsland.exe --watchdog &lt;pid&gt;" yang menunggu app utama.
///   Kalau app utama berhenti karena crash (bukan ditutup biasa / End task), taskbar dikembalikan,
///   lalu app dijalankan ulang otomatis (kalau opsinya menyala). Crash 3x dalam 5 menit = berhenti mencoba.
/// </summary>
public static class CrashGuard
{
    const long MaxLogBytes = 512 * 1024;
    static readonly object _lock = new();
    static string? _logPath;
    static Process? _watchdog;
    /// <summary>True kalau app ini dijalankan ulang oleh penjaga (penjaganya masih hidup).</summary>
    public static bool StartedByWatchdog { get; set; }

    // ---------------------------------------------------------------- log

    /// <summary>Folder proyek (kalau dari hasil build bin\Release\&lt;tfm&gt;\) atau folder exe (paket siap pakai).</summary>
    public static string BaseFolder()
    {
        string bin = AppContext.BaseDirectory;
        char s = Path.DirectorySeparatorChar;
        return bin.Contains(s + "bin" + s, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Path.Combine(bin, "..", "..", ".."))
            : bin;
    }

    /// <summary>Lokasi crash.log. Kalau folder app tidak bisa ditulis (mis. Program Files), pakai %AppData%\DynamicIsland.</summary>
    public static string LogPath
    {
        get
        {
            if (_logPath != null) return _logPath;
            string primary = Path.Combine(BaseFolder(), "crash.log");
            try
            {
                using (new FileStream(primary, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                return _logPath = primary;
            }
            catch
            {
                string dir = Path.GetDirectoryName(Settings.FilePath)!;
                try { Directory.CreateDirectory(dir); } catch { }
                return _logPath = Path.Combine(dir, "crash.log");
            }
        }
    }

    // error yang sama (jenis + pesan + lokasi) maksimal 3x per 10 menit, sisanya hanya dihitung
    static readonly Dictionary<string, (DateTime first, int count)> _seen = new();

    public static void Log(Exception? ex, string context = "")
    {
        if (ex == null) return;
        try
        {
            string key = ex.GetType().FullName + "|" + ex.Message + "|" + FirstFrame(ex);
            int skipped = 0;
            lock (_lock)
            {
                var now = DateTime.Now;
                if (_seen.TryGetValue(key, out var e) && now - e.first < TimeSpan.FromMinutes(10))
                {
                    _seen[key] = (e.first, e.count + 1);
                    if (e.count + 1 > 3) return;
                }
                else
                {
                    if (_seen.TryGetValue(key, out var old) && old.count > 3) skipped = old.count - 3;
                    _seen[key] = (now, 1);
                }
            }
            string head = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{AppVersion()} | Windows {Environment.OSVersion.Version}" +
                          (context.Length > 0 ? " | " + context : "");
            string extra = skipped > 0 ? $"(error yang sama sebelumnya terulang {skipped}x lagi, tidak dicatat)\r\n" : "";
            Write(head + "\r\n" + extra + ex + "\r\n\r\n");
        }
        catch { }
    }

    public static void LogLine(string text)
    {
        try { Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{AppVersion()} | {text}\r\n\r\n"); } catch { }
    }

    static void Write(string text)
    {
        lock (_lock)
        {
            try
            {
                string path = LogPath;
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxLogBytes)
                {
                    // simpan yang lama sebagai crash.old.log, mulai file baru
                    string old = Path.Combine(fi.DirectoryName!, "crash.old.log");
                    try { File.Copy(path, old, overwrite: true); File.Delete(path); } catch { }
                }
                File.AppendAllText(path, text);
            }
            catch { }
        }
    }

    static string FirstFrame(Exception ex)
    {
        string st = ex.StackTrace ?? "";
        int nl = st.IndexOf('\n');
        return (nl > 0 ? st[..nl] : st).Trim();
    }

    public static string AppVersion()
    {
        var v = typeof(CrashGuard).Assembly.GetName().Version;
        return v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>Buka crash.log di Notepad (atau foldernya kalau belum ada error).</summary>
    public static void OpenLog()
    {
        try
        {
            string path = LogPath;
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
            else
                System.Windows.MessageBox.Show(L.T("Belum ada error yang tercatat.") + "\n\n" + path, "Dynamic Island");
        }
        catch { }
    }

    // ---------------------------------------------------------------- penjaga

    /// <summary>Nyalakan penjaga kalau opsinya menyala dan belum ada penjaga.</summary>
    public static void EnsureWatchdog()
    {
        if (!Settings.Current.AutoRecover || StartedByWatchdog) return;
        if (_watchdog != null && !_watchdog.HasExited) return;
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe == null) return;
            _watchdog = Process.Start(new ProcessStartInfo(exe, $"--watchdog {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) { Log(ex, "penjaga gagal dinyalakan"); }
    }

    /// <summary>
    /// Kode keluar karena crash: NTSTATUS / HRESULT (0x8xxxxxxx ke atas), mis. 0xE0434352 (error .NET),
    /// 0xC0000005 (akses memori), 0xC00000FD (stack overflow). Ditutup biasa = 0, End task / taskkill = 1.
    /// </summary>
    static bool IsCrashCode(int code) => (uint)code >= 0x80000000u;

    /// <summary>Dijalankan oleh proses penjaga. Tidak membuka jendela apa pun.</summary>
    public static void RunWatchdog(int pid)
    {
        Process main;
        try { main = Process.GetProcessById(pid); _ = main.Handle; }
        catch { return; } // app utama sudah tidak ada
        string? exe = Environment.ProcessPath;
        var crashes = new List<DateTime>();
        MemoryTrim.Trim(); // penjaga cukup beberapa MB saja

        while (true)
        {
            try { main.WaitForExit(); } catch { return; }
            int code;
            try { code = main.ExitCode; } catch { return; }
            if (!IsCrashCode(code)) return; // ditutup biasa / End task: penjaga ikut selesai

            Settings.Load(); // ambil pilihan terbaru (mungkin diubah sebelum crash)
            LogLine($"Dynamic Island berhenti mendadak (kode keluar 0x{(uint)code:X8}).");
            try { TaskbarService.Restore(); } catch { }
            try { NativeOsdHider.RestoreNative(); } catch { }

            if (!Settings.Current.AutoRecover || exe == null) return;
            var now = DateTime.Now;
            crashes.Add(now);
            crashes.RemoveAll(t => now - t > TimeSpan.FromMinutes(5));
            if (crashes.Count >= 3)
            {
                LogLine("Crash 3x dalam 5 menit: tidak dijalankan ulang otomatis.");
                System.Windows.MessageBox.Show(
                    L.T("Dynamic Island berhenti karena error yang berulang, jadi tidak dijalankan ulang otomatis.") +
                    "\n\n" + L.T("Detail error: ") + LogPath,
                    "Dynamic Island", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            Thread.Sleep(1500); // beri waktu Windows membersihkan sisa proses lama
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, "--recovered") { UseShellExecute = false });
                if (p == null) return;
                main = p;
                LogLine("Dijalankan ulang otomatis oleh penjaga.");
            }
            catch (Exception ex) { Log(ex, "gagal menjalankan ulang"); return; }
        }
    }
}
