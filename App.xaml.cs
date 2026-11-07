using System;
using System.Threading;
using System.Windows;
using DynamicIsland.Core;
using DynamicIsland.Services;
using DynamicIsland.UI;

namespace DynamicIsland;

public partial class App : Application
{
    /// <summary>Skala DPI monitor utama (1.0 = 100%, 1.25 = 125%, dst).</summary>
    public static double DpiScale { get; set; } = 1.0;

    Mutex? _singleInstance;
    MediaService? _media;
    AudioVisualizer? _visualizer;
    VolumeService? _volume;
    BrightnessService? _brightness;
    RadioService? _radio;
    TimerService? _timer;
    EdgeWatcher? _edge;
    IslandWindow? _island;
    EdgePanelWindow? _left, _right;
    BezelWindows? _bezel;
    DockWindow? _dock;
    VolumeKeyHook? _volumeKeys;
    NativeOsdHider? _osd;
    DownloadMonitor? _downloads;
    NotificationService? _notifs;
    ClipboardHistory? _clip;
    CalendarFeed? _calendar;
    WeatherService? _weather;
    PrivacyMonitor? _privacy;
    BluetoothWatcher? _bluetooth;
    bool _gameMode;
    static SettingsWindow? _settingsWindow;
    /// <summary>Island yang sedang berjalan (dipakai Pengaturan untuk pratinjau langsung).</summary>
    public static IslandWindow? Island { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // darurat: "DynamicIsland.exe --restore-taskbar" mengembalikan taskbar Windows lalu keluar
        if (Array.Exists(e.Args, x => x.Equals("--restore-taskbar", StringComparison.OrdinalIgnoreCase)))
        {
            Settings.Load();
            TaskbarService.Restore();
            NativeOsdHider.RestoreNative();
            Shutdown();
            return;
        }

        // penjaga crash: "DynamicIsland.exe --watchdog <pid>" (dinyalakan sendiri oleh app, tanpa jendela)
        int wd = Array.FindIndex(e.Args, x => x.Equals("--watchdog", StringComparison.OrdinalIgnoreCase));
        if (wd >= 0)
        {
            Settings.Load();
            if (wd + 1 < e.Args.Length && int.TryParse(e.Args[wd + 1], out int pid))
                CrashGuard.RunWatchdog(pid);
            Shutdown();
            return;
        }
        // live wallpaper: "DynamicIsland.exe --wallpaper <pid app>" (dinyalakan oleh app utama, proses terpisah)
        int lw = Array.FindIndex(e.Args, x => x.Equals("--wallpaper", StringComparison.OrdinalIgnoreCase));
        if (lw >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DispatcherUnhandledException += (_, args) => { CrashGuard.Log(args.Exception, "live wallpaper"); args.Handled = true; };
            Settings.Load();
            int parent = lw + 1 < e.Args.Length && int.TryParse(e.Args[lw + 1], out int ppid) ? ppid : 0;
            new LiveWallpaperHost(parent, Dispatcher).Run();
            return;
        }

        bool recovered = Array.Exists(e.Args, x => x.Equals("--recovered", StringComparison.OrdinalIgnoreCase));
        CrashGuard.StartedByWatchdog = recovered;

        _singleInstance = new Mutex(true, "DynamicIsland.SingleInstance", out bool isFirst);
        if (!isFirst && recovered)
        {
            // dijalankan ulang setelah crash: tunggu sebentar kalau sisa proses lama belum benar-benar hilang
            try { isFirst = _singleInstance.WaitOne(5000); }
            catch (AbandonedMutexException) { isFirst = true; }
        }
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // catat error ke crash.log (di folder proyek) supaya mudah diperbaiki
        DispatcherUnhandledException += (_, args) => { CrashGuard.Log(args.Exception, "UI (aplikasi tetap jalan)"); args.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            CrashGuard.Log(args.ExceptionObject as Exception, "FATAL (aplikasi berhenti)");
            TaskbarService.Restore(); // jangan biarkan taskbar hilang kalau aplikasi crash
            NativeOsdHider.RestoreNative();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { TaskbarService.Restore(); NativeOsdHider.RestoreNative(); };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) => { CrashGuard.Log(args.Exception, "tugas latar"); args.SetObserved(); };

        Settings.Load();
        // penjaga crash (opsi di Pengaturan → Sistem); bisa dinyalakan kapan saja tanpa restart
        CrashGuard.EnsureWatchdog();
        Settings.Changed += CrashGuard.EnsureWatchdog;
        PerfProbe.Start(); // pengukur performa (hanya aktif kalau ada perf-on.txt)
        SystemStats.CpuPercent(); // pancing nilai awal

        // bersihkan file sisa deteksi tombol / pop-up dari sesi perbaikan sebelumnya
        foreach (var f in new[] { "diagnose-keys.txt", "keys.log", "diagnose-osd.txt", "osd.log", "osd2.log", "privacy.log", "edge.log" })
            try { System.IO.File.Delete(System.IO.Path.Combine(ProjectFolder(), f)); } catch { }

        _media = new MediaService(Dispatcher);
        _visualizer = new AudioVisualizer(Settings.Current.VisualizerBars);
        _volume = new VolumeService(Dispatcher);
        _brightness = new BrightnessService(Dispatcher);
        _radio = new RadioService(Dispatcher);
        _timer = new TimerService();

        // bingkai dibuat lebih dulu supaya notch selalu berada di atasnya
        _bezel = new BezelWindows();

        // dock pengganti taskbar
        if (Settings.Current.DockEnabled)
        {
            _dock = new DockWindow();
            _dock.Show();
        }
        ApplyTaskbarSetting();
        Settings.Changed += ApplyTaskbarSetting;

        _island = new IslandWindow(_media, _visualizer, _volume, _brightness, _radio, _timer);
        Island = _island;
        _island.ScreenStateChanged += (fullscreen, maximized) =>
        {
            _bezel?.OnScreenState(fullscreen, maximized);
            _dock?.OnScreenState(fullscreen, maximized);
        };
        _island.GameModeChanged += on =>
        {
            // mode game: lepas hook mouse, hentikan dock & panel
            _gameMode = on;
            if (on) _edge?.Pause(); else _edge?.Resume();
            ApplyVolumeKeySetting(); // saat main game, tombol volume kembali ke Windows
            _dock?.SetSuspended(on);
            WidgetManager.SetSuspended(on);
            LiveWallpaperManager.SetGame(on);
            if (_left != null) _left.Suspended = on;
            if (_right != null) _right.Suspended = on;
        };
        _island.Show();
        if (!Settings.Current.WelcomeShown)
        {
            // layar sambutan, sekali saja (bisa dibuka lagi dari Pengaturan → Sistem)
            var hello = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            hello.Tick += (_, _) => { hello.Stop(); ShowWelcome(); };
            hello.Start();
        }
        _downloads = new DownloadMonitor(Dispatcher);
        _island.AttachDownloads(_downloads);
        try
        {
            _notifs = new NotificationService(Dispatcher);
            _island.AttachNotifications(_notifs);
        }
        catch (Exception ex) { LogCrash(ex); }
        try
        {
            _clip = new ClipboardHistory(Dispatcher);
            _calendar = new CalendarFeed(Dispatcher);
            _weather = new WeatherService(Dispatcher);
            _island.AttachExtras(_clip, _calendar, _weather);
            // alamat kalender / kota diganti di Pengaturan: baca ulang (hanya kalau benar-benar berubah)
            string lastIcs = Settings.Current.CalendarIcsUrl, lastCity = Settings.Current.WeatherCity;
            bool lastEv = Settings.Current.CalendarEvents, lastWx = Settings.Current.CalendarWeather;
            Settings.Changed += () =>
            {
                var st = Settings.Current;
                if (st.CalendarIcsUrl != lastIcs || st.CalendarEvents != lastEv) { lastIcs = st.CalendarIcsUrl; lastEv = st.CalendarEvents; _calendar.Refresh(); }
                if (st.WeatherCity != lastCity || st.CalendarWeather != lastWx) { lastCity = st.WeatherCity; lastWx = st.CalendarWeather; _weather.Refresh(); }
            };
        }
        catch (Exception ex) { LogCrash(ex); }
        try
        {
            // titik mikrofon/kamera + pop-up Bluetooth (masing-masing bisa dimatikan di Pengaturan)
            _privacy = new PrivacyMonitor(Dispatcher);
            _bluetooth = new BluetoothWatcher(Dispatcher);
            _island.AttachDevices(_privacy, _bluetooth);
        }
        catch (Exception ex) { LogCrash(ex); }

        _left = new EdgePanelWindow(EdgeSide.Left, _volume, null, _media);
        _right = new EdgePanelWindow(EdgeSide.Right, null, _brightness, _media);

        _edge = new EdgeWatcher();
        _edge.TopZoneActive = () => _island?.IsTucked == true;
        _edge.BottomZoneWidth = () => _dock != null && _dock.IsTucked ? _dock.DockWidth : 0;
        _edge.Triggered += (side, y) =>
        {
            switch (side)
            {
                case EdgeSide.Left: _left?.Reveal(y, peek: false); break;
                case EdgeSide.Right: _right?.Reveal(y, peek: false); break;
                case EdgeSide.Top: _island?.RevealFromTopEdge(); break;
                case EdgeSide.Bottom: _dock?.RevealFromEdge(); break;
            }
        };

        _volume.MasterChanged += () =>
        {
            var st = Settings.Current;
            if (st.HudSide) _left?.OnExternalChange();
            else if (_left?.IsVisible == true) _left.RefreshValue();
            // volume berubah dari luar (bukan dari panel samping / island sendiri): tampilkan di notch
            if (st.HudNotch && st.ShowPanelOnKeyChange && _left?.IsMouseOver != true && _volume != null)
                _island?.ShowHud(false, _volume.Master, _volume.Muted);
        };

        // tombol volume keyboard → panel kita, bukan pop-up Windows
        _volumeKeys = new VolumeKeyHook(Dispatcher);
        _volumeKeys.Pressed += key =>
        {
            if (_volume == null) return;
            double step = Settings.Current.KeyVolumeStepPercent / 100.0;
            switch (key)
            {
                case VolumeKey.Up:
                    if (_volume.Muted) _volume.Muted = false;
                    _volume.Master = (float)Math.Round(Math.Min(1, _volume.Master + step), 2);
                    break;
                case VolumeKey.Down:
                    _volume.Master = (float)Math.Round(Math.Max(0, _volume.Master - step), 2);
                    break;
                case VolumeKey.Mute:
                    _volume.Muted = !_volume.Muted;
                    break;
            }
            if (Settings.Current.HudSide) _left?.Reveal(null, peek: true);
            if (Settings.Current.HudNotch) _island?.ShowHud(false, _volume.Master, _volume.Muted);
            _osd?.Enforce();
        };
        _osd = new NativeOsdHider();
        ApplyVolumeKeySetting();
        Settings.Changed += ApplyVolumeKeySetting;
        _brightness.ChangedExternally += () =>
        {
            _osd?.Enforce(); // tombol kecerahan F2/F3: pop-up Windows tetap tersembunyi
            var st = Settings.Current;
            if (st.HudSide) _right?.OnExternalChange();
            else if (_right?.IsVisible == true) _right.RefreshValue();
            if (st.HudNotch && st.ShowPanelOnKeyChange && _right?.IsMouseOver != true && _brightness != null)
                _island?.ShowHud(true, _brightness.Current / 100.0);
        };
        _brightness.Ready += () => _right?.RefreshValue();
        _brightness.InitInBackground();
        QuickToggle.LogFolder = ProjectFolder();
        MemoryTrim.BusyCheck = () => _island?.IsBusy == true;
        MemoryTrim.Start(); // rapikan memori berkala saat diam

        // wallpaper (Fase 1): tidak melakukan apa pun selama fiturnya mati
        WallpaperService.Start(Dispatcher);
        // widget desktop (Fase 2): tidak membuat jendela apa pun selama fiturnya mati
        WidgetManager.Start(Dispatcher, _media, _weather);
        // live wallpaper (Fase 3): proses terpisah, hanya berjalan kalau dinyalakan dan ada file
        LiveWallpaperManager.Start(Dispatcher);
        // pemberitahuan update dari GitHub Releases
        UpdateService.UpdateAvailable += (version, url) =>
            _island?.ShowSystemNotice(L.T("Update tersedia: v") + version, L.T("Buka Pengaturan → Sistem untuk download"));
        UpdateService.Start(Dispatcher);

        if (recovered)
        {
            // beri tahu sebentar di notch bahwa app baru saja dipulihkan
            var notice = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            notice.Tick += (_, _) =>
            {
                notice.Stop();
                _island?.ShowSystemNotice(L.T("Dipulihkan setelah error"), L.T("Detail tersimpan di crash.log (Pengaturan → Sistem)"));
            };
            notice.Start();
        }

        _ = _radio.InitAsync();
        try { await _media.InitAsync(); }
        catch { /* API media tidak tersedia: island tetap jalan tanpa info lagu */ }
    }

    void ApplyVolumeKeySetting()
    {
        if (_volumeKeys == null) return;
        if (Settings.Current.ReplaceVolumeOsd && !_gameMode) _volumeKeys.Start();
        else _volumeKeys.Stop();

        // pop-up volume/kecerahan Windows (cara Bloom); saat mode game dikembalikan ke Windows
        if (_osd != null)
        {
            if (Settings.Current.HideNativeOsd && !_gameMode) _osd.Start();
            else _osd.Stop();
        }
    }

    void ApplyTaskbarSetting()
    {
        // taskbar asli hanya disembunyikan kalau dock benar-benar jalan
        if (_dock != null && Settings.Current.DockEnabled && Settings.Current.HideWindowsTaskbar)
        {
            if (!TaskbarService.IsHidden) TaskbarService.Hide();
            else TaskbarService.ApplySecondary(); // pilihan taskbar monitor kedua diubah di Pengaturan
        }
        else
        {
            TaskbarService.Restore(); // juga membersihkan sisa crash sebelumnya
        }
    }

    /// <summary>Folder proyek (exe ada di bin\Release\&lt;tfm&gt;\, 3 folder di bawahnya).</summary>
    /// <summary>
    /// Folder proyek kalau dijalankan dari hasil build (bin\Release\&lt;tfm&gt;\),
    /// atau folder exe itu sendiri kalau dijalankan dari paket siap pakai.
    /// </summary>
    static string ProjectFolder()
    {
        string bin = AppContext.BaseDirectory;
        return bin.Contains(System.IO.Path.DirectorySeparatorChar + "bin" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetFullPath(System.IO.Path.Combine(bin, "..", "..", ".."))
            : bin;
    }

    static void LogCrash(Exception? ex) => CrashGuard.Log(ex);

    static WelcomeWindow? _welcome;

    /// <summary>Layar sambutan: cara membuka Pengaturan, menu dock, menutup app, dan mengembalikan taskbar.</summary>
    public static void ShowWelcome()
    {
        try
        {
            if (_welcome == null)
            {
                var w = new WelcomeWindow();
                w.Closed += (_, _) =>
                {
                    _welcome = null;
                    if (!Settings.Current.WelcomeShown) { Settings.Current.WelcomeShown = true; Settings.Save(); }
                };
                _welcome = w;
                w.Show();
            }
            _welcome.Topmost = true;
            _welcome.Activate();
            _welcome.Topmost = false;
        }
        catch (Exception ex) { LogCrash(ex); }
    }

    /// <summary>
    /// "Hapus dari laptop ini": kembalikan taskbar & pop-up Windows, hapus dari startup,
    /// hapus file sisa, tutup app, lalu buka folder app supaya foldernya bisa dihapus.
    /// </summary>
    public static void UninstallFromThisPc()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue("DynamicIsland", throwOnMissingValue: false);
        }
        catch (Exception ex) { LogCrash(ex); }
        Settings.Current.RunAtStartup = false; // disimpan saat app ditutup
        try { TaskbarService.Restore(); } catch { }
        try { NativeOsdHider.RestoreNative(); } catch { }
        try
        {
            string flag = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Settings.FilePath)!, "taskbar-state.txt");
            if (System.IO.File.Exists(flag)) System.IO.File.Delete(flag);
        }
        catch { }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppContext.BaseDirectory.TrimEnd('\\')}\"") { UseShellExecute = true });
        }
        catch { }
        Current.Shutdown();
    }

    public static void OpenSettings()
    {
        try
        {
            // jendela lama yang "nyangkut" (tidak terlihat): buang dan buat baru
            if (_settingsWindow != null && !_settingsWindow.IsVisible)
            {
                try { _settingsWindow.Close(); } catch { }
                _settingsWindow = null;
            }
            if (_settingsWindow == null)
            {
                var w = new SettingsWindow();
                w.Closed += (_, _) => { if (_settingsWindow == w) _settingsWindow = null; };
                _settingsWindow = w;
                w.Show();
            }
            var sw = _settingsWindow;
            if (sw.WindowState == WindowState.Minimized) sw.WindowState = WindowState.Normal;
            // dibuka dari jendela tanpa fokus (island/dock): paksa ke depan
            sw.Topmost = true;
            sw.Activate();
            sw.Topmost = false;
            sw.Focus();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            _settingsWindow = null;
            MessageBox.Show("Pengaturan gagal dibuka:\n" + ex.Message, "Dynamic Island");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ShutdownMode == ShutdownMode.OnExplicitShutdown && _island == null) { base.OnExit(e); return; } // proses wallpaper / watchdog
        LiveWallpaperManager.Stop();
        Settings.Save();
        _edge?.Dispose();
        _volumeKeys?.Dispose();
        _osd?.Dispose();
        _downloads?.Dispose();
        _notifs?.Dispose();
        _clip?.Detach();
        _bezel?.Close();
        TaskbarService.Restore();
        _visualizer?.Dispose();
        _volume?.Dispose();
        _brightness?.Dispose();
        try { _singleInstance?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
