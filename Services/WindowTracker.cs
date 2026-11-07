using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>Satu ikon di dock: aplikasi yang disematkan dan/atau sedang terbuka.</summary>
public sealed class DockApp
{
    public string Key = "";          // path exe (huruf kecil) atau path shortcut
    public string Name = "";
    public string? LaunchPath;       // shortcut .lnk atau exe untuk membuka aplikasi
    public string? ExePath;
    public ImageSource? Icon;
    public bool Pinned;
    public readonly List<IntPtr> Windows = new();
    public bool IsForeground;
    public bool IsRunning => Windows.Count > 0;
    /// <summary>Profil browser Chromium (Chrome/Edge/Brave), "Default" untuk aplikasi biasa.</summary>
    public string Profile = "Default";
    /// <summary>Argumen untuk membuka profil ini (mis. --profile-directory="Profile 1").</summary>
    public string? LaunchArgs;
    /// <summary>Ikon khusus profil (Chrome menaruh avatar akun di ikonnya).</summary>
    public string? IconPath;
    /// <summary>Teks persis di daftar pin (untuk melepas sematan).</summary>
    public string? PinEntry;
}

/// <summary>Info yang dipasang aplikasi pada jendelanya untuk taskbar (dipakai Chrome untuk tiap profil).</summary>
public sealed record WindowProfile(string Profile, string? Args, string? DisplayName, string? IconPath);

/// <summary>Membaca daftar jendela aplikasi yang terbuka dan aplikasi yang disematkan.</summary>
public static class WindowTracker
{
    static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string?> ShortcutCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly uint OwnPid = (uint)Environment.ProcessId;
    static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);
    static List<string>? _pinnedCache;
    static long _pinnedCacheTick;

    /// <summary>Paksa baca ulang daftar pin (setelah sematkan / lepas).</summary>
    public static void InvalidatePinned() => _pinnedCache = null;

    static string WindowsPinnedFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

    /// <summary>Aplikasi yang disematkan: dari pengaturan, atau (kalau belum diatur) dari taskbar Windows.</summary>
    public static List<string> PinnedPaths()
    {
        var list = Settings.Current.DockPinned;
        if (list != null) return list.Where(p => p.Length > 0).ToList();

        // folder pin taskbar Windows jarang berubah: cukup dibaca ulang tiap 10 detik
        if (_pinnedCache != null && Environment.TickCount64 - _pinnedCacheTick < 10_000)
            return new List<string>(_pinnedCache);
        try
        {
            _pinnedCache = !Directory.Exists(WindowsPinnedFolder) ? new List<string>()
                : new DirectoryInfo(WindowsPinnedFolder).GetFiles("*.lnk")
                    .OrderBy(f => f.CreationTimeUtc) // kira-kira urutan saat disematkan
                    .Select(f => f.FullName).ToList();
        }
        catch { _pinnedCache = new List<string>(); }
        _pinnedCacheTick = Environment.TickCount64;
        return new List<string>(_pinnedCache);
    }

    /// <summary>Susun isi dock: aplikasi disematkan (urut), lalu aplikasi lain yang sedang terbuka.</summary>
    static readonly object Sync = new();

    public static List<DockApp> Snapshot()
    {
        lock (Sync) return SnapshotCore();
    }

    static List<DockApp> SnapshotCore()
    {
        var apps = new List<DockApp>();
        var byExe = new Dictionary<string, DockApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in PinnedPaths())
        {
            DockApp app;
            if (entry.Contains('|'))
            {
                // profil browser yang disematkan: "exe|argumen|ikon|nama"
                var parts = entry.Split('|');
                string exe = parts[0];
                string? args = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
                app = new DockApp
                {
                    Name = parts.Length > 3 && parts[3].Length > 0 ? parts[3] : FriendlyName(exe),
                    LaunchPath = exe, ExePath = exe, LaunchArgs = args,
                    IconPath = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null,
                    Profile = ProfileFromArgs(args), Pinned = true, PinEntry = entry,
                };
            }
            else
            {
                string? exe = entry.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(entry) : entry;
                string? args = entry.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutArgs(entry) : null;
                app = new DockApp
                {
                    Name = Path.GetFileNameWithoutExtension(entry),
                    LaunchPath = entry, ExePath = exe,
                    Profile = ProfileFromArgs(args), Pinned = true, PinEntry = entry,
                };
            }
            // aplikasi Store/MSIX (Claude, WhatsApp, …): folder instalasinya berganti tiap update,
            // jadi path lama diperbarui ke versi sekarang, dan ikonnya diambil dari menu Start
            if (app.ExePath != null)
            {
                var (current, aumid) = Packaged(app.ExePath);
                if (current != null && !current.Equals(app.ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    string fixedEntry = entry.Replace(app.ExePath, current, StringComparison.OrdinalIgnoreCase);
                    var pins = Settings.Current.DockPinned;
                    int at = pins?.FindIndex(p => p.Equals(entry, StringComparison.OrdinalIgnoreCase)) ?? -1;
                    if (pins != null && at >= 0) { pins[at] = fixedEntry; Settings.Save(); }
                    if (app.LaunchPath != null && app.LaunchPath.Equals(app.ExePath, StringComparison.OrdinalIgnoreCase)) app.LaunchPath = current;
                    app.ExePath = current;
                    app.PinEntry = fixedEntry;
                }
                if (aumid != null)
                {
                    if (!entry.Contains('|') && PackagedName(app.ExePath) is string pname) app.Name = pname;
                    app.IconPath ??= "shell:AppsFolder\\" + aumid;
                    app.LaunchPath = "shell:AppsFolder\\" + aumid; // aplikasi Store dibuka lewat identitasnya, bukan exe langsung
                }
            }
            app.Icon = IconFor(app.IconPath ?? app.LaunchPath!);
            string key = GroupKey(app.ExePath ?? entry, app.Profile);
            app.Key = key;
            apps.Add(app);
            if (!byExe.ContainsKey(key)) byExe[key] = app;
        }

        IntPtr fg = Native.GetForegroundWindow();
        if (Settings.Current.DockShowRunning)
        {
            var seen = new HashSet<IntPtr>();
            foreach (var (hwnd, exe, title) in AppWindows())
            {
                seen.Add(hwnd);
                // browser dengan beberapa akun: tiap profil jadi ikon sendiri
                var prof = exe != null ? ProfileOf(hwnd) : null;
                string profile = prof?.Profile ?? "Default";
                string key = exe != null ? GroupKey(exe, profile) : "win:" + title.ToLowerInvariant();
                if (!byExe.TryGetValue(key, out var app))
                {
                    var (_, aumid) = exe != null ? Packaged(exe) : (null, null);
                    string? iconPath = prof?.IconPath ?? (aumid != null ? "shell:AppsFolder\\" + aumid : null);
                    app = new DockApp
                    {
                        Key = key,
                        Name = exe != null ? (prof?.DisplayName ?? PackagedName(exe) ?? FriendlyName(exe)) : title,
                        LaunchPath = aumid != null ? "shell:AppsFolder\\" + aumid : exe,
                        ExePath = exe,
                        Profile = profile,
                        LaunchArgs = prof?.Args,
                        IconPath = iconPath,
                        Icon = iconPath != null ? IconFor(iconPath) : exe != null ? IconFor(exe) : null,
                    };
                    apps.Add(app);
                    byExe[key] = app;
                }
                // ikon yang disematkan untuk profil yang sama: pakai ikon & nama profil dari jendela
                if (app.Pinned && app.IconPath == null && prof?.IconPath != null && profile != "Default")
                    app.IconPath = prof.IconPath;
                app.Windows.Add(hwnd);
                if (hwnd == fg) app.IsForeground = true;
            }
            // buang info jendela yang sudah ditutup
            foreach (var h in ProfileCache.Keys.Where(h => !seen.Contains(h)).ToList()) ProfileCache.Remove(h);
        }
        return apps;
    }

    static string GroupKey(string exe, string profile) =>
        profile.Equals("Default", StringComparison.OrdinalIgnoreCase)
            ? exe.ToLowerInvariant()
            : exe.ToLowerInvariant() + "#" + profile.ToLowerInvariant();

    /// <summary>Ambil nilai --profile-directory dari argumen (Chrome/Edge/Brave). Tanpa argumen = "Default".</summary>
    public static string ProfileFromArgs(string? args)
    {
        if (string.IsNullOrEmpty(args)) return "Default";
        const string key = "--profile-directory=";
        int i = args.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "Default";
        string rest = args[(i + key.Length)..].TrimStart();
        string v = rest.StartsWith('"') ? rest[1..].Split('"')[0] : rest.Split(' ')[0];
        return v.Length > 0 ? v : "Default";
    }

    // ---------- info profil per jendela ----------

    static readonly Dictionary<IntPtr, (WindowProfile? info, long tick)> ProfileCache = new();

    /// <summary>Profil browser dari properti jendela (yang juga dipakai taskbar Windows untuk memisahkan ikon).</summary>
    static WindowProfile? ProfileOf(IntPtr hwnd)
    {
        long now = Environment.TickCount64;
        if (ProfileCache.TryGetValue(hwnd, out var c) && (c.info != null || now - c.tick < 10_000)) return c.info;
        WindowProfile? info = null;
        try
        {
            string? cmd = WindowProps.Get(hwnd, 2);   // System.AppUserModel.RelaunchCommand
            if (cmd != null && cmd.IndexOf("--profile-directory", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string? icon = WindowProps.Get(hwnd, 3);   // RelaunchIconResource
                string? name = WindowProps.Get(hwnd, 4);   // RelaunchDisplayNameResource
                if (icon != null) { int comma = icon.LastIndexOf(','); if (comma > 2) icon = icon[..comma]; }
                if (icon != null && !File.Exists(icon)) icon = null;
                if (name != null && name.StartsWith('@')) name = null; // berupa referensi resource
                info = new WindowProfile(ProfileFromArgs(cmd), ArgsOf(cmd), name, icon);
            }
        }
        catch { }
        ProfileCache[hwnd] = (info, now);
        return info;
    }

    /// <summary>Bagian argumen dari sebuah perintah ("C:\a.exe" --x → --x).</summary>
    static string? ArgsOf(string cmd)
    {
        cmd = cmd.Trim();
        int end = cmd.StartsWith('"') ? cmd.IndexOf('"', 1) + 1 : cmd.IndexOf(' ');
        if (end <= 0 || end >= cmd.Length) return null;
        string a = cmd[end..].Trim();
        return a.Length > 0 ? a : null;
    }

    /// <summary>Jendela aplikasi yang biasanya tampil di taskbar.</summary>
    public static List<(IntPtr hwnd, string? exe, string title)> AppWindows()
    {
        var result = new List<(IntPtr, string?, string)>();
        Native.EnumWindows((h, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(h)) return true;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return true;
                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                string title = Native.WindowTitle(h);
                if (title.Length == 0) return true;
                string cls = Native.ClassNameOf(h);
                if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == OwnPid) return true;
                string? exe = Native.ProcessPath((int)pid);
                // aplikasi Store dibungkus ApplicationFrameHost: cari proses aslinya di jendela anaknya
                if (exe != null && exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    uint real = 0;
                    EnumChildWindows(h, (c, lp) =>
                    {
                        Native.GetWindowThreadProcessId(c, out uint cp);
                        if (cp != pid && cp != 0) { real = cp; return false; }
                        return true;
                    }, IntPtr.Zero);
                    exe = real != 0 ? Native.ProcessPath((int)real) : null;
                }
                result.Add((h, exe, title));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // ---------- aksi ----------

    public static void Launch(DockApp app)
    {
        string? path = app.LaunchPath ?? app.ExePath;
        if (path == null) return;
        try
        {
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            // profil browser: buka di akun yang sama
            if (app.LaunchArgs != null && !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) psi.Arguments = app.LaunchArgs;
            Process.Start(psi);
        }
        catch { }
    }

    /// <summary>Klik ikon: buka kalau belum jalan, minimize kalau sedang aktif, selain itu munculkan.</summary>
    public static void Activate(DockApp app)
    {
        if (!app.IsRunning) { Launch(app); return; }
        IntPtr fg = Native.GetForegroundWindow();
        if (app.Windows.Contains(fg) && !Native.IsIconic(fg))
        {
            Native.ShowWindow(fg, Native.SW_MINIMIZE);
            return;
        }
        // beberapa jendela: munculkan berikutnya secara bergiliran
        IntPtr target = app.Windows[0];
        int idx = app.Windows.IndexOf(fg);
        if (idx >= 0) target = app.Windows[(idx + 1) % app.Windows.Count];
        if (Native.IsIconic(target)) Native.ShowWindow(target, Native.SW_RESTORE);
        Native.SwitchToThisWindow(target, true);
    }

    public static void CloseAll(DockApp app)
    {
        foreach (var h in app.Windows) CloseWindow(h);
    }

    /// <summary>Tutup jendela seperti menekan tombol X (WM_SYSCOMMAND SC_CLOSE), cadangan WM_CLOSE.</summary>
    public static void CloseWindow(IntPtr h)
    {
        if (!Native.PostMessage(h, 0x0112 /* WM_SYSCOMMAND */, (IntPtr)0xF060 /* SC_CLOSE */, IntPtr.Zero))
            Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Pin(DockApp app)
    {
        var list = PinnedPaths();
        // aplikasi Store: simpan path exe-nya (supaya menyatu dengan jendelanya); path diperbarui otomatis saat update
        string? path = app.LaunchPath != null && app.LaunchPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            ? app.ExePath ?? app.LaunchPath : app.LaunchPath ?? app.ExePath;
        if (path == null) return;
        // profil browser selain Default disimpan lengkap dengan argumen & ikonnya
        if (app.Profile != "Default" && app.ExePath != null)
            path = $"{app.ExePath}|{app.LaunchArgs}|{app.IconPath}|{app.Name}";
        if (list.Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(path);
        Settings.Current.DockPinned = list;
        Settings.Save();
        InvalidatePinned();
    }

    public static void Unpin(DockApp app)
    {
        var list = PinnedPaths();
        if (app.PinEntry != null) list.RemoveAll(p => p.Equals(app.PinEntry, StringComparison.OrdinalIgnoreCase));
        else list.RemoveAll(p => p.Equals(app.LaunchPath, StringComparison.OrdinalIgnoreCase)
                         || (app.ExePath != null && p.Equals(app.ExePath, StringComparison.OrdinalIgnoreCase)));
        Settings.Current.DockPinned = list;
        Settings.Save();
        InvalidatePinned();
    }

    /// <summary>Buka menu Start Windows (sama seperti menekan tombol Windows).</summary>
    public static void OpenStart()
    {
        Native.keybd_event(Native.VK_LWIN, 0, 0, UIntPtr.Zero);
        Native.keybd_event(Native.VK_LWIN, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // ---------- util ----------

    static string FriendlyName(string exe)
    {
        if (NameCache.TryGetValue(exe, out var cached)) return cached;
        string name = Path.GetFileNameWithoutExtension(exe);
        try
        {
            string? d = FileVersionInfo.GetVersionInfo(exe).FileDescription;
            if (!string.IsNullOrWhiteSpace(d)) name = d.Trim();
        }
        catch { }
        NameCache[exe] = name;
        return name;
    }

    static readonly Dictionary<string, string?> ShortcutArgsCache = new(StringComparer.OrdinalIgnoreCase);

    static string? ShortcutArgs(string lnk)
    {
        if (ShortcutArgsCache.TryGetValue(lnk, out var cached)) return cached;
        string? args = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type != null)
            {
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic sc = shell.CreateShortcut(lnk);
                string a = sc.Arguments;
                if (!string.IsNullOrWhiteSpace(a)) args = a;
            }
        }
        catch { }
        ShortcutArgsCache[lnk] = args;
        return args;
    }

    static string? ResolveShortcut(string lnk)
    {
        if (ShortcutCache.TryGetValue(lnk, out var cached)) return cached;
        string? target = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type != null)
            {
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic sc = shell.CreateShortcut(lnk);
                string t = sc.TargetPath;
                if (!string.IsNullOrWhiteSpace(t)) target = t;
            }
        }
        catch { }
        ShortcutCache[lnk] = target;
        return target;
    }

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, EnumChildProc proc, IntPtr lParam);
    delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    static readonly Dictionary<string, (string? path, string? aumid)> PackageCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> PackageNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nama resmi aplikasi Store (mis. "Claude", bukan "claude"), kalau ada.</summary>
    static string? PackagedName(string exe)
    {
        Packaged(exe);
        lock (PackageCache) return PackageNames.TryGetValue(exe, out var n) && !string.IsNullOrWhiteSpace(n) && !n.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? n : null;
    }

    /// <summary>
    /// Untuk exe di dalam C:\Program Files\WindowsApps (aplikasi Store/MSIX): path exe di versi yang
    /// terpasang sekarang + identitas aplikasinya (AUMID) untuk ikon & membuka. Selain itu (null, null).
    /// </summary>
    public static (string? path, string? aumid) Packaged(string exe)
    {
        const string marker = "\\WindowsApps\\";
        int i = exe.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return (null, null);
        lock (PackageCache)
        {
            if (PackageCache.TryGetValue(exe, out var hit)) return hit;
        }
        (string? path, string? aumid) result = (null, null);
        try
        {
            string rest = exe[(i + marker.Length)..];
            int slash = rest.IndexOf('\\');
            if (slash > 0)
            {
                string folder = rest[..slash], inner = rest[(slash + 1)..];
                // nama folder: Nama_Versi_Arsitektur_[Resource]_PublisherId → keluarga paket: Nama_PublisherId
                var parts = folder.Split('_');
                if (parts.Length >= 2)
                {
                    string family = parts[0] + "_" + parts[^1];
                    var pm = new Windows.Management.Deployment.PackageManager();
                    var pkg = pm.FindPackagesForUser(string.Empty, family)
                        .OrderByDescending(p => (p.Id.Version.Major, p.Id.Version.Minor, p.Id.Version.Build, p.Id.Version.Revision))
                        .FirstOrDefault();
                    if (pkg != null)
                    {
                        result.path = Path.Combine(pkg.InstalledLocation.Path, inner);
                        try { result.aumid = pkg.GetAppListEntries().FirstOrDefault()?.AppUserModelId; } catch { }
                        try { lock (PackageCache) PackageNames[exe] = pkg.DisplayName; } catch { }
                    }
                }
            }
        }
        catch { }
        lock (PackageCache) PackageCache[exe] = result;
        return result;
    }

    public static ImageSource? IconFor(string path)
    {
        lock (IconCache) return IconForCore(path);
    }

    static ImageSource? IconForCore(string path)
    {
        if (IconCache.TryGetValue(path, out var cached)) return cached;
        ImageSource? result = null;
        try
        {
            var info = new Native.SHFILEINFO();
            Native.SHGetFileInfo(path, 0, ref info,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.SHFILEINFO>(),
                Native.SHGFI_ICON | Native.SHGFI_LARGEICON);
            if (info.hIcon != IntPtr.Zero)
            {
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    result = src;
                }
                finally { Native.DestroyIcon(info.hIcon); }
            }
        }
        catch { }
        IconCache[path] = result;
        return result;
    }
}

/// <summary>Membaca properti AppUserModel yang dipasang aplikasi pada jendelanya.</summary>
static class WindowProps
{
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;
    }

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    static readonly Guid AppUserModelFmt = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    static Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    /// <summary>pid 2 = RelaunchCommand, 3 = RelaunchIconResource, 4 = RelaunchDisplayNameResource, 5 = ID.</summary>
    public static string? Get(IntPtr hwnd, uint pid)
    {
        if (SHGetPropertyStoreForWindow(hwnd, ref IID_IPropertyStore, out var store) != 0 || store == null) return null;
        try
        {
            var key = new PROPERTYKEY { fmtid = AppUserModelFmt, pid = pid };
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.ptr) : null; }
            finally { PropVariantClear(ref pv); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }
}
