using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.UI;

namespace DynamicIsland.Services;

/// <summary>
/// Fase 3 — live wallpaper, sisi APP UTAMA: menyalakan / mematikan proses "DynamicIsland.exe --wallpaper".
/// Video berjalan di proses terpisah supaya (1) kalau decoder video bermasalah, notch & dock tidak ikut mati,
/// (2) RAM video terlihat terpisah di Task Manager, (3) bisa dihentikan total saat tidak dipakai.
/// Pengaturan dibaca sendiri oleh proses wallpaper dari settings.json (diawasi perubahannya).
/// </summary>
public static class LiveWallpaperManager
{
    public const string GameEventName = @"Local\DynamicIsland.LiveWallpaper.Game";
    public const string QuitEventName = @"Local\DynamicIsland.LiveWallpaper.Quit";

    static Dispatcher? _ui;
    static Process? _proc;
    static EventWaitHandle? _game, _quit;
    static DispatcherTimer? _debounce;
    static readonly Queue<DateTime> _crashes = new();
    static bool _stopping;

    /// <summary>PID proses wallpaper (0 = tidak berjalan). Dipakai halaman Pemakaian.</summary>
    public static int ProcessId { get { try { return _proc != null && !_proc.HasExited ? _proc.Id : 0; } catch { return 0; } } }
    public static string Status { get; private set; } = "";

    public static void Start(Dispatcher ui)
    {
        if (_ui != null) return;
        _ui = ui;
        try
        {
            _game = new EventWaitHandle(false, EventResetMode.ManualReset, GameEventName);
            _quit = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName);
        }
        catch { }
        _debounce = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(700) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Ensure(); };
        Settings.Changed += () => { _debounce.Stop(); _debounce.Start(); };
        Ensure();
    }

    /// <summary>Mode game dari island: video dijeda (kalau diizinkan di Pengaturan).</summary>
    public static void SetGame(bool on)
    {
        try { if (on) _game?.Set(); else _game?.Reset(); } catch { }
    }

    public static bool HasAnyFile(Settings s) => (s.LiveWallpaperFiles ?? new()).Any(f => Usable(f, s));

    /// <summary>File ada dan ukurannya tidak melebihi batas (supaya video 2 GB tidak ikut dimuat).</summary>
    public static bool Usable(string f, Settings s)
    {
        try { return !string.IsNullOrWhiteSpace(f) && File.Exists(f) && new FileInfo(f).Length <= MaxBytes(s); }
        catch { return false; }
    }

    public static long MaxBytes(Settings s) => (long)Math.Clamp(s.LiveWallpaperMaxMB, 50, 4000) * 1024 * 1024;

    static void Ensure()
    {
        var s = Settings.Current;
        bool want = s.LiveWallpaperEnabled && HasAnyFile(s) && !_stopping;
        bool running = ProcessId != 0;
        // proses wallpaper membaca settings.json sendiri → pastikan file sudah berisi pengaturan terbaru
        // (tanpa ini proses baru melihat "masih mati", langsung keluar, lalu dinyalakan ulang terus-menerus)
        if (want || running) Settings.Save();
        if (want && !running)
        {
            // jangan terus-menerus menyalakan ulang kalau prosesnya selalu gagal
            while (_crashes.Count > 0 && DateTime.Now - _crashes.Peek() > TimeSpan.FromMinutes(5)) _crashes.Dequeue();
            if (_crashes.Count >= 3) { Status = L.T("Live wallpaper berhenti 3x dalam 5 menit — dimatikan sementara. Lihat Salin laporan error."); return; }
            try
            {
                _quit?.Reset();
                var (exe, args) = WallpaperCommand();
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                };
                _proc = Process.Start(psi);
                if (_proc != null)
                {
                    _proc.EnableRaisingEvents = true;
                    var p = _proc;
                    var started = DateTime.Now;
                    _proc.Exited += (_, _) => _ui?.BeginInvoke(new Action(() =>
                    {
                        if (_stopping || p != _proc) return;
                        int code = 0;
                        try { code = p.ExitCode; } catch { }
                        // crash, atau keluar terlalu cepat padahal seharusnya jalan → dihitung, supaya tidak berulang tanpa henti
                        if (code != 0 || DateTime.Now - started < TimeSpan.FromSeconds(10)) _crashes.Enqueue(DateTime.Now);
                        _proc = null;
                        Ensure();
                    }));
                }
                Status = "";
            }
            catch (Exception ex)
            {
                Status = L.T("Gagal menjalankan live wallpaper: ") + ex.Message;
                CrashGuard.Log(ex, "live wallpaper");
            }
        }
        else if (!want && running) StopProcess();
    }

    /// <summary>
    /// Exe khusus live wallpaper ("DynamicIsland.LiveWallpaper.exe", nama jelas di Task Manager) kalau ada di
    /// folder yang sama; kalau tidak ada (mis. build lama), pakai exe utama dengan argumen --wallpaper.
    /// </summary>
    static (string exe, string args) WallpaperCommand()
    {
        string main = Environment.ProcessPath ?? "DynamicIsland.exe";
        string own = Path.Combine(Path.GetDirectoryName(main)!, "DynamicIsland.LiveWallpaper.exe");
        return File.Exists(own) && !SameFile(own, main)
            ? (own, $"--wallpaper {Environment.ProcessId}")
            : (main, $"--wallpaper {Environment.ProcessId}");
    }

    static bool SameFile(string a, string b)
    {
        try { var fa = new FileInfo(a); var fb = new FileInfo(b); return fa.Length == fb.Length && fa.LastWriteTimeUtc == fb.LastWriteTimeUtc; }
        catch { return false; }
    }

    static void StopProcess()
    {
        var p = _proc;
        _proc = null;
        if (p == null) return;
        try
        {
            _quit?.Set(); // minta keluar baik-baik (wallpaper Windows dipulihkan oleh prosesnya)
            if (!p.WaitForExit(2500)) p.Kill();
        }
        catch { }
        finally { try { _quit?.Reset(); } catch { } }
    }

    /// <summary>App utama ditutup: matikan juga proses wallpaper.</summary>
    public static void Stop()
    {
        _stopping = true;
        StopProcess();
    }
}

/// <summary>
/// Fase 3 — sisi PROSES WALLPAPER ("DynamicIsland.exe --wallpaper &lt;pid app&gt;"): satu jendela video per monitor,
/// ditempel di lapisan desktop (di belakang ikon), video didekode GPU lewat Windows Media Foundation.
/// Berhenti sendiri kalau app utama sudah tidak ada.
/// </summary>
public sealed class LiveWallpaperHost
{
    static readonly string[] VideoExt = { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm" };

    readonly int _parentPid;
    readonly Dispatcher _ui;
    readonly List<WallWindow> _windows = new();
    readonly DispatcherTimer _tick, _reload;
    FileSystemWatcher? _watch;
    EventWaitHandle? _game, _quit;
    IntPtr _parent;
    string _lastKey = "";

    public LiveWallpaperHost(int parentPid, Dispatcher ui)
    {
        _parentPid = parentPid;
        _ui = ui;
        _tick = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Tick();
        _reload = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(500) };
        _reload.Tick += (_, _) => { _reload.Stop(); Settings.Load(); Apply(false); };
    }

    public void Run()
    {
        try { _game = EventWaitHandle.OpenExisting(LiveWallpaperManager.GameEventName); } catch { }
        try { _quit = EventWaitHandle.OpenExisting(LiveWallpaperManager.QuitEventName); } catch { }
        try
        {
            string dir = Path.GetDirectoryName(Settings.FilePath)!;
            _watch = new FileSystemWatcher(dir, Path.GetFileName(Settings.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler onChange = (_, _) => _ui.BeginInvoke(new Action(() => { _reload.Stop(); _reload.Start(); }));
            _watch.Changed += onChange;
            _watch.Created += onChange;
            _watch.Renamed += (_, _) => _ui.BeginInvoke(new Action(() => { _reload.Stop(); _reload.Start(); }));
        }
        catch { }
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => _ui.BeginInvoke(new Action(() => Apply(true)));
        Apply(true);
        _tick.Start();
    }

    /// <summary>Pasang / perbarui jendela per monitor sesuai pengaturan.</summary>
    void Apply(bool reattach)
    {
        var s = Settings.Current;
        if (!s.LiveWallpaperEnabled || !LiveWallpaperManager.HasAnyFile(s)) { Quit(); return; }
        var mons = Monitors.All();
        var files = s.LiveWallpaperFiles ?? new();
        string primary = files.Count > 0 ? files[0] : "";
        string key = string.Join("|", mons.Select(m => m.Handle)) + "#" + string.Join("|", files) + "#" + s.LiveWallpaperFit
                     + "#" + s.LiveWallpaperSound + "#" + s.LiveWallpaperVolume.ToString("0.00") + "#" + s.LiveWallpaperSeamlessLoop;
        if (key == _lastKey && !reattach) return;
        bool monitorsChanged = _windows.Count != mons.Count || reattach;
        _lastKey = key;

        if (monitorsChanged)
        {
            foreach (var w in _windows) { try { w.Close(); } catch { } }
            _windows.Clear();
            _parent = DesktopLayer.Prepare(out bool newLayout);
            Diag($"lapisan desktop: induk=0x{_parent.ToInt64():X} ({DesktopLayer.ClassOf(_parent)}), layout {(newLayout ? "24H2 (WorkerW di dalam Progman)" : "lama (WorkerW terpisah)")}, monitor={mons.Count}");
        }

        for (int i = 0; i < mons.Count; i++)
        {
            string file = i < files.Count && Usable(files[i]) ? files[i] : primary;
            if (!Usable(file)) file = files.FirstOrDefault(Usable) ?? "";
            if (i >= _windows.Count) _windows.Add(new WallWindow());
            var w = _windows[i];
            bool video = VideoExt.Contains(Path.GetExtension(file).ToLowerInvariant());
            w.SetSource(file, video, s.LiveWallpaperFit, sound: i == 0 && s.LiveWallpaperSound, volume: s.LiveWallpaperVolume, seamless: s.LiveWallpaperSeamlessLoop);
            if (monitorsChanged)
            {
                var r = Native.MonitorRect(mons[i].Handle);
                w.AttachTo(_parent, r);
                Diag(DesktopLayer.Describe(w.HostHandle != IntPtr.Zero ? w.HostHandle : w.Handle));
                Diag($"monitor {i}: {r.Right - r.Left}x{r.Bottom - r.Top} di ({r.Left},{r.Top}), file: {Path.GetFileName(file)} ({(File.Exists(file) ? new FileInfo(file).Length / 1048576.0 : 0):0.0} MB)");
            }
            w.Monitor = mons[i].Handle;
        }
        Tick();
    }

    /// <summary>File ada dan tidak melebihi batas ukuran (Pengaturan → Live wallpaper).</summary>
    static bool Usable(string f) => LiveWallpaperManager.Usable(f, Settings.Current);

    /// <summary>Catatan diagnosa (livewallpaper.log di samping crash.log), ditimpa tiap kali proses mulai.</summary>
    static bool _diagStarted;
    public static void Note(string line) => Diag(line);
    static void Diag(string line)
    {
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(CrashGuard.LogPath)!, "livewallpaper.log");
            string text = $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}";
            if (!_diagStarted) { File.WriteAllText(path, text); _diagStarted = true; }
            else File.AppendAllText(path, text);
        }
        catch { }
    }

    void Tick()
    {
        // app utama sudah tidak ada → ikut keluar
        bool parentAlive;
        try { using var p = Process.GetProcessById(_parentPid); parentAlive = !p.HasExited; } catch { parentAlive = false; }
        if (!parentAlive || (_quit?.WaitOne(0) ?? false)) { Quit(); return; }

        // Explorer di-restart: lapisan desktop lama hilang → pasang ulang
        if (_parent != IntPtr.Zero && !IsWindow(_parent)) { Apply(true); return; }

        var s = Settings.Current;
        bool game = s.LiveWallpaperPauseGame && (_game?.WaitOne(0) ?? false);
        bool battery = s.LiveWallpaperPauseOnBattery && SystemParameters.PowerLineStatus == PowerLineStatus.Offline;
        for (int i = 0; i < _windows.Count; i++)
        {
            var w = _windows[i];
            bool covered = s.LiveWallpaperPauseCovered && WidgetManager.IsMonitorCovered(w.Monitor);
            bool pause = game || battery || covered;
            if (w.SetPaused(pause))
                Diag(pause ? $"monitor {i} dijeda ({(game ? "mode game " : "")}{(battery ? "baterai " : "")}{(covered ? "tertutup jendela penuh: " + Native.ClassNameOf(Native.GetForegroundWindow()) : "")})"
                           : $"monitor {i} lanjut");
        }
    }

    bool _quitting;

    void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _tick.Stop();
        foreach (var w in _windows) { try { w.Close(); } catch { } }
        _windows.Clear();
        DesktopLayer.RefreshWallpaper(); // tampilkan lagi wallpaper Windows (gambar biasa)
        Application.Current.Shutdown(0);
    }

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
}

/// <summary>Jendela video satu monitor, ditempel ke lapisan desktop.</summary>
sealed class WallWindow : Window
{
    readonly Grid _root = new() { Background = Brushes.Black };
    MediaElement? _video;
    MediaClock? _clock;
    Image? _image;
    string _file = "";
    bool _paused, _isVideo;

    // loop mulus: dua pemutar bergantian. Yang aktif di atas; yang cadangan sudah terbuka & diam di frame pertama
    // di bawahnya, lalu mulai diputar sesaat sebelum yang aktif habis → tidak ada jeda "buka ulang file" di tiap loop.
    readonly MediaElement?[] _players = new MediaElement?[2];
    readonly bool[] _opened = new bool[2];
    /// <summary>Durasi video. Sebagian file (mis. ekspor Wallpaper Engine) tidak memberi durasi saat dibuka
    /// ("Automatic") → dipelajari dari posisi saat loop pertama selesai.</summary>
    TimeSpan? _duration;
    // detektor macet: sebagian file tidak pernah memicu MediaEnded → video diam di frame terakhir.
    readonly Stopwatch _playClock = new();
    TimeSpan _lastPos, _lastMove, _lastTick;
    bool _durationConfirmed; // false = masih dari header file (bisa salah) → cek lebih sering
    bool _loggedProgress;
    int _loops, _kicks;
    // pengukur freeze di sekitar pergantian loop (log saja)
    DispatcherTimer? _probe;
    readonly Stopwatch _probeSw = new();
    MediaElement? _probeVis;
    TimeSpan _probePos;
    long _probeChange, _probeMax, _probeMaxAt, _probeRaiseAt;
    int _probeLoop;
    bool _checkAfterSwap;
    int _active;
    bool _seamless, _sound;
    Stretch _stretch = Stretch.UniformToFill;
    double _volume;
    DispatcherTimer? _loopTimer;
    static readonly TimeSpan SwapLead = TimeSpan.FromMilliseconds(260); // mulai cadangan selama ini sebelum habis
    static readonly TimeSpan RaiseDelay = TimeSpan.FromMilliseconds(70); // tunggu frame pertama cadangan tergambar
    public IntPtr Monitor;
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public WallWindow()
    {
        Title = "Dynamic Island – Live wallpaper";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Background = Brushes.Black;
        Left = -32000;
        Top = -32000;
        Width = 100;
        Height = 100;
        Content = _root;
    }

    public void SetSource(string file, bool video, int fit, bool sound, double volume, bool seamless)
    {
        var stretch = fit switch { 1 => Stretch.Uniform, 2 => Stretch.Fill, _ => Stretch.UniformToFill };
        _sound = sound;
        _volume = Math.Clamp(volume, 0, 1);
        if (file != _file || video != _isVideo || (video && seamless != _seamless))
        {
            _file = file;
            _isVideo = video;
            _seamless = seamless;
            _root.Children.Clear();
            StopVideo();
            _image = null;
            if (file.Length == 0) return;
            if (video && seamless)
            {
                var uri = new Uri(file);
                _duration = null;
                _durationConfirmed = false;
                _loggedProgress = false;
                ResetProgress();
                for (int k = 0; k < 2; k++) { _opened[k] = false; _players[k] = CreatePlayer(uri, k, onTop: k == 0); }
                _ = LoadDurationAsync(file);
                _active = 0;
                _loopTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
                _loopTimer.Tick += (_, _) => LoopTick();
                foreach (var p in _players) _root.Children.Add(p);
                foreach (var p in _players) p!.Play(); // membuka file; MediaOpened menentukan diputar / diam
                if (!_paused) { _loopTimer.Start(); _playClock.Start(); }
            }
            else if (video)
            {
                // berulang tanpa henti lewat jam media WPF (lebih andal daripada "putar lagi saat selesai")
                var timeline = new MediaTimeline(new Uri(file)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
                _clock = timeline.CreateClock();
                _video = new MediaElement { IsHitTestVisible = false, ScrubbingEnabled = false };
                RenderOptions.SetBitmapScalingMode(_video, BitmapScalingMode.LowQuality); // GPU, tanpa filter mahal
                _video.MediaFailed += (_, e) => CrashGuard.Log(e.ErrorException, "live wallpaper: video tidak bisa diputar (" + Path.GetFileName(file) + ")");
                _video.MediaOpened += (_, _) =>
                {
                    LiveWallpaperHost.Note($"video terbuka: {Path.GetFileName(file)} {_video?.NaturalVideoWidth}x{_video?.NaturalVideoHeight}, durasi {_video?.NaturalDuration}");
                    // sedang dijeda (mis. pakai baterai): tetap tampilkan frame pertama, jangan layar hitam
                    if (_paused) ShowStillFrame();
                };
                _video.Clock = _clock;
                _root.Children.Add(_video);
            }
            else
            {
                _image = new Image { Source = WallpaperService.Decode(file, 3840), IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
                _root.Children.Add(_image);
            }
        }
        if (_video != null)
        {
            _video.Stretch = stretch;
            _video.IsMuted = !sound;
            _video.Volume = Math.Clamp(volume, 0, 1);
        }
        for (int k = 0; k < 2; k++)
        {
            var p = _players[k];
            if (p == null) continue;
            p.Stretch = stretch;
            _stretch = stretch;
            p.Volume = _volume;
            p.IsMuted = !(sound && k == _active);
        }
        if (_image != null) _image.Stretch = stretch;
    }

    MediaElement CreatePlayer(Uri uri, int index, bool onTop)
    {
        // Manual: Play/Pause/Position dikendalikan sendiri. ScrubbingEnabled: cadangan yang diam & di-seek ke 0
        // benar-benar menampilkan frame pertama (bukan frame terakhir loop sebelumnya).
        var p = new MediaElement
        {
            IsHitTestVisible = false, LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true, IsMuted = true, Source = uri,
        };
        RenderOptions.SetBitmapScalingMode(p, BitmapScalingMode.LowQuality);
        Panel.SetZIndex(p, onTop ? 1 : 0);
        p.Stretch = _stretch;
        p.Volume = _volume;
        p.MediaFailed += (_, e) => CrashGuard.Log(e.ErrorException, "live wallpaper: video tidak bisa diputar (" + Path.GetFileName(uri.LocalPath) + ")");
        p.MediaOpened += (_, _) =>
        {
            _opened[index] = true;
            if (p.NaturalDuration.HasTimeSpan && p.NaturalDuration.TimeSpan > TimeSpan.Zero) _duration ??= p.NaturalDuration.TimeSpan;
            if (index == 0)
                LiveWallpaperHost.Note($"video terbuka (loop mulus): {Path.GetFileName(uri.LocalPath)} {p.NaturalVideoWidth}x{p.NaturalVideoHeight}, durasi {p.NaturalDuration}");
            // cadangan, atau sedang dijeda: diam di frame pertama
            if (_players[_active] != p || _paused) p.Pause(); // tanpa seek: MP4 terfragmentasi tidak bisa di-seek
            p.IsMuted = !(_sound && _players[_active] == p);
        };
        // pengaman: kalau pergantian terlewat (mis. sistem sibuk), ganti saat video habis
        p.MediaEnded += (_, _) =>
        {
            if (_players[_active] != p) return;
            // durasi asli = posisi saat selesai (header file bisa salah, mis. 20.8 dtk padahal videonya ±12.5 dtk)
            // Position saat selesai tidak bisa dipercaya (bisa 0, bisa durasi header 20.8) → ambil yang lebih kecil dari posisi & jam nyata
            var clock = _playClock.Elapsed;
            var end = p.Position > TimeSpan.FromSeconds(0.5) && p.Position < clock ? p.Position : clock;
            // selalu ambil yang TERPENDEK: kalau terlalu panjang, pergantian telat → freeze singkat di akhir
            if (end > TimeSpan.FromSeconds(0.5) && (_duration == null || end < _duration.Value - TimeSpan.FromMilliseconds(30)))
            {
                _duration = end;
                _durationConfirmed = true;
                LiveWallpaperHost.Note($"durasi video dipelajari: {end.TotalSeconds:0.00} dtk");
            }
            if (_loops < 4) LiveWallpaperHost.Note($"selesai: posisi {p.Position.TotalSeconds:0.00}, jam {clock.TotalSeconds:0.00}, akhir {end.TotalSeconds:0.00}");
            if (_opened[1 - _active]) Swap(raiseNow: true, ended: true);
            else { Reopen(index); ResetProgress(); }
        };
        return p;
    }

    /// <summary>
    /// Ganti pemutar ini dengan MediaElement BARU yang membuka file dari awal. Bukan "Position = 0" (MP4 terfragmentasi
    /// tidak bisa di-seek) dan bukan membuka ulang di elemen yang sama (posisinya macet di 0). Elemen baru
    /// terbukti jalan normal. Cadangan berhenti di frame pertama (MediaOpened); pemutar aktif langsung jalan.
    /// </summary>
    void Reopen(int index)
    {
        var old = _players[index];
        if (old == null || old.Source == null) return;
        var uri = old.Source;
        bool onTop = Panel.GetZIndex(old) > 0;
        _opened[index] = false;
        var p = CreatePlayer(uri, index, onTop);
        _players[index] = p;
        // elemen baru dipasang dulu, elemen lama dibuang setelahnya → tidak ada frame kosong
        _root.Children.Insert(0, p);
        _root.Children.Remove(old);
        try { old.Stop(); old.Close(); } catch { }
        p.Play();
    }

    void LoopTick()
    {
        var a = _players[_active];
        if (a == null || _paused || _loopTimer == null || !_opened[_active]) return;
        if (_duration == null && a.NaturalDuration.HasTimeSpan && a.NaturalDuration.TimeSpan > TimeSpan.Zero)
            _duration = a.NaturalDuration.TimeSpan; // kadang baru tersedia setelah diputar
        bool ready = _opened[1 - _active];

        var pos = a.Position;
        var now = _playClock.Elapsed;
        // Di akhir file MP4 terfragmentasi, Position MELOMPAT ke durasi header (mis. 12.6 → 20.8 dtk).
        // Lompatan jauh lebih besar dari waktu yang berlalu = video sudah habis di posisi terakhir yang wajar.
        // (bisa sekaligus, atau "berlari" cepat beberapa tick sementara gambar diam) → maju > 2× waktu nyata + 120 ms
        if (_lastPos > TimeSpan.FromSeconds(0.5) && _lastTick > TimeSpan.Zero
            && pos - _lastPos > (now - _lastTick) * 2 + TimeSpan.FromMilliseconds(120))
        {
            var end = _lastPos; // perkiraan aman (sedikit lebih awal lebih baik daripada telat)
            if (_duration == null || end < _duration.Value - TimeSpan.FromMilliseconds(30))
            {
                _duration = end;
                _durationConfirmed = true;
                LiveWallpaperHost.Note($"posisi melompat {_lastPos.TotalSeconds:0.00} → {pos.TotalSeconds:0.00} dtk: durasi asli {end.TotalSeconds:0.00} dtk");
            }
            _lastTick = now;
            if (ready) Swap(raiseNow: true); else { Reopen(_active); ResetProgress(); }
            return;
        }
        _lastTick = now;
        if (pos != _lastPos) { _lastPos = pos; _lastMove = now; }
        bool posWorks = pos > TimeSpan.Zero;

        // pemutar baru tidak benar-benar jalan (posisi tetap 0) → paksa putar ulang
        if (_checkAfterSwap && now > TimeSpan.FromMilliseconds(1500))
        {
            _checkAfterSwap = false;
            if (_loops <= 4 || pos < TimeSpan.FromMilliseconds(100))
                LiveWallpaperHost.Note($"loop #{_loops}: pemutar {_active} posisi {pos.TotalSeconds:0.00} dtk setelah {now.TotalSeconds:0.0} dtk");
            if (pos < TimeSpan.FromMilliseconds(100))
            {
                _kicks++;
                LiveWallpaperHost.Note($"pemutar {_active} tidak bergerak → dibuat ulang (#{_kicks})");
                Reopen(_active);
                ResetProgress();
                return;
            }
        }
        if (!_loggedProgress && now > TimeSpan.FromSeconds(3))
        {
            _loggedProgress = true;
            LiveWallpaperHost.Note($"posisi setelah {now.TotalSeconds:0.0} dtk: {pos.TotalSeconds:0.00} dtk, durasi {(_duration?.TotalSeconds.ToString("0.00") ?? "belum diketahui")}");
        }

        // macet di akhir (posisi diam padahal sedang diputar) → anggap selesai
        if (posWorks && now - _lastMove > TimeSpan.FromMilliseconds(400) && pos > TimeSpan.FromSeconds(0.5))
        {
            if (_duration == null || pos < _duration.Value - TimeSpan.FromMilliseconds(300))
            {
                _duration = pos; _durationConfirmed = true;
                LiveWallpaperHost.Note($"video berhenti di {pos.TotalSeconds:0.00} dtk tanpa sinyal selesai → durasi dipakai {pos.TotalSeconds:0.00} dtk");
            }
            if (ready) Swap(raiseNow: true); else { Reopen(_active); ResetProgress(); }
            return;
        }

        if (_duration == null || !ready) { _loopTimer.Interval = TimeSpan.FromMilliseconds(100); return; }
        var est = posWorks ? pos : now; // posisi tidak bergerak sama sekali → pakai jam sendiri
        var left = _duration.Value - est;
        if (left <= SwapLead) { Swap(raiseNow: false); _loopTimer.Interval = TimeSpan.FromMilliseconds(250); }
        else if (left <= TimeSpan.FromMilliseconds(700)) { _loopTimer.Interval = TimeSpan.FromMilliseconds(15); StartProbe(); } // presisi menjelang akhir
        else _loopTimer.Interval = TimeSpan.FromMilliseconds(_durationConfirmed ? 250 : 50);
    }

    void StartProbe()
    {
        if (_probe != null || _loops >= 8) return;
        _probeSw.Restart();
        _probeVis = null; _probeChange = 0; _probeMax = 0; _probeMaxAt = 0; _probeRaiseAt = -1; _probeLoop = _loops + 1;
        _probe = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(8) };
        _probe.Tick += (_, _) =>
        {
            long t = _probeSw.ElapsedMilliseconds;
            MediaElement? vis = null;
            foreach (var p in _players) if (p != null && Panel.GetZIndex(p) > 0) vis = p;
            if (vis == null) return;
            var pos = vis.Position;
            if (vis != _probeVis)
            {
                if (_probeVis != null) _probeRaiseAt = t;
                _probeVis = vis; _probePos = pos;
                long st = t - _probeChange;
                if (st > _probeMax) { _probeMax = st; _probeMaxAt = _probeChange; }
                _probeChange = t;
            }
            else if (pos != _probePos)
            {
                long st = t - _probeChange;
                if (st > _probeMax) { _probeMax = st; _probeMaxAt = _probeChange; }
                _probePos = pos; _probeChange = t;
            }
            if (t > 3200 || _paused)
            {
                _probe?.Stop(); _probe = null;
                string rel = _probeRaiseAt < 0 ? "tanpa pergantian" : $"{_probeMaxAt - _probeRaiseAt:+0;-0} ms dari pergantian";
                LiveWallpaperHost.Note($"ukur loop #{_probeLoop}: gambar diam terlama {_probeMax} ms, mulai {rel}");
            }
        };
        _probe.Start();
    }

    void ResetProgress()
    {
        _lastPos = TimeSpan.Zero;
        _lastMove = TimeSpan.Zero;
        _lastTick = TimeSpan.Zero;
        if (_playClock.IsRunning) _playClock.Restart(); else _playClock.Reset();
    }

    /// <summary>Durasi dari header MP4/MOV (kotak mvhd, atau mehd untuk MP4 terfragmentasi). Di thread latar.</summary>
    async System.Threading.Tasks.Task LoadDurationAsync(string file)
    {
        if (DurationCache.Load(file) is { } cached)
        {
            _duration = cached;
            _durationConfirmed = true;
            LiveWallpaperHost.Note($"durasi tersimpan: {cached.TotalSeconds:0.00} dtk");
            return;
        }
        TimeSpan? d = null;
        string how = "tidak ada di header";
        try { d = await System.Threading.Tasks.Task.Run(() => Mp4Duration.Read(file)); }
        catch (Exception e) { how = "gagal: " + e.Message; }
        if (_file != file) return;
        if (d is { } v && v > TimeSpan.FromSeconds(0.5))
        {
            _duration ??= v;
            LiveWallpaperHost.Note($"durasi dari header file: {v.TotalSeconds:0.00} dtk");
        }
        else LiveWallpaperHost.Note("durasi dari header file: " + how + " (pakai detektor macet)");
    }

    /// <summary>Cadangan mulai diputar, naik ke atas; yang lama dijeda & kembali ke awal untuk loop berikutnya.</summary>
    void Swap(bool raiseNow, bool ended = false, [System.Runtime.CompilerServices.CallerLineNumber] int from = 0)
    {
        if (ended && _loops > 0) // video benar-benar habis (bukan loop pertama: jam di loop pertama termasuk waktu membuka file)
        {
            var real = _playClock.Elapsed;
            if (real > TimeSpan.FromSeconds(0.5) && (_duration == null || real < _duration.Value - TimeSpan.FromMilliseconds(30)))
            {
                _duration = real;
                _durationConfirmed = true;
                LiveWallpaperHost.Note($"durasi dari jam nyata: {real.TotalSeconds:0.00} dtk");
                DurationCache.Save(_file, real);
            }
        }
        int oldI = _active, newI = 1 - _active;
        var a = _players[oldI];
        var b = _players[newI];
        if (a == null || b == null) return;
        if (_loops < 6)
            LiveWallpaperHost.Note($"ganti #{_loops + 1} (baris {from}): posisi lama {a.Position.TotalSeconds:0.00} dtk, jam {_playClock.Elapsed.TotalSeconds:0.00} dtk, durasi {_duration?.TotalSeconds.ToString("0.00") ?? "?"}{(_durationConfirmed ? "" : " (header)")}");
        StartProbe();
        _active = newI;
        _loops++;
        _checkAfterSwap = true;
        ResetProgress();
        b.IsMuted = !_sound;
        b.Play();
        a.IsMuted = true;
        void Raise()
        {
            if (_players[newI] != b) return; // sumber sudah diganti
            Panel.SetZIndex(b, 1);
            Panel.SetZIndex(a, 0);
            a.Pause();
            // buka ulang di belakang (seek ke 0 gagal di MP4 terfragmentasi) — DITUNDA 2 dtk: membuka file &
            // menyiapkan dekoder memakai thread UI/GPU, kalau langsung saat pergantian video yang baru tampil tersendat.
            var later = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
            later.Tick += (_, _) => { later.Stop(); if (_players[oldI] == a && _active != oldI) Reopen(oldI); };
            later.Start();
        }
        // Naikkan cadangan HANYA setelah ia benar-benar menggambar frame (posisi bergerak). Sampai saat itu
        // pemutar lama tetap di atas: masih jalan, atau diam di frame terakhirnya → tidak ada kedip hitam.
        var sw = Stopwatch.StartNew();
        var t = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(15) };
        t.Tick += (_, _) =>
        {
            if (_players[newI] != b) { t.Stop(); return; }
            if (b.Position < TimeSpan.FromMilliseconds(50) && sw.ElapsedMilliseconds < 600) return;
            t.Stop();
            if (_loops <= 3) LiveWallpaperHost.Note($"loop #{_loops}: cadangan tampil setelah {sw.ElapsedMilliseconds} ms (posisi {b.Position.TotalMilliseconds:0} ms)");
            Raise();
        };
        t.Start();
    }

    /// <summary>Putar sebentar lalu jeda, supaya satu frame tergambar (MediaElement yang belum pernah diputar = hitam).</summary>
    void ShowStillFrame()
    {
        var c = _clock;
        if (c == null) return;
        c.Controller?.Resume();
        var t = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        t.Tick += (_, _) => { t.Stop(); if (_paused && _clock == c) c.Controller?.Pause(); };
        t.Start();
    }

    // ---------- lepas memori saat lama dijeda ----------
    // Dua pemutar video 1080p memakai ±500 MB walau sedang dijeda (desktop tertutup aplikasi). Setelah dijeda
    // cukup lama: simpan frame yang tampil sebagai gambar diam, tutup pemutar → memori kembali. Saat desktop
    // terlihat lagi, gambar diam tetap tampil sampai video baru benar-benar bergerak (tanpa layar hitam).
    DispatcherTimer? _releaseTimer;
    Image? _still;
    bool _released;

    void ScheduleRelease()
    {
        if (!_isVideo || !_seamless || _released || _players[_active] == null) return;
        if (!Settings.Current.LiveWallpaperReleaseWhenPaused) return;
        if (_releaseTimer == null)
        {
            _releaseTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _releaseTimer.Tick += (_, _) => { _releaseTimer!.Stop(); if (_paused && !_released) ReleasePlayers(); };
        }
        _releaseTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(Settings.Current.LiveWallpaperReleaseAfterSec, 5, 600));
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    void ReleasePlayers()
    {
        var bmp = Snapshot();
        if (bmp == null) { LiveWallpaperHost.Note("memori tidak dilepas: frame video tidak bisa disalin"); return; }
        _still = new Image { Source = bmp, Stretch = Stretch.Fill, IsHitTestVisible = false };
        Panel.SetZIndex(_still, 5);
        _root.Children.Add(_still);
        _loopTimer?.Stop();
        _probe?.Stop(); _probe = null;
        for (int k = 0; k < 2; k++)
        {
            var p = _players[k];
            if (p == null) continue;
            _root.Children.Remove(p);
            try { p.Stop(); p.Close(); } catch { }
            _players[k] = null;
            _opened[k] = false;
        }
        _released = true;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        MemoryTrim.Trim();
        LiveWallpaperHost.Note("video dilepas dari memori (lama dijeda); tampil gambar diam");
    }

    void RestorePlayers()
    {
        _released = false;
        if (_file.Length == 0) return;
        var uri = new Uri(_file);
        ResetProgress();
        _active = 0;
        for (int k = 0; k < 2; k++)
        {
            _opened[k] = false;
            _players[k] = CreatePlayer(uri, k, onTop: k == 0);
            _root.Children.Insert(0, _players[k]);
        }
        foreach (var p in _players) p!.Play();
        _loopTimer?.Start();
        _playClock.Start();
        // gambar diam dibuang setelah video baru benar-benar bergerak
        var sw = Stopwatch.StartNew();
        var t = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
        t.Tick += (_, _) =>
        {
            var a = _players[_active];
            bool moving = a != null && a.Position >= TimeSpan.FromMilliseconds(60);
            if (!moving && sw.ElapsedMilliseconds < 3000 && !_released) return;
            t.Stop();
            if (_released) return; // sudah dilepas lagi
            if (_still != null) { _root.Children.Remove(_still); _still = null; }
            LiveWallpaperHost.Note($"video dimuat lagi setelah {sw.ElapsedMilliseconds} ms");
        };
        t.Start();
    }

    /// <summary>Salin tampilan video saat ini. Null kalau gagal atau hasilnya hitam (supaya tidak ada kedip hitam).</summary>
    BitmapSource? Snapshot()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)Math.Round(_root.ActualWidth * dpi.DpiScaleX), h = (int)Math.Round(_root.ActualHeight * dpi.DpiScaleY);
            if (w < 16 || h < 16) return null;
            var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(_root);
            // cek beberapa baris: semuanya hitam → anggap gagal
            int stride = w * 4;
            var row = new byte[stride];
            long sum = 0;
            foreach (int y in new[] { h / 4, h / 2, h * 3 / 4 })
            {
                rtb.CopyPixels(new Int32Rect(0, y, w, 1), row, stride, 0);
                for (int i = 0; i < stride; i += 16) sum += row[i] + row[i + 1] + row[i + 2];
            }
            if (sum < (long)w * 3 / 4 * 3 * 4) return null; // rata-rata < ±4 per kanal
            rtb.Freeze();
            return rtb;
        }
        catch { return null; }
    }

    void StopVideo()
    {
        _releaseTimer?.Stop();
        _released = false;
        _still = null;
        _loopTimer?.Stop();
        _loopTimer = null;
        for (int k = 0; k < 2; k++)
        {
            var p = _players[k];
            if (p == null) continue;
            try { p.Stop(); p.Close(); } catch { }
            _players[k] = null;
        }
        try { _clock?.Controller?.Stop(); } catch { }
        if (_video != null) { try { _video.Clock = null; } catch { } }
        _clock = null;
        _video = null;
    }

    /// <summary>True kalau statusnya berubah.</summary>
    public bool SetPaused(bool paused)
    {
        if (paused == _paused) return false;
        _paused = paused;
        if (paused) ScheduleRelease(); else _releaseTimer?.Stop();
        if (_released)
        {
            if (!paused) RestorePlayers();
            return true;
        }
        var cur = _players[_active];
        if (cur != null)
        {
            if (paused) { _loopTimer?.Stop(); cur.Pause(); _playClock.Stop(); }
            else { cur.Play(); _loopTimer?.Start(); _lastMove = _playClock.Elapsed; _playClock.Start(); }
            return true;
        }
        if (_clock == null) return true;
        if (paused) _clock.Controller?.Pause(); else _clock.Controller?.Resume();
        return true;
    }

    IntPtr _host;

    /// <summary>
    /// Tempelkan ke lapisan desktop, seukuran monitor (piksel fisik).
    /// Windows 11 24H2 hanya menampilkan anak bertipe "layered" di lapisan desktop, tapi WPF membuang tanda
    /// layered dari jendelanya sendiri. Jadi: jendela perantara Win32 murni (layered, alpha 255) dibuat di lapisan
    /// desktop, lalu jendela video WPF ditaruh DI DALAM perantara itu.
    /// </summary>
    public void AttachTo(IntPtr parent, Native.RECT monitor)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        // jangan muncul di Alt+Tab / Task View (WS_EX_TOOLWINDOW, tanpa WS_EX_APPWINDOW)
        long ex = Native.GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr((ex | 0x80L) & ~0x40000L));
        Show();
        try
        {
            var src = HwndSource.FromHwnd(hwnd);
            LiveWallpaperHost.Note($"render: tier {RenderCapability.Tier >> 16}, mode jendela {src?.CompositionTarget?.RenderMode}, mode proses {RenderOptions.ProcessRenderMode}");
        }
        catch { }
        int w = monitor.Right - monitor.Left, h = monitor.Bottom - monitor.Top;
        var pt = new POINT { X = monitor.Left, Y = monitor.Top };
        if (parent != IntPtr.Zero) ScreenToClient(parent, ref pt);

        if (_host != IntPtr.Zero) { DestroyWindow(_host); _host = IntPtr.Zero; }
        if (parent != IntPtr.Zero) _host = LayeredHost.Create(parent, pt.X, pt.Y, w, h);
        IntPtr target = _host != IntPtr.Zero ? _host : parent;

        if (target != IntPtr.Zero)
        {
            long style = Native.GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            style = (style & ~WS_POPUP) | WS_CHILD;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
            SetParent(hwnd, target);
        }
        if (_host != IntPtr.Zero)
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        else
            SetWindowPos(hwnd, IntPtr.Zero, pt.X, pt.Y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        if (_clock != null && !_paused) _clock.Controller?.Resume(); // pastikan berjalan setelah jendelanya tampil
        if (_players[_active] is { } cur && !_paused) cur.Play();
    }

    public IntPtr HostHandle => _host;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        StopVideo();
        if (_host != IntPtr.Zero) { DestroyWindow(_host); _host = IntPtr.Zero; }
    }

    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);

    const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    const long WS_EX_LAYERED = 0x00080000L;
    const uint LWA_ALPHA = 0x2;
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    const long WS_POPUP = 0x80000000L, WS_CHILD = 0x40000000L;
    const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_FRAMECHANGED = 0x0020;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

/// <summary>Jendela Win32 murni bertipe layered (alpha 255) sebagai wadah video di lapisan desktop.</summary>
static class LayeredHost
{
    const string ClassName = "DynamicIslandWallHost";
    static WndProc? _proc; // disimpan supaya tidak dibersihkan GC
    static bool _registered;

    public static IntPtr Create(IntPtr parent, int x, int y, int w, int h)
    {
        IntPtr inst = GetModuleHandle(null);
        if (!_registered)
        {
            _proc = DefWindowProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = inst, lpszClassName = ClassName, hbrBackground = GetStockObject(4 /*BLACK_BRUSH*/),
            };
            if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /*sudah terdaftar*/) return IntPtr.Zero;
            _registered = true;
        }
        IntPtr hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_NOACTIVATE | 0x80 /*WS_EX_TOOLWINDOW*/, ClassName, "Dynamic Island – Live wallpaper",
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, x, y, w, h, parent, IntPtr.Zero, inst, IntPtr.Zero);
        if (hwnd != IntPtr.Zero) SetLayeredWindowAttributes(hwnd, 0, 255, 0x2 /*LWA_ALPHA*/);
        return hwnd;
    }

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    const uint WS_EX_LAYERED = 0x00080000, WS_EX_NOACTIVATE = 0x08000000;
    const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int obj);
}

/// <summary>
/// Lapisan desktop di belakang ikon. Windows tidak punya API resmi; caranya (dipakai semua aplikasi live wallpaper):
/// kirim pesan 0x052C ke Progman supaya Explorer membuat jendela WorkerW di belakang ikon.
/// - Windows 11 24H2 ke atas: WorkerW menjadi ANAK Progman → video ditaruh di Progman, di bawah ikon (SHELLDLL_DefView).
/// - Versi lama: WorkerW berdiri sendiri tepat di belakang jendela ikon → video menjadi anak WorkerW itu.
/// </summary>
static class DesktopLayer
{
    static bool _newLayout;

    public static IntPtr Prepare(out bool newLayout)
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0, 1000, out _);
            SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
        }
        // 24H2: Progman → [SHELLDLL_DefView, WorkerW]
        IntPtr childWorker = progman != IntPtr.Zero ? FindWindowEx(progman, IntPtr.Zero, "WorkerW", null) : IntPtr.Zero;
        if (childWorker != IntPtr.Zero)
        {
            // 24H2: WorkerW (lapisan gambar wallpaper) ada DI DALAM Progman, di bawah ikon (SHELLDLL_DefView).
            // Video dijadikan anak WorkerW itu → menutupi gambar wallpaper, tapi tetap di bawah ikon.
            _newLayout = newLayout = true;
            return childWorker;
        }
        // versi lama: jendela top-level yang berisi ikon, lalu WorkerW sesudahnya
        IntPtr worker = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                worker = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            return true;
        }, IntPtr.Zero);
        _newLayout = newLayout = false;
        return worker != IntPtr.Zero ? worker : progman;
    }

    /// <summary>Urutan di dalam induk: 24H2 → tepat di bawah ikon (DefView); versi lama → paling atas di WorkerW.</summary>
    public static IntPtr InsertAfter(IntPtr parent)
    {
        return IntPtr.Zero; // HWND_TOP di dalam induknya
    }

    /// <summary>Untuk diagnosa: induk, terlihat atau tidak, posisi, gaya, dan urutan saudara-saudaranya.</summary>
    public static string Describe(IntPtr hwnd)
    {
        var sb = new StringBuilder();
        try
        {
            IntPtr parent = GetParent(hwnd);
            GetWindowRect(hwnd, out var r);
            sb.Append($"jendela video 0x{hwnd.ToInt64():X}: induk=0x{parent.ToInt64():X} ({ClassOf(parent)}), terlihat={IsWindowVisible(hwnd)}, ");
            sb.Append($"rect=({r.Left},{r.Top},{r.Right},{r.Bottom}), style=0x{GetWindowLongPtrW(hwnd, -16).ToInt64():X}, ex=0x{GetWindowLongPtrW(hwnd, -20).ToInt64():X}");
            IntPtr root = parent != IntPtr.Zero ? GetParent(parent) : IntPtr.Zero;
            IntPtr top = root != IntPtr.Zero ? root : parent;
            sb.Append($" | anak-anak {ClassOf(top)}: ");
            for (IntPtr c = GetWindow(top, 5 /*GW_CHILD*/); c != IntPtr.Zero; c = GetWindow(c, 2 /*GW_HWNDNEXT*/))
            {
                GetWindowRect(c, out var cr);
                sb.Append($"[{ClassOf(c)} 0x{c.ToInt64():X} vis={IsWindowVisible(c)} {cr.Right - cr.Left}x{cr.Bottom - cr.Top}] ");
            }
        }
        catch (Exception ex) { sb.Append("gagal: " + ex.Message); }
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);

    public static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(64);
        try { GetClassName(h, sb, sb.Capacity); } catch { }
        return sb.ToString();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

    /// <summary>Saat keluar: suruh Windows menggambar ulang wallpaper biasa (kalau tidak, bisa tersisa hitam).</summary>
    public static void RefreshWallpaper()
    {
        try
        {
            var sb = new StringBuilder(520);
            if (SystemParametersInfo(0x0073, (uint)sb.Capacity, sb, 0) && sb.Length > 0) // SPI_GETDESKWALLPAPER
                SystemParametersInfo(0x0014, 0, sb, 0x02); // SPI_SETDESKWALLPAPER, kirim perubahan
        }
        catch { }
    }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint winIni);
}


/// <summary>
/// Pembaca durasi MP4/MOV minimal: mvhd (durasi film), mvex/mehd, atau — untuk MP4 terfragmentasi tanpa
/// keduanya — jumlah durasi sampel di semua fragmen (moof/traf/trun) trak video pertama.
/// </summary>
static class Mp4Duration
{
    public static TimeSpan? Read(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var st = new State();
        Walk(f, 0, f.Length, 0, st);
        if (st.Mvhd is { } m) return m;
        if (st.MehdUnits > 0 && st.MovieScale > 0) return TimeSpan.FromSeconds((double)st.MehdUnits / st.MovieScale);
        uint scale = st.TrackScale.TryGetValue(st.VideoTrack, out var ts) ? ts : 0;
        if (st.FragUnits > 0 && scale > 0) return TimeSpan.FromSeconds((double)st.FragUnits / scale);
        return null;
    }

    sealed class State
    {
        public TimeSpan? Mvhd;
        public ulong MovieScale, MehdUnits, FragUnits;
        public uint VideoTrack, CurTrack, TrafTrack, TrafDefaultDur;
        public readonly System.Collections.Generic.Dictionary<uint, uint> TrackScale = new();
        public readonly System.Collections.Generic.Dictionary<uint, uint> TrexDefaultDur = new();
    }

    static void Walk(FileStream f, long start, long end, int depth, State st)
    {
        long pos = start;
        var h = new byte[16];
        while (pos + 8 <= end && depth < 6)
        {
            f.Position = pos;
            if (f.Read(h, 0, 8) < 8) return;
            long size = BE32(h, 0);
            string type = Encoding.ASCII.GetString(h, 4, 4);
            int hdr = 8;
            if (size == 1) { if (f.Read(h, 8, 8) < 8) return; size = (long)BE64(h, 8); hdr = 16; }
            else if (size == 0) size = end - pos;
            if (size < hdr || pos + size > end) return;
            long body = pos + hdr, len = size - hdr;
            switch (type)
            {
                case "moov": case "mvex": case "trak": case "mdia": case "moof":
                    Walk(f, body, pos + size, depth + 1, st);
                    break;
                case "traf":
                    st.TrafTrack = 0; st.TrafDefaultDur = 0;
                    Walk(f, body, pos + size, depth + 1, st);
                    break;
                case "mvhd":
                {
                    var b = Bytes(f, body, len, 32);
                    if (b == null) break;
                    bool v1 = b[0] == 1;
                    ulong scale = v1 ? BE32(b, 20) : BE32(b, 12);
                    ulong dur = v1 ? BE64(b, 24) : BE32(b, 16);
                    st.MovieScale = scale;
                    if (scale > 0 && dur > 0 && dur != uint.MaxValue && dur != ulong.MaxValue) st.Mvhd = TimeSpan.FromSeconds((double)dur / scale);
                    break;
                }
                case "mehd":
                {
                    var b = Bytes(f, body, len, 12);
                    if (b != null) st.MehdUnits = b[0] == 1 ? BE64(b, 4) : BE32(b, 4);
                    break;
                }
                case "tkhd":
                {
                    var b = Bytes(f, body, len, 24);
                    if (b != null) st.CurTrack = b[0] == 1 ? BE32(b, 20) : BE32(b, 12);
                    break;
                }
                case "mdhd":
                {
                    var b = Bytes(f, body, len, 24);
                    if (b != null) st.TrackScale[st.CurTrack] = b[0] == 1 ? BE32(b, 20) : BE32(b, 12);
                    break;
                }
                case "hdlr":
                {
                    var b = Bytes(f, body, len, 12);
                    if (b != null && st.VideoTrack == 0 && Encoding.ASCII.GetString(b, 8, 4) == "vide") st.VideoTrack = st.CurTrack;
                    break;
                }
                case "trex":
                {
                    var b = Bytes(f, body, len, 24);
                    if (b != null) st.TrexDefaultDur[BE32(b, 4)] = BE32(b, 12);
                    break;
                }
                case "tfhd":
                {
                    var b = Bytes(f, body, len, Math.Min(len, 40));
                    if (b == null) break;
                    uint flags = (uint)(b[1] << 16 | b[2] << 8 | b[3]);
                    st.TrafTrack = BE32(b, 4);
                    int o = 8;
                    if ((flags & 0x01) != 0) o += 8; // base_data_offset
                    if ((flags & 0x02) != 0) o += 4; // sample_description_index
                    st.TrafDefaultDur = (flags & 0x08) != 0 && o + 4 <= b.Length ? BE32(b, o)
                        : st.TrexDefaultDur.TryGetValue(st.TrafTrack, out var dd) ? dd : 0;
                    break;
                }
                case "trun":
                {
                    if (st.TrafTrack != st.VideoTrack || len < 8 || len > 16 * 1024 * 1024) break;
                    var b = Bytes(f, body, len, len);
                    if (b == null) break;
                    uint flags = (uint)(b[1] << 16 | b[2] << 8 | b[3]);
                    uint count = BE32(b, 4);
                    int o = 8;
                    if ((flags & 0x001) != 0) o += 4; // data_offset
                    if ((flags & 0x004) != 0) o += 4; // first_sample_flags
                    int per = ((flags & 0x100) != 0 ? 4 : 0) + ((flags & 0x200) != 0 ? 4 : 0) + ((flags & 0x400) != 0 ? 4 : 0) + ((flags & 0x800) != 0 ? 4 : 0);
                    if ((flags & 0x100) == 0) { st.FragUnits += (ulong)count * st.TrafDefaultDur; break; }
                    for (uint k = 0; k < count && o + 4 <= b.Length; k++, o += per) st.FragUnits += BE32(b, o);
                    break;
                }
            }
            pos += size;
        }
    }

    static byte[]? Bytes(FileStream f, long at, long available, long want)
    {
        if (available < want) return null;
        var b = new byte[want];
        f.Position = at;
        return f.Read(b, 0, b.Length) == b.Length ? b : null;
    }

    static uint BE32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    static ulong BE64(byte[] b, int o) => (ulong)BE32(b, o) << 32 | BE32(b, o + 4);
}


/// <summary>
/// Durasi asli video yang sudah dipelajari (header MP4 terfragmentasi bisa salah), disimpan per file
/// supaya loop langsung mulus sejak awal saat app dibuka lagi. File kecil di samping crash.log.
/// </summary>
static class DurationCache
{
    static string PathOf() => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(CrashGuard.LogPath)!, "livewallpaper-durasi.txt");

    static string Key(string file)
    {
        var fi = new FileInfo(file);
        return $"{file.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
    }

    public static TimeSpan? Load(string file)
    {
        try
        {
            string key = Key(file);
            foreach (var line in File.ReadAllLines(PathOf()))
            {
                int i = line.LastIndexOf('=');
                if (i > 0 && line[..i] == key && double.TryParse(line[(i + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec) && sec > 0.5)
                    return TimeSpan.FromSeconds(sec);
            }
        }
        catch { }
        return null;
    }

    public static void Save(string file, TimeSpan d)
    {
        try
        {
            string key = Key(file);
            var lines = File.Exists(PathOf()) ? File.ReadAllLines(PathOf()).Where(l => !l.StartsWith(key + "=")).ToList() : new List<string>();
            lines.Add(key + "=" + d.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            if (lines.Count > 50) lines = lines.Skip(lines.Count - 50).ToList();
            File.WriteAllLines(PathOf(), lines);
        }
        catch { }
    }
}
