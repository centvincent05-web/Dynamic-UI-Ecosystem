using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;

namespace DynamicIsland.Services;

/// <summary>
/// Memantau download yang sedang berjalan untuk progress di sekeliling notch.
/// Dua sumber:
///  1. Folder Downloads: file sementara (.crdownload, .part, .partial) → tahu ada download & kapan selesai
///     (persen tidak diketahui karena ukuran total tidak tercatat di file).
///  2. Ekstensi Chrome "Dynamic Island" (folder chrome-extension) → kirim persen yang tepat ke
///     http://127.0.0.1:48321 (hanya bisa diakses dari laptop ini sendiri).
/// </summary>
public sealed class DownloadMonitor : IDisposable
{
    public const int Port = 48321;
    static readonly string[] TempExt = { ".crdownload", ".part", ".partial", ".download", ".opdownload" };

    readonly Dispatcher _ui;
    readonly FileSystemWatcher? _watcher;
    readonly DispatcherTimer _poll;
    readonly Dictionary<string, long> _tempFiles = new(StringComparer.OrdinalIgnoreCase); // path → ukuran terakhir
    TcpListener? _listener;
    Thread? _serverThread;
    volatile bool _disposed;

    // data dari ekstensi
    double _extReceived, _extTotal;
    int _extCount;
    string _extName = "";
    long _extTick;
    readonly Dictionary<string, long> _recentDone = new(StringComparer.OrdinalIgnoreCase); // anti dobel notifikasi

    /// <summary>Kondisi berubah (dipanggil di thread UI).</summary>
    public event Action? Changed;
    /// <summary>Download selesai: nama file (dipanggil di thread UI).</summary>
    public event Action<string>? Completed;

    public bool IsActive => ExtFresh ? _extCount > 0 : _tempFiles.Count > 0;
    /// <summary>0..1, atau null kalau persen tidak diketahui.</summary>
    public double? Progress => ExtFresh && _extCount > 0 && _extTotal > 0 ? Math.Clamp(_extReceived / _extTotal, 0, 1) : null;
    public int Count => ExtFresh ? _extCount : _tempFiles.Count;
    public string Name => ExtFresh ? _extName : (_tempFiles.Keys.Select(CleanName).FirstOrDefault() ?? "");
    bool ExtFresh => Environment.TickCount64 - _extTick < 3000;

    public DownloadMonitor(Dispatcher ui)
    {
        _ui = ui;
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => Poll();

        string? folder = DownloadsFolder();
        if (folder != null && Directory.Exists(folder))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(folder))
                    if (IsTemp(f) && (DateTime.Now - File.GetLastWriteTime(f)).TotalSeconds < 30) _tempFiles[f] = 0;
                _watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    InternalBufferSize = 16384,
                };
                _watcher.Created += (_, e) => Post(() => OnTempSeen(e.FullPath));
                _watcher.Changed += (_, e) => Post(() => OnTempSeen(e.FullPath));
                _watcher.Deleted += (_, e) => Post(() => OnGone(e.FullPath, null));
                _watcher.Renamed += (_, e) => Post(() => OnGone(e.OldFullPath, e.FullPath));
                _watcher.EnableRaisingEvents = true;
            }
            catch { _watcher = null; }
        }
        StartServer();
        if (_tempFiles.Count > 0) _poll.Start();
    }

    // ---------- folder Downloads ----------

    static bool IsTemp(string path) => TempExt.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    static string CleanName(string path)
    {
        string n = Path.GetFileName(path);
        foreach (var x in TempExt)
            if (n.EndsWith(x, StringComparison.OrdinalIgnoreCase)) return n[..^x.Length];
        return n;
    }

    void Post(Action a)
    {
        if (!_disposed) _ui.BeginInvoke(a, DispatcherPriority.Background);
    }

    void OnTempSeen(string path)
    {
        if (!IsTemp(path)) return;
        bool isNew = !_tempFiles.ContainsKey(path);
        _tempFiles[path] = 0;
        if (!_poll.IsEnabled) _poll.Start();
        if (isNew) Changed?.Invoke();
    }

    void OnGone(string oldPath, string? newPath)
    {
        if (!_tempFiles.Remove(oldPath)) { if (newPath != null) OnTempSeen(newPath); return; }
        if (newPath != null && IsTemp(newPath)) { _tempFiles[newPath] = 0; Changed?.Invoke(); return; } // ganti nama sementara
        // file sementara berganti nama jadi file akhir → selesai; dihapus → dibatalkan
        if (newPath != null && !newPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) Done(Path.GetFileName(newPath));
        Changed?.Invoke();
    }

    void Poll()
    {
        foreach (var f in _tempFiles.Keys.ToList())
            if (!File.Exists(f)) _tempFiles.Remove(f);
        if (!IsActive && !ExtFresh) _poll.Stop();
        Changed?.Invoke();
    }

    void Done(string name)
    {
        long now = Environment.TickCount64;
        foreach (var k in _recentDone.Where(kv => now - kv.Value > 10_000).Select(kv => kv.Key).ToList()) _recentDone.Remove(k);
        if (_recentDone.ContainsKey(name)) return; // sudah diberitahu oleh sumber lain
        _recentDone[name] = now;
        Completed?.Invoke(name);
    }

    // ---------- server kecil untuk ekstensi Chrome ----------

    void StartServer()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
        }
        catch { _listener = null; return; } // port dipakai aplikasi lain: lewati
        _serverThread = new Thread(ServerLoop, 256 * 1024) { IsBackground = true, Name = "DownloadServer" };
        _serverThread.Start();
    }

    void ServerLoop()
    {
        while (!_disposed && _listener != null)
        {
            try
            {
                using var client = _listener.AcceptTcpClient();
                client.ReceiveTimeout = 2000;
                using var stream = client.GetStream();
                string? body = ReadRequest(stream);
                byte[] resp = Encoding.ASCII.GetBytes(body != null
                    ? "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n");
                stream.Write(resp, 0, resp.Length);
                if (body != null) Handle(body);
            }
            catch { if (_disposed) return; }
        }
    }

    /// <summary>Baca request HTTP; hanya POST /downloads dengan header X-Island yang diterima.</summary>
    static string? ReadRequest(NetworkStream s)
    {
        var buf = new byte[65536];
        int len = 0, headerEnd = -1;
        while (len < buf.Length)
        {
            int n = s.Read(buf, len, buf.Length - len);
            if (n <= 0) break;
            len += n;
            headerEnd = IndexOf(buf, len, "\r\n\r\n"u8);
            if (headerEnd >= 0) break;
        }
        if (headerEnd < 0) return null;
        string head = Encoding.ASCII.GetString(buf, 0, headerEnd);
        if (!head.StartsWith("POST /downloads ", StringComparison.Ordinal)) return null;
        // header khusus memaksa browser melakukan cek CORS: halaman web biasa tidak bisa mengirim data palsu
        if (head.IndexOf("\r\nX-Island:", StringComparison.OrdinalIgnoreCase) < 0) return null;
        int contentLength = 0;
        foreach (var line in head.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[15..].Trim(), out contentLength);
        contentLength = Math.Clamp(contentLength, 0, buf.Length - headerEnd - 4);
        int bodyStart = headerEnd + 4;
        while (len - bodyStart < contentLength)
        {
            int n = s.Read(buf, len, buf.Length - len);
            if (n <= 0) break;
            len += n;
        }
        return Encoding.UTF8.GetString(buf, bodyStart, Math.Min(contentLength, len - bodyStart));
    }

    static int IndexOf(byte[] b, int len, ReadOnlySpan<byte> pat)
    {
        int i = b.AsSpan(0, len).IndexOf(pat);
        return i;
    }

    /// <summary>{"items":[{"name":..,"received":..,"total":..}],"done":["nama file"]}</summary>
    void Handle(string json)
    {
        double rec = 0, tot = 0;
        int count = 0;
        string name = "";
        var done = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out var items))
                foreach (var it in items.EnumerateArray())
                {
                    count++;
                    double r = it.TryGetProperty("received", out var rv) ? rv.GetDouble() : 0;
                    double t = it.TryGetProperty("total", out var tv) ? tv.GetDouble() : 0;
                    if (t > 0) { rec += r; tot += t; }
                    if (name.Length == 0 && it.TryGetProperty("name", out var nv)) name = Path.GetFileName(nv.GetString() ?? "");
                }
            if (doc.RootElement.TryGetProperty("done", out var d))
                foreach (var it in d.EnumerateArray()) done.Add(Path.GetFileName(it.GetString() ?? ""));
        }
        catch { return; }
        Post(() =>
        {
            _extReceived = rec;
            _extTotal = tot;
            _extCount = count;
            _extName = name;
            _extTick = Environment.TickCount64;
            if (!_poll.IsEnabled) _poll.Start();
            foreach (var n in done) if (n.Length > 0) Done(n);
            Changed?.Invoke();
        });
    }

    // ---------- util ----------

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    static string? DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
            {
                string s = Marshal.PtrToStringUni(p) ?? "";
                Marshal.FreeCoTaskMem(p);
                if (s.Length > 0) return s;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public void Dispose()
    {
        _disposed = true;
        _poll.Stop();
        try { _watcher?.Dispose(); } catch { }
        try { _listener?.Stop(); } catch { }
    }
}
