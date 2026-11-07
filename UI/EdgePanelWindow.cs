using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.UI;

/// <summary>
/// Kapsul di tepi layar (gaya Bloom) dengan bentuk notch yang sama seperti island (sudut cekung).
///   Kiri  : volume utama. Diam sebentar di atasnya → melebar jadi mixer per aplikasi.
///   Kanan : kecerahan layar.
/// Scroll di mana saja di dalam kapsul untuk mengatur (mouse maupun touchpad).
/// </summary>
public sealed class EdgePanelWindow : Window
{
    // ukuran (DIP)
    const double ColumnWidth = 50, Length = 224, Shoulder = 14, Radius = 24, Inset = 6;
    const double BarHeight = 156, BarWidth = 8, MaxColumns = 9;

    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly Brush Subtle = Frozen(Color.FromRgb(0xB4, 0xB2, 0xA9));
    static readonly Brush TrackBg = Frozen(Color.FromArgb(0x55, 0x80, 0x80, 0x80));

    readonly EdgeSide _side;
    readonly VolumeService? _volume;
    readonly BrightnessService? _brightness;
    readonly MediaService _media;

    readonly Spring _slide = new();
    readonly Spring _widen = new();
    readonly Func<double, bool> _tick;
    readonly DispatcherTimer _hideTimer, _listTimer;

    readonly double _winW, _winH, _depth0, _maxDepth;
    readonly Canvas _root, _layer;
    readonly StackPanel _columns;
    readonly SurfaceFx _fx;
    readonly System.Windows.Shapes.Path _shape;
    readonly System.Windows.Shapes.Path _edge = new()
    {
        Stroke = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1, IsHitTestVisible = false,
    };
    readonly TranslateTransform _slideTransform = new();
    readonly Column _master;
    readonly List<Column> _appColumns = new();
    double _expandedDepth;

    /// <summary>Satu slider vertikal (utama atau per aplikasi).</summary>
    sealed class Column
    {
        public readonly Grid Root = new() { Width = ColumnWidth, Background = Brushes.Transparent };
        public readonly Border Fill = new()
        {
            Width = BarWidth, CornerRadius = new CornerRadius(BarWidth / 2), Background = Brushes.White,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        public readonly Grid Bar = new() { Width = 22, Height = BarHeight, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        public readonly TextBlock Percent = new()
        {
            Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        public readonly FrameworkElement Icon;
        public readonly DispatcherTimer LabelTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
        public Func<double> Get = () => 0;
        public Action<double> Set = _ => { };
        public Func<bool> IsMuted = () => false;
        public Action ToggleMute = () => { };
        public bool AlwaysShowPercent;
        public double Wheel; // sisa scroll touchpad (pecahan)

        public Column(FrameworkElement icon)
        {
            Icon = icon;
            var track = new Border { Width = BarWidth, CornerRadius = new CornerRadius(BarWidth / 2), Background = TrackBg };
            Bar.Children.Add(track);
            Bar.Children.Add(Fill);
            Bar.HorizontalAlignment = HorizontalAlignment.Center;
            Bar.VerticalAlignment = VerticalAlignment.Top;
            Bar.Margin = new Thickness(0, 20, 0, 0);

            var label = new Grid { Height = 24, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 20 + BarHeight + 6, 0, 0) };
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            label.Children.Add(icon);
            label.Children.Add(Percent);

            Root.Children.Add(Bar);
            Root.Children.Add(label);
            LabelTimer.Tick += (_, _) => { LabelTimer.Stop(); ShowLabel(false); };
        }

        public void Refresh()
        {
            double v = Math.Clamp(Get(), 0, 1);
            bool muted = IsMuted();
            // isi bar bergerak halus mengejar nilai baru (bukan melompat)
            double target = BarHeight * (muted ? 0 : v);
            if (double.IsNaN(Fill.Height)) Fill.Height = target; // belum punya tinggi awal: langsung set, tanpa animasi
            var anim = new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            };
            Fill.BeginAnimation(FrameworkElement.HeightProperty, anim, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
            Percent.Text = muted ? "mute" : Math.Round(v * 100) + "%";
            Icon.Opacity = muted ? 0.4 : 1;
            if (AlwaysShowPercent) ShowLabel(true);
        }

        /// <summary>Tampilkan persen (bukan ikon) sebentar saat disentuh / diubah.</summary>
        public void ShowLabel(bool percent)
        {
            percent |= AlwaysShowPercent;
            Percent.Visibility = percent ? Visibility.Visible : Visibility.Collapsed;
            Icon.Visibility = percent ? Visibility.Collapsed : Visibility.Visible;
        }

        public void Flash()
        {
            ShowLabel(true);
            LabelTimer.Stop();
            LabelTimer.Start();
        }
    }

    public EdgePanelWindow(EdgeSide side, VolumeService? volume, BrightnessService? brightness, MediaService media)
    {
        _side = side;
        _volume = volume;
        _brightness = brightness;
        _media = media;
        _tick = Tick;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Title = side == EdgeSide.Left ? "Volume" : "Kecerahan";

        _depth0 = ColumnWidth + Inset * 2;
        _maxDepth = side == EdgeSide.Left ? ColumnWidth * MaxColumns + Inset * 2 : _depth0;
        _expandedDepth = _depth0;
        _winW = _maxDepth + 30;
        _winH = Length + 2 * Shoulder + 40;
        Width = _winW;
        Height = _winH;

        _root = new Canvas { Width = _winW, Height = _winH, RenderTransform = _slideTransform };
        _shape = new System.Windows.Shapes.Path();
        _layer = new Canvas { Width = _winW, Height = _winH };
        _root.Children.Add(_shape);
        _fx = new SurfaceFx(_root,
            () => Settings.Current.HoverSpotlight && Settings.Current.SpotlightPanels,
            () => Settings.Current.AmbientLight && Settings.Current.AmbientPanels);
        _root.Children.Add(_fx.Layer);
        _root.Children.Add(_layer);
        _root.Children.Add(_edge); // garis tepi tipis ala macOS
        Content = _root;
        Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular); // angka tidak bergeser

        // deretan kolom slider
        _columns = new StackPanel { Orientation = Orientation.Horizontal, Height = Length };
        Canvas.SetTop(_columns, (_winH - Length) / 2);
        _layer.Children.Add(_columns);

        if (side == EdgeSide.Left)
        {
            _master = new Column(Glyph(""))
            {
                Get = () => _volume?.Master ?? 0,
                Set = v => { if (_volume == null) return; if (_volume.Muted && v > _volume.Master) _volume.Muted = false; _volume.Master = (float)v; },
                IsMuted = () => _volume?.Muted ?? false,
                ToggleMute = () => { if (_volume != null) _volume.Muted = !_volume.Muted; },
                AlwaysShowPercent = true,
            };
        }
        else
        {
            _master = new Column(Glyph(""))
            {
                Get = () => _brightness != null && _brightness.Available ? _brightness.Current / 100.0 : 0,
                Set = v => _brightness?.Set((int)Math.Round(v * 100)),
                AlwaysShowPercent = true,
            };
        }
        AddColumn(_master);

        // ----- interaksi -----
        _hideTimer = new DispatcherTimer(DispatcherPriority.Input);
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); HideNow(); };
        _listTimer = new DispatcherTimer(DispatcherPriority.Input);
        _listTimer.Tick += (_, _) => { _listTimer.Stop(); OpenMixer(); };

        _root.MouseEnter += (_, _) =>
        {
            _hideTimer.Stop();
            if (_side == EdgeSide.Left) StartTimer(_listTimer, Settings.Current.AppListOpenDelayMs);
        };
        _root.MouseLeave += (_, _) =>
        {
            _listTimer.Stop();
            StartTimer(_hideTimer, Settings.Current.EdgeHideDelayMs);
        };

        // scroll di mana saja di dalam kapsul: kolom di bawah pointer, atau slider utama
        _root.MouseWheel += (_, e) =>
        {
            var col = ColumnAt(e.OriginalSource as DependencyObject) ?? _master;
            double step = Settings.Current.ScrollStepPercent / 100.0;
            col.Wheel += e.Delta / 120.0 * step; // pecahan → halus di touchpad
            if (Math.Abs(col.Wheel) >= 0.005)
            {
                double before = col.Get();
                double target = Math.Clamp(before + col.Wheel, 0, 1);
                col.Set(target);
                // kecerahan hanya bisa bilangan bulat: sisa scroll yang belum cukup 1% disimpan, bukan dibuang
                if (Math.Abs(col.Get() - before) > 1e-6 || target <= 0 || target >= 1) col.Wheel = 0;
                col.Refresh();
                col.Flash();
            }
            e.Handled = true;
        };

        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle);
        Settings.Changed += () => { ApplyColors(); RefreshValue(); };
        ApplyColors();
        Redraw();
    }

    static FrameworkElement Glyph(string glyph) => new TextBlock
    {
        Text = glyph, FontFamily = IconFont, FontSize = 15, Foreground = Brushes.White,
    };

    void ApplyColors()
    {
        _shape.Fill = IslandWindow.ParseBrush(Settings.Current.IslandColor, Settings.Current.IslandOpacity);
        _edge.Visibility = Settings.Current.EdgeHighlight ? Visibility.Visible : Visibility.Collapsed;
        FontFamily = IslandWindow.UiFont();
    }

    void AddColumn(Column col)
    {
        col.Bar.MouseLeftButtonDown += (_, e) =>
        {
            double y = e.GetPosition(col.Bar).Y;
            col.Set(Math.Clamp(1 - y / BarHeight, 0, 1));
            col.Refresh();
            col.Flash();
            e.Handled = true;
        };
        col.Icon.MouseLeftButtonUp += (_, e) => { col.ToggleMute(); col.Refresh(); e.Handled = true; };
        col.Icon.Cursor = Cursors.Hand;
        col.Root.MouseEnter += (_, _) => col.ShowLabel(true);
        col.Root.MouseLeave += (_, _) => { if (!col.LabelTimer.IsEnabled) col.ShowLabel(false); };
        col.Refresh();
        col.ShowLabel(false);
        _columns.Children.Add(col.Root);
    }

    Column? ColumnAt(DependencyObject? source)
    {
        while (source != null)
        {
            if (source == _master.Root) return _master;
            foreach (var c in _appColumns) if (source == c.Root) return c;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    // =====================================================================
    // Tampil / sembunyi
    // =====================================================================

    /// <param name="cursorYPhysical">Posisi Y pointer (piksel fisik). Kapsul muncul sejajar pointer.</param>
    /// <param name="peek">True jika dipicu tombol keyboard: tampil sebentar lalu hilang sendiri.</param>
    public void Reveal(int? cursorYPhysical, bool peek)
    {
        if (_suspended) return;
        if (!IsVisible || _parked)
        {
            double screenW = SystemParameters.PrimaryScreenWidth;
            double screenH = SystemParameters.PrimaryScreenHeight;
            double centerY = cursorYPhysical.HasValue ? cursorYPhysical.Value / App.DpiScale : screenH / 2;
            Top = Math.Clamp(centerY - _winH / 2, -Shoulder - 20, screenH - _winH + Shoulder + 20);
            Left = _side == EdgeSide.Left ? 0 : screenW - _winW;
            _parked = false;
            if (!IsVisible) Show(); // hanya pertama kali: menampilkan jendela transparan lumayan berat
        }

        Native.BringToTopmost(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        RefreshValue();
        _slide.Target = 1;
        FrameLoop.Add(_tick);

        if (!_root.IsMouseOver)
            StartTimer(_hideTimer, peek ? 1500 : Settings.Current.EdgeHideDelayMs + 1200);
    }

    /// <summary>Nilai berubah dari luar (tombol volume/kecerahan di keyboard).</summary>
    /// <summary>Mode game: panel tidak muncul sama sekali.</summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            _suspended = value;
            if (value)
            {
                _hideTimer.Stop();
                _listTimer.Stop();
                _slide.Value = _slide.Target = 0;
                _widen.Value = _widen.Target = 0;
                Hide();
                ClearAppColumns();
            }
        }
    }
    bool _suspended;

    // Jendela tidak disembunyikan (Hide/Show itu lambat, membuat panel telat muncul), tapi dipindah
    // ke luar layar. Saat diam tidak ada yang digambar, jadi tidak membebani.
    bool _parked;

    void Park()
    {
        _parked = true;
        double screenW = SystemParameters.PrimaryScreenWidth;
        Left = _side == EdgeSide.Left ? -_winW - 200 : screenW + 200;
    }

    public void OnExternalChange()
    {
        if (_suspended) return;
        if (Settings.Current.ShowPanelOnKeyChange) Reveal(null, peek: true);
        else if (IsVisible && !_parked) RefreshValue();
    }

    void HideNow()
    {
        _slide.Target = 0;
        _widen.Target = 0;
        FrameLoop.Add(_tick);
    }

    void OpenMixer()
    {
        if (_side != EdgeSide.Left || _volume == null) return;
        BuildAppColumns();
        _widen.Target = 1;
        FrameLoop.Add(_tick);
    }

    bool Tick(double dt)
    {
        var s = Settings.Current;
        var (rk, rc) = s.RevealSpring; // muncul dari tepi: lebih kencang supaya tidak terasa telat
        bool a = _slide.Step(dt, rk, rc);
        bool b = _widen.Step(dt, s.AnimStiffness, s.AnimDamping);
        Redraw();

        bool moving = a || b;
        if (!moving && _slide.Target == 0)
        {
            Park();
            ClearAppColumns(); // lepas referensi sesi audio
        }
        if (!moving && _widen.Target == 0 && _appColumns.Count > 0) ClearAppColumns();
        return moving;
    }

    void Redraw()
    {
        double wv = Math.Max(0, _widen.Value);
        double depth = MathEx.Lerp(_depth0, _expandedDepth, wv);

        // bentuk notch horizontal lalu "diputar" supaya menempel ke tepi kiri / kanan
        Transform t = _side == EdgeSide.Left
            ? new MatrixTransform(0, 1, 1, 0, 0, 0)             // x' = y, y' = x
            : new MatrixTransform(0, 1, -1, 0, _winW, 0);       // x' = W - y, y' = x
        var geometry = NotchGeometry.Build(_winH / 2, Length, depth, Shoulder, Radius, t);
        _shape.Data = geometry;
        if (Settings.Current.EdgeHighlight) _edge.Data = NotchGeometry.Build(_winH / 2, Length, depth, Shoulder, Radius, t, closed: false);
        _layer.Clip = geometry;
        _fx.SetShape(geometry, _winW, _winH);

        Canvas.SetLeft(_columns, _side == EdgeSide.Left ? Inset : _winW - _depth0 + Inset);
        double fade = MathEx.Clamp01((wv - 0.25) / 0.75);
        foreach (var c in _appColumns) c.Root.Opacity = fade;

        double travel = _depth0 + Shoulder + 6;
        double offset = travel * (1 - Math.Min(1, _slide.Value));
        _slideTransform.X = _side == EdgeSide.Left ? -offset : offset;
    }

    public void RefreshValue()
    {
        _master.Refresh();
        foreach (var c in _appColumns) c.Refresh();
        _master.Root.ToolTip = _side == EdgeSide.Right && !(_brightness?.Available ?? false)
            ? L.T("Kecerahan layar ini tidak bisa diatur dari aplikasi") : null;
    }

    // =====================================================================
    // Mixer: satu slider per aplikasi yang bersuara
    // =====================================================================

    void BuildAppColumns()
    {
        ClearAppColumns();
        var apps = _volume!.GetApps();
        int max = (int)MaxColumns - 1;
        foreach (var app in apps)
        {
            if (_appColumns.Count >= max) break;
            var a = app;
            FrameworkElement icon = a.Icon != null
                ? new Image { Source = a.Icon, Width = 18, Height = 18 }
                : Glyph("");

            // sumber suara: nama aplikasi + judul media kalau aplikasi ini yang sedang memutar
            string tip = a.Name;
            if (_media.HasSession && _media.Title.Length > 0 && _media.SourceName.Length > 0 &&
                a.Name.Contains(_media.SourceName, StringComparison.OrdinalIgnoreCase))
                tip += "\n▶ " + _media.Title + (_media.Artist.Length > 0 ? " · " + _media.Artist : "");

            var col = new Column(icon)
            {
                Get = () => a.Volume,
                Set = v => { if (a.Muted && v > a.Volume) a.Muted = false; a.Volume = (float)v; },
                IsMuted = () => a.Muted,
                ToggleMute = () => a.Muted = !a.Muted,
            };
            col.Root.ToolTip = tip;
            col.Root.Opacity = 0;
            _appColumns.Add(col);
            AddColumn(col);
        }
        _expandedDepth = _depth0 + _appColumns.Count * ColumnWidth;
    }

    void ClearAppColumns()
    {
        foreach (var c in _appColumns) _columns.Children.Remove(c.Root);
        _appColumns.Clear();
        _expandedDepth = _depth0;
    }

    // =====================================================================

    static void StartTimer(DispatcherTimer t, int ms)
    {
        t.Stop();
        t.Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms));
        t.Start();
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
