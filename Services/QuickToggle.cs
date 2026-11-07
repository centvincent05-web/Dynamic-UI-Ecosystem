using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>
/// Menyalakan / mematikan fitur Windows yang tidak punya API resmi (Night light, Energy saver,
/// Jangan ganggu) dengan "menekan" tombolnya di Quick Settings / Notification Center lewat
/// UI Automation (fitur aksesibilitas resmi Windows). Panel Windows muncul sekejap lalu ditutup.
/// </summary>
public static class QuickToggle
{
    public enum Kind { NightLight, EnergySaver, DoNotDisturb }

    // nama tombol dalam bahasa Inggris & Indonesia (Windows bisa memakai salah satunya)
    static string[] NamesOf(Kind k) => k switch
    {
        Kind.NightLight => new[] { "Night light", "Lampu malam", "Cahaya malam" },
        Kind.EnergySaver => new[] { "Energy saver", "Battery saver", "Penghemat energi", "Penghemat baterai", "Penghemat daya" },
        _ => new[] { "Do not disturb", "Jangan ganggu" },
    };

    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Hasil: true = sekarang menyala, false = mati, null = tombol tidak ditemukan.</summary>
    public static async Task<bool?> ToggleAsync(Kind kind)
    {
        if (!await Gate.WaitAsync(0)) return null; // sedang memproses tombol lain
        try { return await Task.Run(() => Toggle(kind)); }
        catch { return null; }
        finally { Gate.Release(); }
    }

    /// <summary>Folder untuk catatan diagnosa (diisi oleh App).</summary>
    public static string? LogFolder;

    static bool? Toggle(Kind kind)
    {
        var names = NamesOf(kind);
        // Win+A = Quick Settings, Win+N = Notification Center (tempat tombol Jangan ganggu)
        Native.SendWinCombo(kind == Kind.DoNotDisturb ? (byte)0x4E : (byte)0x41);
        AutomationElement? button = null;
        long deadline = Environment.TickCount64 + 4000; // panel Quick Settings kadang lambat terbuka
        while (button == null && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(150);
            foreach (var root in PanelRoots())
            {
                try { button = Find(root, names); } catch { }
                if (button != null) break;
            }
        }
        if (button == null) { WriteDiagnostics(kind); Close(); return null; }

        bool? state = null;
        try
        {
            if (button.TryGetCurrentPattern(TogglePattern.Pattern, out var tp))
            {
                var toggle = (TogglePattern)tp;
                // status sebelum ditekan; setelah ditekan Windows kadang belum memperbarui statusnya,
                // jadi tunggu sampai berubah (maks 1,5 detik), kalau tidak anggap kebalikannya
                bool before = toggle.Current.ToggleState == ToggleState.On;
                toggle.Toggle();
                state = !before;
                long until = Environment.TickCount64 + 1500;
                while (Environment.TickCount64 < until)
                {
                    Thread.Sleep(100);
                    bool now;
                    try { now = toggle.Current.ToggleState == ToggleState.On; } catch { break; }
                    if (now != before) { state = now; break; }
                }
            }
            else if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
            {
                ((InvokePattern)ip).Invoke();
                state = true;
            }
            else if (button.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp))
            {
                ((SelectionItemPattern)sp).Select();
                state = true;
            }
        }
        catch { }
        Thread.Sleep(150);
        Close();
        return state;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    /// <summary>
    /// Jendela tempat panel Windows berada: jendela aktif, plus semua jendela anaknya.
    /// Isi Quick Settings (XAML) ada di jendela anak tersendiri (DesktopWindowContentBridge),
    /// jadi pencarian dari jendela utamanya saja tidak menemukan tombol apa pun.
    /// </summary>
    static System.Collections.Generic.IEnumerable<AutomationElement> PanelRoots()
    {
        var handles = new System.Collections.Generic.List<IntPtr>();
        IntPtr fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            handles.Add(fg);
            EnumChildWindows(fg, (h, _) => { handles.Add(h); return true; }, IntPtr.Zero);
        }
        // anak-anak dulu: di sanalah isi panel berada
        handles.Reverse();
        foreach (var h in handles)
        {
            AutomationElement? el = null;
            try { el = AutomationElement.FromHandle(h); } catch { }
            if (el != null) yield return el;
        }
    }

    static AutomationElement? Find(AutomationElement root, string[] names)
    {
        AutomationElement? invokable = null;
        foreach (AutomationElement el in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            string n, id;
            try { n = el.Current.Name ?? ""; id = el.Current.AutomationId ?? ""; } catch { continue; }
            bool match = names.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase)) ||
                         names.Any(x => id.Replace("_", "").Contains(x.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            if (!match) continue;
            // utamakan tombol toggle (nyala/mati); tombol biasa hanya cadangan (bisa jadi membuka sub-halaman)
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out _)) return el;
            if (invokable == null && (el.TryGetCurrentPattern(InvokePattern.Pattern, out _) || el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)))
                invokable = el;
        }
        return invokable;
    }

    /// <summary>Tombol tidak ketemu: catat nama tombol yang terlihat di panel (hanya nama kontrol) untuk diperbaiki.</summary>
    static void WriteDiagnostics(Kind kind)
    {
        if (LogFolder == null) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] tombol {kind} tidak ketemu. Jendela aktif: {Native.ClassNameOf(Native.GetForegroundWindow())}");
            foreach (var root in PanelRoots())
            {
                string rc = "";
                try { rc = root.Current.ClassName + " | " + root.Current.Name; } catch { }
                AutomationElementCollection all;
                try { all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition); } catch { continue; }
                sb.AppendLine($"== {rc} | elemen: {all.Count}");
                int count = 0;
                foreach (AutomationElement el in all)
                {
                    if (++count > 400) break;
                    try
                    {
                        var c = el.Current;
                        bool tog = el.TryGetCurrentPattern(TogglePattern.Pattern, out _), inv = el.TryGetCurrentPattern(InvokePattern.Pattern, out _);
                        if (!tog && !inv) continue;
                        sb.AppendLine($"{c.ControlType.ProgrammaticName} | name={c.Name} | id={c.AutomationId} | toggle={tog} invoke={inv}");
                    }
                    catch { }
                }
            }
            System.IO.File.AppendAllText(System.IO.Path.Combine(LogFolder, "quicktoggle.log"), sb.ToString() + "\r\n");
        }
        catch { }
    }

    /// <summary>Tutup panel Windows yang tadi dibuka (Esc).</summary>
    static void Close()
    {
        Native.keybd_event(0x1B, 0, 0, UIntPtr.Zero);
        Native.keybd_event(0x1B, 0, 2 /* KEYUP */, UIntPtr.Zero);
    }

    /// <summary>Energy saver sedang aktif (cadangan lama: status baterai Windows).</summary>
    public static bool EnergySaverOn() => Native.GetSystemPowerStatus(out var s) && s.SystemStatusFlag == 1;

    // =====================================================================
    // Membaca status langsung dari Windows (tanpa membuka panel), supaya indikator
    // tetap benar walaupun fiturnya dinyalakan lewat Win+A / Pengaturan.
    // =====================================================================

    /// <summary>Status Energy saver berubah (dipanggil dari thread lain).</summary>
    public static event Action? StateChanged;

    // pembaca yang terbukti tidak cocok dengan Windows di laptop ini tidak dipakai lagi
    static readonly bool[] Untrusted = new bool[3];
    public static void MarkUntrusted(Kind k) => Untrusted[(int)k] = true;

    /// <summary>true/false = status dari Windows; null = tidak bisa dibaca (pakai status terakhir).</summary>
    public static bool? ReadState(Kind k)
    {
        if (Untrusted[(int)k]) return null;
        try
        {
            return k switch
            {
                Kind.NightLight => ReadNightLight(),
                Kind.EnergySaver => ReadEnergySaver(),
                _ => ReadDoNotDisturb(),
            };
        }
        catch { return null; }
    }

    const string NightLightKey = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\" +
        @"default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    static bool? ReadNightLight()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NightLightKey);
        if (key?.GetValue("Data") is not byte[] d || d.Length < 19) return null;
        return d[18] == 0x15 ? true : d[18] == 0x13 ? false : null;
    }

    // Jangan ganggu / Focus: status "quiet hours" Windows (WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED)
    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr scope, out uint changeStamp, byte[] buffer, ref uint size);

    static bool? ReadDoNotDisturb()
    {
        ulong name = 0x0D83063EA3BF1C75;
        var buf = new byte[4];
        uint size = (uint)buf.Length;
        if (NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out _, buf, ref size) != 0) return null;
        if (size == 0) return false;
        return BitConverter.ToInt32(buf, 0) != 0; // 0 = mati, 1 = prioritas saja, 2 = alarm saja
    }

    // Energy saver: Windows memberi tahu "mode daya efektif" setiap kali berubah (0 = penghemat aktif)
    delegate void EffectiveModeCallback(int mode, IntPtr context);
    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    static extern int PowerRegisterForEffectivePowerModeNotifications(uint version, EffectiveModeCallback callback, IntPtr context, out IntPtr handle);
    static EffectiveModeCallback? _modeCallback; // disimpan supaya tidak dibuang GC
    static volatile int _effectiveMode = -1;

    /// <summary>Mulai mendengarkan perubahan Energy saver (sekali saja).</summary>
    public static void StartWatching()
    {
        if (_modeCallback != null) return;
        _modeCallback = (mode, _) =>
        {
            int old = _effectiveMode;
            _effectiveMode = mode;
            if (old != -1 && (old == 0) != (mode == 0)) StateChanged?.Invoke();
        };
        try { PowerRegisterForEffectivePowerModeNotifications(2, _modeCallback, IntPtr.Zero, out _); } catch { }
    }

    static bool? ReadEnergySaver()
    {
        int m = _effectiveMode;
        return m >= 0 ? m == 0 : EnergySaverOn();
    }
}
