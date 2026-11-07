using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DynamicIsland.Core;

/// <summary>
/// Tombol "Tidur" yang benar untuk dua jenis laptop:
/// - Laptop Modern Standby (S0 low power idle, kebanyakan laptop baru): S3 tidak ada, jadi
///   SetSuspendState(false, ...) malah membuat HIBERNATE (terlihat seperti laptop mati).
///   Di laptop ini cukup matikan layar — Windows sendiri lalu masuk standby, sama seperti Start → Sleep.
/// - Laptop lama dengan S3: SetSuspendState(false, false, false) = sleep biasa.
/// </summary>
public static class PowerActions
{
    // Offset di SYSTEM_POWER_CAPABILITIES (semua field sebelum ini 1 byte: 15 BOOLEAN + 2 BYTE + 3 BOOLEAN)
    const int OffsetSystemS3 = 5;
    const int OffsetAoAc = 20;

    [DllImport("powrprof.dll", SetLastError = true)] static extern bool GetPwrCapabilities(byte[] caps);
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    const uint WM_SYSCOMMAND = 0x0112;
    static readonly IntPtr HWND_BROADCAST = (IntPtr)0xFFFF;
    static readonly IntPtr SC_MONITORPOWER = (IntPtr)0xF170;

    /// <summary>True kalau laptop memakai Modern Standby (AoAc).</summary>
    public static bool IsModernStandby(out bool hasS3)
    {
        hasS3 = false;
        try
        {
            var buf = new byte[256]; // struktur aslinya ±76 byte, sisanya cadangan
            if (!GetPwrCapabilities(buf)) return false;
            hasS3 = buf[OffsetSystemS3] != 0;
            return buf[OffsetAoAc] != 0;
        }
        catch { return false; }
    }

    public static void Sleep()
    {
        bool modern = IsModernStandby(out bool hasS3);
        if (modern || !hasS3)
        {
            // jeda sebentar supaya gerakan/klik mouse saat menutup menu tidak langsung menyalakan layar lagi
            Task.Delay(600).ContinueWith(_ =>
                PostMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, (IntPtr)2)); // 2 = layar mati → standby
        }
        else
        {
            SetSuspendState(false, false, false);
        }
    }
}
