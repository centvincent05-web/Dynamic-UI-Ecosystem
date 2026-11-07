using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.UI;

/// <summary>
/// Dock pengganti taskbar (gaya Bloom): menempel di tepi bawah dengan sudut cekung,
/// ikon di dalam kotak bersudut halus, titik di bawah aplikasi yang terbuka, membesar saat hover.
/// Mode "Smart": sembunyi saat ada jendela maximize/fullscreen, muncul saat pointer ke tepi bawah.
/// </summary>
public sealed partial class DockWindow : Window
{
    const double Pad = 10, Gap = 8, Shoulder = 14, DotSpace = 8;

    static readonly Brush TileBg = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)); // kotak Start: kaca tipis
    static readonly Brush DotDim = Frozen(Color.FromRgb(0xB4, 0xB2, 0xA9));

    readonly Canvas _root, _layer;
    readonly Path _shape;
    // kilau kaca: putih sangat tipis dari atas ke bawah, supaya dock tetap terlihat seperti kaca walau wallpaper gelap
    readonly Path _sheen = new()
    {
        IsHitTestVisible = false,
        Fill = new LinearGradientBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 90),
    };
    readonly Path _edge = new() { Stroke = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1, IsHitTestVisible = false };
    readonly StackPanel _tiles;
    readonly SurfaceFx _fx;
    readonly TranslateTransform _slide = new();
    readonly Spring _width = new(), _presence = new() { Value = 1, Target = 1 };
    readonly Func<double, bool> _tick;
    readonly DispatcherTimer _refreshTimer, _hideTimer, _previewTimer, _previewHideTimer;
    readonly DockPreviewWindow _preview = new();
    DockApp? _hoverApp;
    Border? _hoverTile;
    // magnification ala macOS: tiap ikon punya skala & geser sendiri
    readonly List<(Border tile, FrameworkElement wrap, ScaleTransform scale, TranslateTransform lift)> _tileFx = new();

    double _winW, _winH, _tile, _dockH, _shapeAlpha = -1;
    string _menuStyleSig = "";
    bool _tucked, _edgeRevealed;
    int _idleTicks;
    string _signature = "";

    public DockWindow()
    {
        _tick = Tick;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        Title = "Dock";

        _root = new Canvas { RenderTransform = _slide };
        _shape = new Path();
        _layer = new Canvas();
        _tiles = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        _tiles.MouseMove += (_, e) => { if (Settings.Current.DockMagnify) Magnify(e.GetPosition(_tiles).X); };
        _tiles.MouseLeave += (_, _) => { if (Settings.Current.DockMagnify) ResetMagnify(); };
        _layer.Children.Add(_tiles);
        _root.Children.Add(_shape);
        _root.Children.Add(_sheen);
        // cahaya pointer & ambient light (ikut pengaturan island; bawaan mati untuk dock)
        _fx = new SurfaceFx(_root,
            () => Settings.Current.HoverSpotlight && Settings.Current.SpotlightDock,
            () => Settings.Current.AmbientLight && Settings.Current.AmbientDock);
        _root.Children.Add(_fx.Layer);
        _root.Children.Add(_layer);
        _root.Children.Add(_edge); // garis tepi tipis ala macOS
        Content = _root;
        Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);
        InitMenu(); // menu dock (memanjang dari dock, lihat DockWindow.Menu.cs)

        // pemindaian cadangan (jarang): perubahan jendela biasanya sudah dikabarkan Windows lewat DockWindow.Events.cs
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) =>
        {
            TaskbarService.Reapply();
            if (!IsTucked) Refresh(); // dock tersembunyi: tidak perlu memindai jendela
        };
        _hideTimer = new DispatcherTimer(DispatcherPriority.Input);
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (_menuOpen || _root.IsMouseOver || (_preview.IsVisible && _preview.IsMouseOver)) { StartTimer(_hideTimer, 700); return; }
            _edgeRevealed = false;
            ApplyPresence();
        };

        // preview jendela: muncul setelah hover sebentar di ikon
        _previewTimer = new DispatcherTimer(DispatcherPriority.Input);
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); ShowPreview(); };
        _previewHideTimer = new DispatcherTimer(DispatcherPriority.Input);
        _previewHideTimer.Tick += (_, _) =>
        {
            _previewHideTimer.Stop();
            if (_preview.IsMouseOver || (_hoverTile?.IsMouseOver ?? false)) return;
            _preview.Hide();
        };
        _preview.MouseEnter += (_, _) => _previewHideTimer.Stop();
        _preview.MouseLeave += (_, _) => StartTimer(_previewHideTimer, 250);
        _preview.DockChanged += () => { _signature = ""; Refresh(); };

        _root.MouseEnter += (_, _) => _hideTimer.Stop();
        _root.MouseLeave += (_, _) => { if (_edgeRevealed) StartTimer(_hideTimer, 700); };
        _root.ContextMenu = BuildBackgroundMenu();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd);
            // explorer restart → taskbar muncul lagi → sembunyikan lagi
            uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if ((uint)msg == taskbarCreated)
                    Dispatcher.BeginInvoke(new Action(TaskbarService.Reapply), DispatcherPriority.Background);
                return IntPtr.Zero;
            });
        };

        Settings.Changed += ApplySettings;
        ApplySettings();
        Refresh();
        _refreshTimer.Start();
        StartWindowEvents();
    }

    // =====================================================================

    void ApplySettings()
    {
        var s = Settings.Current;
        _tile = s.DockIconSize;
        _dockH = Pad + _tile + DotSpace + Pad * 0.4;
        _winW = Math.Min(SystemParameters.PrimaryScreenWidth, 1400);
        if (!s.DockMenu) CloseMenu();
        UpdateWindowBounds(); // tinggi jendela ikut menu dock (terbuka / tertutup)
        UpdateStartKeyHook();
        // gaya / warna ikon menu berubah → susun ulang (hanya kalau memang berubah; Changed bisa sering terpanggil)
        string menuSig = $"{s.DockIconShape}|{s.IconColorMode}|{s.IconTintColor}|{s.MenuIconsLikeDock}|{s.Language}";
        if (menuSig != _menuStyleSig)
        {
            _menuStyleSig = menuSig;
            _allAppsCache = null;
            if (_menuOpen) RebuildMenuApps();
        }
        _shapeAlpha = -1; // warna (sama dengan island) diterapkan di Redraw, termasuk versi pekat saat menu terbuka
        _edge.Visibility = s.EdgeHighlight ? Visibility.Visible : Visibility.Collapsed;
        FontFamily = IslandWindow.UiFont();
        _signature = ""; // paksa susun ulang
        Redraw();
        Refresh();
    }

    /// <summary>Lebar dock (px) untuk zona pemicu di tepi bawah.</summary>
    public double DockWidth => _width.Value;

    // ---------- isi dock ----------

    bool _scanning, _rescan;

    /// <summary>Pindai jendela di thread latar (bisa lambat), lalu susun ikon di thread tampilan.</summary>
    void Refresh()
    {
        if (!IsVisible) return;
        if (_scanning) { _rescan = true; return; } // ada perubahan selama memindai: pindai sekali lagi setelahnya
        _scanning = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            List<DockApp>? apps = null;
            try { apps = WindowTracker.Snapshot(); } catch { }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _scanning = false;
                if (apps != null) Apply(apps);
                if (_rescan) { _rescan = false; Refresh(); }
            }), DispatcherPriority.Background);
        });
    }

    void Apply(List<DockApp> apps)
    {
        if (!IsVisible) return;
        RememberWindows(apps); // jendela mana saja yang ada di dock (untuk kabar "jendela ditutup" dari Windows)

        // susun ulang hanya kalau isinya berubah
        string sig = string.Join("|", apps.Select(a => a.Key + ":" + a.Windows.Count + ":" + (a.IsForeground ? 1 : 0)));
        if (sig == _signature) return;
        _signature = sig;

        _tiles.Children.Clear();
        _tileFx.Clear();
        _tiles.Children.Add(BuildStartTile());
        bool separated = false;
        double extra = 0;
        foreach (var app in apps)
        {
            // garis pemisah tipis antara aplikasi yang disematkan dan yang sekadar terbuka
            if (!app.Pinned && !separated && apps.Exists(a => a.Pinned))
            {
                _tiles.Children.Add(new Border
                {
                    Width = 1.5, Height = _tile * 0.6, Background = SeparatorBrush, CornerRadius = new CornerRadius(1),
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, _tile * 0.2, Gap, 0),
                });
                extra = 1.5 + Gap;
                separated = true;
            }
            _tiles.Children.Add(BuildTile(app));
        }

        // preview yang sedang terbuka ikut diperbarui
        if (_preview.IsVisible && _preview.CurrentApp != null)
        {
            var same = apps.Find(a => a.Key == _preview.CurrentApp.Key);
            if (same == null) _preview.Hide();
        }

        int count = apps.Count + 1;
        double w = Pad * 2 + count * _tile + (count - 1) * Gap + extra;
        _width.Target = w;
        if (_width.Value <= 0) { _width.Value = w; _width.Velocity = 0; } // pertama kali: tanpa animasi
        FrameLoop.Add(_tick);
        Redraw();
    }

    FrameworkElement BuildStartTile()
    {
        var text = new TextBlock
        {
            Text = "⊞", FontSize = _tile * 0.5, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, _tile * 0.06),
        };
        var tile = MakeTile(text, Settings.Current.DockMenu ? L.T("Menu") : "Start");
        ApplyShape(tile, text, null);
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (Settings.Current.DockMenu) ToggleMenu(); // dock memanjang jadi menu
            else WindowTracker.OpenStart();               // perilaku lama: Start Windows
        };
        return Wrap(tile, 0, false);
    }

    FrameworkElement BuildTile(DockApp app)
    {
        // ikon resolusi tinggi memenuhi seluruh kotak; bagian transparan diisi warna dominan ikon
        string? path = app.IconPath ?? app.LaunchPath ?? app.ExePath; // profil Chrome: ikon dengan avatar akun
        var info = path != null ? IconLoader.Get(path, 96) : new IconLoader.IconInfo(null, Color.FromRgb(0x2C, 0x2C, 0x2E));
        info = new IconLoader.IconInfo(IconStyle.Apply(info.Image), IconStyle.ApplyColor(info.Dominant)); // warna ikon: asli / monokrom / pilihan
        FrameworkElement content = info.Image != null
            ? new Image { Source = info.Image, Width = _tile, Height = _tile, Stretch = Stretch.Uniform }
            : new TextBlock
            {
                Text = app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "?", FontSize = _tile * 0.42,
                FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        RenderOptions.SetBitmapScalingMode(content, BitmapScalingMode.HighQuality);

        var tile = MakeTile(content, app.Profile != "Default" ? app.Name : null);
        ApplyShape(tile, content, info.Dominant);
        tile.MouseEnter += (_, _) =>
        {
            _hoverApp = app;
            _hoverTile = tile;
            _previewHideTimer.Stop();
            // preview sudah terbuka untuk ikon lain: pindah langsung tanpa menunggu
            if (_preview.IsVisible) ShowPreview();
            else StartTimer(_previewTimer, Settings.Current.DockPreviewDelayMs);
        };
        tile.MouseLeave += (_, _) =>
        {
            _previewTimer.Stop();
            if (_preview.IsVisible) StartTimer(_previewHideTimer, 250);
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            _previewTimer.Stop();
            _preview.Hide();
            if (!app.IsRunning && Settings.Current.DockMagnify) Bounce(tile); // memantul saat aplikasi dibuka
            WindowTracker.Activate(app);
            e.Handled = true;
            Dispatcher.BeginInvoke(new Action(Refresh));
        };
        tile.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) { WindowTracker.Launch(app); e.Handled = true; }
        };
        tile.ContextMenu = BuildAppMenu(app);
        return Wrap(tile, app.Windows.Count, app.IsForeground);
    }

    /// <summary>
    /// Bentuk ikon (Pengaturan → Dock): 0 = kotak membulat dengan warna ikon (bawaan), 1 = lingkaran,
    /// 2 = kaca (latar tembus, ikon sedikit lebih kecil), 3 = ikon asli tanpa kotak.
    /// </summary>
    void ApplyShape(Border tile, FrameworkElement content, Color? dominant) => ApplyShape(tile, content, dominant, _tile);

    /// <summary>Sama seperti di atas, untuk ukuran ikon apa saja (dipakai juga oleh menu dock).</summary>
    static void ApplyShape(Border tile, FrameworkElement content, Color? dominant, double t)
    {
        int shape = Settings.Current.DockIconShape;
        bool image = content is Image;
        Brush colored = TileBg;
        if (dominant is Color dom)
        {
            var d = Darken(dom, 0.55);
            colored = Frozen(Color.FromArgb(0xD0, d.R, d.G, d.B)); // sedikit tembus, ikut nuansa kaca
        }
        switch (shape)
        {
            case 1: // lingkaran
                tile.CornerRadius = new CornerRadius(t / 2);
                tile.Background = colored;
                tile.Clip = new EllipseGeometry(new Point(t / 2, t / 2), t / 2, t / 2);
                if (image) { content.Width = content.Height = t * 0.78; }
                break;
            case 2: // kaca
                tile.CornerRadius = new CornerRadius(t * 0.28);
                tile.Background = TileBg;
                tile.Clip = new RectangleGeometry(new Rect(0, 0, t, t), t * 0.28, t * 0.28);
                if (image) { content.Width = content.Height = t * 0.7; }
                break;
            case 3: // ikon asli
                tile.Background = dominant == null ? TileBg : Brushes.Transparent;
                tile.Clip = null;
                if (image) { content.Width = content.Height = t; }
                break;
            default: // kotak membulat (bawaan)
                tile.CornerRadius = new CornerRadius(t * 0.26);
                tile.Background = colored;
                tile.Clip = new RectangleGeometry(new Rect(0, 0, t, t), t * 0.26, t * 0.26);
                break;
        }
        if (image)
        {
            content.HorizontalAlignment = HorizontalAlignment.Center;
            content.VerticalAlignment = VerticalAlignment.Center;
        }
    }

    Border MakeTile(UIElement content, string? tooltip)
    {
        var scale = new ScaleTransform(1, 1);
        var lift = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(lift);
        var tile = new Border
        {
            Width = _tile, Height = _tile,
            CornerRadius = new CornerRadius(_tile * 0.26), // kotak dengan sudut melengkung halus
            Background = TileBg, Child = content, Cursor = Cursors.Hand, ToolTip = tooltip,
            RenderTransformOrigin = new Point(0.5, 1), RenderTransform = group,
        };
        ToolTipService.SetInitialShowDelay(tile, 350);
        tile.MouseEnter += (_, _) => { if (!Settings.Current.DockMagnify) Animate(scale, 1.2); };
        tile.MouseLeave += (_, _) => { if (!Settings.Current.DockMagnify) Animate(scale, 1.0); };
        _pendingFx = (tile, scale, lift);
        return tile;
    }

    (Border tile, ScaleTransform scale, TranslateTransform lift)? _pendingFx;

    FrameworkElement Wrap(Border tile, int windows, bool foreground)
    {
        var grid = new Grid { Width = _tile, Height = _tile + DotSpace, Margin = new Thickness(0, 0, Gap, 0) };
        if (_pendingFx is { } fx && fx.tile == tile) _tileFx.Add((tile, grid, fx.scale, fx.lift));
        _pendingFx = null;
        tile.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(tile);
        if (windows > 0)
        {
            // satu titik per jendela, maksimal 3 supaya tidak sesak; putih kalau aplikasinya sedang aktif
            var dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 1),
            };
            int n = Math.Min(windows, 3);
            for (int i = 0; i < n; i++)
                dots.Children.Add(new Ellipse
                {
                    Width = 4, Height = 4, Fill = foreground ? Brushes.White : DotDim,
                    Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0),
                });
            grid.Children.Add(dots);
        }
        return grid;
    }

    void ShowPreview()
    {
        if (_hoverApp == null || _hoverTile == null || !_hoverTile.IsMouseOver) return;
        // tengah ikon dan atas dock, dalam DIP layar
        Point p = _hoverTile.PointToScreen(new Point(_tile / 2, 0));
        double x = p.X / App.DpiScale;
        double dockTop = Top + (_winH - _dockH);
        _preview.ShowFor(_hoverApp, x, dockTop);
    }

    static readonly Brush SeparatorBrush = Frozen(Color.FromRgb(0x48, 0x48, 0x4A));

    static Color Darken(Color c, double f) =>
        Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    /// <summary>Ikon di dekat pointer membesar, makin jauh makin kecil (kurva halus seperti macOS).</summary>
    // pembesaran halus: tiap ikon punya pegas sendiri; nilai diterapkan per frame
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScaleTransform, Spring> _magSprings = new();
    Func<double, bool>? _magTick;

    Spring MagSpring(ScaleTransform st)
    {
        if (!_magSprings.TryGetValue(st, out var sp))
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            sp = new Spring { Value = st.ScaleX, Target = st.ScaleX };
            _magSprings.Add(st, sp);
        }
        return sp;
    }

    void StartMagTick()
    {
        _magTick ??= dt =>
        {
            var s = Settings.Current;
            double k = s.AnimStiffness * 2.2, c = s.AnimDamping * 1.5;
            bool moving = false;
            foreach (var (_, _, scale, _) in _tileFx)
            {
                var sp = MagSpring(scale);
                moving |= sp.Step(dt, k, c);
                scale.ScaleX = scale.ScaleY = sp.Value;
            }
            return moving;
        };
        FrameLoop.Add(_magTick);
    }

    void Magnify(double mouseX)
    {
        if (Settings.Current.DockMagnifySmooth)
        {
            double rng = _tile * 2.0;
            foreach (var (_, wrap, scale, _) in _tileFx)
            {
                double cx = wrap.TranslatePoint(new Point(_tile / 2, 0), _tiles).X;
                double d = Math.Abs(mouseX - cx) / rng;
                double f = d >= 1 ? 0 : (1 + Math.Cos(Math.PI * d)) / 2;
                MagSpring(scale).Target = 1 + 0.16 * f;
                Panel.SetZIndex(wrap, (int)(f * 100));
            }
            StartMagTick();
            return;
        }
        double range = _tile * 2.0;
        foreach (var (tile, wrap, scale, _) in _tileFx)
        {
            double cx = wrap.TranslatePoint(new Point(_tile / 2, 0), _tiles).X;
            double d = Math.Abs(mouseX - cx) / range;
            double f = d >= 1 ? 0 : (1 + Math.Cos(Math.PI * d)) / 2;
            double v = 1 + 0.16 * f; // zoom kecil saja supaya tidak terlalu besar
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = v;
            Panel.SetZIndex(wrap, (int)(f * 100));
        }
    }

    void ResetMagnify()
    {
        if (Settings.Current.DockMagnifySmooth)
        {
            foreach (var (_, wrap, scale, _) in _tileFx) { MagSpring(scale).Target = 1; Panel.SetZIndex(wrap, 0); }
            StartMagTick();
            return;
        }
        foreach (var (_, wrap, scale, _) in _tileFx)
        {
            Animate(scale, 1.0);
            Panel.SetZIndex(wrap, 0);
        }
    }

    /// <summary>Ikon memantul beberapa kali saat aplikasinya sedang dibuka.</summary>
    void Bounce(Border tile)
    {
        foreach (var fx in _tileFx)
        {
            if (fx.tile != tile) continue;
            if (Settings.Current.DockBounceRealistic)
            {
                fx.lift.BeginAnimation(TranslateTransform.YProperty, RealisticBounce(_tile * 0.32));
                continue;
            }
            var a = new DoubleAnimation(0, -_tile * 0.28, TimeSpan.FromMilliseconds(260))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(3),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            fx.lift.BeginAnimation(TranslateTransform.YProperty, a);
        }
    }

    /// <summary>Seperti bola jatuh: naik cepat melambat, turun makin cepat; tiap pantulan lebih rendah & lebih singkat.</summary>
    static DoubleAnimationUsingKeyFrames RealisticBounce(double height)
    {
        var kf = new DoubleAnimationUsingKeyFrames();
        var up = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var down = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        double t = 0, h = height;
        for (int i = 0; i < 4 && h > 1.5; i++)
        {
            double half = 0.30 * Math.Sqrt(h / height); // waktu naik ∝ √tinggi (gravitasi)
            t += half; kf.KeyFrames.Add(new EasingDoubleKeyFrame(-h, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t)), up));
            t += half; kf.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t)), down));
            h *= i == 0 ? 0.5 : 0.4;
        }
        return kf;
    }

    static void Animate(ScaleTransform st, double to)
    {
        var ease = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    ContextMenu BuildAppMenu(DockApp app)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = L.T("Buka jendela baru") };
        open.Click += (_, _) => WindowTracker.Launch(app);
        menu.Items.Add(open);

        var pin = new MenuItem { Header = app.Pinned ? L.T("Lepas dari dock") : L.T("Sematkan ke dock") };
        pin.Click += (_, _) =>
        {
            if (app.Pinned) WindowTracker.Unpin(app); else WindowTracker.Pin(app);
            _signature = "";
            Refresh();
        };
        menu.Items.Add(pin);

        if (app.IsRunning)
        {
            menu.Items.Add(new Separator());
            var close = new MenuItem { Header = app.Windows.Count > 1 ? L.T("Tutup semua jendela") : L.T("Tutup jendela") };
            close.Click += (_, _) => WindowTracker.CloseAll(app);
            menu.Items.Add(close);
        }
        return menu;
    }

    ContextMenu BuildBackgroundMenu()
    {
        var menu = new ContextMenu();
        var tray = new MenuItem { Header = L.T("Tampilkan taskbar Windows sebentar (system tray)") };
        tray.Click += (_, _) => TaskbarService.PeekOriginal(TimeSpan.FromSeconds(10));
        var settings = new MenuItem { Header = L.T("Pengaturan") };
        settings.Click += (_, _) => App.OpenSettings();
        menu.Items.Add(tray);
        menu.Items.Add(settings);
        return menu;
    }

    // ---------- bentuk & animasi ----------

    bool Tick(double dt)
    {
        var s = Settings.Current;
        bool a = _width.Step(dt, s.AnimStiffness, s.AnimDamping);
        var (rk, rc) = s.RevealSpring;
        bool b = _presence.Step(dt, rk, rc);
        bool c = _menu.Step(dt, s.AnimStiffness, s.AnimDamping);
        if (!c && !_menuOpen && _menu.Value == 0 && _winH > _baseWinH + 0.5)
        {
            UpdateWindowBounds(); // menu selesai menutup: kecilkan jendela lagi
            ReleaseMenuContent();
        }
        else Redraw();
        return a || b || c;
    }

    void Redraw()
    {
        double dockW = Math.Max(_width.Value, _tile + Pad * 2);
        // menu dock: bentuk yang sama memanjang ke atas & melebar (pegas boleh sedikit melewati batas → memantul)
        double open = Math.Clamp(_menu.Value, -0.05, 1.08);
        double w = MathEx.Lerp(dockW, Math.Max(_menuW, dockW), open);
        double h = Math.Max(_dockH, MathEx.Lerp(_dockH, _dockH + MenuHeight, open));
        double r = MathEx.Lerp(Math.Min(_tile * 0.45, 22), 26, MathEx.Clamp01(open));
        // menu terbuka: latar makin pekat supaya jendela di belakangnya tidak ikut terlihat dan isi menu mudah dibaca
        var st = Settings.Current;
        double alpha = MathEx.Lerp(st.IslandOpacity, Math.Max(st.IslandOpacity, 0.95), MathEx.Clamp01(open));
        alpha = Math.Round(alpha, 2);
        if (Math.Abs(alpha - _shapeAlpha) > 0.001) { _shapeAlpha = alpha; _shape.Fill = IslandWindow.ParseBrush(st.IslandColor, alpha); }
        // bentuk notch dibalik vertikal supaya menempel ke tepi bawah
        var flip = new MatrixTransform(1, 0, 0, -1, 0, _winH);
        var geometry = NotchGeometry.Build(_winW / 2, w, h, Shoulder, r, flip);
        _shape.Data = geometry;
        _sheen.Data = geometry;
        _fx.SetShape(geometry, _winW, _winH);
        if (Settings.Current.EdgeHighlight)
            _edge.Data = NotchGeometry.Build(_winW / 2, w, h, Shoulder, r, flip, closed: false);
        if (_menuPanel != null) LayoutMenu(w, h, open);

        Canvas.SetLeft(_tiles, _winW / 2 - dockW / 2 + Pad);
        Canvas.SetTop(_tiles, _winH - _dockH + Pad);

        _slide.Y = (h + Shoulder + 6) * (1 - MathEx.Clamp01(_presence.Value));
        _root.IsHitTestVisible = _presence.Target > 0;
    }

    // ---------- sembunyi / muncul (mode Smart) ----------

    public bool IsTucked => _tucked && !_edgeRevealed && !_menuOpen;

    bool _suspended;

    /// <summary>Mode game: berhenti memindai jendela dan sembunyikan dock sepenuhnya.</summary>
    public void SetSuspended(bool on)
    {
        _suspended = on;
        UpdateStartKeyHook(); // mode game: tombol Windows kembali normal
        if (on)
        {
            CloseMenu();
            StopWindowEvents();
            _refreshTimer.Stop();
            _previewTimer.Stop();
            _preview.Hide();
            Hide();
        }
        else
        {
            Show();
            _signature = "";
            Refresh();
            _refreshTimer.Start();
            StartWindowEvents();
        }
    }

    /// <summary>Dipanggil saat kondisi layar berubah.</summary>
    public void OnScreenState(bool fullscreen, bool maximized)
    {
        var s = Settings.Current;
        bool was = _tucked;
        _tucked = fullscreen || (s.DockSmart && maximized);
        if (!_tucked) _edgeRevealed = false;
        ApplyPresence();
        if (was && !_tucked) Refresh(); // muncul lagi: segarkan isi yang mungkin berubah selama tersembunyi
    }

    /// <summary>Pointer di tepi bawah saat dock tersembunyi.</summary>
    public void RevealFromEdge()
    {
        if (!_tucked || _suspended) return;
        Native.BringToTopmost(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        _edgeRevealed = true;
        ApplyPresence();
        Refresh();
        StartTimer(_hideTimer, 1500);
    }

    void ApplyPresence()
    {
        if (IsTucked) _preview.Hide();
        _presence.Target = IsTucked ? 0 : 1;
        FrameLoop.Add(_tick);
    }

    static void StartTimer(DispatcherTimer t, int ms)
    {
        t.Stop();
        t.Interval = TimeSpan.FromMilliseconds(ms);
        t.Start();
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
