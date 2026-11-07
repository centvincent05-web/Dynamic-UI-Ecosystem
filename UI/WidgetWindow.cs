using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.UI;

/// <summary>
/// Satu widget desktop: jam, cuaca, musik, atau performa. Jendela kecil transparan yang selalu berada di
/// paling bawah (di atas wallpaper, di bawah semua aplikasi), tidak pernah mengambil fokus.
/// Hanya digambar ulang saat datanya berubah; berhenti total saat tertutup jendela maximize atau mode game.
/// </summary>
public sealed class WidgetWindow : Window
{
    public const double Cell = 158, Gap = 12, EdgeMargin = 24, GridStep = 12;

    static readonly FontFamily TextFont = new("Segoe UI Variable Display, Segoe UI");
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly FontFamily EmojiFont = new("Segoe UI Emoji");
    static readonly Brush White = Brushes.White;
    static readonly Brush SecWhite = Frozen(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    static readonly Brush FaintWhite = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    static readonly Brush DarkText = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E));
    static readonly Brush SecDark = Frozen(Color.FromArgb(0xB3, 0x1C, 0x1C, 0x1E));
    static readonly Brush FaintDark = Frozen(Color.FromArgb(0x33, 0x1C, 0x1C, 0x1E));
    // warna teks sekunder & garis tipis: putih di latar gelap, gelap di latar terang (dipilih otomatis)
    Brush _sec = SecWhite, _faint = FaintWhite;
    Brush Secondary => _sec;
    Brush Faint => _faint;

    WidgetConfig _cfg;
    string _builtKey = "";
    IntPtr _hwnd, _monitor;
    readonly Grid _host = new();
    Rectangle? _editFrame;
    Action? _refresh;
    DispatcherTimer? _timer;
    readonly List<Action> _unsubscribe = new();
    bool _paused, _covered, _dragging, _typing;
    Brush _fg = Brushes.White;
    Border? _glow;
    Action? _endTyping;

    /// <summary>Ruang di sekeliling kartu untuk cahaya ambient (0 kalau cahaya mati).</summary>
    const double GlowPad = 30;
    double Pad => _cfg.Ambient != 0 ? GlowPad : 0;
    Native.POINT _dragStart;
    Native.RECT _dragRect;

    public IntPtr Handle => _hwnd;

    public static (double w, double h) SizeOf(WidgetConfig c) => c.Size switch
    {
        1 => (Cell * 2 + Gap, Cell),      // sedang
        2 => (Cell * 2 + Gap, Cell * 2 + Gap), // besar
        3 => (Cell * 3 + Gap * 2, Cell),  // lebar
        _ => (Cell, Cell),                // kecil
    };

    public WidgetWindow(WidgetConfig cfg)
    {
        _cfg = cfg;
        Title = "Dynamic Island widget";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        Focusable = false;
        Left = -32000;
        Top = -32000;
        FontFamily = TextFont;
        Foreground = White;
        Content = _host;
        UseLayoutRounding = true;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd); // tidak muncul di Alt+Tab, tidak mencuri fokus
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        };
        Closed += (_, _) => Teardown();
        Deactivated += (_, _) => _endTyping?.Invoke(); // klik di luar catatan = selesai mengetik

        MouseLeftButtonDown += OnDragStart;
        MouseMove += OnDragMove;
        MouseLeftButtonUp += OnDragEnd;
        ContextMenu = BuildMenu();

        Build();
    }

    // =====================================================================
    // Posisi & urutan z
    // =====================================================================

    /// <summary>Pengaturan berubah: bangun ulang kalau tampilannya berubah, lalu pasang di posisinya.</summary>
    public void Update(WidgetConfig cfg)
    {
        _cfg = cfg;
        if (BuildKey() != _builtKey) Build();
        else if (_cfg.Kind == 4 && !_typing) SafeRefresh(); // isi catatan diubah dari Pengaturan
        if (!_dragging) Reposition();
    }

    string BuildKey() => _cfg.LookKey() + "|" + EffectiveColor() + "|" + L.English + "|" + Settings.Current.Use24Hour + "|" + Settings.Current.FontName + "|" + WidgetManager.K.ToString("0.000")
        + "|" + (_cfg.BgMode == 1 ? WallpaperService.WallColor?.ToString() : "");

    public void Reposition()
    {
        if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
        var mon = Monitors.Get(_cfg.Monitor);
        _monitor = mon.Handle;
        var (_, _, step, left, top) = WidgetManager.Grid(mon);
        double k = WidgetManager.K;
        double xd = left + Math.Max(0, _cfg.Col) * step, yd = top + Math.Max(0, _cfg.Row) * step;
        // jendela lebih besar dari kartu sebesar Pad (ruang cahaya) → geser supaya kartunya pas di sel grid
        int x = mon.Work.Left + (int)Math.Round((xd - Pad * k) * mon.Scale), y = mon.Work.Top + (int)Math.Round((yd - Pad * k) * mon.Scale);
        // geser beranimasi (opsi): hanya kalau sudah tampil di monitor yang sama
        bool animate = Settings.Current.WidgetAnimateMove && _placedOn == mon.Handle && IsVisible && !_dragging;
        _placedOn = mon.Handle;
        if (animate && Native.GetWindowRect(_hwnd, out var cur))
        {
            if (cur.Left == x && cur.Top == y && _moveX.IsSettled) return;
            if (!_moving) { _moveX.Value = cur.Left; _moveY.Value = cur.Top; _moveX.Velocity = _moveY.Velocity = 0; }
            _moveX.Target = x; _moveY.Target = y;
            _moveTick ??= MoveTick;
            _moving = true;
            FrameLoop.Add(_moveTick);
            return;
        }
        if (_moveTick != null) FrameLoop.Remove(_moveTick);
        _moving = false;
        _moveX.Value = _moveX.Target = x; _moveY.Value = _moveY.Target = y; _moveX.Velocity = _moveY.Velocity = 0;
        SetWindowPos(_hwnd, ZAfter(), x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        // pindah ke monitor dengan DPI lain: WPF mengubah ukuran jendela dulu → pasang posisinya sekali lagi
        Dispatcher.BeginInvoke(new Action(() => SetWindowPos(_hwnd, ZAfter(), x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE)),
            DispatcherPriority.Background);
    }

    IntPtr _placedOn;
    readonly Spring _moveX = new(), _moveY = new();
    Func<double, bool>? _moveTick;
    bool _moving;

    bool MoveTick(double dt)
    {
        if (_dragging || _hwnd == IntPtr.Zero) { _moving = false; return false; }
        var s = Settings.Current;
        double k = s.AnimStiffness * 1.2, c = s.AnimDamping * 1.1;
        bool a = _moveX.Step(dt, k, c), b = _moveY.Step(dt, k, c);
        SetWindowPos(_hwnd, ZAfter(), (int)Math.Round(_moveX.Value), (int)Math.Round(_moveY.Value), 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        _moving = a || b;
        return _moving;
    }

    IntPtr ZAfter() => WidgetManager.Raised || _typing ? HWND_TOP : HWND_BOTTOM;

    public void ApplyZOrder()
    {
        if (_hwnd != IntPtr.Zero) SetWindowPos(_hwnd, ZAfter(), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING:
                // selalu kembali ke paling bawah, kecuali saat desktop di depan (Win+D) atau mode edit
                if (!WidgetManager.Raised && !_typing)
                {
                    var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    if ((wp.flags & SWP_NOZORDER) == 0 && wp.hwndInsertAfter != HWND_BOTTOM)
                    {
                        wp.hwndInsertAfter = HWND_BOTTOM;
                        Marshal.StructureToPtr(wp, lParam, false);
                    }
                }
                break;
            case WM_MOUSEACTIVATE:
                if (_typing) break; // sedang mengetik catatan: boleh aktif
                handled = true;
                return (IntPtr)MA_NOACTIVATE;
        }
        return IntPtr.Zero;
    }

    // =====================================================================
    // Jeda (tertutup / mode game)
    // =====================================================================

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (!paused) SafeRefresh();
    }

    /// <summary>Dipanggil saat jendela aktif berganti: kalau tadinya tertutup dan sekarang terlihat, segarkan.</summary>
    public void CheckCovered()
    {
        bool c = _monitor != IntPtr.Zero && WidgetManager.IsMonitorCovered(_monitor);
        if (c == _covered) return;
        _covered = c;
        if (!c) SafeRefresh();
    }

    bool Idle => _paused || !IsVisible || (_covered = _monitor != IntPtr.Zero && WidgetManager.IsMonitorCovered(_monitor));

    void SafeRefresh()
    {
        try { _refresh?.Invoke(); } catch (Exception ex) { CrashGuard.Log(ex, "widget"); }
    }

    void StartTimer(TimeSpan interval, Action tick)
    {
        _timer?.Stop();
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = interval };
        _timer.Tick += (_, _) => { if (!Idle) tick(); };
        _timer.Start();
    }

    void Teardown()
    {
        _endTyping?.Invoke();
        _endTyping = null;
        _timer?.Stop();
        _timer = null;
        foreach (var u in _unsubscribe) { try { u(); } catch { } }
        _unsubscribe.Clear();
    }

    // =====================================================================
    // Mode edit: geser dengan grid
    // =====================================================================

    public void OnEditModeChanged()
    {
        if (_editFrame != null) _editFrame.Visibility = WidgetManager.EditMode ? Visibility.Visible : Visibility.Collapsed;
        Cursor = WidgetManager.EditMode ? Cursors.SizeAll : null;
        ContextMenu = BuildMenu();
    }

    void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (!WidgetManager.EditMode) return;
        Native.GetCursorPos(out _dragStart);
        Native.GetWindowRect(_hwnd, out _dragRect);
        _dragging = true;
        _lastCell = (-1, -1, -1);
        CaptureMouse();
        e.Handled = true;
    }

    (int mon, int col, int row) _lastCell = (-1, -1, -1);

    /// <summary>Sel grid terdekat dengan posisi kartu sekarang (monitor = tempat titik tengah kartu berada).</summary>
    (int mon, int col, int row) CellUnderWindow()
    {
        Native.GetWindowRect(_hwnd, out var r);
        var center = new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        int idx = Monitors.IndexOf(Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTONEAREST));
        var mon = Monitors.Get(idx);
        var (cols, rows, step, left, top) = WidgetManager.Grid(mon);
        var (sc, sr) = WidgetManager.Span(_cfg);
        double k = WidgetManager.K;
        double xd = (r.Left - mon.Work.Left) / mon.Scale + Pad * k - left, yd = (r.Top - mon.Work.Top) / mon.Scale + Pad * k - top;
        int col = Math.Clamp((int)Math.Round(xd / step), 0, Math.Max(0, cols - sc));
        int row = Math.Clamp((int)Math.Round(yd / step), 0, Math.Max(0, rows - sr));
        return (idx, col, row);
    }

    void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Native.GetCursorPos(out var p);
        SetWindowPos(_hwnd, IntPtr.Zero, _dragRect.Left + p.X - _dragStart.X, _dragRect.Top + p.Y - _dragStart.Y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        // widget lain langsung menyingkir saat sel tujuan berubah (seperti macOS)
        var cell = CellUnderWindow();
        if (cell == _lastCell) return;
        _lastCell = cell;
        WidgetManager.DragTo(this, _cfg, cell.mon, cell.col, cell.row);
    }

    void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
        var cell = CellUnderWindow();
        WidgetManager.DragTo(this, _cfg, cell.mon, cell.col, cell.row);
        _lastCell = (-1, -1, -1);
        Settings.Save();
        Reposition(); // menempel ke sel
    }

    ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        var edit = new MenuItem { Header = WidgetManager.EditMode ? L.T("Selesai mengatur posisi") : L.T("Atur posisi widget") };
        edit.Click += (_, _) => WidgetManager.SetEditMode(!WidgetManager.EditMode);
        var settings = new MenuItem { Header = L.T("Pengaturan widget") };
        settings.Click += (_, _) => App.OpenSettings();
        var hide = new MenuItem { Header = L.T("Sembunyikan widget ini") };
        hide.Click += (_, _) =>
        {
            _cfg.Visible = false;
            Settings.NotifyChanged();
            Settings.Save();
        };
        m.Items.Add(edit);
        m.Items.Add(settings);
        m.Items.Add(new Separator());
        m.Items.Add(hide);
        return m;
    }

    // =====================================================================
    // Tampilan
    // =====================================================================

    Color EffectiveColor()
    {
        Color Parse(string hex, Color fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
        }
        var accent = Parse(Settings.Current.AccentColor, Color.FromRgb(0x5D, 0xCA, 0xA5));
        return _cfg.ColorMode switch
        {
            1 => WallpaperService.WallColor ?? accent,
            2 => Parse(_cfg.Color, accent),
            _ => accent,
        };
    }

    void Build()
    {
        Teardown();
        // langganan warna wallpaper dipasang lagi (Teardown melepas semua)
        Action onWall = () => { if (_cfg.ColorMode == 1 || _cfg.BgMode == 1) Update(_cfg); };
        WallpaperService.Changed += onWall;
        _unsubscribe.Add(() => WallpaperService.Changed -= onWall);

        _builtKey = BuildKey();
        var (w, h) = SizeOf(_cfg);
        double pad0 = Pad, k = WidgetManager.K;
        // ukuran grid diubah → seluruh isi (teks, gambar, sudut, jarak) ikut diskalakan, seperti widget iPhone
        _host.LayoutTransform = Math.Abs(k - 1) < 0.001 ? Transform.Identity : new ScaleTransform(k, k);
        Width = (w + 2 * pad0) * k;
        Height = (h + 2 * pad0) * k;
        // font: pilihan widget, atau ikut font app
        string fontName = _cfg.Font.Length > 0 ? _cfg.Font : Settings.Current.FontName;
        try { FontFamily = new FontFamily(fontName + ", Segoe UI"); } catch { FontFamily = TextFont; }
        bool square = _cfg.Size is 0 or 2;
        bool circle = _cfg.Shape == 1 && square;
        double radius = _cfg.Shape switch
        {
            1 => square ? w / 2 : h / 2,
            2 => h / 2,
            _ => _cfg.Size == 2 ? 30 : 24,
        };
        var accent = EffectiveColor();
        var accentBrush = Frozen(accent);

        // warna latar + kontras teks otomatis (latar terang → teks gelap)
        var bg = BackgroundColor();
        bool lightBg = _cfg.Style != 2 && Luminance(bg) > 0.58;
        _sec = lightBg ? SecDark : SecWhite;
        _faint = lightBg ? FaintDark : FaintWhite;
        _fg = _cfg.TextColor == 1 ? accentBrush : lightBg ? DarkText : White;

        var card = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(radius), Margin = new Thickness(pad0),
            Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius),
        };
        _glowShadow = null;
        switch (_cfg.Style)
        {
            case 1: // pekat
                card.Background = Frozen(Color.FromArgb(0xF2, bg.R, bg.G, bg.B));
                card.BorderBrush = Frozen(lightBg ? Color.FromArgb(0x1A, 0, 0, 0) : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
                card.BorderThickness = new Thickness(1);
                break;
            case 2: // minimal: tanpa latar; teks diberi bayangan (atau cahaya berwarna kalau ambient nyala)
                card.Background = Brushes.Transparent;
                card.Clip = null; // bayangan/cahaya teks tidak terpotong
                _glowShadow = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 0, Opacity = 0.75, Color = Colors.Black };
                if (_cfg.Ambient != 0)
                {
                    _glowShadow.BlurRadius = 20;
                    _glowShadow.Color = _cfg.Ambient == 1 ? accent : Colors.Black;
                    _glowShadow.Opacity = _cfg.Ambient == 1 ? Math.Clamp(_cfg.AmbientStrength + 0.3, 0.3, 1) : 0.75;
                }
                card.Effect = _glowShadow;
                break;
            default: // kaca
                var top = Blend(bg, Colors.White, 0.12);
                card.Background = new LinearGradientBrush(Color.FromArgb(0x8C, top.R, top.G, top.B), Color.FromArgb(0x73, bg.R, bg.G, bg.B), 90);
                card.BorderBrush = new LinearGradientBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 90);
                card.BorderThickness = new Thickness(1);
                break;
        }

        double pad = circle ? w * 0.16 : _cfg.Shape == 2 ? Math.Max(18, h * 0.22) : 16;
        var inner = new Grid { Margin = new Thickness(pad) };
        card.Child = inner;

        _refresh = null;
        switch (_cfg.Kind)
        {
            case 4: BuildNote(inner, w - 2 * pad, h - 2 * pad, accentBrush); break;
            case 1: BuildWeather(inner, w - 2 * pad, h - 2 * pad, accentBrush); break;
            case 2: BuildMusic(inner, w - 2 * pad, h - 2 * pad, accentBrush, circle); break;
            case 3: BuildPerf(inner, w - 2 * pad, h - 2 * pad, accent); break;
            default: BuildClock(inner, w - 2 * pad, h - 2 * pad, accentBrush, circle); break;
        }

        // cahaya ambient (kaca & pekat): bentuk yang sama, diburamkan, dan HANYA di luar kartu
        // (dipotong tepat di bentuk kartu supaya tidak tembus ke dalam kartu yang transparan)
        _glow = null;
        Grid? glowLayer = null;
        if (_cfg.Ambient != 0 && _cfg.Style != 2)
        {
            _glow = new Border
            {
                Width = w, Height = h, CornerRadius = new CornerRadius(radius), Margin = new Thickness(pad0),
                Background = _cfg.Ambient == 1 ? accentBrush : Brushes.Transparent,
                Opacity = Math.Clamp(_cfg.AmbientStrength, 0.1, 1),
                Effect = new BlurEffect { Radius = GlowPad * 1.2, RenderingBias = RenderingBias.Performance },
            };
            glowLayer = new Grid
            {
                Width = w + 2 * pad0, Height = h + 2 * pad0, IsHitTestVisible = false,
                Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(new Rect(0, 0, w + 2 * pad0, h + 2 * pad0)),
                    new RectangleGeometry(new Rect(pad0, pad0, w, h), radius, radius)),
            };
            glowLayer.Children.Add(_glow);
        }
        if (_cfg.Ambient == 2) WatchSongColor();
        _editFrame = new Rectangle
        {
            Width = w, Height = h, RadiusX = radius, RadiusY = radius, Margin = new Thickness(pad0), Stroke = Frozen(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false,
            Visibility = WidgetManager.EditMode ? Visibility.Visible : Visibility.Collapsed,
        };
        _host.Children.Clear();
        if (glowLayer != null) _host.Children.Add(glowLayer);
        _host.Children.Add(card);
        _host.Children.Add(_editFrame);
        Cursor = WidgetManager.EditMode ? Cursors.SizeAll : null;
        SafeRefresh();
    }

    DropShadowEffect? _glowShadow;

    Color BackgroundColor()
    {
        var dark = Color.FromRgb(0x1C, 0x1C, 0x1E);
        switch (_cfg.BgMode)
        {
            case 1: // ikut wallpaper: warna utama wallpaper, digelapkan supaya tetap elegan
                if (WallpaperService.WallColor is Color wc)
                {
                    double max = Math.Max(wc.R, Math.Max(wc.G, wc.B)) / 255.0;
                    double f = max <= 0 ? 0 : 0.30 / max;
                    return Color.FromRgb((byte)(wc.R * f), (byte)(wc.G * f), (byte)(wc.B * f));
                }
                return dark;
            case 2:
                try { return (Color)ColorConverter.ConvertFromString(_cfg.BgColor); } catch { return dark; }
            default:
                return dark;
        }
    }

    static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Teks besar yang mengecil sendiri kalau tidak muat (tidak pernah terpotong).</summary>
    static Viewbox Fit(TextBlock tb, double maxWidth) => new()
    {
        Child = tb, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
        MaxWidth = Math.Max(10, maxWidth), HorizontalAlignment = tb.HorizontalAlignment,
    };

    TextBlock Text(string text, double size, Brush fg, FontWeight? weight = null, HorizontalAlignment align = HorizontalAlignment.Left) => new()
    {
        Text = text, FontSize = size,
        Foreground = ReferenceEquals(fg, White) ? _fg : fg, // teks utama bisa ikut warna widget
        FontWeight = _cfg.FontWeightMode switch { 1 => FontWeights.Light, 2 => FontWeights.Normal, 3 => FontWeights.Bold, _ => weight ?? FontWeights.Normal },
        TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = align,
    };

    /// <summary>Cahaya "ikut lagu": warna dari cover lagu yang diputar; padam saat tidak ada lagu.</summary>
    void WatchSongColor()
    {
        var media = WidgetManager.Media;
        void Apply()
        {
            bool has = media != null && media.HasSession && media.Art != null;
            var c = has ? IconLoader.DominantColor(media!.Art!) : Colors.Black;
            double level = Math.Clamp(_cfg.AmbientStrength, 0.1, 1) * (has && media!.IsPlaying ? 1 : 0.5);
            if (_glow != null)
            {
                _glow.Background = has ? Frozen(c) : Brushes.Transparent;
                _glow.Opacity = level;
            }
            if (_glowShadow != null)
            {
                // gaya minimal: teksnya yang bercahaya; tanpa lagu kembali ke bayangan gelap biasa
                _glowShadow.Color = c;
                _glowShadow.Opacity = has ? Math.Clamp(level + 0.3, 0.3, 1) : 0.75;
            }
        }
        if (media == null) return;
        Action changed = () => Dispatcher.BeginInvoke(new Action(Apply));
        media.Changed += changed;
        media.TrackChanged += changed;
        _unsubscribe.Add(() => { media.Changed -= changed; media.TrackChanged -= changed; });
        Dispatcher.BeginInvoke(new Action(Apply));
    }

    // ---------------------------------------------------------------- catatan

    /// <summary>Catatan bebas. Klik dua kali untuk mengetik langsung di desktop; klik di luar / Esc untuk selesai.</summary>
    void BuildNote(Grid g, double w, double h, Brush accent)
    {
        double size = _cfg.Size == 0 ? 14 : 16;
        var view = Text("", size, White);
        view.TextWrapping = TextWrapping.Wrap;
        view.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        view.LineHeight = size * 1.35; // jarak antarbaris yang nyaman dibaca
        view.TextTrimming = TextTrimming.WordEllipsis;
        view.VerticalAlignment = VerticalAlignment.Top;
        var box = new TextBox
        {
            FontSize = size, Foreground = _fg, CaretBrush = accent, SelectionBrush = accent, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), AcceptsReturn = true, AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Visibility = Visibility.Collapsed, FontWeight = view.FontWeight, MaxLength = 2000,
        };
        g.Children.Add(view);
        g.Children.Add(box);

        void ShowText()
        {
            bool empty = _cfg.NoteText.Trim().Length == 0;
            view.Text = empty ? L.T("Klik dua kali untuk menulis…") : _cfg.NoteText;
            view.Foreground = empty ? Secondary : _fg;
        }
        _refresh = ShowText;

        void Begin()
        {
            if (_typing || WidgetManager.EditMode) return;
            _typing = true;
            // izinkan jendela ini aktif supaya bisa menerima ketikan
            long ex = Native.GetWindowLongPtr(_hwnd, -20).ToInt64() & ~Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(_hwnd, -20, new IntPtr(ex));
            ApplyZOrder();
            box.Text = _cfg.NoteText;
            view.Visibility = Visibility.Collapsed;
            box.Visibility = Visibility.Visible;
            Activate();
            box.Focus();
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
        }
        void End()
        {
            if (!_typing) return;
            _typing = false;
            _cfg.NoteText = box.Text.TrimEnd();
            Settings.Save();
            box.Visibility = Visibility.Collapsed;
            view.Visibility = Visibility.Visible;
            ShowText();
            long ex = Native.GetWindowLongPtr(_hwnd, -20).ToInt64() | Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(_hwnd, -20, new IntPtr(ex));
            ApplyZOrder();
        }
        _endTyping = End;
        view.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { e.Handled = true; Begin(); } };
        g.Background = Brushes.Transparent; // area kosong juga bisa diklik dua kali
        g.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && !_typing) { e.Handled = true; Begin(); } };
        box.LostKeyboardFocus += (_, _) => End();
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; End(); } };
    }

    // ---------------------------------------------------------------- jam

    void BuildClock(Grid g, double w, double h, Brush accent, bool circle)
    {
        bool analog = circle || _cfg.Size == 2;
        bool h24 = Settings.Current.Use24Hour;
        if (analog)
        {
            double d = Math.Min(w, h);
            var face = new Canvas { Width = d, Height = d, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            double c = d / 2;
            for (int i = 0; i < 60; i += 5)
            {
                bool major = i % 15 == 0;
                double a = i * 6 * Math.PI / 180, r1 = c * (major ? 0.78 : 0.84), r2 = c * 0.94;
                face.Children.Add(new Line
                {
                    X1 = c + r1 * Math.Sin(a), Y1 = c - r1 * Math.Cos(a), X2 = c + r2 * Math.Sin(a), Y2 = c - r2 * Math.Cos(a),
                    Stroke = major ? _fg : Secondary, StrokeThickness = major ? 3 : 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                });
            }
            var day = Text("", Math.Max(10, d * 0.075), accent, FontWeights.SemiBold);
            day.Width = d;
            day.TextAlignment = TextAlignment.Center;
            Canvas.SetTop(day, c + d * 0.16);
            face.Children.Add(day);
            var hourRot = new RotateTransform(0, c, c);
            var minRot = new RotateTransform(0, c, c);
            face.Children.Add(new Line { X1 = c, Y1 = c, X2 = c, Y2 = c - d * 0.24, Stroke = _fg, StrokeThickness = Math.Max(3, d * 0.028), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = hourRot });
            face.Children.Add(new Line { X1 = c, Y1 = c, X2 = c, Y2 = c - d * 0.36, Stroke = _fg, StrokeThickness = Math.Max(2, d * 0.018), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = minRot });
            var dot = new Ellipse { Width = d * 0.05, Height = d * 0.05, Fill = accent };
            Canvas.SetLeft(dot, c - d * 0.025);
            Canvas.SetTop(dot, c - d * 0.025);
            face.Children.Add(dot);
            g.Children.Add(face);
            _refresh = () =>
            {
                var now = DateTime.Now;
                minRot.Angle = now.Minute * 6;
                hourRot.Angle = (now.Hour % 12) * 30 + now.Minute * 0.5;
                day.Text = now.ToString("ddd d", L.Culture).ToUpper(L.Culture);
            };
        }
        else
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = _cfg.Size == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center };
            var dayName = Text("", _cfg.Size == 0 ? 13 : 15, accent, FontWeights.SemiBold, stack.HorizontalAlignment);
            var time = Text("", _cfg.Size == 0 ? 42 : 60, White, FontWeights.Light, stack.HorizontalAlignment);
            time.Margin = new Thickness(0, -time.FontSize * 0.08, 0, -time.FontSize * 0.06); // rapatkan jarak atas/bawah angka besar
            var date = Text("", _cfg.Size == 0 ? 12.5 : 14, Secondary, null, stack.HorizontalAlignment);
            stack.Children.Add(dayName);
            stack.Children.Add(Fit(time, w));
            stack.Children.Add(date);
            g.Children.Add(stack);
            _refresh = () =>
            {
                var now = DateTime.Now;
                time.Text = now.ToString(h24 ? "HH:mm" : "h:mm", L.Culture);
                dayName.Text = now.ToString("dddd", L.Culture);
                date.Text = now.ToString(_cfg.Size == 0 ? "d MMM" : "d MMMM yyyy", L.Culture);
            };
        }
        // tepat di pergantian menit, bukan dicek terus
        void Schedule()
        {
            var now = DateTime.Now;
            var next = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(50);
            StartTimer(next, () => { SafeRefresh(); Schedule(); });
        }
        Schedule();
    }

    // ---------------------------------------------------------------- cuaca

    void BuildWeather(Grid g, double w, double h, Brush accent)
    {
        var weather = WidgetManager.Weather;
        var city = Text("", _cfg.Size == 0 ? 12.5 : 14, Secondary, FontWeights.SemiBold);
        var icon = new TextBlock { FontFamily = EmojiFont, FontSize = _cfg.Size switch { 2 => 84, 0 => 30, _ => 52 } };
        var temp = Text("", _cfg.Size switch { 2 => 64, 0 => 40, _ => 54 }, White, FontWeights.Light);
        var desc = Text("", _cfg.Size == 0 ? 12 : 14, Secondary);
        desc.TextWrapping = TextWrapping.Wrap;
        desc.TextTrimming = TextTrimming.CharacterEllipsis;

        var bottom = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        switch (_cfg.Size)
        {
            case 0: // kecil: kota + ikon di atas, suhu & keterangan di bawah
            {
                var top = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(icon, Dock.Right);
                top.Children.Add(icon);
                top.Children.Add(city);
                bottom.Children.Add(Fit(temp, w));
                bottom.Children.Add(desc);
                g.Children.Add(top);
                g.Children.Add(bottom);
                break;
            }
            case 2: // besar: kota di atas, ikon besar di tengah, suhu di bawah
            {
                city.VerticalAlignment = VerticalAlignment.Top;
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                icon.VerticalAlignment = VerticalAlignment.Center;
                bottom.Children.Add(Fit(temp, w));
                bottom.Children.Add(desc);
                g.Children.Add(city);
                g.Children.Add(icon);
                g.Children.Add(bottom);
                break;
            }
            default: // sedang / lebar: teks di kiri, ikon di kanan
            {
                var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 70, 0) };
                left.Children.Add(city);
                left.Children.Add(Fit(temp, w - 70));
                left.Children.Add(desc);
                icon.HorizontalAlignment = HorizontalAlignment.Right;
                icon.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(left);
                g.Children.Add(icon);
                break;
            }
        }

        _refresh = () =>
        {
            var s = Settings.Current;
            if (weather?.Temperature is double t)
            {
                var (ic, tx) = WeatherService.Describe(weather.Code);
                icon.Text = ic;
                temp.Text = $"{Math.Round(t):0}°";
                desc.Text = L.T(tx);
                city.Text = weather.City.Length > 0 ? weather.City : s.WeatherCity;
            }
            else
            {
                icon.Text = "🌡";
                temp.Text = "—";
                city.Text = s.WeatherCity.Length > 0 ? s.WeatherCity : L.T("Cuaca");
                desc.Text = s.WeatherCity.Trim().Length == 0 ? L.T("Isi kota di Pengaturan → Kalender, cuaca & timer") : L.T("Memuat cuaca…");
            }
        };
        if (weather != null)
        {
            Action changed = () => { if (!Idle) SafeRefresh(); };
            weather.Changed += changed;
            _unsubscribe.Add(() => weather.Changed -= changed);
        }
    }

    // ---------------------------------------------------------------- musik

    void BuildMusic(Grid g, double w, double h, Brush accent, bool circle)
    {
        var media = WidgetManager.Media;
        bool showProgress = _cfg.Size != 0;
        double coverSize = _cfg.Size switch { 0 => Math.Min(w, h) - 40, 2 => Math.Min(w, h * 0.55), _ => h };
        var cover = new Border
        {
            Width = coverSize, Height = coverSize, CornerRadius = new CornerRadius(circle ? coverSize / 2 : 14),
            Background = Faint,
        };
        var coverGlyph = new TextBlock { Text = "", FontFamily = IconFont, FontSize = coverSize * 0.32, Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        cover.Child = coverGlyph;
        var title = Text("", _cfg.Size == 2 ? 17 : 15, White, FontWeights.SemiBold);
        var artist = Text("", _cfg.Size == 2 ? 14 : 13, Secondary);
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Faint, Margin = new Thickness(0, 10, 0, 6) };
        var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = accent, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        track.Child = fill;

        TextBlock Btn(string glyph, double size, Action click)
        {
            var b = new TextBlock
            {
                Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = _fg, Cursor = Cursors.Hand,
                Margin = new Thickness(size * 0.55, 0, size * 0.55, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
            };
            b.MouseLeftButtonUp += (_, e) => { if (WidgetManager.EditMode) return; e.Handled = true; click(); };
            return b;
        }
        double bs = _cfg.Size == 0 ? 15 : 18;
        var play = Btn("", bs + 4, () => media?.PlayPause());
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        controls.Children.Add(Btn("", bs, () => media?.Previous()));
        controls.Children.Add(play);
        controls.Children.Add(Btn("", bs, () => media?.Next()));

        switch (_cfg.Size)
        {
            case 0:
            {
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                cover.HorizontalAlignment = HorizontalAlignment.Center;
                controls.Margin = new Thickness(0, 10, 0, 0);
                stack.Children.Add(cover);
                stack.Children.Add(controls);
                g.Children.Add(stack);
                break;
            }
            case 2:
            {
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                cover.HorizontalAlignment = HorizontalAlignment.Center;
                title.Margin = new Thickness(0, 12, 0, 0);
                title.HorizontalAlignment = artist.HorizontalAlignment = HorizontalAlignment.Center;
                stack.Children.Add(cover);
                stack.Children.Add(title);
                stack.Children.Add(artist);
                stack.Children.Add(track);
                stack.Children.Add(controls);
                g.Children.Add(stack);
                break;
            }
            default:
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(cover);
                var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
                right.Children.Add(title);
                right.Children.Add(artist);
                right.Children.Add(track);
                right.Children.Add(controls);
                Grid.SetColumn(right, 1);
                g.Children.Add(right);
                break;
            }
        }

        BitmapSourceRef lastArt = new();
        void UpdateProgress()
        {
            if (media == null || !showProgress) return;
            var dur = media.Duration;
            double frac = dur > TimeSpan.Zero ? Math.Clamp(media.Position.TotalSeconds / dur.TotalSeconds, 0, 1) : 0;
            fill.Width = Math.Max(0, track.ActualWidth) * frac;
        }
        _refresh = () =>
        {
            bool has = media != null && media.HasSession && (media.Title.Length > 0 || media.Art != null);
            title.Text = has ? media!.Title : L.T("Tidak ada media yang diputar");
            artist.Text = has ? (media!.Artist.Length > 0 ? media.Artist : media.SourceName) : "";
            play.Text = has && media!.IsPlaying ? "" : "";
            var art = has ? media!.Art : null;
            if (!ReferenceEquals(art, lastArt.Value))
            {
                lastArt.Value = art;
                cover.Background = art != null ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : Faint;
                coverGlyph.Visibility = art != null ? Visibility.Collapsed : Visibility.Visible;
            }
            UpdateProgress();
            // garis progress hanya bergerak saat lagu diputar (1x per detik)
            if (showProgress && has && media!.IsPlaying) { if (_timer == null) StartTimer(TimeSpan.FromSeconds(1), UpdateProgress); }
            else { _timer?.Stop(); _timer = null; }
        };
        track.SizeChanged += (_, _) => UpdateProgress();
        if (media != null)
        {
            Action changed = () => Dispatcher.BeginInvoke(new Action(() => { if (!Idle) SafeRefresh(); }));
            media.Changed += changed;
            media.TrackChanged += changed;
            _unsubscribe.Add(() => { media.Changed -= changed; media.TrackChanged -= changed; });
        }
    }

    sealed class BitmapSourceRef { public ImageSource? Value; }

    // ---------------------------------------------------------------- performa

    readonly Queue<double> _cpuHist = new(), _ramHist = new();
    ulong _pIdle, _pKernel, _pUser;

    double SampleCpu()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        ulong dIdle = idle.Value - _pIdle, dKernel = kernel.Value - _pKernel, dUser = user.Value - _pUser;
        bool first = _pKernel == 0;
        _pIdle = idle.Value; _pKernel = kernel.Value; _pUser = user.Value;
        ulong total = dKernel + dUser;
        return first || total == 0 ? 0 : Math.Clamp((total - dIdle) * 100.0 / total, 0, 100);
    }

    void BuildPerf(Grid g, double w, double h, Color accent)
    {
        var accentBrush = Frozen(accent);
        var rows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var bars = new List<(TextBlock value, Border fill, Border track, Func<double> get)>();
        void Row(string label, Func<double> get)
        {
            var head = new DockPanel { Margin = new Thickness(0, rows.Children.Count == 0 ? 0 : 9, 0, 4) };
            var val = Text("", 13, White, FontWeights.SemiBold);
            DockPanel.SetDock(val, Dock.Right);
            head.Children.Add(val);
            head.Children.Add(Text(L.T(label), 12.5, Secondary));
            var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Faint };
            var fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = accentBrush, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            track.Child = fill;
            rows.Children.Add(head);
            rows.Children.Add(track);
            bars.Add((val, fill, track, get));
        }
        double cpu = 0, ram = 0;
        Row("CPU", () => cpu);
        Row("RAM", () => ram);
        var bat = SystemStats.Battery();
        if (bat.HasBattery) Row("Baterai", () => SystemStats.Battery().Percent);

        Polyline? cpuLine = null, ramLine = null;
        Canvas Spark(string label, out Polyline line, Brush stroke)
        {
            var c = new Canvas { ClipToBounds = true };
            line = new Polyline { Stroke = stroke, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            c.Children.Add(line);
            var lbl = Text(L.T(label), 11.5, Secondary);
            c.Children.Add(lbl);
            return c;
        }
        if (_cfg.Size == 1 || _cfg.Size == 3)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_cfg.Size == 3 ? 170 : 130) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(rows);
            var spark = Spark("CPU 80 dtk", out var l1, accentBrush);
            cpuLine = l1;
            spark.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(spark, 1);
            g.Children.Add(spark);
        }
        else if (_cfg.Size == 2)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition());
            g.Children.Add(rows);
            var s1 = Spark("CPU", out var l1, accentBrush);
            var s2 = Spark("RAM", out var l2, Frozen(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)));
            cpuLine = l1;
            ramLine = l2;
            s1.Margin = new Thickness(0, 14, 0, 4);
            s2.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(s1, 1);
            Grid.SetRow(s2, 2);
            g.Children.Add(s1);
            g.Children.Add(s2);
        }
        else g.Children.Add(rows);

        void Plot(Polyline? line, Queue<double> hist)
        {
            if (line?.Parent is not Canvas c || c.ActualWidth <= 0 || c.ActualHeight <= 0) return;
            var pts = new PointCollection();
            double step = c.ActualWidth / (HistoryLength - 1), top = 16, hh = c.ActualHeight - top - 2;
            int i = HistoryLength - hist.Count;
            foreach (var v in hist) pts.Add(new Point(i++ * step, top + hh * (1 - v / 100)));
            pts.Freeze();
            line.Points = pts;
        }

        void Sample()
        {
            cpu = SampleCpu();
            ram = SystemStats.RamPercent();
            Push(_cpuHist, cpu);
            Push(_ramHist, ram);
        }
        _refresh = () =>
        {
            foreach (var (value, fill, track, get) in bars)
            {
                double v = Math.Clamp(get(), 0, 100);
                value.Text = $"{v:0}%";
                fill.Width = Math.Max(0, track.ActualWidth) * v / 100;
            }
            Plot(cpuLine, _cpuHist);
            Plot(ramLine, _ramHist);
        };
        Sample(); // titik awal
        StartTimer(TimeSpan.FromSeconds(2), () => { Sample(); SafeRefresh(); });
        rows.SizeChanged += (_, _) => SafeRefresh();
    }

    const int HistoryLength = 40; // 40 x 2 detik = 80 detik

    static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLength) q.Dequeue();
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // =====================================================================
    // Native
    // =====================================================================

    const int WM_WINDOWPOSCHANGING = 0x0046, WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_BOTTOM = (IntPtr)1;

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
