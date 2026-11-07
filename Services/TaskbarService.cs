using System;
using System.IO;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>
/// Menyembunyikan taskbar asli Windows dan mengembalikannya lagi.
/// - Taskbar diset "auto-hide" supaya jendela maximize memakai layar penuh, lalu jendelanya disembunyikan.
/// - Kondisi awal disimpan di file, jadi kalau aplikasi crash, kondisi asli tetap bisa dipulihkan
///   saat aplikasi dibuka lagi (atau saat fitur ini dimatikan).
/// </summary>
public static class TaskbarService
{
    static bool _hidden;
    static bool _peeking;
    static int _originalState = -1;

    static string FlagPath => Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "taskbar-state.txt");

    public static bool IsHidden => _hidden;

    public static void Hide()
    {
        try
        {
            if (_originalState < 0)
            {
                // file sisa dari sesi sebelumnya (mis. crash) = kondisi asli yang sebenarnya
                if (File.Exists(FlagPath) && int.TryParse(File.ReadAllText(FlagPath).Trim(), out int saved))
                    _originalState = saved;
                else
                {
                    _originalState = GetState();
                    Directory.CreateDirectory(Path.GetDirectoryName(FlagPath)!);
                    File.WriteAllText(FlagPath, _originalState.ToString());
                }
            }
            SetState(Native.ABS_AUTOHIDE);
            ShowTrays(false);
            _hidden = true;
        }
        catch { }
    }

    /// <summary>Sembunyikan lagi kalau Windows memunculkan taskbar sendiri (mis. explorer restart).</summary>
    public static void Reapply()
    {
        if (!_hidden || _peeking) return;
        IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero && Native.IsWindowVisible(tray)) ShowTrays(false);
        else if (Settings.Current.HideSecondaryTaskbars) ShowSecondary(false); // monitor baru dicolok
    }

    /// <summary>Terapkan ulang pilihan "sembunyikan taskbar monitor kedua" tanpa menyentuh taskbar utama.</summary>
    public static void ApplySecondary()
    {
        if (!_hidden || _peeking) return;
        ShowSecondary(!Settings.Current.HideSecondaryTaskbars);
    }

    /// <summary>Kembalikan taskbar seperti semula.</summary>
    public static void Restore()
    {
        try
        {
            int orig = _originalState;
            if (orig < 0 && File.Exists(FlagPath) && int.TryParse(File.ReadAllText(FlagPath).Trim(), out int saved))
                orig = saved;
            if (orig < 0 && !_hidden) return;

            SetState(orig < 0 ? 0 : orig);
            ShowTrays(true);
            if (File.Exists(FlagPath)) File.Delete(FlagPath);
        }
        catch { }
        _hidden = false;
        _originalState = -1;
    }

    /// <summary>Tampilkan taskbar asli sebentar (untuk akses ikon system tray).</summary>
    public static void PeekOriginal(TimeSpan duration)
    {
        if (!_hidden) return;
        _peeking = true;
        ShowTrays(true);
        var t = new System.Windows.Threading.DispatcherTimer { Interval = duration };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _peeking = false;
            if (_hidden) ShowTrays(false);
        };
        t.Start();
    }

    static void ShowTrays(bool show)
    {
        int cmd = show ? Native.SW_SHOWNA : Native.SW_HIDE;
        IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero) Native.ShowWindow(tray, cmd);

        // taskbar di monitor kedua dan seterusnya: bawaan tetap tampil, kecuali dipilih ikut disembunyikan
        ShowSecondary(show || !Settings.Current.HideSecondaryTaskbars);
    }

    static void ShowSecondary(bool show)
    {
        int cmd = show ? Native.SW_SHOWNA : Native.SW_HIDE;
        IntPtr h = IntPtr.Zero;
        while ((h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            if (Native.IsWindowVisible(h) != show) Native.ShowWindow(h, cmd);
    }

    static int GetState()
    {
        var d = new Native.APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>() };
        return (int)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref d).ToUInt32();
    }

    static void SetState(int state)
    {
        var d = new Native.APPBARDATA
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>(),
            hWnd = Native.FindWindow("Shell_TrayWnd", null),
            lParam = (IntPtr)state,
        };
        Native.SHAppBarMessage(Native.ABM_SETSTATE, ref d);
    }
}
