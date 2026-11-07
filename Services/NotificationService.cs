using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Xml;

namespace DynamicIsland.Services;

/// <summary>Satu notifikasi Windows (toast).</summary>
public sealed record NotificationItem(long Id, string AppId, string AppName, string Title, string Body, string Attribution, DateTime Arrived);

/// <summary>
/// Membaca notifikasi Windows (Chrome, Phone Link, Discord, dll.) dari database notifikasi milik
/// pengguna sendiri (%LocalAppData%\Microsoft\Windows\Notifications\wpndatabase.db), hanya-baca.
/// Database disalin dulu ke folder sementara supaya tidak mengganggu Windows yang sedang memakainya.
/// Memakai SQLite bawaan Windows (winsqlite3.dll), tanpa library tambahan.
/// </summary>
public sealed class NotificationService : IDisposable
{
    static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");
    static readonly string DbPath = Path.Combine(Folder, "wpndatabase.db");
    static readonly string TempDir = Path.Combine(Path.GetTempPath(), "DynamicIsland-notif");

    readonly Dispatcher _ui;
    readonly FileSystemWatcher? _watcher;
    readonly DispatcherTimer _debounce;
    readonly HashSet<long> _hidden = new(); // dihapus dari daftar island oleh pengguna
    long _lastSeenId = -1;
    bool _reading, _again;
    volatile bool _disposed;

    public List<NotificationItem> Items { get; private set; } = new();
    public bool Available => File.Exists(DbPath);

    /// <summary>Daftar berubah (thread UI).</summary>
    public event Action? Changed;
    /// <summary>Notifikasi baru masuk (thread UI).</summary>
    public event Action<NotificationItem>? Arrived;

    public NotificationService(Dispatcher ui)
    {
        _ui = ui;
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        if (!Directory.Exists(Folder)) return;
        try
        {
            _watcher = new FileSystemWatcher(Folder, "wpndatabase.db*")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 8192,
            };
            _watcher.Changed += (_, _) => { if (!_disposed) _ui.BeginInvoke(new Action(Kick), DispatcherPriority.Background); };
            _watcher.EnableRaisingEvents = true;
        }
        catch { _watcher = null; }
        Refresh();
    }

    void Kick()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Baca ulang di thread latar.</summary>
    public void Refresh()
    {
        if (_reading) { _again = true; return; }
        _reading = true;
        Task.Run(() =>
        {
            List<NotificationItem>? list = null;
            try { list = ReadAll(); } catch { }
            _ui.BeginInvoke(new Action(() =>
            {
                _reading = false;
                if (list != null) Apply(list);
                if (_again) { _again = false; Refresh(); }
            }), DispatcherPriority.Background);
        });
    }

    void Apply(List<NotificationItem> list)
    {
        list = list.Where(n => !_hidden.Contains(n.Id)).OrderByDescending(n => n.Arrived).Take(30).ToList();
        bool first = _lastSeenId < 0;
        long maxId = list.Count > 0 ? list.Max(n => n.Id) : 0;
        var fresh = first ? new List<NotificationItem>()
            : list.Where(n => n.Id > _lastSeenId && (DateTime.Now - n.Arrived).TotalMinutes < 2).OrderBy(n => n.Id).ToList();
        _lastSeenId = Math.Max(_lastSeenId, maxId);
        Items = list;
        Changed?.Invoke();
        // beberapa sekaligus: cukup tampilkan yang terbaru
        if (fresh.Count > 0) Arrived?.Invoke(fresh[^1]);
    }

    public void Hide(NotificationItem n)
    {
        _hidden.Add(n.Id);
        Items = Items.Where(x => x.Id != n.Id).ToList();
        Changed?.Invoke();
    }

    public void HideAll()
    {
        foreach (var n in Items) _hidden.Add(n.Id);
        Items = new List<NotificationItem>();
        Changed?.Invoke();
    }

    // ---------- baca database ----------

    static List<NotificationItem> ReadAll()
    {
        var result = new List<NotificationItem>();
        if (!File.Exists(DbPath)) return result;
        Directory.CreateDirectory(TempDir);
        string copy = Path.Combine(TempDir, "n.db");
        // salin database + log WAL (berisi notifikasi terbaru) apa adanya
        CopyShared(DbPath, copy);
        CopyShared(DbPath + "-wal", copy + "-wal");
        try { File.Delete(copy + "-shm"); } catch { }

        if (Sqlite.sqlite3_open_v2(Encoding.UTF8.GetBytes(copy + "\0"), out var db, 0x02 /* READWRITE (salinan) */, IntPtr.Zero) != 0)
        {
            Sqlite.sqlite3_close(db);
            return result;
        }
        try
        {
            const string sql =
                "SELECT n.Id, h.PrimaryId, n.Payload, n.ArrivalTime FROM Notification n " +
                "JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
                "WHERE n.Type = 'toast' ORDER BY n.ArrivalTime DESC LIMIT 60";
            if (Sqlite.sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out var stmt, IntPtr.Zero) != 0) return result;
            try
            {
                while (Sqlite.sqlite3_step(stmt) == 100 /* ROW */)
                {
                    long id = Sqlite.sqlite3_column_int64(stmt, 0);
                    string appId = Sqlite.Text(stmt, 1);
                    string xml = Sqlite.BlobText(stmt, 2);
                    long ft = Sqlite.sqlite3_column_int64(stmt, 3);
                    DateTime arrived;
                    try { arrived = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { arrived = DateTime.Now; }
                    var (title, body, attribution) = ParsePayload(xml);
                    if (title.Length == 0 && body.Length == 0) continue;
                    result.Add(new NotificationItem(id, appId, AppName(appId), title, body, attribution, arrived));
                }
            }
            finally { Sqlite.sqlite3_finalize(stmt); }
        }
        finally { Sqlite.sqlite3_close(db); }
        return result;
    }

    static void CopyShared(string from, string to)
    {
        if (!File.Exists(from)) { try { File.Delete(to); } catch { } return; }
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
    }

    /// <summary>Ambil judul, isi, dan atribusi (mis. nama situs) dari XML toast.</summary>
    static (string title, string body, string attribution) ParsePayload(string xml)
    {
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(xml);
            var texts = doc.SelectNodes("//binding/text");
            var lines = new List<string>();
            string attribution = "";
            if (texts != null)
                foreach (XmlNode t in texts)
                {
                    string v = (t.InnerText ?? "").Trim();
                    if (v.Length == 0) continue;
                    if ((t.Attributes?["placement"]?.Value ?? "") == "attribution") attribution = v;
                    else lines.Add(v);
                }
            string title = lines.Count > 0 ? lines[0] : "";
            string body = string.Join(" · ", lines.Skip(1));
            return (title, body, attribution);
        }
        catch { return ("", "", ""); }
    }

    // ---------- nama aplikasi dari AppUserModelID ----------

    static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    public static string AppName(string appId)
    {
        lock (NameCache)
        {
            if (NameCache.TryGetValue(appId, out var cached)) return cached;
        }
        string name = Fallback(appId);
        try
        {
            SHCreateItemFromParsingName("shell:AppsFolder\\" + appId, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
            item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var n);
            if (!string.IsNullOrWhiteSpace(n)) name = n;
            Marshal.ReleaseComObject(item);
        }
        catch { }
        lock (NameCache) NameCache[appId] = name;
        return name;
    }

    static string Fallback(string appId)
    {
        string a = appId;
        if (a.Contains("YourPhone", StringComparison.OrdinalIgnoreCase)) return "Phone Link";
        if (a.StartsWith("Chrome", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
        int bang = a.LastIndexOf('!');
        if (bang > 0) a = a[..bang];
        int us = a.IndexOf('_');
        if (us > 0) a = a[..us];
        int dot = a.LastIndexOf('.');
        if (dot >= 0 && dot < a.Length - 1) a = a[(dot + 1)..];
        if (a.Contains('\\')) a = Path.GetFileNameWithoutExtension(a);
        return a;
    }

    public void Dispose()
    {
        _disposed = true;
        _debounce.Stop();
        try { _watcher?.Dispose(); } catch { }
    }

    // ---------- SQLite bawaan Windows ----------

    static class Sqlite
    {
        const string Lib = "winsqlite3.dll";
        [DllImport(Lib)] public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib)] public static extern int sqlite3_close(IntPtr db);
        [DllImport(Lib)] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib)] public static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib)] public static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib)] public static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport(Lib)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport(Lib)] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
        [DllImport(Lib)] static extern int sqlite3_column_bytes(IntPtr stmt, int col);

        public static string Text(IntPtr stmt, int col)
        {
            IntPtr p = sqlite3_column_text(stmt, col);
            int n = sqlite3_column_bytes(stmt, col);
            return p == IntPtr.Zero || n <= 0 ? "" : Marshal.PtrToStringUTF8(p, n) ?? "";
        }

        public static string BlobText(IntPtr stmt, int col)
        {
            IntPtr p = sqlite3_column_blob(stmt, col);
            int n = sqlite3_column_bytes(stmt, col);
            if (p == IntPtr.Zero || n <= 0) return "";
            var bytes = new byte[n];
            Marshal.Copy(p, bytes, 0, n);
            // biasanya UTF-8; kalau UTF-16 (byte ke-2 nol) baca sebagai Unicode
            bool utf16 = n > 1 && bytes[1] == 0;
            return (utf16 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes)).TrimEnd('\0').TrimStart('\uFEFF');
        }
    }
}
