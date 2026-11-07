using System;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace DynamicIsland.Core;

/// <summary>
/// "Salin laporan error": versi app + versi Windows + isi crash.log ke clipboard,
/// supaya teman bisa langsung menempelkannya di WhatsApp.
/// </summary>
public static class ErrorReport
{
    // pesan WhatsApp maksimal ±65 ribu karakter; ambil bagian log yang paling baru saja
    const int MaxLogChars = 20000;

    public static string Build()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== Laporan error Dynamic Island ==");
        sb.AppendLine($"Versi app : {CrashGuard.AppVersion()}");
        sb.AppendLine($"Windows   : {WindowsVersion()}");
        sb.AppendLine($"Layar     : {SystemParameters.PrimaryScreenWidth:0}x{SystemParameters.PrimaryScreenHeight:0} (skala {App.DpiScale * 100:0}%), monitor: {MonitorCount()}");
        sb.AppendLine($"Bahasa    : {(L.English ? "English" : "Indonesia")}");
        sb.AppendLine($"Waktu     : {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        string log = "";
        try { if (File.Exists(CrashGuard.LogPath)) log = File.ReadAllText(CrashGuard.LogPath); } catch { }
        if (string.IsNullOrWhiteSpace(log))
        {
            sb.AppendLine("(crash.log kosong — belum ada error yang tercatat)");
        }
        else
        {
            if (log.Length > MaxLogChars)
            {
                sb.AppendLine($"(log dipotong: hanya {MaxLogChars} karakter terakhir)");
                log = log[^MaxLogChars..];
            }
            sb.AppendLine("--- crash.log ---");
            sb.Append(log.TrimEnd());
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static bool CopyToClipboard()
    {
        string text = Build();
        // clipboard kadang sedang dipakai aplikasi lain: coba beberapa kali
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(text, true); return true; }
            catch { System.Threading.Thread.Sleep(60); }
        }
        return false;
    }

    /// <summary>Contoh: "Windows 11 Home 24H2 (build 26100.2605)".</summary>
    static string WindowsVersion()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = k?.GetValue("ProductName") as string ?? "Windows";
            string display = k?.GetValue("DisplayVersion") as string ?? "";
            string build = k?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
            object? ubr = k?.GetValue("UBR");
            // ProductName masih menulis "Windows 10" di Windows 11
            if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            return $"{product} {display} (build {build}{(ubr != null ? "." + ubr : "")})".Replace("  ", " ");
        }
        catch { return Environment.OSVersion.VersionString; }
    }

    static int MonitorCount()
    {
        try { return GetSystemMetrics(80); } // SM_CMONITORS
        catch { return 1; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
}
