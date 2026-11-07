using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.UI;

/// <summary>Halaman tampilan besar. Pindah halaman dengan scroll.</summary>
public enum IslandMode { Media, Control, Calendar, NetList, Notifications, Performance, Shelf, Clipboard, Gemini }

public partial class IslandWindow : Window
{
    static readonly CultureInfo Indo = new("id-ID");
    static readonly Brush Subtle = MakeFrozen(Color.FromRgb(0xB4, 0xB2, 0xA9));
    static readonly Brush DimDot = MakeFrozen(Color.FromRgb(0x5F, 0x5E, 0x5A));
    static readonly Brush TileOff = MakeFrozen(Color.FromRgb(0x1F, 0x1F, 0x21));
    static readonly Brush AlarmRed = MakeFrozen(Color.FromRgb(0xFF, 0x5A, 0x4E));
    static readonly Brush TodayBg = Brushes.White;
    static readonly Brush RoundOff = MakeFrozen(Color.FromRgb(0x1F, 0x1F, 0x21));

    readonly MediaService _media;
    readonly AudioVisualizer _viz;
    readonly VolumeService _volume;
    readonly BrightnessService _brightness;
    readonly RadioService _radio;
    readonly TimerService _timer;

    // animasi
    readonly Spring _spring = new();                                  // 0 = kecil, 1 = besar
    readonly Spring _activity = new();                                // 0 = diam, 1 = musik diputar
    readonly Spring _presence = new() { Value = 1, Target = 1 };      // 0 = tersembunyi di atas layar
    readonly Spring _pageW = new(), _pageH = new();                   // ukuran halaman aktif
    readonly Spring _wMedia = new(), _wControl = new(), _wCalendar = new(), _wNet = new(), _wNotif = new(), _wPerf = new(), _wShelf = new(), _wClip = new(), _wGem = new(); // bobot tiap halaman (crossfade)
    readonly NetworkListService _netList = new();
    bool _netIsWifi;
    WifiItem? _pendingWifi;
    readonly TranslateTransform _presenceTransform = new();
    readonly Func<double, bool> _animTick, _vizTick;

    readonly DispatcherTimer _hoverTimer, _collapseTimer, _peekTimer, _clockTimer, _liveTimer, _screenTimer, _edgeRevealTimer;
    const double NotifPeekWidth = 380;
    double _alertW = 300, _alertH = 40; // ukuran notifikasi di notch (charger/download 300x40, notifikasi 380x60)
    bool _alertNotif;
    bool _alertHud;
    double _boostAlpha;          // volume / kecerahan di notch
    double _hudOutStart, _hudValue, _hudShown;
    // animasi charger: alur waktu dengan easing (lebih halus dari pegas)
    readonly System.Diagnostics.Stopwatch _alertClock = new();
    bool _alertActive, _alertPlug, _alertDownload;
    // progress download di tepi notch
    DownloadMonitor? _downloads;
    double _dlShown, _dlOpacity, _dlPhase;
    bool _dlIndeterminate;
    // warna yang bisa diatur di Pengaturan → Warna (diperbarui di ApplyColors)
    static Brush DownloadBlue = MakeFrozen(Color.FromRgb(0x0A, 0x84, 0xFF));
    static Brush TimerOrange = MakeFrozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    static Brush OnBrush = Brushes.White, OnFore = Brushes.Black;
    static Color OnColor = Colors.White;
    Brush _progressBrush = DownloadBlue; // garis progress: biru = download, oranye = timer
    double _alertA;          // 0..1 lebar notifikasi
    double _rimProgress;     // 0..1 seberapa jauh garis hijau menelusuri notch (dari tengah bawah ke kedua sisi)
    double _rimOpacity;
    double _lineProgress = 1; // garis hijau kecil di bawah notch (tumbuh setelah dicolok)

    bool? _lastCharging;
    int _alertPercent;
    Brush _alertFillBrush = Brushes.White;

    readonly List<Rectangle> _miniBars = new(), _fullBars = new();
    float[] _levels = Array.Empty<float>();
    float[] _shown = Array.Empty<float>();

    double _cx;
    IslandMode _mode = IslandMode.Control;
    double _hoverX = 0.5; // posisi pointer di notch kecil (0 = kiri, 1 = kanan)
    bool _expanded, _peeking, _vizRunning, _tucked, _edgeRevealed;
    bool _lastFullscreen, _lastMaximized;
    double _vizAccum, _vizTime;
    int _pageWheel, _sliderWheel, _dotCount = -1, _calendarMonth = -1;
    long _lastPageSwitch;
    int _lastBatteryPercent = -1;
    Brush _accent = Brushes.White;
    readonly SolidColorBrush _coverBrush = new(Colors.White); // warna bar musik (mengikuti cover)
    ImageSource? _coverArtSeen;
    Color? _coverTarget;
    readonly RadialGradientBrush _innerGlow = new() { MappingMode = BrushMappingMode.Absolute };
    readonly GradientStop _glowStop0 = new(Colors.Transparent, 0), _glowStop1 = new(Colors.Transparent, 1);
    bool _ambientOn;

    /// <summary>Mode game berubah (true = sedang fullscreen, semua fitur dihentikan).</summary>
    public event Action<bool>? GameModeChanged;
    bool _suspended;

    /// <summary>Kondisi layar berubah: (fullscreen, ada jendela maximize).</summary>
    public event Action<bool, bool>? ScreenStateChanged;

    /// <summary>Island sedang terbuka atau sedang menampilkan notifikasi (jangan ganggu dengan perapian memori).</summary>
    public bool IsBusy => _expanded || _alertActive || _spring.Value > 0.01;

    /// <summary>True saat notch sedang disembunyikan.</summary>
    public bool IsTucked => _tucked && !_edgeRevealed;

    public IslandWindow(MediaService media, AudioVisualizer viz, VolumeService volume,
                        BrightnessService brightness, RadioService radio, TimerService timer)
    {
        InitializeComponent();
        // isi selalu menempel ke kiri-atas jendela (bukan di tengah): saat ukuran jendela berubah,
        // tidak ada satu frame pun di mana island tergambar bergeser
        Root.HorizontalAlignment = HorizontalAlignment.Left;
        Root.VerticalAlignment = VerticalAlignment.Top;
        _media = media;
        _viz = viz;
        _volume = volume;
        _brightness = brightness;
        _radio = radio;
        _timer = timer;
        _animTick = AnimTick;
        _vizTick = VizTick;

        _hoverTimer = OneShot(() => Expand(ModeFromHover()));
        _collapseTimer = OneShot(() =>
        {
            if (_peeking) return;
            if (PasswordPanel.Visibility == Visibility.Visible) return; // sedang mengetik password
            if (_dragOver || GeminiInput.IsKeyboardFocused || _geminiBusy) return; // sedang menarik file / mengetik ke Gemini
            Collapse();
        });
        _peekTimer = OneShot(() => { _peeking = false; if (!Root.IsMouseOver) Collapse(); });
        _edgeRevealTimer = OneShot(() =>
        {
            if (Root.IsMouseOver || _timer.IsRinging) return;
            _edgeRevealed = false;
            ApplyPresence();
        });


        _clockTimer = new DispatcherTimer(DispatcherPriority.Background);
        _clockTimer.Tick += (_, _) => UpdateClock();

        // hanya berjalan saat island terbuka
        _liveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _liveTimer.Tick += (_, _) => UpdateLive();

        // cek ringan (beberapa panggilan Windows API) apakah ada jendela maximize / fullscreen
        _screenTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _screenTimer.Tick += (_, _) => CheckScreenState();
        _screenTimer.Start();

        Root.RenderTransform = _presenceTransform;
        // efek blur cahaya ambient digambar sekali lalu disimpan sebagai gambar;
        // hanya digambar ulang saat bentuk island berubah, bukan tiap frame bar musik
        Glow.CacheMode = new BitmapCache { RenderAtScale = 1 };
        _innerGlow.GradientStops.Add(_glowStop0);
        _innerGlow.GradientStops.Add(_glowStop1);
        InnerGlow.Fill = _innerGlow;

        SourceInitialized += (_, _) =>
        {
            Native.MakeToolWindow(new WindowInteropHelper(this).Handle);
            App.DpiScale = VisualTreeHelper.GetDpi(this).DpiScaleY;
            InitHotkeys(new WindowInteropHelper(this).Handle);
        };

        HookIsland();
        HookMedia();
        HookControl();
        HookCalendar();

        _media.Changed += UpdateMedia;
        _media.TrackChanged += OnTrackChanged;
        _timer.Changed += UpdateTimer;
        _timer.Changed += () => FrameLoop.Add(_animTick); // garis timer di tepi notch
        _timer.Finished += OnTimerFinished;
        Settings.Changed += ApplySettings;
        WallpaperService.Changed += () => Dispatcher.BeginInvoke(new Action(UpdateAmbient)); // ambient ikut wallpaper (opsional)
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateMiniInfo));

        ApplySettings();
        UpdateMedia();
        UpdateTimer();
    }

    // =====================================================================
    // Pengaturan & tata letak
    // =====================================================================

    void ApplySettings()
    {
        var s = Settings.Current;

        Mini.Height = s.CollapsedHeight;
        Canvas.SetTop(Mini, 0);
        Shape.Fill = ParseBrush(s.IslandColor, s.IslandOpacity);
        // lapisan tambahan: total kepekatan saat terbuka = OpacityExpanded (rumus menumpuk dua lapisan tembus)
        double a = Math.Clamp(s.IslandOpacity, 0, 1), target = s.ExpandedSolid ? Math.Max(a, 0.94) : a;
        _boostAlpha = a >= 0.999 || target <= a ? 0 : (target - a) / (1 - a);
        ShapeBoost.Fill = ParseBrush(s.IslandColor, _boostAlpha);
        Top = 0;
        // ukuran jendela mengikuti isi yang tampil (lihat SettleWindowSize)
        _needW = _needH = 0;
        SettleWindowSize(force: true);
        FontFamily = UiFont();
        if (_expanded) SetMode(_mode, animate: false);
        ApplyColors();
        ApplyHotkeys();
        ApplyDeviceSettings();

        _accent = ParseBrush(s.AccentColor);
        SourceText.Foreground = _coverBrush;
        ArtPlaceholder.Background = _accent;
        TrackFill.Background = _coverBrush;
        Edge.Visibility = s.EdgeHighlight ? Visibility.Visible : Visibility.Collapsed;
        // bahasa: terjemahkan teks statis lalu susun ulang teks yang dinamis
        L.Apply(this);
        Root.ContextMenu = BuildMenu();
        BuildStats();
        UpdateDiscordWatch();
        _calendarMonth = -1;
        _coverArtSeen = null;
        _coverTarget = null;
        ApplyCoverColor();

        BuildBars(s.VisualizerBars);
        _viz.SetBarCount(s.VisualizerBars);

        Redraw();
        UpdateClock();
        UpdateVisualizerState();
        UpdateControl();
        UpdateAmbient();
    }

    // ---------- ukuran jendela dinamis ----------
    // Jendela transparan digambar ulang seluruhnya oleh CPU tiap frame animasi, jadi jendela dibuat
    // sekecil isi yang sedang tampil: kecil saat notch kecil, membesar tepat sebelum animasi membuka.

    double _needW, _needH;

    const double GlowPad = 30; // ruang untuk cahaya ambient di sekitar island

    (double w, double h) CollapsedNeed()
    {
        var s = Settings.Current;
        double w = new[] { s.CollapsedWidth, s.CollapsedActiveWidth, _miniNeedIdle, _miniNeedActive }.Max();
        double h = Math.Max(s.CollapsedHeight, 40);
        if (_alertActive) { w = Math.Max(w, _alertW); h = Math.Max(h, _alertH); }
        return (w, h);
    }

    /// <summary>Perbesar jendela (tidak pernah mengecil) supaya isi berukuran w x h muat.</summary>
    void GrowWindow(double w, double h)
    {
        if (h <= _needH && w <= _needW) return;
        ApplyWindowSize(Math.Max(w, _needW), Math.Max(h, _needH));
    }

    // Mengubah ukuran jendela transparan bisa membuat satu frame tampil bergeser (notch terlihat "lepas" dari
    // tepi atas sepersekian detik). Karena itu: jendela dibesarkan SEBELUM island membuka (saat pointer masuk),
    // dan baru dikecilkan lagi beberapa detik setelah island tertutup (bukan langsung).
    DispatcherTimer? _shrinkTimer;

    void SettleSoon()
    {
        if (_expanded) return; // terbuka: jendela tetap seukuran halaman terbesar (ganti halaman tanpa ubah ukuran)
        _shrinkTimer ??= OneShot(() =>
        {
            if (_expanded || Root.IsMouseOver || FrameLoop.IsActive) { Start(_shrinkTimer!, 2000); return; }
            SettleWindowSize();
        });
        Start(_shrinkTimer, 4000);
    }

    /// <summary>Pointer masuk ke notch: siapkan ukuran jendela untuk halaman terbesar sekarang juga.</summary>
    void PrepareWindowForExpand()
    {
        _shrinkTimer?.Stop();
        double w = 0, h = 0;
        foreach (var m in Pages())
        {
            var d = Dims(m);
            w = Math.Max(w, d.w);
            h = Math.Max(h, d.h);
        }
        GrowWindow(w, h);
    }

    /// <summary>Setelah animasi selesai: kecilkan jendela ke ukuran isi yang sedang tampil.</summary>
    void SettleWindowSize(bool force = false)
    {
        var (w, h) = CollapsedNeed();
        if (_expanded) { var d = Dims(_mode); w = Math.Max(w, d.w); h = Math.Max(h, d.h); }
        if (!force && Math.Abs(h - _needH) < 1 && w <= _needW + 1) return;
        ApplyWindowSize(w, h);
    }

    void ApplyWindowSize(double w, double h)
    {
        var s = Settings.Current;
        _needW = w;
        _needH = h;
        // lebar jendela TETAP (selebar halaman terlebar) supaya posisi notch tidak pernah bergeser saat
        // jendela berubah ukuran; yang menyesuaikan hanya tingginya (dari bawah), jadi tidak ada "loncatan".
        double maxW = new[] { s.MediaWidth, s.ControlWidth, s.CalendarWidth, s.NetListWidth, s.NotifWidth, s.PerfWidth,
                              s.ShelfWidth, s.ClipWidth, s.GeminiWidth, NotifPeekWidth, w }.Max();
        double winW = Math.Ceiling(maxW * 1.12 + 2 * s.ShoulderRadius + 24 + 2 * GlowPad);
        double winH = Math.Ceiling(h * 1.12 + 12 + GlowPad);
        bool widthChanged = Math.Abs(winW - Width) > 0.5 || double.IsNaN(Width);
        if (widthChanged)
        {
            void Widen()
            {
                Root.Width = Layer.Width = winW;
                Width = winW;
                Left = (SystemParameters.PrimaryScreenWidth - winW) / 2;
                _cx = winW / 2;
                InnerGlow.Width = winW;
                PlaceAllViews();
            }
            // jendela digeser ke kiri + dilebarkan: sembunyikan sebentar supaya gambar lama tidak sempat
            // tampil bergeser (bayangan island sepersekian detik)
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) WindowCloak.Run(hwnd, Dispatcher, Widen);
            else Widen();
        }
        // isi diubah DULU, baru jendelanya: kalau jendela lebih dulu membesar, WPF sempat menggambar satu frame
        // dengan isi lama di tengah jendela baru (island "bayangan" yang lebih rendah sepersekian detik)
        Root.Height = Layer.Height = winH;
        InnerGlow.Height = winH;
        Height = winH;
        Redraw();
    }

    void PlaceAllViews()
    {
        var s = Settings.Current;
        PlaceView(MediaView, s.MediaWidth, s.MediaHeight);
        PlaceView(ControlView, s.ControlWidth, s.ControlHeight);
        PlaceView(CalendarView, s.CalendarWidth, s.CalendarHeight + CalendarExtra);
        PlaceView(NetListView, s.NetListWidth, s.NetListHeight);
        PlaceView(NotifView, s.NotifWidth, s.NotifHeight);
        PlaceView(PerfView, s.PerfWidth, s.PerfHeight);
        PlaceView(ShelfView, s.ShelfWidth, s.ShelfHeight);
        PlaceView(ClipView, s.ClipWidth, s.ClipHeight);
        PlaceView(GeminiView, s.GeminiWidth, s.GeminiHeight);
    }

    void PlaceView(FrameworkElement view, double w, double h)
    {
        view.Width = w;
        view.Height = h;
        Canvas.SetLeft(view, _cx - w / 2);
        Canvas.SetTop(view, 0);
    }

    void BuildBars(int count)
    {
        MiniBars.Children.Clear();
        FullBars.Children.Clear();
        _miniBars.Clear();
        _fullBars.Clear();
        _levels = new float[count];
        _shown = new float[count];

        for (int i = 0; i < count; i++)
        {
            var mini = MakeBar(2.5, 14);
            _miniBars.Add(mini);
            MiniBars.Children.Add(mini);

            var full = MakeBar(3, 20);
            _fullBars.Add(full);
            FullBars.Children.Add(full);
        }
    }

    Rectangle MakeBar(double width, double height) => new()
    {
        Width = width,
        Height = height,
        RadiusX = width / 2,
        RadiusY = width / 2,
        Fill = _coverBrush,
        Margin = new Thickness(width * 0.4, 0, width * 0.4, 0),
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new ScaleTransform(1, 0.15),
    };

    // =====================================================================
    // Hover, klik, buka/tutup
    // =====================================================================

    // cahaya pointer (warna, bentuk, ukuran diatur di Pengaturan → Tampilan → Cahaya pointer)
    PointerLight? _pointer;

    PointerLight Pointer
    {
        get
        {
            if (_pointer != null) return _pointer;
            _pointer = new PointerLight(() => Settings.Current.HoverSpotlight);
            // ditaruh di tempat cahaya lama (di dalam lapisan yang dipotong mengikuti bentuk island)
            int at = Layer.Children.IndexOf(Spotlight);
            Layer.Children.Insert(at < 0 ? 0 : at, _pointer.Visual);
            Spotlight.Visibility = Visibility.Collapsed;
            return _pointer;
        }
    }

    void MoveSpotlight(Point p) => Pointer.Move(p);

    void FadeSpotlight(bool show) => Pointer.Fade(show);

    void HookIsland()
    {
        Root.MouseEnter += (_, _) =>
        {
            if (!_expanded && Settings.Current.PrewarmWindow) PrepareWindowForExpand();
            _collapseTimer.Stop();
            if (!_expanded) Start(_hoverTimer, Settings.Current.HoverExpandDelayMs);
        };
        Root.MouseLeave += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_expanded) Start(_collapseTimer, Settings.Current.CollapseDelayMs);
            if (_edgeRevealed) Start(_edgeRevealTimer, Settings.Current.CollapseDelayMs + 600);
        };
        // cahaya yang mengikuti pointer
        Root.MouseEnter += (_, e) => { MoveSpotlight(e.GetPosition(Layer)); FadeSpotlight(true); };
        Root.MouseLeave += (_, _) => FadeSpotlight(false);
        Root.MouseMove += (_, e) => MoveSpotlight(e.GetPosition(Layer));
        Root.MouseMove += (_, e) =>
        {
            if (_expanded || Mini.ActualWidth <= 0) return;
            _hoverX = Math.Clamp(e.GetPosition(Mini).X / Mini.ActualWidth, 0, 1);
        };
        Root.MouseLeftButtonUp += (_, e) =>
        {
            if (_timer.IsRinging) { _timer.Dismiss(); e.Handled = true; return; }
            if (_expanded) return;
            _hoverTimer.Stop();
            Expand(ModeFromHover());
            e.Handled = true;
        };
        Root.MouseWheel += OnPageWheel;
        Root.ContextMenu = BuildMenu();
    }

    IslandMode ModeFromHover()
    {
        var pages = Pages();
        var wanted = ModeFromHoverRaw();
        return Array.IndexOf(pages, wanted) >= 0 ? wanted : pages[0]; // halaman dimatikan: buka halaman pertama
    }

    IslandMode ModeFromHoverRaw()
    {
        if (!HasMedia) return IslandMode.Control;
        // tengah (jam) = control center; kiri (bar musik) dan kanan (cover) = player musik
        return _hoverX < 0.3 || _hoverX > 0.7 ? IslandMode.Media : IslandMode.Control;
    }

    // saat lagu berganti, Windows sebentar melaporkan judul kosong: anggap musik masih ada selama 2,5 detik
    // supaya player musik tidak hilang-muncul (dan gestur ganti lagu tetap jalan)
    long _mediaSeenAt;
    const int MediaGraceMs = 2500;
    DispatcherTimer? _mediaGraceTimer;

    bool HasMedia
    {
        get
        {
            // Chrome bahkan sempat menutup sesi medianya sebentar saat ganti lagu, jadi masa tenggang berlaku untuk keduanya
            long now = Environment.TickCount64;
            if (_media.HasSession && _media.Title.Length > 0) { _mediaSeenAt = now; return true; }
            return _mediaSeenAt != 0 && now - _mediaSeenAt < MediaGraceMs;
        }
    }

    void Expand(IslandMode mode)
    {
        if (IsTucked) return;
        var pages = Pages();
        if (Array.IndexOf(pages, mode) < 0 && mode != IslandMode.NetList) mode = pages[0];
        var d = Dims(mode);
        if (Settings.Current.PrewarmWindow) PrepareWindowForExpand(); // seukuran halaman terbesar: ganti halaman tanpa ubah ukuran jendela
        GrowWindow(d.w, d.h);
        if (!_expanded) SetMode(mode, animate: false);
        _expanded = true;
        _spring.Target = 1;
        FrameLoop.Add(_animTick);
        UpdateLive();
        _liveTimer.Start();
    }

    void Collapse()
    {
        EndGeminiTyping();
        _expanded = false;
        MemoryTrim.Soon(); // memori yang dipakai saat terbuka cepat dikembalikan
        _spring.Target = 0;
        FrameLoop.Add(_animTick);
        _liveTimer.Stop();
    }

    bool AnimTick(double dt)
    {
        var s = Settings.Current;
        double k = s.AnimStiffness, c = s.AnimDamping;
        bool moving = false;
        moving |= _spring.Step(dt, k, c);
        moving |= _activity.Step(dt, k, c);
        var (rk, rc) = s.RevealSpring;
        moving |= _presence.Step(dt, rk, rc);
        if (_alertActive) moving |= StepChargeAlert();
        moving |= StepDownload(dt);
        moving |= _pageW.Step(dt, k, c);
        moving |= _pageH.Step(dt, k, c);
        // bobot halaman tanpa pantulan (kritis teredam) supaya crossfade rata
        double kw = k * 1.3, cw = 2 * Math.Sqrt(kw);
        moving |= _wMedia.Step(dt, kw, cw);
        moving |= _wControl.Step(dt, kw, cw);
        moving |= _wCalendar.Step(dt, kw, cw);
        moving |= _wNet.Step(dt, kw, cw);
        moving |= _wNotif.Step(dt, kw, cw);
        moving |= _wPerf.Step(dt, kw, cw);
        moving |= _wShelf.Step(dt, kw, cw);
        moving |= _wClip.Step(dt, kw, cw);
        moving |= _wGem.Step(dt, kw, cw);
        Redraw();
        if (!moving)
        {
            // animasi selesai: kecilkan jendela ke isi yang tampil (langsung, atau ditunda kalau opsi "siapkan ukuran" aktif)
            if (Settings.Current.PrewarmWindow) SettleSoon(); else SettleWindowSize();
        }
        return moving;
    }

    void Redraw()
    {
        var s = Settings.Current;
        double p = _spring.Value;
        double ps = Math.Clamp(p, -0.03, 1.06); // ukuran boleh "lewat" sedikit untuk efek pantul halus

        // notch kecil melebar sendiri saat musik diputar atau timer aktif (kebiasaan dynamic island)
        double act = MathEx.Clamp01(_activity.Value);
        // lebar minimum mengikuti isi (jam + baterai + ikon), supaya tidak pernah berdesakan
        double cw = MathEx.Lerp(Math.Max(s.CollapsedWidth, _miniNeedIdle), Math.Max(s.CollapsedActiveWidth, _miniNeedActive), _activity.Value);
        // notifikasi charger: notch kecil melebar & sedikit lebih tinggi sebentar
        double a = _alertA;
        double bw = MathEx.Lerp(cw, Math.Max(cw, _alertW), a);
        double bh = MathEx.Lerp(s.CollapsedHeight, _alertH, a);
        double w = MathEx.Lerp(bw, _pageW.Value, ps);
        double h = MathEx.Lerp(bh, _pageH.Value, ps);
        if (s.IslandJelly)
        {
            // kenyal: saat bergerak cepat notch sedikit melebar & memendek (atau sebaliknya), lalu kembali
            double st = Math.Clamp(_spring.Velocity * 0.008, -0.035, 0.035);
            w *= 1 + st;
            h *= 1 - st * 0.6;
        }
        double r = MathEx.Lerp(s.CollapsedBottomRadius, s.ExpandedBottomRadius, MathEx.Clamp01(p));

        var geometry = NotchGeometry.Build(_cx, w, h, s.ShoulderRadius, r);
        Shape.Data = geometry;
        if (_boostAlpha > 0)
        {
            ShapeBoost.Data = geometry;
            ShapeBoost.Opacity = MathEx.Clamp01(p);
        }
        else if (ShapeBoost.Opacity != 0) ShapeBoost.Opacity = 0;
        if (s.EdgeHighlight) Edge.Data = NotchGeometry.Build(_cx, w, h, s.ShoulderRadius, r, closed: false);
        Layer.Clip = geometry;
        UpdateGlowShape(geometry);

        Mini.Width = cw;
        Canvas.SetLeft(Mini, _cx - cw / 2);
        if (ChargeTrack.Visibility == Visibility.Visible && _lastBatteryPercent >= 0)
            ChargeFill.Width = Math.Max(0, cw - 36) * _lastBatteryPercent / 100.0 * _lineProgress;
        bool music = HasMedia; // tetap tampil walau di-pause, selama sumbernya masih ada
        MiniArt.Opacity = music ? act : 0;
        // Discord dalam panggilan: ikon Discord di kiri, musik tetap di kanan
        MiniDiscord.Opacity = _discordShown ? act : 0;
        MiniBars.Opacity = music && !_discordShown ? act : 0;
        MiniInfo.Opacity = music ? 1 - act : 1;
        MiniWifi.Opacity = (music || _discordShown ? 1 - act : 1) * (_radio.NetworkName.Length > 0 ? 1 : 0.35);

        // meluncur ke atas layar saat disembunyikan
        _presenceTransform.Y = -(h + s.ShoulderRadius + 6) * (1 - MathEx.Clamp01(_presence.Value));

        double ae = _alertA;
        double miniOpacity = Smooth(MathEx.Clamp01(1 - p * 2.2)) * Smooth(MathEx.Clamp01(1 - ae * 1.6));
        DrawChargeAlert(ae, p, bh);
        DrawChargeRim(w, h, r, p);
        Mini.Opacity = miniOpacity;
        Mini.Visibility = miniOpacity <= 0 ? Visibility.Hidden : Visibility.Visible;

        double fullOpacity = Smooth(MathEx.Clamp01((p - 0.25) / 0.75));
        double scale = 0.95 + 0.05 * Smooth(MathEx.Clamp01(p));
        ApplyView(MediaView, MediaScale, _mode == IslandMode.Media, fullOpacity * Smooth(MathEx.Clamp01(_wMedia.Value)), scale, p);
        ApplyView(ControlView, ControlScale, _mode == IslandMode.Control, fullOpacity * Smooth(MathEx.Clamp01(_wControl.Value)), scale, p);
        ApplyView(CalendarView, CalendarScale, _mode == IslandMode.Calendar, fullOpacity * Smooth(MathEx.Clamp01(_wCalendar.Value)), scale, p);
        ApplyView(NetListView, NetListScale, _mode == IslandMode.NetList, fullOpacity * Smooth(MathEx.Clamp01(_wNet.Value)), scale, p);
        ApplyView(NotifView, NotifScale, _mode == IslandMode.Notifications, fullOpacity * Smooth(MathEx.Clamp01(_wNotif.Value)), scale, p);
        ApplyView(PerfView, PerfScale, _mode == IslandMode.Performance, fullOpacity * Smooth(MathEx.Clamp01(_wPerf.Value)), scale, p);
        ApplyView(ShelfView, ShelfScale, _mode == IslandMode.Shelf, fullOpacity * Smooth(MathEx.Clamp01(_wShelf.Value)), scale, p);
        ApplyView(ClipView, ClipScale, _mode == IslandMode.Clipboard, fullOpacity * Smooth(MathEx.Clamp01(_wClip.Value)), scale, p);
        ApplyView(GeminiView, GeminiScale, _mode == IslandMode.Gemini, fullOpacity * Smooth(MathEx.Clamp01(_wGem.Value)), scale, p);
        PageDots.Visibility = _mode == IslandMode.NetList ? Visibility.Hidden : Visibility.Visible;
        if (s.IslandContentBlur || _blurUsed) ApplyContentBlur(s.IslandContentBlur, miniOpacity);

        UpdateInnerGlow(cw, w, h, p);

        // titik penanda halaman di bawah
        PageDots.Opacity = fullOpacity;
        if (PageDots.ActualWidth > 0) Canvas.SetLeft(PageDots, _cx - PageDots.ActualWidth / 2);
        Canvas.SetTop(PageDots, h - 10);
    }

    /// <summary>Posisi & kekuatan semburat warna di dalam island, mengikuti posisi cover lagu.</summary>
    void UpdateInnerGlow(double cw, double w, double h, double p)
    {
        if (!_ambientOn) { InnerGlow.Opacity = 0; return; }
        var s = Settings.Current;
        double e = MathEx.Clamp01(p);

        // notch kecil: cover di kanan
        Point mini = new(_cx + cw / 2 - 23, s.CollapsedHeight / 2);
        // tampilan besar: player musik → cover di kiri atas; halaman lain → tengah atas
        Point big = _mode == IslandMode.Media
            ? new Point(_cx - _pageW.Value / 2 + 18 + 26, 12 + 26)
            : new Point(_cx, 0);
        var c = new Point(MathEx.Lerp(mini.X, big.X, e), MathEx.Lerp(mini.Y, big.Y, e));
        _innerGlow.Center = c;
        _innerGlow.GradientOrigin = c;
        _innerGlow.RadiusX = MathEx.Lerp(70, _mode == IslandMode.Media ? w * 0.75 : w * 0.6, e);
        _innerGlow.RadiusY = MathEx.Lerp(40, _mode == IslandMode.Media ? h * 1.1 : h * 0.7, e);

        double strength = MathEx.Lerp(0.55, _mode == IslandMode.Media ? 0.65 : 0.3, e);
        InnerGlow.Opacity = strength * s.AmbientIntensity * (_media.IsPlaying ? 1 : 0.7);
    }

    // ---------- isi muncul dari buram ke tajam (opsi "IslandContentBlur") ----------
    readonly Dictionary<FrameworkElement, System.Windows.Media.Effects.BlurEffect> _blurs = new();
    bool _blurUsed;

    void ApplyContentBlur(bool on, double miniOpacity)
    {
        _blurUsed = on;
        Blur(Mini, on ? (1 - miniOpacity) * 6 : 0);
        foreach (var v in new FrameworkElement[] { MediaView, ControlView, CalendarView, NetListView, NotifView, PerfView, ShelfView, ClipView, GeminiView })
            Blur(v, on && v.Visibility == Visibility.Visible ? (1 - v.Opacity) * 12 : 0);
    }

    /// <summary>Efek blur hanya terpasang selama transisi (radius > 0.4) — saat diam tanpa efek, jadi tidak ada biaya GPU.</summary>
    void Blur(FrameworkElement el, double radius)
    {
        if (radius < 0.4)
        {
            if (el.Effect != null) el.Effect = null;
            return;
        }
        if (!_blurs.TryGetValue(el, out var b))
        {
            b = new System.Windows.Media.Effects.BlurEffect { RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
            _blurs[el] = b;
        }
        b.Radius = radius;
        if (el.Effect != b) el.Effect = b;
    }

    /// <summary>Kurva smoothstep: awal dan akhir pelan, tengah cepat.</summary>
    static double Smooth(double t) => t * t * (3 - 2 * t);

    static void ApplyView(FrameworkElement view, ScaleTransform st, bool active, double opacity, double scale, double p)
    {
        view.Opacity = opacity;
        // Hidden (bukan Collapsed): tata letak halaman tetap tersimpan, jadi saat island membuka
        // tidak perlu menghitung ulang tata letak di tengah animasi (penyebab patah-patah)
        var vis = opacity <= 0.001 ? Visibility.Hidden : Visibility.Visible;
        if (view.Visibility != vis) view.Visibility = vis;
        view.IsHitTestVisible = active && p > 0.6;
        st.ScaleX = scale;
        st.ScaleY = scale;
    }

    // =====================================================================
    // Halaman: player musik ↔ control center ↔ kalender + timer (scroll)
    // =====================================================================

    IslandMode[] Pages()
    {
        var s = Settings.Current;
        var list = new List<IslandMode>();
        if (HasMedia && s.PageMusic) list.Add(IslandMode.Media);
        if (s.PageControl) list.Add(IslandMode.Control);
        if (s.PageCalendar) list.Add(IslandMode.Calendar);
        if (_notifs != null && s.NotificationsEnabled) list.Add(IslandMode.Notifications);
        if (s.PageShelf) list.Add(IslandMode.Shelf);
        if (s.PageClipboard && _clip != null) list.Add(IslandMode.Clipboard);
        if (s.PageGemini) list.Add(IslandMode.Gemini);
        if (s.PagePerformance) list.Add(IslandMode.Performance);
        if (list.Count == 0) list.Add(IslandMode.Control); // minimal satu halaman
        return list.ToArray();
    }

    (double w, double h) Dims(IslandMode mode)
    {
        var s = Settings.Current;
        return mode switch
        {
            IslandMode.Media => (s.MediaWidth, s.MediaHeight),
            IslandMode.NetList => (s.NetListWidth, s.NetListHeight),
            IslandMode.Notifications => (s.NotifWidth, s.NotifHeight),
            IslandMode.Performance => (s.PerfWidth, s.PerfHeight),
            // halaman kosong tidak perlu setinggi saat penuh
            IslandMode.Shelf => (s.ShelfWidth, s.ShelfItems.Count == 0 ? Math.Min(s.ShelfHeight, 150) : s.ShelfHeight),
            IslandMode.Clipboard => (s.ClipWidth, _clip == null || _clip.Items.Count == 0 ? Math.Min(s.ClipHeight, 150) : s.ClipHeight),
            IslandMode.Gemini => (s.GeminiWidth, s.GeminiHeight),
            IslandMode.Calendar => (s.CalendarWidth, s.CalendarHeight + CalendarExtra),
            _ => (s.ControlWidth, s.ControlHeight),
        };
    }

    void SetMode(IslandMode mode, bool animate)
    {
        _mode = mode;
        var (w, h) = Dims(mode);
        GrowWindow(w, h);
        _pageW.Target = w;
        _pageH.Target = h;
        _wMedia.Target = mode == IslandMode.Media ? 1 : 0;
        _wControl.Target = mode == IslandMode.Control ? 1 : 0;
        _wCalendar.Target = mode == IslandMode.Calendar ? 1 : 0;
        _wNet.Target = mode == IslandMode.NetList ? 1 : 0;
        _wNotif.Target = mode == IslandMode.Notifications ? 1 : 0;
        _wPerf.Target = mode == IslandMode.Performance ? 1 : 0;
        _wShelf.Target = mode == IslandMode.Shelf ? 1 : 0;
        _wClip.Target = mode == IslandMode.Clipboard ? 1 : 0;
        _wGem.Target = mode == IslandMode.Gemini ? 1 : 0;
        if (mode != IslandMode.Gemini) EndGeminiTyping();
        if (mode == IslandMode.Shelf) RebuildShelf();
        if (mode == IslandMode.Clipboard) RebuildClip();
        if (mode == IslandMode.Gemini) UpdateGeminiState();
        if (mode != IslandMode.NetList) ClosePasswordPanel();
        if (!animate)
        {
            foreach (var sp in new[] { _pageW, _pageH, _wMedia, _wControl, _wCalendar, _wNet, _wNotif, _wPerf, _wShelf, _wClip, _wGem })
            {
                sp.Value = sp.Target;
                sp.Velocity = 0;
            }
        }
        UpdateDots();
        UpdateLive();
        if (mode == IslandMode.Control) UpdateControl();
        if (mode == IslandMode.Calendar) BuildCalendar();
        FrameLoop.Add(_animTick);
    }

    void OnPageWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_expanded || _mode == IslandMode.NetList) return;
        e.Handled = true;
        // baru saja geser ke samping (ganti lagu): gerakan naik-turun kecil dari touchpad jangan dianggap ganti halaman
        if (Environment.TickCount64 - _hWheelAt < 700) { _pageWheel = 0; return; }
        // arah berubah: mulai hitung ulang
        if (_pageWheel != 0 && Math.Sign(_pageWheel) != Math.Sign(e.Delta)) _pageWheel = 0;
        _pageWheel += e.Delta;
        if (Math.Abs(_pageWheel) < 90) return; // cukup satu "klik" mouse atau gesekan pendek touchpad
        int dir = _pageWheel < 0 ? 1 : -1; // scroll ke bawah = halaman berikutnya
        _pageWheel = 0;
        if (Environment.TickCount64 - _lastPageSwitch < 220) return; // cegah lompat 2 halaman sekaligus
        var pages = Pages();
        int idx = Array.IndexOf(pages, _mode);
        // berputar: dari halaman terakhir lanjut ke halaman pertama (tidak mentok)
        int next = ((idx < 0 ? 0 : idx) + dir + pages.Length) % pages.Length;
        if (next == idx) return;
        _lastPageSwitch = Environment.TickCount64;
        _peeking = false;
        SetMode(pages[next], animate: true);
    }

    void UpdateDots()
    {
        var pages = Pages();
        if (_dotCount != pages.Length)
        {
            PageDots.Children.Clear();
            for (int i = 0; i < pages.Length; i++)
                PageDots.Children.Add(new Ellipse { Width = 5, Height = 5, Margin = new Thickness(2.5, 0, 2.5, 0) });
            _dotCount = pages.Length;
        }
        int active = Array.IndexOf(pages, _mode);
        for (int i = 0; i < PageDots.Children.Count; i++)
            ((Ellipse)PageDots.Children[i]).Fill = i == active ? Brushes.White : DimDot;
    }

    int WheelSteps(int delta)
    {
        _sliderWheel += delta;
        int steps = _sliderWheel / 120;
        _sliderWheel -= steps * 120;
        return steps;
    }

    // =====================================================================
    // Halaman 1: player musik
    // =====================================================================

    void HookMedia()
    {
        PrevButton.Click += (_, _) => _media.Previous();
        PlayButton.Click += (_, _) => _media.PlayPause();
        NextButton.Click += (_, _) => _media.Next();
        SeekArea.MouseLeftButtonDown += OnSeek;
        OutputButton.Click += (_, _) => RadioService.OpenSettings("ms-settings:sound");
        MediaVolumeButton.Click += (_, _) => { _volume.Muted = !_volume.Muted; UpdateMediaVolumeIcon(); };
        MediaVolumeButton.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            NudgeVolume(WheelSteps(e.Delta));
            UpdateMediaVolumeIcon();
        };
        _volume.MasterChanged += UpdateMediaVolumeIcon;
    }

    void UpdateMediaVolumeIcon()
    {
        MediaVolumeButton.Content = _volume.Muted ? "" : "";
        MediaVolumeButton.ToolTip = (_volume.Muted ? "Mute" : $"Volume {Math.Round(_volume.Master * 100)}%")
                                    + L.T("  ·  Scroll: ubah  ·  Klik: mute");
    }

    void NudgeVolume(int steps)
    {
        if (steps == 0) return;
        if (_volume.Muted && steps > 0) _volume.Muted = false;
        _volume.Master = (float)Math.Round(_volume.Master + Settings.Current.ScrollStepPercent / 100.0 * steps, 2);
    }

    void UpdateMedia()
    {
        bool has = HasMedia;
        bool between = has && (!_media.HasSession || _media.Title.Length == 0); // sedang ganti lagu: tampilan lama dipertahankan
        if (between)
        {
            // cek lagi setelah masa tenggang habis (kalau ternyata musik benar-benar berhenti)
            _mediaGraceTimer ??= OneShot(UpdateMedia);
            Start(_mediaGraceTimer, MediaGraceMs + 100);
        }
        MediaPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        NoMediaText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;

        if (!between)
        {
            TitleText.Text = _media.Title;
            ArtistText.Text = _media.Artist;
            SourceText.Text = _media.SourceName;
            ArtImage.Source = _media.Art;
            MiniArt.Source = _media.Art;
        }
        ArtImage.Visibility = ArtImage.Source != null ? Visibility.Visible : Visibility.Hidden;
        PlayButton.Content = _media.IsPlaying ? "" : "";
        UpdateMediaVolumeIcon();
        UpdateAmbient();
        ApplyCoverColor();

        UpdateProgress();
        UpdateVisualizerState();
        UpdateActivity();
        if (_expanded && _mode == IslandMode.Media && !has) SetMode(IslandMode.Control, animate: true);
        else if (_expanded) UpdateDots();
    }

    /// <summary>Bar musik, progress, dan nama sumber berganti warna halus mengikuti cover lagu.</summary>
    void ApplyCoverColor()
    {
        var art = HasMedia ? _media.Art : null;
        if (ReferenceEquals(art, _coverArtSeen) && _coverArtSeen != null) return;
        _coverArtSeen = art;
        Color target = _accent is SolidColorBrush sb ? sb.Color : Colors.White;
        if (Settings.Current.CoverColors && art != null)
        {
            var v = Vivid(IconLoader.DominantColor(art));
            if (v is Color c) target = c;
        }
        if (_coverTarget == target) return;
        _coverTarget = target;
        var anim = new System.Windows.Media.Animation.ColorAnimation(target, TimeSpan.FromMilliseconds(500))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
        };
        _coverBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    /// <summary>Warna cover dibuat cukup terang & berwarna supaya terlihat di latar gelap; abu-abu → null.</summary>
    static Color? Vivid(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        if (max <= 0 || d / max < 0.15) return null; // hampir abu-abu: pakai warna aksen
        double h = d == 0 ? 0 : max == r ? ((g - b) / d % 6) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60; if (h < 0) h += 360;
        double sat = Math.Clamp(d / max, 0.45, 0.85), val = Math.Max(max, 0.88);
        double cc = val * sat, x = cc * (1 - Math.Abs(h / 60 % 2 - 1)), m = val - cc;
        (double R, double G, double B) = h < 60 ? (cc, x, 0.0) : h < 120 ? (x, cc, 0.0) : h < 180 ? (0.0, cc, x)
                                     : h < 240 ? (0.0, x, cc) : h < 300 ? (x, 0.0, cc) : (cc, 0.0, x);
        return Color.FromRgb((byte)((R + m) * 255), (byte)((G + m) * 255), (byte)((B + m) * 255));
    }

    /// <summary>Ambient light: warna dominan cover lagu dipancarkan lembut di sekitar island.</summary>
    // Cahaya ambient memakai efek blur yang mahal kalau dihitung ulang tiap frame.
    // Saat island sedang bergerak (buka/tutup/ganti halaman) cahayanya disembunyikan,
    // lalu muncul lagi dengan halus setelah bentuknya diam → animasi tidak patah-patah.
    DispatcherTimer? _glowRestore;
    Geometry? _glowPending;

    void UpdateGlowShape(Geometry geometry)
    {
        if (!_ambientOn || !Settings.Current.SmoothGlow)
        {
            Glow.Data = geometry;
            if (_ambientOn && Glow.Visibility != Visibility.Visible) Glow.Visibility = Visibility.Visible;
            return;
        }
        bool moving = Math.Abs(_spring.Velocity) > 0.02 || Math.Abs(_spring.Target - _spring.Value) > 0.002
                      || Math.Abs(_pageH.Velocity) > 0.5 || Math.Abs(_pageW.Velocity) > 0.5 || _alertActive;
        if (!moving && Glow.Visibility == Visibility.Visible) { Glow.Data = geometry; return; }
        _glowPending = geometry;
        if (moving && Glow.Visibility == Visibility.Visible) Glow.Visibility = Visibility.Hidden;
        _glowRestore ??= OneShot(() =>
        {
            if (!_ambientOn || _glowPending == null) return;
            Glow.Data = _glowPending;
            Glow.Visibility = Visibility.Visible;
            double target = Glow.Opacity;
            Glow.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, target, TimeSpan.FromMilliseconds(220))
            {
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
            });
        });
        Start(_glowRestore, 90); // dimulai ulang tiap frame selama masih bergerak
    }

    /// <summary>Warna ambient light sekarang (null = mati), dipakai juga oleh dock & panel samping.</summary>
    public static Color? AmbientColor { get; private set; }
    public static double AmbientStrength { get; private set; }
    public static event Action? AmbientChanged;

    void UpdateAmbient()
    {
        var s = Settings.Current;
        bool fromMedia = s.AmbientLight && HasMedia && _media.Art != null;
        // tanpa lagu: cahaya boleh ikut warna wallpaper (opsi di Pengaturan → Wallpaper, bawaan mati)
        bool fromWall = !fromMedia && s.AmbientLight && s.AmbientFromWallpaper && WallpaperService.WallColor != null;
        bool on = fromMedia || fromWall;
        if (on)
        {
            var c = fromMedia ? IconLoader.DominantColor(_media.Art!) : WallpaperService.WallColor!.Value;
            // terangkan sedikit supaya cahaya tetap terlihat walau cover gelap (diatur di Pengaturan → Kecerahan)
            double bright = Math.Clamp(s.AmbientBrightness, 0.5, 2);
            double max = Math.Max(c.R, Math.Max(c.G, c.B));
            double boost = max < 1 ? 1 : Math.Min(2.2 * bright, 200 * bright / max);
            var lit = Color.FromRgb((byte)Math.Min(255, c.R * boost), (byte)Math.Min(255, c.G * boost), (byte)Math.Min(255, c.B * boost));
            GlowEffect.Color = lit;
            Glow.Fill = new SolidColorBrush(lit);
            _glowStop0.Color = Color.FromArgb(200, lit.R, lit.G, lit.B);
            _glowStop1.Color = Color.FromArgb(0, lit.R, lit.G, lit.B);
        }
        _ambientOn = on;
        double level = fromMedia ? (_media.IsPlaying ? 1 : 0.6) : 0.6;
        Glow.Opacity = on ? s.AmbientIntensity * level : 0;
        AmbientColor = on ? GlowEffect.Color : null;
        AmbientStrength = on ? Math.Clamp(s.AmbientIntensity * level, 0, 1) : 0;
        AmbientChanged?.Invoke();
        Glow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        InnerGlow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Redraw();
    }

    void UpdateProgress()
    {
        var dur = _media.Duration;
        if (dur <= TimeSpan.Zero)
        {
            TrackFill.Width = 0;
            PosText.Text = DurText.Text = "";
            return;
        }
        var pos = _media.Position;
        double frac = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1);
        TrackFill.Width = SeekArea.ActualWidth * frac;
        PosText.Text = FormatTime(pos);
        DurText.Text = "-" + FormatTime(dur - pos); // sisa waktu, seperti Bloom
    }

    void OnSeek(object sender, MouseButtonEventArgs e)
    {
        var dur = _media.Duration;
        if (dur <= TimeSpan.Zero || SeekArea.ActualWidth <= 0) return;
        double frac = Math.Clamp(e.GetPosition(SeekArea).X / SeekArea.ActualWidth, 0, 1);
        _media.Seek(TimeSpan.FromTicks((long)(dur.Ticks * frac)));
        TrackFill.Width = SeekArea.ActualWidth * frac;
        e.Handled = true;
    }

    static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    void OnTrackChanged()
    {
        var s = Settings.Current;
        if (!s.PeekOnTrackChange || _expanded || _suspended) return;
        if (IsTucked)
        {
            // mode sembunyi otomatis: muncul sebentar saat lagu berganti, lalu sembunyi lagi
            // (tidak saat fullscreen / ada jendela maximize supaya tidak mengganggu)
            if (_lastFullscreen || _lastMaximized) return;
            _edgeRevealed = true;
            ApplyPresence();
            Start(_edgeRevealTimer, s.PeekDurationMs + 500);
        }
        _peeking = true;
        Expand(IslandMode.Media);
        Start(_peekTimer, s.PeekDurationMs);
    }

    // =====================================================================
    // Halaman 2: control center
    // =====================================================================

    void HookControl()
    {
        WifiTile.MouseLeftButtonUp += (_, e) => { _radio.ToggleWifi(); e.Handled = true; };
        BluetoothTile.MouseLeftButtonUp += (_, e) => { _radio.ToggleBluetooth(); e.Handled = true; };
        // klik kanan / klik dua jari di touchpad: buka daftar di dalam island
        WifiTile.ContextMenuOpening += (_, e) => { e.Handled = true; OpenNetList(wifi: true); };
        BluetoothTile.ContextMenuOpening += (_, e) => { e.Handled = true; OpenNetList(wifi: false); };
        HookNetList();
        DockTile.MouseLeftButtonUp += (_, e) =>
        {
            Settings.Current.DockSmart = !Settings.Current.DockSmart;
            Settings.NotifyChanged();
            Settings.Save();
            e.Handled = true;
        };
        NotchTile.MouseLeftButtonUp += (_, e) =>
        {
            Settings.Current.AlwaysHidden = !Settings.Current.AlwaysHidden;
            Settings.NotifyChanged();
            Settings.Save();
            e.Handled = true;
        };
        foreach (var tile in new[] { WifiTile, BluetoothTile, DockTile, NotchTile })
        {
            var t = tile;
            t.MouseEnter += (_, _) => t.Opacity = 0.88;
            t.MouseLeave += (_, _) => t.Opacity = 1;
            AddPressEffect(t);
        }
        foreach (var b in new FrameworkElement[] { DndButton, SaverButton, AppsButton, NotifButton, SettingsButton,
                                                   AirplaneButton, NightLightButton, HiddenIconsButton, CastButton, ProjectButton, PlayButton })
            AddPressEffect(b);

        // langsung nyala/mati (lewat tombol Quick Settings Windows); klik kanan = buka pengaturannya
        HookQuickToggle(DndButton, QuickToggle.Kind.DoNotDisturb, "ms-settings:notifications");
        HookQuickToggle(SaverButton, QuickToggle.Kind.EnergySaver, "ms-settings:batterysaver");
        AppsButton.Click += (_, _) => RadioService.OpenSettings("shell:AppsFolder");
        NotifButton.Click += (_, _) => RadioService.OpenSettings("ms-actioncenter:");
        SettingsButton.Click += (_, _) => App.OpenSettings();
        AirplaneButton.Click += (_, _) => _radio.ToggleAirplane();
        HookQuickToggle(NightLightButton, QuickToggle.Kind.NightLight, "ms-settings:nightlight");
        HiddenIconsButton.Click += (_, _) =>
        {
            // ikon system tray ada di taskbar Windows: tampilkan sebentar lalu buka daftar ikon tersembunyi
            TaskbarService.PeekOriginal(TimeSpan.FromSeconds(12));
            Native.SendWinCombo(0x42); // Win+B = fokus ke system tray
        };
        CastButton.Click += (_, _) => Native.SendWinCombo(0x4B);    // Win+K
        ProjectButton.Click += (_, _) => Native.SendWinCombo(0x50); // Win+P

        VolumeTrack.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            NudgeVolume(WheelSteps(e.Delta));
            UpdateControl();
        };
        VolumeRow.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == VolumeIcon) _volume.Muted = !_volume.Muted;
            else if (VolumeTrack.ActualWidth > 0)
            {
                double x = e.GetPosition(VolumeTrack).X / VolumeTrack.ActualWidth;
                if (x >= -0.05) _volume.Master = (float)Math.Clamp(x, 0, 1);
            }
            UpdateControl();
            e.Handled = true;
        };
        BrightnessTrack.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            int steps = WheelSteps(e.Delta);
            if (steps == 0 || !_brightness.Available) return;
            _brightness.Set(_brightness.Current + Settings.Current.ScrollStepPercent * steps);
            UpdateControl();
        };
        BrightnessRow.MouseLeftButtonDown += (_, e) =>
        {
            if (_brightness.Available && BrightnessTrack.ActualWidth > 0)
            {
                double x = e.GetPosition(BrightnessTrack).X / BrightnessTrack.ActualWidth;
                if (x >= -0.05) _brightness.Set((int)Math.Round(Math.Clamp(x, 0, 1) * 100));
            }
            UpdateControl();
            e.Handled = true;
        };

        _radio.Changed += () => { UpdateControl(); Redraw(); };
        _volume.MasterChanged += () => { if (_expanded && _mode == IslandMode.Control) UpdateControl(); };
        _brightness.ChangedExternally += () => { if (_expanded && _mode == IslandMode.Control) UpdateControl(); };
        _brightness.Ready += UpdateControl;
        QuickToggle.StateChanged += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_expanded && _mode == IslandMode.Control) UpdateControl();
        }));
        QuickToggle.StartWatching();
    }

    void UpdateControl()
    {
        // status dibaca langsung dari Windows (juga kalau dinyalakan lewat Win+A); cadangan: tombol terakhir ditekan
        SetRound(SaverButton, QuickStateOf(QuickToggle.Kind.EnergySaver));
        SetRound(NightLightButton, QuickStateOf(QuickToggle.Kind.NightLight));
        SetRound(DndButton, QuickStateOf(QuickToggle.Kind.DoNotDisturb));
        var s = Settings.Current;
        SetTile(WifiTile, _radio.WifiOn);
        WifiSub.Text = !_radio.HasWifi ? L.T("Buka pengaturan")
            : _radio.WifiOn ? (_radio.NetworkName.Length > 0 ? _radio.NetworkName : L.T("Nyala")) : L.T("Mati");
        SetTile(BluetoothTile, _radio.BluetoothOn);
        BluetoothSub.Text = !_radio.HasBluetooth ? L.T("Buka pengaturan") : _radio.BluetoothOn ? L.T("Nyala") : L.T("Mati");
        SetTile(DockTile, s.DockEnabled);
        DockSub.Text = !s.DockEnabled ? L.T("Mati") : s.DockSmart ? "Smart" : L.T("Selalu tampil");
        SetTile(NotchTile, true);
        SetRound(AirplaneButton, _radio.AirplaneOn);
        NotchSub.Text = s.AlwaysHidden ? L.T("Selalu sembunyi") : L.T("Tampil di desktop");

        double vol = _volume.Muted ? 0 : _volume.Master;
        VolumeFill.Width = Math.Max(0, VolumeTrack.ActualWidth) * vol;
        VolumeText.Text = _volume.Muted ? "mute" : Math.Round(_volume.Master * 100) + "%";
        VolumeIcon.Text = _volume.Muted ? "" : "";

        double br = _brightness.Available ? _brightness.Current / 100.0 : 0;
        BrightnessFill.Width = Math.Max(0, BrightnessTrack.ActualWidth) * br;
        BrightnessText.Text = _brightness.Available ? _brightness.Current + "%" : "—";
    }

    /// <summary>Tile aktif: putih dengan teks hitam (gaya Bloom). Mati: gelap dengan teks putih.</summary>
    readonly System.Collections.Generic.Dictionary<QuickToggle.Kind, bool> _quickState = new();
    readonly System.Collections.Generic.Dictionary<QuickToggle.Kind, long> _quickPressedAt = new();

    bool QuickStateOf(QuickToggle.Kind kind)
    {
        // baru saja ditekan: Windows kadang telat memperbarui statusnya, jadi percayai hasil tombol dulu
        bool recent = _quickPressedAt.TryGetValue(kind, out var at) && Environment.TickCount64 - at < 3000;
        if (!recent && QuickToggle.ReadState(kind) is bool live) _quickState[kind] = live;
        return _quickState.TryGetValue(kind, out var v) && v;
    }

    void HookQuickToggle(Button button, QuickToggle.Kind kind, string settingsUri)
    {
        button.MouseRightButtonUp += (_, e) => { e.Handled = true; RadioService.OpenSettings(settingsUri); };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            bool? result = await QuickToggle.ToggleAsync(kind);
            button.IsEnabled = true;
            if (result == null) { RadioService.OpenSettings(settingsUri); return; } // tombol Windows tidak ketemu
            _quickState[kind] = result.Value;
            long pressed = Environment.TickCount64;
            _quickPressedAt[kind] = pressed;
            SetRound(button, result.Value);
            // cek sekali: kalau status yang dibaca dari Windows tidak cocok, jangan pakai pembacanya lagi
            await System.Threading.Tasks.Task.Delay(2500);
            if (_quickPressedAt[kind] == pressed && QuickToggle.ReadState(kind) is bool live && live != result.Value)
                QuickToggle.MarkUntrusted(kind);
        };
    }

    /// <summary>Tombol bulat menyala (putih) / mati.</summary>
    static void SetRound(Button b, bool on)
    {
        b.Background = on ? OnBrush : RoundOff;
        b.Foreground = on ? OnFore : Brushes.White;
    }

    static void SetTile(Border tile, bool on)
    {
        // warna latar berganti halus (bukan langsung)
        Color target = on ? OnColor : ((SolidColorBrush)TileOff).Color;
        if (tile.Background is not SolidColorBrush b || b.IsFrozen)
        {
            b = new SolidColorBrush(tile.Background is SolidColorBrush old ? old.Color : target);
            tile.Background = b;
        }
        if (b.Color != target || b.HasAnimatedProperties)
            b.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(target, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            });
        SetForeground(tile, on ? OnFore : Brushes.White);
    }

    /// <summary>Saat ditekan, sedikit mengecil; saat dilepas memantul balik (seperti iOS).</summary>
    static void AddPressEffect(FrameworkElement el)
    {
        var st = new ScaleTransform(1, 1);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = st;
        void To(double v, int ms, bool bounce)
        {
            var a = new System.Windows.Media.Animation.DoubleAnimation(v, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = bounce
                    ? new System.Windows.Media.Animation.BackEase { Amplitude = 0.5, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                    : new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        el.PreviewMouseLeftButtonDown += (_, _) => To(0.93, 90, false);
        el.PreviewMouseLeftButtonUp += (_, _) => To(1, 260, true);
        el.MouseLeave += (_, _) => { if (st.ScaleX < 1) To(1, 260, true); };
    }

    static void SetForeground(DependencyObject root, Brush brush)
    {
        if (root is TextBlock tb) { tb.Foreground = brush; return; }
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d) SetForeground(d, brush);
    }

    // =====================================================================
    // Daftar Wi-Fi / Bluetooth
    // =====================================================================

    void HookNetList()
    {
        NetBackButton.Click += (_, _) => SetMode(IslandMode.Control, animate: true);
        NetRefreshButton.Click += (_, _) => _ = LoadNetList();
        NetSettingsButton.Click += (_, _) => RadioService.OpenSettings(_netIsWifi ? "ms-settings:network-wifi" : "ms-settings:bluetooth");
        PasswordCancel.Click += (_, _) => ClosePasswordPanel();
        PasswordConnect.Click += (_, _) => _ = ConnectWithPassword();
        PasswordInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = ConnectWithPassword(); if (e.Key == Key.Escape) ClosePasswordPanel(); };
    }

    void OpenNetList(bool wifi)
    {
        _netIsWifi = wifi;
        NetListTitle.Text = wifi ? "Wi-Fi" : "Bluetooth";
        NetRows.Children.Clear();
        NetStatusText.Text = wifi ? L.T("Memindai jaringan…") : L.T("Membaca perangkat…");
        SetMode(IslandMode.NetList, animate: true);
        _ = LoadNetList();
    }

    async System.Threading.Tasks.Task LoadNetList()
    {
        NetStatusText.Visibility = Visibility.Visible;
        if (_netIsWifi)
        {
            NetStatusText.Text = L.T("Memindai jaringan…");
            var (items, error) = await _netList.ScanWifiAsync();
            if (!_netIsWifi) return;
            NetRows.Children.Clear();
            foreach (var item in items) NetRows.Children.Add(WifiRow(item));
            NetStatusText.Text = error ?? (items.Count == 0 ? L.T("Tidak ada jaringan ditemukan") : L.T("Klik jaringan untuk menyambung"));
        }
        else
        {
            NetStatusText.Text = L.T("Membaca perangkat…");
            var (items, error) = await _netList.PairedBluetoothAsync();
            if (_netIsWifi) return;
            NetRows.Children.Clear();
            foreach (var item in items) NetRows.Children.Add(BluetoothRow(item));
            NetStatusText.Text = error ?? (items.Count == 0
                ? L.T("Belum ada perangkat yang dipasangkan")
                : L.T("Perangkat yang dipasangkan · klik untuk sambung/putus di Pengaturan"));
        }
    }

    FrameworkElement WifiRow(WifiItem item)
    {
        // ikon sinyal Wi-Fi 1-4 bar
        string glyph = item.Bars switch { >= 4 => "\uE701", 3 => "\uE874", 2 => "\uE873", _ => "\uE872" };
        var row = NetRow(glyph, item.Ssid,
            item.Connected ? L.T("Tersambung") : item.Secured ? L.T("Terkunci") : L.T("Terbuka"),
            item.Connected, item.Secured ? "\uE72E" : null);
        row.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            if (item.Connected)
            {
                _netList.Disconnect();
                NetStatusText.Text = L.T("Terputus dari ") + item.Ssid;
                await System.Threading.Tasks.Task.Delay(600);
                _ = LoadNetList();
                return;
            }
            NetStatusText.Text = L.T("Menyambung ke ") + item.Ssid + "…";
            var (ok, needPassword, message) = await _netList.ConnectAsync(item, null);
            NetStatusText.Text = message;
            if (needPassword) OpenPasswordPanel(item);
            else if (ok) _ = LoadNetList();
        };
        return row;
    }

    FrameworkElement BluetoothRow(BluetoothItem item)
    {
        var row = NetRow("\uE702", item.Name, item.Connected ? L.T("Tersambung") : L.T("Tidak tersambung"), item.Connected, null);
        // Windows tidak menyediakan cara resmi untuk menyambungkan headset/speaker dari aplikasi lain
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; RadioService.OpenSettings("ms-settings:bluetooth"); };
        return row;
    }

    Border NetRow(string glyph, string title, string sub, bool active, string? rightGlyph)
    {
        var grid = new Grid { Height = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        Brush fg = active ? Brushes.Black : Brushes.White;

        var icon = new TextBlock { Text = glyph, FontFamily = IconFontFamily, FontSize = 15, Foreground = fg, VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Foreground = fg, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = sub, Foreground = active ? Brushes.DimGray : Subtle, FontSize = 10.5 });
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        if (rightGlyph != null)
        {
            var lockIcon = new TextBlock { Text = rightGlyph, FontFamily = IconFontFamily, FontSize = 12, Foreground = active ? Brushes.DimGray : Subtle, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(lockIcon, 2);
            grid.Children.Add(lockIcon);
        }

        var row = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(2, 0, 2, 4),
            Background = active ? Brushes.White : TileOff, Cursor = Cursors.Hand,
        };
        row.MouseEnter += (_, _) => row.Opacity = 0.85;
        row.MouseLeave += (_, _) => row.Opacity = 1;
        return row;
    }

    static readonly FontFamily IconFontFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    void OpenPasswordPanel(WifiItem item)
    {
        _pendingWifi = item;
        PasswordLabel.Text = L.T("Password untuk ") + item.Ssid;
        PasswordInput.Password = "";
        PasswordPanel.Visibility = Visibility.Visible;
        NetStatusText.Visibility = Visibility.Collapsed;
        // izinkan island menerima ketikan keyboard sementara
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetNoActivate(hwnd, false);
        Activate();
        PasswordInput.Focus();
    }

    void ClosePasswordPanel()
    {
        if (PasswordPanel.Visibility != Visibility.Visible) return;
        PasswordPanel.Visibility = Visibility.Collapsed;
        NetStatusText.Visibility = Visibility.Visible;
        PasswordInput.Password = "";
        _pendingWifi = null;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) Native.SetNoActivate(hwnd, true);
    }

    async System.Threading.Tasks.Task ConnectWithPassword()
    {
        if (_pendingWifi == null || PasswordInput.Password.Length == 0) return;
        var item = _pendingWifi;
        string pw = PasswordInput.Password;
        PasswordLabel.Text = L.T("Menyambung ke ") + item.Ssid + "…";
        var (ok, _, message) = await _netList.ConnectAsync(item, pw);
        if (ok)
        {
            ClosePasswordPanel();
            NetStatusText.Text = message;
            _ = LoadNetList();
        }
        else
        {
            PasswordLabel.Text = message + " · " + item.Ssid;
            PasswordInput.Password = "";
            PasswordInput.Focus();
        }
    }

    // =====================================================================
    // Halaman 3: kalender + timer
    // =====================================================================

    void HookCalendar()
    {
        // klik tanggal: buka Google Calendar di browser
        CalendarBox.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenUrl("https://calendar.google.com/calendar/r");
        };
        TimerStartButton.Click += (_, _) => { if (_timer.IsRinging) _timer.Dismiss(); else _timer.Toggle(); };
        TimerResetButton.Click += (_, _) => _timer.Reset();
        Preset5.Click += (_, _) => _timer.SetMinutes(5);
        Preset15.Click += (_, _) => _timer.SetMinutes(15);
        Preset25.Click += (_, _) => _timer.SetMinutes(25);
        Preset50.Click += (_, _) => _timer.SetMinutes(50);
        TimerText.MouseWheel += (_, e) =>
        {
            e.Handled = true; // jangan ikut pindah halaman
            int steps = WheelSteps(e.Delta);
            if (steps == 0 || _timer.IsRunning) return;
            _timer.SetMinutes(_timer.Duration.TotalMinutes + steps);
        };
    }

    void BuildCalendar()
    {
        var today = DateTime.Today;
        int key = today.Year * 10000 + today.Month * 100 + today.Day;
        if (key == _calendarMonth) return; // sudah dibuat untuk hari ini
        _calendarMonth = key;

        DayName.Text = today.ToString("dddd", L.Culture);
        DayNumber.Text = today.Day.ToString();
        MonthTitle.Text = today.ToString("MMMM yyyy", L.Culture);

        // satu baris minggu ini (Senin - Minggu), hari ini ditandai lingkaran putih
        WeekStrip.Children.Clear();
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        string[] names = Array.ConvertAll(new[] { "Sen", "Sel", "Rab", "Kam", "Jum", "Sab", "Min" }, L.T);
        for (int i = 0; i < 7; i++)
        {
            var date = monday.AddDays(i);
            bool isToday = date == today;
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(new TextBlock
            {
                Text = names[i], Foreground = Subtle, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4),
            });
            col.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
                Background = isToday ? TodayBg : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = date.Day.ToString(), FontSize = 12.5, FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = isToday ? Brushes.Black : Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
            // klik salah satu hari: buka hari itu di Google Calendar
            col.Background = Brushes.Transparent;
            col.Cursor = Cursors.Hand;
            var day = date;
            col.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                OpenUrl($"https://calendar.google.com/calendar/r/day/{day.Year}/{day.Month}/{day.Day}");
            };
            WeekStrip.Children.Add(col);
        }
    }

    void UpdateTimer()
    {
        TimerText.Text = TimerService.Format(_timer.Remaining);
        TimerText.Foreground = _timer.IsRinging ? AlarmRed : Brushes.White;
        TimerStartButton.Content = _timer.IsRinging ? L.T("Matikan") : _timer.IsRunning ? L.T("❚❚  Jeda") : L.T("▶  Mulai");
        PomodoroButton.Visibility = Settings.Current.PomodoroEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (_pomoPhase >= 0) PomodoroButton.Background = TimerOrange; else PomodoroButton.ClearValue(Control.BackgroundProperty);
        TimerHint.Text = _pomoPhase >= 0 && _timer.IsRunning
                       ? (_pomoPhase % 2 == 0 ? "🍅 " + L.T("Fokus") + $" {(_pomoRound - 1) % 4 + 1}/4" : "☕ " + L.T("Istirahat")) + " · " + _timer.EndsAtLocal.ToString("HH:mm")
                       : _timer.IsRunning ? L.T("selesai ") + _timer.EndsAtLocal.ToString("HH:mm")
                       : _timer.IsRinging ? L.T("Waktu habis")
                       : L.T("Scroll angka untuk mengubah");
        UpdateClock(reschedule: false);
        UpdateActivity();
    }

    void OnTimerFinished()
    {
        if (PomodoroNext()) return;
        if (_suspended) return; // main game: jangan ganggu (bunyi notifikasi tetap terdengar)
        // munculkan island walaupun sedang tersembunyi, dengan lonceng merah
        // (kecuali sedang main game fullscreen)
        IntPtr fg = Native.GetForegroundWindow();
        if (_lastFullscreen && !ScreenState.IsRevealAllowedApp(fg)) return;
        if (IsTucked)
        {
            _edgeRevealed = true;
            ApplyPresence();
        }
        Start(_edgeRevealTimer, 10000);
    }

    // =====================================================================
    // Jam, kondisi laptop
    // =====================================================================

    void UpdateClock() => UpdateClock(reschedule: true);

    void UpdateClock(bool reschedule)
    {
        var s = Settings.Current;
        var now = DateTime.Now;
        string fmt = s.Use24Hour
            ? (s.ShowSeconds ? "HH:mm:ss" : "HH:mm")
            : (s.ShowSeconds ? "h:mm:ss tt" : "h:mm tt");
        string time = now.ToString(fmt, CultureInfo.InvariantCulture);

        // timer aktif: jam di notch & header diganti hitungan mundur (seperti Bloom)
        bool timerShown = _timer.IsRunning || _timer.IsRinging;
        string center = timerShown ? TimerService.Format(_timer.Remaining) : time;
        Brush centerBrush = _timer.IsRinging ? AlarmRed : Brushes.White;
        var bell = _timer.IsRinging ? Visibility.Visible : Visibility.Collapsed;

        MiniClock.Text = center;
        Dispatcher.BeginInvoke(new Action(FitMini), DispatcherPriority.Render);
        MiniClock.Foreground = centerBrush;
        MiniBell.Visibility = bell;
        HeaderClock.Text = center;
        HeaderClock.Foreground = centerBrush;
        HeaderBell.Visibility = bell;
        CalClockText.Text = time;
        CalDateText.Text = WeatherPrefix() + L.T("Minggu ke-") + ISOWeek.GetWeekOfYear(now);
        UpdateMiniInfo();

        if (!reschedule) return;
        if (_calendarMonth != -1 && now.Hour == 0 && now.Minute == 0) BuildCalendar(); // ganti hari

        // jadwalkan tepat di pergantian menit/detik berikutnya (bukan polling)
        double ms = s.ShowSeconds
            ? 1000 - now.Millisecond
            : (60 - now.Second) * 1000 - now.Millisecond + 20;
        _clockTimer.Stop();
        _clockTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, ms));
        _clockTimer.Start();
    }

    double _miniNeedIdle, _miniNeedActive;

    /// <summary>Hitung lebar notch kecil yang dibutuhkan isinya (jam di tengah, kiri & kanan simetris).</summary>
    void FitMini()
    {
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        MiniClock.Measure(inf);
        MiniInfo.Measure(inf);
        double clock = MiniClock.DesiredSize.Width + (MiniBell.Visibility == Visibility.Visible ? 16 : 0);
        // titik mic/kamera di kanan jam: sisi kiri ikut diberi ruang supaya jam tetap di tengah
        double sideR = MiniPrivacy.Visibility == Visibility.Visible ? MiniPrivacyShift.X : 0;
        double sideL = MiniLive.Visibility == Visibility.Visible ? -MiniLiveShift.X : 0;
        clock += 2 * Math.Max(sideR, sideL); // kiri & kanan jam diberi ruang yang sama supaya jam tetap di tengah
        double info = MiniInfo.DesiredSize.Width;
        const double gap = 14;          // jarak minimal antara jam dan isi samping
        double leftIdle = 16 + 14;      // padding + ikon Wi-Fi
        double rightIdle = 13 + info;   // padding + baterai
        double idle = clock + 2 * (Math.Max(leftIdle, rightIdle) + gap);
        double leftActive = 14 + Math.Max(_miniBars.Count * 4.5, 22); // bar musik / ikon Discord
        double rightActive = 14 + 18;   // cover lagu
        double active = clock + 2 * (Math.Max(leftActive, rightActive) + gap);
        if (Math.Abs(idle - _miniNeedIdle) > 0.5 || Math.Abs(active - _miniNeedActive) > 0.5)
        {
            _miniNeedIdle = Math.Ceiling(idle);
            _miniNeedActive = Math.Ceiling(active);
            FrameLoop.Add(_animTick);
        }
    }

    void UpdateMiniInfo()
    {
        var b = SystemStats.Battery();
        MiniInfo.Text = b.HasBattery ? (b.Charging ? "⚡" : "") + b.Percent + "%" : "";
        MiniInfo.Foreground = b.HasBattery && b.Charging ? ChargeGreen : Subtle;
        if (_downloads?.IsActive == true && Settings.Current.DownloadProgress)
        {
            // sedang download: tampilkan persennya di notch kecil
            double? pr = _downloads.Progress;
            MiniInfo.Text = "↓ " + (pr is double v ? $"{v * 100:0}%" : (_downloads.Count > 1 ? _downloads.Count + " file" : ""));
            MiniInfo.Foreground = DownloadBlue;
        }

        // charger baru dicolok / dicabut → animasi (bukan saat aplikasi baru dibuka)
        if (b.HasBattery)
        {
            if (_lastCharging.HasValue && _lastCharging.Value != b.Charging) ShowChargeAlert(b.Charging, b.Percent);
            _lastCharging = b.Charging;
        }

        // garis hijau di bawah notch saat mengisi daya, panjangnya mengikuti persentase baterai
        FitMini();
        bool showLine = b.HasBattery && b.Charging && Settings.Current.ShowChargeLine;
        _lastBatteryPercent = b.Percent;
        ChargeTrack.Visibility = showLine ? Visibility.Visible : Visibility.Collapsed;
        if (showLine)
        {
            double trackW = Math.Max(0, Mini.Width - 36);
            ChargeFill.Width = trackW * b.Percent / 100.0 * _lineProgress;
        }
    }

    static Brush ChargeGreen = MakeFrozen(Color.FromRgb(0x34, 0xC7, 0x59));

    static Color ColorOr(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }

    /// <summary>Teks di atas warna terang = hitam, di atas warna gelap = putih.</summary>
    static Brush ContrastOf(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 > 0.6 ? Brushes.Black : Brushes.White;

    void ApplyColors()
    {
        var s = Settings.Current;
        bool wasTimer = ReferenceEquals(_progressBrush, TimerOrange);
        var charge = ColorOr(s.ChargeColor, Color.FromRgb(0x34, 0xC7, 0x59));
        ChargeGreen = MakeFrozen(charge);
        DownloadBlue = MakeFrozen(ColorOr(s.DownloadColor, Color.FromRgb(0x0A, 0x84, 0xFF)));
        TimerOrange = MakeFrozen(ColorOr(s.TimerColor, Color.FromRgb(0xFF, 0x9F, 0x0A)));
        OnColor = ColorOr(s.ToggleOnColor, Colors.White);
        OnBrush = MakeFrozen(OnColor);
        OnFore = ContrastOf(OnColor);
        ChargeFill.Background = ChargeGreen;
        ChargeTrackBg.Background = MakeFrozen(Color.FromArgb(0x26, charge.R, charge.G, charge.B));
        MiniCamDot.Fill = MakeFrozen(ColorOr(s.CameraDotColor, Color.FromRgb(0x34, 0xC7, 0x59)));
        MiniMicDot.Fill = MakeFrozen(ColorOr(s.MicDotColor, Color.FromRgb(0xFF, 0x9F, 0x0A)));
        _progressBrush = wasTimer ? TimerOrange : DownloadBlue; // garis download/timer memakai warna baru
    }

    /// <summary>Charger dicolok / dicabut: notch melebar sebentar dengan status baterai (seperti iPhone).</summary>
    void ShowChargeAlert(bool charging, int percent, bool force = false)
    {
        // mode game: island dimatikan total; fullscreen biasa: muncul seperti notifikasi lalu sembunyi lagi
        if (_suspended || (!Settings.Current.ChargeAnimation && !force)) return;
        _alertPercent = Math.Clamp(percent, 0, 100);
        _alertPlug = charging;
        _alertDownload = false;
        _alertHud = false;
        _alertNotif = false;
        _alertW = 300; _alertH = 40;
        AlertBolt.Text = "\uE945";
        AlertBolt.Foreground = ChargeGreen;
        AlertBattery.Visibility = Visibility.Visible;
        SetRimBrush(ChargeGreen);
        if (charging) _lineProgress = 0; // garis kecil di bawah notch baru tumbuh setelah animasi
        AlertLabel.Text = charging ? L.T("Mengisi daya") : L.T("Tidak mengisi daya");
        AlertLabel.Foreground = charging ? Brushes.White : Subtle;
        AlertBolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
        AlertBatFill.Background = charging ? ChargeGreen : percent <= 20 ? AlarmRed : Brushes.White;
        AlertPercent.Foreground = charging ? ChargeGreen : percent <= 20 ? AlarmRed : Brushes.White;
        AlertPercent.Text = _alertPercent + "%";
        BeginAlert();
    }

    /// <summary>Download selesai: notifikasi dengan nama file, garis biru penuh lalu memudar.</summary>
    void ShowDownloadAlert(string name)
    {
        if (_suspended || !Settings.Current.DownloadProgress) return;
        _alertPlug = false;
        _alertDownload = true;
        _alertHud = false;
        _alertNotif = false;
        _alertW = 300; _alertH = 40;
        AlertBolt.Text = "\uE896"; // ikon download
        AlertBolt.Foreground = DownloadBlue;
        AlertBolt.Visibility = Visibility.Visible;
        AlertLabel.Text = L.T("Download selesai");
        AlertLabel.Foreground = Brushes.White;
        AlertPercent.Text = name;
        AlertPercent.Foreground = Subtle;
        AlertBattery.Visibility = Visibility.Collapsed;
        SetRimBrush(DownloadBlue);
        BeginAlert();
    }

    void BeginAlert()
    {
        GrowWindow(_alertW, _alertH);
        if (IsTucked || _edgeRevealed) // notch sedang sembunyi (fullscreen / maximize): turun sebentar
        {
            _edgeRevealed = true;
            ApplyPresence();
            Start(_edgeRevealTimer, (int)(AlertTotal * 1000) + 200);
        }
        _alertClock.Restart();
        _alertActive = true;
        FrameLoop.Add(_animTick);
    }

    double AlertTotal => _alertHud ? _hudOutStart + 0.6 : _alertNotif ? 4.6 : _alertPlug ? 3.4 : 3.0;
    double AlertOutStart => _alertHud ? _hudOutStart : _alertNotif ? 3.9 : 2.35;

    void SetRimBrush(Brush b)
    {
        if (RimL.Stroke == b) return;
        RimL.Stroke = RimR.Stroke = RimGlowL.Stroke = RimGlowR.Stroke = b;
    }

    void UpdateLive()
    {
        if (_mode == IslandMode.Control)
        {
            var b = SystemStats.Battery();
            UpdateStats(b);
        }
        if (_mode == IslandMode.Media) UpdateProgress();
        if (_mode == IslandMode.Performance) UpdatePerf();
    }

    /// <summary>Notch kecil melebar saat musik diputar atau timer aktif.</summary>
    void UpdateActivity()
    {
        double target = HasMedia || _discordShown || _timer.IsRunning || _timer.IsRinging ? 1 : 0;
        if (_activity.Target == target) return;
        _activity.Target = target;
        FrameLoop.Add(_animTick);
    }

    // =====================================================================
    // Sembunyi / muncul
    // =====================================================================

    void CheckScreenState()
    {
        IntPtr fg = Native.GetForegroundWindow();
        bool fullscreen = ScreenState.IsFullscreen(fg);
        bool maximized = !fullscreen && ScreenState.IsMaximized(fg);

        if (fullscreen != _lastFullscreen || maximized != _lastMaximized)
        {
            _lastFullscreen = fullscreen;
            _lastMaximized = maximized;
            ScreenStateChanged?.Invoke(fullscreen, maximized);
        }

        // mode game: fullscreen (kecuali aplikasi pengecualian) → hentikan semuanya
        bool game = Settings.Current.GameMode && fullscreen && !ScreenState.IsRevealAllowedApp(fg);
        if (game != _suspended) SetSuspended(game);

        var s = Settings.Current;
        bool tuck = s.AlwaysHidden || (fullscreen && s.HideInFullscreen) || (maximized && s.HideWhenMaximized);
        if (tuck == _tucked) return;
        _tucked = tuck;
        if (!tuck) _edgeRevealed = false;
        ApplyPresence();
    }

    void SetSuspended(bool on)
    {
        _suspended = on;
        if (on)
        {
            if (_expanded) Collapse();
            _liveTimer.Stop();
            _vizRunning = false;
            _viz.Stop();
            Visibility = Visibility.Hidden;
            UpdateDiscordWatch();
        }
        else
        {
            Visibility = Visibility.Visible;
            UpdateVisualizerState();
            UpdateMedia();
            UpdateDiscordWatch();
        }
        GameModeChanged?.Invoke(on);
    }

    void ApplyPresence()
    {
        bool hidden = IsTucked;
        if (hidden && _expanded) Collapse();
        _presence.Target = hidden ? 0 : 1;
        Root.IsHitTestVisible = !hidden;
        FrameLoop.Add(_animTick);
    }

    /// <summary>Pointer di tengah tepi atas saat notch tersembunyi: munculkan sementara.</summary>
    DispatcherTimer? _revealCheck;

    public void RevealFromTopEdge()
    {
        if (!_tucked) return;
        Native.BringToTopmost(new WindowInteropHelper(this).Handle); // jangan sampai muncul di belakang jendela lain
        _edgeRevealed = true;
        ApplyPresence();
        Start(_edgeRevealTimer, 2000);
        // diagnosa: setelah meluncur turun, apakah notch benar-benar yang paling atas di titik itu?
        _revealCheck ??= OneShot(() =>
        {
            try
            {
                var me = new WindowInteropHelper(this).Handle;
                var pt = PointToScreen(new Point(_cx, 6));
                var hit = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = (int)pt.X, Y = (int)pt.Y }), 2 /* GA_ROOT */);
                EdgeWatcher.Log($"  notch muncul: presence={_presence.Value:0.00} tucked={_tucked} revealed={_edgeRevealed} | paling atas di notch: " +
                                (hit == me ? "island (OK)" : ScreenState.ProcessNameOf(hit) + " (" + Native.ClassNameOf(hit) + ") ← menutupi island"));
            }
            catch { }
        });
        Start(_revealCheck, 400);
    }

    // =====================================================================
    // Bar musik
    // =====================================================================

    void UpdateVisualizerState()
    {
        var s = Settings.Current;
        bool show = HasMedia && s.Visualizer != VisualizerMode.Off;
        bool want = show && _media.IsPlaying && !_suspended; // bergerak hanya saat diputar, dan tidak saat main game

        MiniBars.Visibility = show ? Visibility.Visible : Visibility.Hidden;
        FullBars.Visibility = show ? Visibility.Visible : Visibility.Hidden;

        // tangkap audio hanya kalau mode "Real" dan musik sedang diputar
        if (want && s.Visualizer == VisualizerMode.Real) _viz.Start();
        else _viz.Stop();

        if (want && !_vizRunning)
        {
            _vizRunning = true;
            StartVizTimer();
        }
        else if (!want && _vizRunning)
        {
            _vizRunning = false;
            _vizTimer?.Stop();
        }
        if (!want) PausedBars(); // di-pause: bar diam membentuk gelombang, tanpa memakan CPU
    }

    bool VizTick(double dt)
    {
        if (!_vizRunning) return false;
        var s = Settings.Current;

        _vizTime += dt;
        _vizAccum += dt;
        // batasi fps → hemat CPU; saat notch kecil bar-nya kecil, cukup fps lebih rendah
        int fps = _spring.Value > 0.5 ? s.VisualizerFps : Math.Min(s.VisualizerFps, s.MiniVisualizerFps);
        if (_vizAccum < 1.0 / Math.Max(10, fps)) return true;
        double step = _vizAccum;
        _vizAccum = 0;

        if (s.Visualizer == VisualizerMode.Real) _viz.ReadLevels(_levels);
        else FakeLevels();

        var bars = _spring.Value > 0.5 ? _fullBars : _miniBars;
        for (int i = 0; i < _levels.Length; i++)
        {
            float target = _levels[i];
            // naik cepat, turun pelan → terasa "nendang" tapi halus
            double k = target > _shown[i] ? 1 - Math.Exp(-step * 30) : 1 - Math.Exp(-step * 9);
            _shown[i] += (float)((target - _shown[i]) * k);
            if (i < bars.Count && bars[i].RenderTransform is ScaleTransform st)
                st.ScaleY = 0.15 + 0.85 * _shown[i];
        }
        return true;
    }

    // ---- bar musik dengan timer sendiri ----
    // Dulu bar ikut loop frame (CompositionTarget.Rendering): selama musik diputar WPF dibangunkan
    // setiap frame layar (60–165x per detik) walau bar hanya berubah 30x per detik.
    // Sekarang timer berjalan tepat sesuai fps bar, dan bar hanya digambar ulang kalau tingginya berubah.
    System.Windows.Threading.DispatcherTimer? _vizTimer;
    long _vizLast;

    void StartVizTimer()
    {
        if (_vizTimer == null)
        {
            _vizTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render);
            _vizTimer.Tick += (_, _) => VizTimerTick();
        }
        _vizLast = System.Diagnostics.Stopwatch.GetTimestamp();
        SetVizInterval();
        _vizTimer.Start();
    }

    void SetVizInterval()
    {
        var s = Settings.Current;
        int fps = _spring.Value > 0.5 ? s.VisualizerFps : Math.Min(s.VisualizerFps, s.MiniVisualizerFps);
        // timer Windows berdetak ~15,6 ms; sedikit dimajukan supaya fps tercapai (mis. 30 fps → tiap ~31 ms)
        var iv = TimeSpan.FromMilliseconds(Math.Max(8, 1000.0 / Math.Max(10, fps) - 6));
        if (_vizTimer != null && _vizTimer.Interval != iv) _vizTimer.Interval = iv;
    }

    void VizTimerTick()
    {
        if (!_vizRunning) { _vizTimer?.Stop(); return; }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double step = Math.Clamp((now - _vizLast) / (double)System.Diagnostics.Stopwatch.Frequency, 0.001, 0.1);
        _vizLast = now;
        _vizTime += step;
        var s = Settings.Current;
        if (s.Visualizer == VisualizerMode.Real) _viz.ReadLevels(_levels);
        else FakeLevels();

        var bars = _spring.Value > 0.5 ? _fullBars : _miniBars;
        for (int i = 0; i < _levels.Length; i++)
        {
            float target = _levels[i];
            // naik cepat, turun pelan → terasa "nendang" tapi halus
            double k = target > _shown[i] ? 1 - Math.Exp(-step * 30) : 1 - Math.Exp(-step * 9);
            _shown[i] += (float)((target - _shown[i]) * k);
            if (i < bars.Count && bars[i].RenderTransform is ScaleTransform st)
            {
                double v = 0.15 + 0.85 * _shown[i];
                if (Math.Abs(st.ScaleY - v) > 0.004) st.ScaleY = v; // perubahan tak terlihat: jangan gambar ulang
            }
        }
        SetVizInterval();
    }

    void FakeLevels()
    {
        for (int i = 0; i < _levels.Length; i++)
        {
            double v = 0.45
                       + 0.30 * Math.Sin(_vizTime * (5 + i * 1.3) + i)
                       + 0.20 * Math.Sin(_vizTime * (11.7 - i) + i * 2);
            _levels[i] = (float)Math.Clamp(v, 0, 1);
        }
    }

    void PausedBars()
    {
        double[] pattern = { 0.45, 0.75, 0.55, 0.9, 0.5, 0.7, 0.4, 0.8, 0.6, 0.5, 0.7, 0.45 };
        for (int i = 0; i < _miniBars.Count; i++)
        {
            double v = 0.15 + 0.85 * pattern[i % pattern.Length] * 0.7;
            if (_miniBars[i].RenderTransform is ScaleTransform a) a.ScaleY = v;
            if (i < _fullBars.Count && _fullBars[i].RenderTransform is ScaleTransform b) b.ScaleY = v;
        }
    }

    void ResetBars()
    {
        Array.Clear(_shown);
        foreach (var b in _miniBars) if (b.RenderTransform is ScaleTransform st) st.ScaleY = 0.15;
        foreach (var b in _fullBars) if (b.RenderTransform is ScaleTransform st) st.ScaleY = 0.15;
    }

    // =====================================================================
    // Util
    // =====================================================================

    ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var settings = new MenuItem { Header = L.T("Pengaturan") };
        settings.Click += (_, _) => App.OpenSettings();
        var exit = new MenuItem { Header = L.T("Keluar") };
        exit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    static DispatcherTimer OneShot(Action action)
    {
        var t = new DispatcherTimer(DispatcherPriority.Input);
        t.Tick += (_, _) => { t.Stop(); action(); };
        return t;
    }

    static void Start(DispatcherTimer t, int ms)
    {
        t.Stop();
        t.Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms));
        t.Start();
    }

    static Brush MakeFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ---------- Discord di kiri notch ----------

    bool _discordShown;
    DispatcherTimer? _discordTimer;
    double _discordLevel;

    /// <summary>Cek Discord tiap 70 ms saat dalam panggilan (cincin bicara), tiap 1 detik saat tidak.</summary>
    void UpdateDiscordWatch()
    {
        bool want = Settings.Current.DiscordInIsland && !_suspended;
        if (!want)
        {
            _discordTimer?.Stop();
            if (_discordShown) { _discordShown = false; UpdateActivity(); UpdateVisualizerState(); Redraw(); }
            return;
        }
        if (_discordTimer == null)
        {
            _discordTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _discordTimer.Tick += (_, _) => DiscordTick();
        }
        _discordTimer.Start();
    }

    void DiscordTick()
    {
        var (active, peak, exe) = _volume.DiscordState();
        if (active != _discordShown)
        {
            _discordShown = active;
            if (active && exe != null && MiniDiscordIcon.Source == null) MiniDiscordIcon.Source = VolumeService.IconOf(exe);
            // dalam panggilan: cek lebih sering supaya cincin bicara terasa hidup
            _discordTimer!.Interval = active ? TimeSpan.FromMilliseconds(70) : TimeSpan.FromSeconds(1);
            UpdateActivity();
            UpdateVisualizerState();
            Redraw();
        }
        if (!active) return;
        // naik cepat, turun pelan
        double target = Math.Clamp(peak * 4, 0, 1);
        _discordLevel = target > _discordLevel ? target : _discordLevel * 0.8;
        MiniDiscordRing.Opacity = _discordLevel > 0.05 ? 0.35 + 0.65 * _discordLevel : 0;
    }

    /// <summary>Font pilihan pengguna (cadangan: Segoe UI).</summary>
    public static FontFamily UiFont()
    {
        string name = Settings.Current.FontName;
        return new FontFamily((string.IsNullOrWhiteSpace(name) ? "Segoe UI Variable Text" : name) + ", Segoe UI");
    }

    public static Brush ParseBrush(string hex, double opacity = 1)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            c.A = (byte)Math.Round(255 * Math.Clamp(opacity, 0, 1));
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        catch
        {
            var b = new SolidColorBrush(Color.FromRgb(0x5D, 0xCA, 0xA5));
            b.Freeze();
            return b;
        }
    }
}
