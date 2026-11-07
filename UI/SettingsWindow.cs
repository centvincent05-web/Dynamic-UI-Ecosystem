using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using DynamicIsland.Core;
using Microsoft.Win32;
using WallpaperService = DynamicIsland.Services.WallpaperService;
using UpdateService = DynamicIsland.Services.UpdateService;
using LiveWallpaperManager = DynamicIsland.Services.LiveWallpaperManager;

namespace DynamicIsland.UI;

/// <summary>
/// Menu pengaturan bergaya Apple: kategori dengan ikon berwarna, grup kartu membulat, toggle switch.
/// Semua perubahan langsung diterapkan (tanpa restart) dan dipratinjau langsung di island.
/// </summary>
public sealed class SettingsWindow : Window
{
    // ---------- warna (gaya macOS gelap) ----------
    static readonly Brush Bg = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E));
    static readonly Brush SidebarBg = Frozen(Color.FromRgb(0x24, 0x24, 0x26));
    static readonly Brush Card = Frozen(Color.FromRgb(0x2C, 0x2C, 0x2E));
    static readonly Brush CardBorder = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    static readonly Brush Separator = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3C));
    static readonly Brush Secondary = Frozen(Color.FromRgb(0x98, 0x98, 0x9D));
    static readonly Brush ControlBg = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3C));
    static readonly Brush ControlHover = Frozen(Color.FromRgb(0x48, 0x48, 0x4A));
    static readonly Brush NavHover = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    static readonly Color AccentColor = Color.FromRgb(0x0A, 0x84, 0xFF);
    static readonly Brush Accent = Frozen(AccentColor);
    static readonly Brush Warning = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    static readonly string[] Swatches =
        { "#FFFFFF", "#0A84FF", "#64D2FF", "#34C759", "#FFD60A", "#FF9F0A", "#FF453A", "#FF375F", "#BF5AF2" };

    sealed record Page(string Icon, Color IconColor, string Name, PreviewKind? Preview, Action Build)
    {
        /// <summary>Judul kelompok di menu kiri (Umum, Island, Fitur island, Desktop, Sistem).</summary>
        public string Group { get; init; } = "";
        /// <summary>Satu kalimat penjelasan di bawah judul halaman.</summary>
        public string Desc { get; init; } = "";
        /// <summary>Fitur halaman ini sedang menyala? Kalau mati, logonya di menu kiri jadi abu-abu. Null = selalu berwarna.</summary>
        public Func<bool>? IsOn { get; init; }
    }

    static readonly Color IconOffColor = Color.FromRgb(0x48, 0x48, 0x4A);
    readonly List<(Page page, Border icon, TextBlock glyph)> _navIcons = new();

    readonly StackPanel _content = new() { Margin = new Thickness(28, 22, 28, 28), MaxWidth = 720 };
    StackPanel _panel;                 // tempat kontrol baru ditambahkan (isi halaman, atau panel sementara saat mengindeks)
    StackPanel? _group;                // kartu yang sedang diisi
    PreviewKind? _pv;                  // pratinjau untuk kontrol yang ditambahkan berikutnya
    readonly StackPanel _nav = new() { Margin = new Thickness(10, 6, 10, 10) };
    readonly TextBox _search = new();
    readonly ScrollViewer _scroll;
    readonly List<Page> _pages = new();
    readonly Dictionary<Page, string> _index = new();
    int _page;
    double _scrollTarget;
    bool _scrolling;

    public SettingsWindow()
    {
        _panel = _content;
        Title = L.T("Pengaturan") + " · Dynamic Island";
        Width = 880;
        Height = 660;
        MinWidth = 680;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Bg;
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico")); } catch { } // logo Dynamic Island di kiri atas
        LoadStyles();
        SourceInitialized += (sender, args) => DarkTitleBar(new WindowInteropHelper(this).Handle);

        // kiri: pencarian + daftar kategori · kanan: isi kategori
        _scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _scroll.PreviewMouseWheel += OnWheel;

        var sidebar = new DockPanel();
        var searchBox = BuildSearch();
        DockPanel.SetDock(searchBox, Dock.Top);
        sidebar.Children.Add(searchBox);
        var version = new TextBlock
        {
            Text = "Dynamic Island " + VersionText() + " · by Vincent", FontSize = 11, Foreground = Secondary,
            Margin = new Thickness(22, 6, 0, 12),
        };
        DockPanel.SetDock(version, Dock.Bottom);
        sidebar.Children.Add(version);
        sidebar.Children.Add(SmoothScroll(new ScrollViewer { Content = _nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));
        var sidebarBg = new Border
        {
            Background = SidebarBg, Child = sidebar,
            BorderBrush = CardBorder, BorderThickness = new Thickness(0, 0, 1, 0),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(sidebarBg);
        Grid.SetColumn(_scroll, 1);
        grid.Children.Add(_scroll);
        Content = grid;

        DefinePages();
        BuildNav();
        ShowPage(0);
        Action navRefresh = () => Dispatcher.BeginInvoke(new Action(RefreshNavIcons));
        Settings.Changed += navRefresh;
        Closed += (sender, args) =>
        {
            Settings.Changed -= navRefresh;
            Settings.Save();
            // jendela pengaturan berisi banyak kontrol: setelah ditutup, kembalikan memorinya ke Windows
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (s2, a2) => { t.Stop(); if (App.Island?.IsBusy != true) MemoryTrim.Trim(); };
            t.Start();
        };
    }

    // =====================================================================
    // Gaya kontrol (slider, scrollbar, kotak teks) ala macOS
    // =====================================================================

    Style? _scrollBarStyle;

    void LoadStyles()
    {
        const string ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
        string slider = $@"<Style {ns} TargetType='Slider'>
  <Setter Property='IsMoveToPointEnabled' Value='True'/>
  <Setter Property='Focusable' Value='False'/>
  <Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='Slider'>
      <Grid Height='24' Background='Transparent'>
        <Border Height='4' CornerRadius='2' Background='#48484A' VerticalAlignment='Center'/>
        <Track x:Name='PART_Track'>
          <Track.DecreaseRepeatButton>
            <RepeatButton Command='Slider.DecreaseLarge' Focusable='False' OverridesDefaultStyle='True'>
              <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>
                <Border Background='Transparent'><Border Height='4' CornerRadius='2' Background='#0A84FF' VerticalAlignment='Center'/></Border>
              </ControlTemplate></RepeatButton.Template>
            </RepeatButton>
          </Track.DecreaseRepeatButton>
          <Track.IncreaseRepeatButton>
            <RepeatButton Command='Slider.IncreaseLarge' Focusable='False' OverridesDefaultStyle='True'>
              <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>
            </RepeatButton>
          </Track.IncreaseRepeatButton>
          <Track.Thumb>
            <Thumb OverridesDefaultStyle='True' Focusable='False'>
              <Thumb.Template><ControlTemplate TargetType='Thumb'>
                <Ellipse Width='20' Height='20' Fill='White'>
                  <Ellipse.Effect><DropShadowEffect BlurRadius='5' ShadowDepth='1' Opacity='0.45'/></Ellipse.Effect>
                </Ellipse>
              </ControlTemplate></Thumb.Template>
            </Thumb>
          </Track.Thumb>
        </Track>
      </Grid>
    </ControlTemplate>
  </Setter.Value></Setter>
</Style>";
        string scrollBar = $@"<Style {ns} TargetType='ScrollBar'>
  <Setter Property='Width' Value='10'/>
  <Setter Property='MinWidth' Value='10'/>
  <Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='ScrollBar'>
      <Track x:Name='PART_Track' IsDirectionReversed='True'>
        <Track.Thumb>
          <Thumb OverridesDefaultStyle='True'>
            <Thumb.Template><ControlTemplate TargetType='Thumb'>
              <Border CornerRadius='3' Background='#55FFFFFF' Margin='3,2,3,2'/>
            </ControlTemplate></Thumb.Template>
          </Thumb>
        </Track.Thumb>
      </Track>
    </ControlTemplate>
  </Setter.Value></Setter>
</Style>";
        string textBox = $@"<Style {ns} TargetType='TextBox'>
  <Setter Property='Foreground' Value='White'/>
  <Setter Property='CaretBrush' Value='White'/>
  <Setter Property='SelectionBrush' Value='#0A84FF'/>
  <Setter Property='FontSize' Value='12.5'/>
  <Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='TextBox'>
      <Border x:Name='B' CornerRadius='7' Background='#1C1C1E' BorderBrush='#48484A' BorderThickness='1' Padding='8,5'>
        <ScrollViewer x:Name='PART_ContentHost' Focusable='False' VerticalScrollBarVisibility='Hidden' HorizontalScrollBarVisibility='Hidden'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#0A84FF'/></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Setter.Value></Setter>
</Style>";
        try
        {
            Resources.Add(typeof(Slider), (Style)XamlReader.Parse(slider));
            _scrollBarStyle = (Style)XamlReader.Parse(scrollBar);
            Resources.Add(typeof(ScrollBar), _scrollBarStyle);
            Resources.Add(typeof(TextBox), (Style)XamlReader.Parse(textBox));
        }
        catch { /* gaya gagal dimuat: pakai tampilan bawaan Windows */ }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Judul jendela ikut gelap.</summary>
    static void DarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            int round = 2;
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // sudut jendela membulat (Windows 11)
        }
        catch { }
    }

    // =====================================================================
    // Scroll pelan & halus
    // =====================================================================

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        // pointer di atas daftar yang bisa digulir sendiri (mis. daftar aplikasi): biarkan daftar itu yang menggulir
        for (var d = e.OriginalSource as DependencyObject; d != null && d != _scroll; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ScrollViewer inner && inner.ScrollableHeight > 0) return;
        e.Handled = true;
        if (!_scrolling) _scrollTarget = _scroll.VerticalOffset;
        _scrollTarget = Math.Clamp(_scrollTarget - e.Delta * 0.4, 0, _scroll.ScrollableHeight);
        if (_scrolling) return;
        _scrolling = true;
        CompositionTarget.Rendering += ScrollStep;
    }

    /// <summary>Pasang scroll pelan & halus (sama seperti isi halaman) ke ScrollViewer lain: daftar kategori, daftar pilihan.</summary>
    static ScrollViewer SmoothScroll(ScrollViewer sv)
    {
        double target = 0;
        bool running = false;
        EventHandler? step = null;
        step = (_, _) =>
        {
            double cur = sv.VerticalOffset;
            double next = cur + (target - cur) * 0.22;
            if (Math.Abs(target - next) < 0.5) next = target;
            sv.ScrollToVerticalOffset(next);
            if (next == target) { running = false; CompositionTarget.Rendering -= step; }
        };
        sv.PreviewMouseWheel += (_, e) =>
        {
            if (sv.ScrollableHeight <= 0) return; // tidak bisa digulir: biarkan induknya yang menggulir
            e.Handled = true;
            if (!running) target = sv.VerticalOffset;
            target = Math.Clamp(target - e.Delta * 0.4, 0, sv.ScrollableHeight);
            if (running) return;
            running = true;
            CompositionTarget.Rendering += step;
        };
        sv.Unloaded += (_, _) => { if (running) { running = false; CompositionTarget.Rendering -= step; } };
        return sv;
    }

    void ScrollStep(object? sender, EventArgs e)
    {
        double cur = _scroll.VerticalOffset;
        double next = cur + (_scrollTarget - cur) * 0.22;
        if (Math.Abs(_scrollTarget - next) < 0.5) next = _scrollTarget;
        _scroll.ScrollToVerticalOffset(next);
        if (next == _scrollTarget)
        {
            _scrolling = false;
            CompositionTarget.Rendering -= ScrollStep;
        }
    }

    // =====================================================================
    // Sidebar: pencarian & kategori
    // =====================================================================

    UIElement BuildSearch()
    {
        // ikon dan teks memakai font masing-masing (font ikon tidak punya huruf biasa → jadi kotak-kotak)
        var placeholder = WithIcon("\uE721", L.T("Cari"), 12.5);
        placeholder.Foreground = Secondary;
        placeholder.IsHitTestVisible = false;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.Margin = new Thickness(10, 0, 0, 0);
        _search.TextChanged += (sender, args) =>
        {
            placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_index.Count == 0 && _search.Text.Length > 0) BuildIndex(); // indeks dibuat saat pertama kali mencari saja
            BuildNav();
        };
        var g = new Grid { Margin = new Thickness(12, 14, 12, 8) };
        g.Children.Add(_search);
        g.Children.Add(placeholder);
        return g;
    }

    /// <summary>Kumpulkan semua teks tiap halaman supaya pencarian juga menemukan pengaturan di dalamnya.</summary>
    void BuildIndex()
    {
        foreach (var p in _pages)
        {
            var temp = new StackPanel();
            _panel = temp;
            _group = null;
            _pv = null;
            try { p.Build(); } catch { }
            var sb = new System.Text.StringBuilder(L.T(p.Name));
            void Walk(object o)
            {
                if (o is TextBlock tb) sb.Append(' ').Append(tb.Text);
                if (o is DependencyObject d)
                    foreach (var c in LogicalTreeHelper.GetChildren(d)) Walk(c);
            }
            Walk(temp);
            _index[p] = sb.ToString();
        }
        _panel = _content;
        _group = null;
    }

    void BuildNav()
    {
        _nav.Children.Clear();
        _navIcons.Clear();
        string q = _search.Text.Trim();
        int shown = 0;
        string lastGroup = "";
        for (int i = 0; i < _pages.Count; i++)
        {
            var p = _pages[i];
            if (q.Length > 0 && !(_index.TryGetValue(p, out var text) ? text : p.Name).Contains(q, StringComparison.CurrentCultureIgnoreCase)) continue;
            shown++;
            int index = i;
            bool selected = i == _page;
            if (p.Group.Length > 0 && p.Group != lastGroup)
            {
                _nav.Children.Add(new TextBlock
                {
                    Text = L.T(p.Group).ToUpper(L.Culture), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Secondary,
                    Margin = new Thickness(10, lastGroup.Length == 0 ? 2 : 14, 0, 4),
                });
                lastGroup = p.Group;
            }
            var glyph = new TextBlock
            {
                Text = p.Icon, FontFamily = IconFont, FontSize = 12.5, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var icon = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(6), Background = Frozen(p.IconColor),
                Child = glyph, Margin = new Thickness(0, 0, 10, 0),
            };
            _navIcons.Add((p, icon, glyph));
            PaintNavIcon(p, icon, glyph);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = L.T(p.Name), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160 });
            var item = new Border
            {
                Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand,
                Background = selected ? Accent : Brushes.Transparent,
            };
            item.MouseEnter += (sender, args) => { if (index != _page) item.Background = NavHover; };
            item.MouseLeave += (sender, args) => { if (index != _page) item.Background = Brushes.Transparent; };
            item.MouseLeftButtonUp += (sender, args) => ShowPage(index);
            _nav.Children.Add(item);
        }
        if (shown == 0)
            _nav.Children.Add(new TextBlock { Text = L.T("Tidak ada hasil"), Foreground = Secondary, FontSize = 12.5, Margin = new Thickness(10, 8, 0, 0) });
    }

    /// <summary>Logo berwarna kalau fiturnya menyala, abu-abu kalau mati.</summary>
    static void PaintNavIcon(Page p, Border icon, TextBlock glyph)
    {
        bool on = true;
        try { on = p.IsOn?.Invoke() ?? true; } catch { }
        icon.Background = Frozen(on ? p.IconColor : IconOffColor);
        glyph.Foreground = on ? Brushes.White : Secondary;
        icon.ToolTip = on ? null : L.T("Fitur ini sedang mati");
    }

    /// <summary>Dipanggil tiap pengaturan berubah: perbarui warna logo di menu kiri.</summary>
    void RefreshNavIcons()
    {
        foreach (var (p, icon, glyph) in _navIcons) PaintNavIcon(p, icon, glyph);
    }

    void ShowPage(int index)
    {
        _page = index;
        BuildNav();
        _content.Children.Clear();
        _panel = _content;
        _group = null;
        _pv = null;
        var p = _pages[index];

        // judul halaman + tombol pratinjau
        var head = new Grid { Margin = new Thickness(2, 0, 0, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = L.T(p.Name), FontSize = 26, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (p.Preview is PreviewKind kind)
        {
            var btn = MakeButton(L.T("Lihat di island"), primary: false, () => App.Island?.Preview(kind), icon: "\uE7B3");
            Grid.SetColumn(btn, 1);
            head.Children.Add(btn);
        }
        _content.Children.Add(head);
        if (p.Desc.Length > 0)
            _content.Children.Add(new TextBlock
            {
                Text = L.T(p.Desc), FontSize = 13, Foreground = Secondary, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 0, 0, 6),
            });
        p.Build();
        _group = null;
        _scroll.ScrollToTop();
        _scrollTarget = 0;
    }

    /// <summary>Kontrol yang ditambahkan setelah ini akan memunculkan pratinjau <paramref name="kind"/> saat diubah.</summary>
    void Pv(PreviewKind? kind) => _pv = kind;

    static void Fire(PreviewKind? kind)
    {
        if (kind is PreviewKind k) App.Island?.Preview(k);
    }

    // =====================================================================
    // Isi halaman
    // =====================================================================

    void DefinePages()
    {
        var s = Settings.Current;
        var d = new Settings(); // nilai bawaan, untuk menampilkan ukuran dalam persen
        Func<double, string> Rel(double def) => v => def > 0 ? $"{v / def * 100:0}%" : $"{v:0}";
        const string GUmum = "Umum", GIsland = "Island (notch)", GFitur = "Fitur island", GDesktop = "Desktop", GSistem = "Sistem";

        // ================================================================ UMUM

        _pages.Add(new Page("", Color.FromRgb(0x8E, 0x8E, 0x93), "Umum", PreviewKind.Control, () =>
        {
            AddCombo("Bahasa / Language", new[] { "Bahasa Indonesia", "English" }, L.English ? 1 : 0, i =>
            {
                s.Language = i == 1 ? "en" : "id";
                Title = L.T("Pengaturan") + " · Dynamic Island";
                Dispatcher.BeginInvoke(new Action(() => { _index.Clear(); ShowPage(_page); })); // susun ulang dalam bahasa baru
            }, translate: false);
            AddCheck("Jalankan saat Windows menyala", s.RunAtStartup, v => { s.RunAtStartup = v; SetStartup(v); },
                hint: "Dynamic Island langsung aktif setiap kali laptop dinyalakan.");
            AddHeader("Tema");
            AddThemes();
            AddNote("Klik salah satu tema untuk mengganti warna, bentuk, dan gaya animasi sekaligus. Setelah itu semuanya tetap bisa kamu ubah sendiri di halaman lain.");
        }) { Group = GUmum, Desc = "Bahasa, tema, dan cara Dynamic Island dijalankan." });

        // ================================================================ ISLAND

        _pages.Add(new Page("", Color.FromRgb(0x0A, 0x84, 0xFF), "Halaman island", PreviewKind.Control, () =>
        {
            AddHeader("Halaman yang muncul saat island dibuka");
            AddCheck("Musik", s.PageMusic, v => s.PageMusic = v, hint: "Player lagu (Spotify, YouTube, dll.) — hanya saat ada lagu diputar.");
            AddCheck("Control center", s.PageControl, v => s.PageControl = v, hint: "Tombol cepat Wi-Fi, Bluetooth, Jangan ganggu, dll. plus info baterai/CPU.");
            AddCheck("Kalender & timer", s.PageCalendar, v => s.PageCalendar = v);
            AddCheck("Notifikasi", s.NotificationsEnabled, v => s.NotificationsEnabled = v);
            AddCheck("Performa", s.PagePerformance, v => s.PagePerformance = v, hint: "Grafik CPU, RAM, GPU seperti Task Manager mini.");
            AddCheck("Shelf", s.PageShelf, v => s.PageShelf = v, hint: "Tempat menaruh file sementara: tarik file ke notch, lalu tarik keluar ke aplikasi lain.");
            AddCheck("Riwayat clipboard", s.PageClipboard, v => s.PageClipboard = v, hint: "Daftar teks yang terakhir kamu salin (Ctrl+C).");
            AddCheck("Chat Gemini", s.PageGemini, v => s.PageGemini = v, hint: "Tanya AI Gemini langsung dari notch (perlu API key, lihat halaman Gemini).");
            AddNote("Pindah antar halaman dengan scroll atau geser di island.");
            AddHeader("Isi control center");
            Pv(PreviewKind.Control);
            foreach (var (key, label) in new[] { ("battery", "Baterai"), ("cpu", "CPU"), ("gpu", "GPU"), ("ram", "Memori (RAM)"), ("disk", "Disk (ruang C:)") })
            {
                bool on = (s.StatItems ?? "").Split(',').Any(x => x.Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
                AddCheck(label, on, v =>
                {
                    var list = (s.StatItems ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    list.RemoveAll(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
                    if (v) list.Add(key);
                    s.StatItems = string.Join(",", list);
                });
            }
        }) { Group = GIsland, Desc = "Pilih halaman apa saja yang ada di island saat dibuka." });

        _pages.Add(new Page("", Color.FromRgb(0xFF, 0x37, 0x5F), "Warna & font", PreviewKind.Control, () =>
        {
            AddHeader("Font");
            Pv(PreviewKind.Control);
            var fonts = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Where(n => !n.Contains("Icons") && !n.Contains("MDL2"))
                .Distinct().OrderBy(n => n).ToArray();
            AddCombo("Font", fonts, Math.Max(0, Array.IndexOf(fonts, s.FontName)), i => s.FontName = fonts[i], translate: false,
                fontOf: i => PreviewFont(fonts[i]), hint: "Dipakai di island, panel samping, dan dock.");
            AddHeader("Warna island");
            AddColor("Warna latar", s.IslandColor, v => s.IslandColor = v,
                new[] { "#000000", "#1C1C1E", "#2C2C2E", "#0B1F3A", "#1E1033", "#0F2A1E", "#3A1010" },
                hint: "Warna island, panel samping, dan dock.");
            AddSlider("Transparansi", 0.2, 1, 0.05, s.IslandOpacity, v => s.IslandOpacity = v, Pct, hint: "100% = pekat, makin kecil makin tembus pandang.");
            AddCheck("Lebih pekat saat terbuka", s.ExpandedSolid, v => s.ExpandedSolid = v, hint: "Supaya tulisan tetap mudah dibaca walau island transparan.");
            AddCheck("Garis tepi tipis", s.EdgeHighlight, v => s.EdgeHighlight = v, hint: "Garis terang tipis di pinggir island, seperti di macOS.");
            AddHeader("Warna aksen");
            AddColor("Bar musik & progress lagu", s.AccentColor, v => s.AccentColor = v);
            AddColor("Tombol yang sedang menyala", s.ToggleOnColor, v => s.ToggleOnColor = v, hint: "Misalnya tombol Wi-Fi, Bluetooth, atau Jangan ganggu saat aktif.");
            AddNote("Warna indikator (cas, download, timer, mikrofon) ada di halaman Status & indikator.");
            AddHeader("Jam");
            Pv(PreviewKind.Mini);
            AddCheck("Format 24 jam", s.Use24Hour, v => s.Use24Hour = v, hint: "Mati = format 12 jam (AM/PM).");
            AddCheck("Tampilkan detik", s.ShowSeconds, v => s.ShowSeconds = v);
        }) { Group = GIsland, Desc = "Warna, transparansi, dan font island." });

        _pages.Add(new Page("", Color.FromRgb(0xBF, 0x5A, 0xF2), "Ukuran & bentuk", PreviewKind.Mini, () =>
        {
            AddNote("Persen dihitung dari ukuran bawaan (100% = bawaan). Setiap perubahan langsung terlihat di island.");
            AddHeader("Notch kecil (saat island tertutup)");
            Pv(PreviewKind.Mini);
            AddSlider("Lebar saat diam", 80, 600, 2, s.CollapsedWidth, v => s.CollapsedWidth = v, Rel(d.CollapsedWidth));
            AddSlider("Lebar saat musik diputar", 80, 700, 2, s.CollapsedActiveWidth, v => s.CollapsedActiveWidth = v, Rel(d.CollapsedActiveWidth));
            AddSlider("Tinggi", 20, 80, 1, s.CollapsedHeight, v => s.CollapsedHeight = v, Rel(d.CollapsedHeight));
            AddSlider("Lengkungan sudut bawah", 0, 40, 1, s.CollapsedBottomRadius, v => s.CollapsedBottomRadius = v, Rel(d.CollapsedBottomRadius));
            AddSlider("Lengkungan sambungan ke tepi layar", 0, 40, 1, s.ShoulderRadius, v => s.ShoulderRadius = v, Rel(d.ShoulderRadius),
                hint: "Lengkungan cekung di kiri-kanan atas notch, supaya terlihat menyatu dengan layar.");
            AddHeader("Player musik");
            Pv(PreviewKind.Media);
            AddSlider("Lebar", 200, 900, 2, s.MediaWidth, v => s.MediaWidth = v, Rel(d.MediaWidth));
            AddSlider("Tinggi", 80, 500, 2, s.MediaHeight, v => s.MediaHeight = v, Rel(d.MediaHeight));
            AddHeader("Control center");
            Pv(PreviewKind.Control);
            AddSlider("Lebar", 250, 900, 2, s.ControlWidth, v => s.ControlWidth = v, Rel(d.ControlWidth));
            AddSlider("Tinggi", 150, 600, 2, s.ControlHeight, v => s.ControlHeight = v, Rel(d.ControlHeight));
            AddSlider("Lengkungan sudut bawah (semua halaman)", 0, 80, 1, s.ExpandedBottomRadius, v => s.ExpandedBottomRadius = v, Rel(d.ExpandedBottomRadius));
            AddHeader("Kalender & timer");
            Pv(PreviewKind.Calendar);
            AddSlider("Lebar", 250, 900, 2, s.CalendarWidth, v => s.CalendarWidth = v, Rel(d.CalendarWidth));
            AddSlider("Tinggi", 150, 600, 2, s.CalendarHeight, v => s.CalendarHeight = v, Rel(d.CalendarHeight));
            AddHeader("Notifikasi");
            Pv(PreviewKind.Notifications);
            AddSlider("Lebar", 250, 900, 2, s.NotifWidth, v => s.NotifWidth = v, Rel(d.NotifWidth));
            AddSlider("Tinggi", 150, 600, 2, s.NotifHeight, v => s.NotifHeight = v, Rel(d.NotifHeight));
            AddHeader("Performa");
            Pv(PreviewKind.Performance);
            AddSlider("Lebar", 250, 900, 2, s.PerfWidth, v => s.PerfWidth = v, Rel(d.PerfWidth));
            AddSlider("Tinggi", 150, 600, 2, s.PerfHeight, v => s.PerfHeight = v, Rel(d.PerfHeight));
        }) { Group = GIsland, Desc = "Atur lebar, tinggi, dan lengkungan notch serta tiap halaman island." });

        _pages.Add(new Page("", Color.FromRgb(0xFF, 0x9F, 0x0A), "Animasi", PreviewKind.Control, () =>
        {
            AddHeader("Gerakan");
            Pv(PreviewKind.Control);
            AddSlider("Kecepatan", 50, 1500, 10, s.AnimStiffness, v => s.AnimStiffness = v, v => $"{v:0}",
                hint: "Makin besar, island makin cepat membuka dan menutup.");
            AddSlider("Pantulan", 5, 100, 1, s.AnimDamping, v => s.AnimDamping = v, v => $"{v:0}",
                hint: "Angka kecil = memantul seperti per; angka besar = berhenti mulus tanpa memantul.");
            AddCombo("Kehalusan animasi (FPS)", new[] { "Ikuti layar (paling halus)", "120 fps", "60 fps", "30 fps (paling hemat)" },
                s.AnimationFpsLimit switch { 120 => 1, 60 => 2, 30 => 3, _ => 0 },
                i => s.AnimationFpsLimit = i switch { 1 => 120, 2 => 60, 3 => 30, _ => 0 },
                hint: "Pilih yang lebih rendah kalau ingin hemat baterai.");
            AddHeader("Buka & tutup");
            Pv(null);
            AddSlider("Jeda sebelum terbuka saat pointer di atas notch", 0, 1500, 10, s.HoverExpandDelayMs, v => s.HoverExpandDelayMs = (int)v, Ms,
                hint: "Supaya island tidak terbuka saat pointer hanya lewat.");
            AddSlider("Jeda sebelum tertutup setelah pointer pergi", 0, 3000, 50, s.CollapseDelayMs, v => s.CollapseDelayMs = (int)v, Ms);
            AddSlider("Kecepatan muncul dari tepi layar", 1, 3, 0.25, s.EdgeRevealSpeed, v => s.EdgeRevealSpeed = v,
                v => v <= 1.001 ? L.T("Seperti versi lama") : $"{v:0.##}×", hint: "Berlaku untuk notch, dock, dan panel samping yang muncul dari tepi.");
            AddCheck("Siapkan jendela sebelum island membuka", s.PrewarmWindow, v => s.PrewarmWindow = v,
                hint: "Nyalakan kalau notch sempat terlihat terlepas dari tepi atas saat membuka.");
            AddHeader("Gaya iPhone (opsional)");
            AddCheck("Isi muncul dari buram ke tajam", s.IslandContentBlur, v => s.IslandContentBlur = v,
                hint: "Saat island membuka atau berganti halaman, isinya muncul dari buram menjadi tajam. Efek hanya aktif selama gerakan, jadi saat diam tidak memakai GPU.");
            AddCheck("Notch kenyal", s.IslandJelly, v => s.IslandJelly = v,
                hint: "Saat bergerak cepat, notch sedikit meregang lalu kembali — seperti jeli.");
        }) { Group = GIsland, Desc = "Seberapa cepat dan halus island bergerak." });

        _pages.Add(new Page("", Color.FromRgb(0x63, 0x63, 0x66), "Sembunyi & game", PreviewKind.Mini, () =>
        {
            AddHeader("Kapan island disembunyikan");
            AddCheck("Selalu sembunyi", s.AlwaysHidden, v => s.AlwaysHidden = v, hint: "Island hanya muncul saat pointer diarahkan ke tepi atas layar.");
            AddCheck("Sembunyi saat ada jendela maximize", s.HideWhenMaximized, v => s.HideWhenMaximized = v);
            AddCheck("Sembunyi saat fullscreen", s.HideInFullscreen, v => s.HideInFullscreen = v, hint: "Misalnya saat menonton video atau main game.");
            AddHeader("Memunculkan lagi dari tepi atas");
            AddCheck("Arahkan pointer ke tepi atas untuk memunculkan", s.RevealOnTopEdge, v => s.RevealOnTopEdge = v);
            AddCheck("Seluruh tepi atas bisa dipakai", s.TopRevealFullWidth, v => s.TopRevealFullWidth = v, hint: "Mati = hanya bagian tengah tepi atas (di atas notch).");
            AddSlider("Jeda sebelum muncul", 0, 3000, 50, s.TopRevealDelayMs, v => s.TopRevealDelayMs = (int)v, Ms);
            AddSlider("Lebar area tengah", 60, 1000, 10, s.TopEdgeZoneWidth, v => s.TopEdgeZoneWidth = v, Rel(d.TopEdgeZoneWidth),
                hint: "Seberapa lebar bagian tepi atas yang memunculkan notch (kalau \"Seluruh tepi atas\" mati).");
            AddHeader("Mode game");
            AddCheck("Berhenti total saat fullscreen", s.GameMode, v => s.GameMode = v, hint: "Dynamic Island diam sepenuhnya supaya game berjalan semaksimal mungkin.");
            AddText("Aplikasi pengecualian", s.FullscreenRevealList, v => s.FullscreenRevealList = v,
                hint: "Aplikasi yang tetap boleh memunculkan notch walau fullscreen. Tulis nama prosesnya, pisahkan dengan koma (mis. chrome, vlc).");
        }) { Group = GIsland, Desc = "Kapan island menyingkir supaya tidak mengganggu, misalnya saat nonton atau main game." });

        // ================================================================ FITUR ISLAND

        _pages.Add(new Page("", Color.FromRgb(0xFF, 0x45, 0x3A), "Musik", PreviewKind.Media, () =>
        {
            Pv(PreviewKind.Media);
            AddCheck("Halaman musik di island", s.PageMusic, v => s.PageMusic = v, hint: "Muncul saat ada lagu diputar di Spotify, YouTube, dll.");
            AddHeader("Bar musik (gelombang suara)");
            AddCombo("Gerakan bar", new[] { "Ikuti suara asli", "Animasi dekorasi", "Mati" }, (int)s.Visualizer, i => s.Visualizer = (VisualizerMode)i,
                hint: "\"Ikuti suara asli\" bergerak sesuai lagu; \"Animasi dekorasi\" hanya hiasan yang lebih hemat.");
            AddSlider("Jumlah bar", 3, 12, 1, s.VisualizerBars, v => s.VisualizerBars = (int)v, v => $"{v:0}");
            AddSlider("Kehalusan bar (island terbuka)", 15, 144, 1, s.VisualizerFps, v => s.VisualizerFps = (int)v, v => $"{v:0} fps",
                hint: "Makin tinggi makin halus, tapi sedikit lebih berat.");
            AddSlider("Kehalusan bar (notch kecil)", 10, 144, 1, s.MiniVisualizerFps, v => s.MiniVisualizerFps = (int)v, v => $"{v:0} fps");
            AddCheck("Warna bar mengikuti cover lagu", s.CoverColors, v => s.CoverColors = v, hint: "Mati = pakai warna aksen dari halaman Warna & font.");
            AddNote("Cahaya yang mengikuti warna cover lagu (ambient light) ada di halaman Efek cahaya.");
            AddHeader("Saat lagu berganti");
            Pv(null);
            AddCheck("Buka island sebentar", s.PeekOnTrackChange, v => s.PeekOnTrackChange = v, hint: "Menampilkan judul lagu baru beberapa detik.");
            AddSlider("Lama terbuka", 500, 8000, 100, s.PeekDurationMs, v => s.PeekDurationMs = (int)v, Ms);
            AddHeader("Gestur touchpad");
            AddCheck("Geser 2 jari di island untuk ganti lagu", s.TouchpadGestures, v => s.TouchpadGestures = v,
                hint: "Ke kiri / kanan = lagu sebelumnya / berikutnya. Mouse dengan scroll samping juga bisa.");
            AddCheck("Balik arah geser", s.GestureReverse, v => s.GestureReverse = v);
            AddHeader("Discord");
            Pv(PreviewKind.Mini);
            AddCheck("Ikon Discord di notch saat dalam panggilan", s.DiscordInIsland, v => s.DiscordInIsland = v,
                hint: "Ikon di kiri notch, musik tetap di kanan.");
        }) { Group = GFitur, Desc = "Player lagu di island dan bar gelombang suara.", IsOn = () => s.PageMusic });

        _pages.Add(new Page("", Color.FromRgb(0xFF, 0x37, 0x5F), "Notifikasi", PreviewKind.NotifPeek, () =>
        {
            Pv(PreviewKind.NotifPeek);
            AddCheck("Halaman notifikasi di island", s.NotificationsEnabled, v => s.NotificationsEnabled = v,
                hint: "Notifikasi Windows (Chrome, WhatsApp, Phone Link, dll.) dikumpulkan di island.");
            AddCheck("Notifikasi baru muncul sebentar di notch", s.NotifPeek, v => { s.NotifPeek = v; Dispatcher.BeginInvoke(new Action(() => ShowPage(_page))); });
            if (s.NotifPeek)
                AddCheck("Animasi notifikasi masuk", s.NotifPeekAnimated, v => s.NotifPeekAnimated = v,
                    hint: "Ikon aplikasi muncul membesar dan meluncur masuk, lalu nama aplikasi, judul, dan isi menyusul satu per satu.");
            AddHeader("Aplikasi yang boleh muncul di notch");
            Pv(null);
            if (s.NotifKnownApps.Count == 0)
                AddNote("Belum ada aplikasi yang tercatat. Aplikasi akan muncul di sini setelah mengirim notifikasi.");
            else
            {
                foreach (var (appId, appName) in s.NotifKnownApps.OrderBy(kv => kv.Value, StringComparer.CurrentCultureIgnoreCase))
                {
                    string id = appId;
                    AddCheck(appName.Length > 0 ? appName : appId, !s.IsNotifMuted(id), v => s.SetNotifMuted(id, !v), translate: false);
                }
                AddNote("Notifikasi dari aplikasi yang dimatikan tetap tersimpan di halaman notifikasi, hanya tidak muncul sebentar di notch. Bisa juga lewat klik kanan pada notifikasi di island.");
            }
        }) { Group = GFitur, Desc = "Notifikasi Windows yang tampil di island.", IsOn = () => s.NotificationsEnabled });

        _pages.Add(new Page("", Color.FromRgb(0xFF, 0x9F, 0x0A), "Kalender, cuaca & timer", PreviewKind.Calendar, () =>
        {
            Pv(PreviewKind.Calendar);
            AddCheck("Halaman kalender & timer di island", s.PageCalendar, v => s.PageCalendar = v);
            AddHeader("Jadwal Google Calendar");
            AddCheck("Tampilkan acara hari ini", s.CalendarEvents, v => s.CalendarEvents = v);
            AddText("Alamat rahasia iCal", s.CalendarIcsUrl, v => s.CalendarIcsUrl = v,
                hint: "Cara dapat: buka Google Calendar → Setelan → pilih kalendermu → salin \"Alamat rahasia dalam format iCal\", lalu tempel di sini.");
            AddSlider("Ingatkan sebelum acara", 0, 60, 5, s.ReminderMinutes, v => s.ReminderMinutes = (int)v, v => v == 0 ? L.T("Mati") : $"{v:0} {L.T("menit")}");
            AddNote("Alamat ini rahasia: siapa pun yang punya link-nya bisa melihat jadwalmu. Disimpan hanya di laptop ini.");
            AddHeader("Cuaca");
            AddCheck("Tampilkan cuaca", s.CalendarWeather, v => s.CalendarWeather = v);
            AddText("Kota", s.WeatherCity, v => s.WeatherCity = v, hint: "Contoh: Jakarta, Bandung, Surabaya.");
            AddHeader("Timer");
            AddCheck("Tombol Pomodoro 🍅", s.PomodoroEnabled, v => s.PomodoroEnabled = v, hint: "Timer belajar: 25 menit fokus, lalu 5 menit istirahat.");
        }) { Group = GFitur, Desc = "Jadwal hari ini, cuaca, dan timer di island.", IsOn = () => s.PageCalendar });

        _pages.Add(new Page("", Color.FromRgb(0x5E, 0x5C, 0xE6), "Gemini", PreviewKind.Gemini, () =>
        {
            Pv(PreviewKind.Gemini);
            AddCheck("Halaman chat Gemini di island", s.PageGemini, v => s.PageGemini = v);
            AddHeader("Akun");
            Pv(null);
            AddText("API key Gemini", s.GeminiApiKey, v => s.GeminiApiKey = v,
                hint: "Buat gratis di aistudio.google.com/apikey, lalu tempel di sini.");
            AddText("Model", s.GeminiModel, v => s.GeminiModel = v, hint: "Biarkan seperti bawaan kalau tidak yakin.");
            AddNote("Pertanyaanmu dikirim langsung ke Google. API key disimpan hanya di laptop ini.");
        }) { Group = GFitur, Desc = "Tanya AI Gemini langsung dari notch.", IsOn = () => s.PageGemini });

        _pages.Add(new Page("", Color.FromRgb(0x64, 0xD2, 0xFF), "Shelf & clipboard", null, () =>
        {
            AddHeader("Shelf");
            AddCheck("Halaman Shelf di island", s.PageShelf, v => s.PageShelf = v,
                hint: "Tarik file ke notch untuk disimpan sementara, lalu tarik keluar ke aplikasi lain.");
            AddCheck("Tampilkan isi file", s.ShelfThumbnails, v => s.ShelfThumbnails = v,
                hint: "Halaman PDF/Word, gambar, atau video tampil sebagai gambar kecil, bukan ikon biasa.");
            AddCheck("Pratinjau besar saat pointer diam di atas file", s.ShelfHoverPreview, v => s.ShelfHoverPreview = v,
                hint: "Seperti Quick Look di Mac: gambar besar + nama, ukuran, dan tanggal file.");
            AddHeader("Clipboard");
            AddCheck("Halaman riwayat clipboard di island", s.PageClipboard, v => s.PageClipboard = v,
                hint: "Menyimpan teks yang terakhir kamu salin, supaya bisa ditempel lagi kapan saja.");
        }) { Group = GFitur, Desc = "Tempat file dan teks sementara di island.", IsOn = () => s.PageShelf || s.PageClipboard });

        _pages.Add(new Page("", Color.FromRgb(0x30, 0xD1, 0x58), "Status & indikator", PreviewKind.Charge, () =>
        {
            AddHeader("Baterai");
            Pv(PreviewKind.Charge);
            AddCheck("Garis di tepi notch saat mengisi daya", s.ShowChargeLine, v => s.ShowChargeLine = v);
            AddCheck("Animasi saat charger dicolok / dicabut", s.ChargeAnimation, v => s.ChargeAnimation = v);
            AddColor("Warna", s.ChargeColor, v => s.ChargeColor = v);
            AddHeader("Download");
            Pv(PreviewKind.Download);
            AddCheck("Garis progress download di tepi notch", s.DownloadProgress, v => s.DownloadProgress = v);
            AddColor("Warna", s.DownloadColor, v => s.DownloadColor = v);
            AddNote("Supaya persen download di Chrome tepat, pasang ekstensi di folder chrome-extension (lihat CARA-PASANG.txt).");
            AddHeader("Timer");
            Pv(PreviewKind.Timer);
            AddCheck("Garis sisa waktu timer di tepi notch", s.TimerRim, v => s.TimerRim = v);
            AddColor("Warna", s.TimerColor, v => s.TimerColor = v);
            AddHeader("Privasi");
            Pv(PreviewKind.Privacy);
            AddCheck("Titik saat mikrofon dipakai", s.PrivacyDots, v => s.PrivacyDots = v, hint: "Arahkan pointer ke titiknya untuk melihat aplikasi yang memakai mikrofon.");
            AddColor("Warna titik mikrofon", s.MicDotColor, v => s.MicDotColor = v);
            AddCheck("Titik saat kamera dipakai", s.CameraDot, v => s.CameraDot = v,
                hint: "Windows versi terbaru tidak lagi memberi tahu aplikasi lain kapan kamera dipakai, jadi titik ini mungkin tidak muncul.");
            AddColor("Warna titik kamera", s.CameraDotColor, v => s.CameraDotColor = v);
            AddHeader("Panggilan & rekam layar");
            Pv(PreviewKind.Call);
            AddCheck("Pil hijau + durasi saat dalam panggilan", s.CallIndicator, v => s.CallIndicator = v,
                hint: "Discord, WhatsApp, Zoom, Meet, dll. Kalau ikon Discord di notch (halaman Musik) menyala, panggilan Discord cukup ditandai ikon itu.");
            AddCheck("Pil merah + durasi saat layar direkam", s.RecordIndicator, v => s.RecordIndicator = v, hint: "OBS, Snipping Tool, dll.");
            AddHeader("Bluetooth");
            Pv(PreviewKind.Bluetooth);
            AddCheck("Pop-up saat perangkat Bluetooth tersambung", s.BluetoothPopup, v => s.BluetoothPopup = v,
                hint: "Lengkap dengan sisa baterai, kalau perangkatnya melaporkannya ke Windows.");
        }) { Group = GFitur, Desc = "Tanda kecil di notch: mengisi daya, download, timer, mikrofon, panggilan, dan Bluetooth.",
             IsOn = () => s.ShowChargeLine || s.ChargeAnimation || s.DownloadProgress || s.TimerRim || s.PrivacyDots || s.CameraDot
                          || s.CallIndicator || s.RecordIndicator || s.BluetoothPopup });

        // ================================================================ DESKTOP

        _pages.Add(new Page("\uE771", Color.FromRgb(0x30, 0xB0, 0xC7), "Wallpaper", null, () =>
        {
            void Rebuild() => Dispatcher.BeginInvoke(new Action(() => ShowPage(_page)));
            AddCheck("Atur wallpaper dari Dynamic Island", s.WallpaperEnabled, v => { s.WallpaperEnabled = v; Rebuild(); },
                hint: "Nyala (bawaan), tapi wallpaper Windows kamu baru diganti setelah kamu memilih gambar atau folder di bawah.");
            if (s.WallpaperEnabled)
            {
                AddCombo("Sumber", new[] { "Satu gambar", "Folder (berganti otomatis)", "Siang & malam" },
                    Math.Clamp(s.WallpaperMode, 0, 2), i => { s.WallpaperMode = i; Rebuild(); });
                switch (s.WallpaperMode)
                {
                    case 1:
                    {
                        AddPathPicker("Folder gambar", s.WallpaperFolder, true, v => { s.WallpaperFolder = v; s.WallpaperIndex = -1; },
                            hint: L.T("Isi folder: ") + WallpaperService.ListFolder(s.WallpaperFolder).Count + L.T(" gambar (JPG, PNG, BMP, TIFF, WEBP)."));
                        int[] mins = { 15, 30, 60, 180, 360, 1440 };
                        int sel = Array.IndexOf(mins, s.WallpaperIntervalMin);
                        AddCombo("Ganti setiap", new[] { "15 menit", "30 menit", "1 jam", "3 jam", "6 jam", "Sehari" },
                            sel < 0 ? 2 : sel, i => s.WallpaperIntervalMin = mins[i]);
                        AddCheck("Urutan acak", s.WallpaperShuffle, v => s.WallpaperShuffle = v);
                        AddCheck("Gambar berbeda di tiap monitor", s.WallpaperPerMonitor, v => s.WallpaperPerMonitor = v,
                            hint: "Kalau ada lebih dari satu monitor, masing-masing mendapat gambar sendiri dari folder ini.");
                        var nextBtn = MakeButton(L.T("Ganti sekarang"), primary: false, WallpaperService.Next);
                        nextBtn.HorizontalAlignment = HorizontalAlignment.Left;
                        nextBtn.Margin = new Thickness(2, 10, 0, 0);
                        _group = null;
                        _panel.Children.Add(nextBtn);
                        break;
                    }
                    case 2:
                    {
                        AddPathPicker("Gambar siang", s.WallpaperDayImage, false, v => s.WallpaperDayImage = v);
                        AddPathPicker("Gambar malam", s.WallpaperNightImage, false, v => s.WallpaperNightImage = v);
                        AddCheck("Ikut matahari terbit & terbenam", s.WallpaperBySun, v => { s.WallpaperBySun = v; Rebuild(); },
                            hint: "Memakai kota di Pengaturan → Kalender, cuaca & timer. Mati = jam tetap di bawah.");
                        if (s.WallpaperBySun)
                        {
                            if (s.WallpaperLat is double lat && s.WallpaperLon is double lon &&
                                WallpaperService.SunTimes(DateTime.Today, lat, lon, out var rise, out var set) && s.WallpaperGeoCity.Length > 0)
                                AddNote(L.T("Hari ini di ") + s.WallpaperGeoCity + L.T(": terbit ") + rise.ToString(@"hh\.mm") + L.T(", terbenam ") + set.ToString(@"hh\.mm") + ".");
                            else
                                AddNote("Jam matahari belum diketahui (kota belum diisi atau belum dicari). Sementara memakai jam tetap di bawah.");
                        }
                        AddSlider("Siang mulai", 0, 23, 1, s.WallpaperDayHour, v => s.WallpaperDayHour = (int)v, v => $"{v:00}.00");
                        AddSlider("Malam mulai", 0, 23, 1, s.WallpaperNightHour, v => s.WallpaperNightHour = (int)v, v => $"{v:00}.00");
                        break;
                    }
                    default:
                        AddPathPicker("Gambar", s.WallpaperImage, false, v => s.WallpaperImage = v);
                        AddPathPicker("Gambar untuk monitor lain", s.WallpaperImageOther, false, v => s.WallpaperImageOther = v,
                            hint: "Opsional. Kosong = semua monitor memakai gambar yang sama.", clearable: true);
                        break;
                }
                AddHeader("Tampilan");
                AddCombo("Posisi gambar", new[] { "Isi layar (bawaan)", "Pas (gambar utuh)", "Regang", "Tengah", "Ubin", "Bentang ke semua monitor" },
                    Math.Clamp(s.WallpaperFit, 0, 5), i => s.WallpaperFit = i);
                AddCheck("Efek fade saat berganti", s.WallpaperFade, v => s.WallpaperFade = v,
                    hint: "Gambar lama memudar pelan (±1 detik). Ikon desktop ikut tertutup sebentar selama fade.");
                AddCheck("Lock screen ikut diganti", s.WallpaperLockScreen, v => s.WallpaperLockScreen = v,
                    hint: "Memakai gambar monitor utama. Kalau lock screen Windows memakai Windows Spotlight, ganti dulu ke \"Picture\" di Pengaturan Windows → Personalization → Lock screen.");
                var applyBtn = MakeButton(L.T("Terapkan sekarang"), primary: true, WallpaperService.ApplyNow);
                applyBtn.HorizontalAlignment = HorizontalAlignment.Left;
                applyBtn.Margin = new Thickness(2, 12, 0, 0);
                _group = null;
                _panel.Children.Add(applyBtn);
                if (WallpaperService.Status.Length > 0) AddNote(WallpaperService.Status);
                if (s.WallpaperLockScreen && WallpaperService.LockStatus.Length > 0) AddNote(WallpaperService.LockStatus);
            }
            AddHeader("Warna dari wallpaper");
            AddCheck("Warna aksen ikut wallpaper", s.AccentFromWallpaper, v => s.AccentFromWallpaper = v,
                hint: "Bar musik & progress lagu memakai warna utama wallpaper dan berubah saat wallpaper berganti. Berlaku juga untuk wallpaper yang dipasang dari Pengaturan Windows. Warna lamamu dikembalikan saat dimatikan.");
            AddCheck("Cahaya ambient ikut wallpaper saat tidak ada lagu", s.AmbientFromWallpaper, v => s.AmbientFromWallpaper = v,
                hint: "Cahaya lembut di sekitar island memakai warna wallpaper. Perlu \"Cahaya mengikuti warna cover lagu\" di Efek cahaya menyala.");
        }) { Group = GDesktop, Desc = "Wallpaper satu gambar, folder bergilir, atau siang & malam — plus lock screen dan warna otomatis.",
             IsOn = () => s.WallpaperEnabled || s.AccentFromWallpaper || s.AmbientFromWallpaper });

        _pages.Add(new Page("\uE714", Color.FromRgb(0xFF, 0x37, 0x5F), "Live wallpaper", null, () =>
        {
            void Rebuild() => Dispatcher.BeginInvoke(new Action(() => ShowPage(_page)));
            AddCheck("Nyalakan live wallpaper", s.LiveWallpaperEnabled, v => { s.LiveWallpaperEnabled = v; Rebuild(); },
                hint: "Video bergerak di belakang ikon desktop. Berjalan di proses terpisah (\"DynamicIsland.LiveWallpaper.exe\" di Task Manager → Details), jadi notch & dock aman kalau videonya bermasalah. Memakai RAM ±80–200 MB (video 1080p).");
            if (!s.LiveWallpaperEnabled) return;
            AddHeader("File");
            var monitors = Monitors.All();
            var files = s.LiveWallpaperFiles ??= new List<string>();
            while (files.Count < monitors.Count) files.Add("");
            for (int i = 0; i < monitors.Count; i++)
            {
                int idx = i;
                string label = monitors.Count == 1 ? L.T("Video / gambar") : (i == 0 ? L.T("Layar utama") : L.T("Monitor") + " " + (i + 1));
                AddPathPicker(label, files[i], false, v =>
                {
                    // tolak file yang terlalu besar (mis. video 2 GB) supaya tidak memakan RAM & disk berlebihan
                    long size = 0;
                    try { if (v.Length > 0) size = new System.IO.FileInfo(v).Length; } catch { }
                    if (size > LiveWallpaperManager.MaxBytes(s))
                    {
                        MessageBox.Show(this, L.T("File ini ") + $"{size / 1048576.0:0} MB" + L.T(", melebihi batas ") + $"{s.LiveWallpaperMaxMB} MB." + "\n\n" +
                            L.T("Pakai video yang lebih kecil (disarankan 1080p, di bawah 200 MB), atau naikkan batasnya di bawah."),
                            "Dynamic Island", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    files[idx] = v;
                }, video: true, clearable: i > 0,
                    hint: i == 0 ? "MP4 paling aman (H.264/H.265, 1080p). Gambar juga bisa." : "Kosong = sama dengan layar utama.");
            }
            AddHeader("Pemutaran");
            AddSlider("Batas ukuran video", 50, 2000, 50, s.LiveWallpaperMaxMB, v => s.LiveWallpaperMaxMB = (int)v, v => $"{v:0} MB",
                hint: "File yang lebih besar ditolak. Video 1080p 30 detik biasanya cuma 20–80 MB.");
            AddCombo("Posisi video", new[] { "Isi layar (bawaan)", "Pas (gambar utuh)", "Regang" }, Math.Clamp(s.LiveWallpaperFit, 0, 2), i => s.LiveWallpaperFit = i);
            AddCheck("Loop mulus (tanpa jeda)", s.LiveWallpaperSeamlessLoop, v => s.LiveWallpaperSeamlessLoop = v,
                hint: "Dua pemutar bergantian supaya tidak ada jeda saat video mengulang. Memakai RAM/VRAM kira-kira 2× satu video. Matikan untuk cara lama (satu pemutar, ada jeda singkat tiap loop).");

            AddHeader("Suara");
            AddCheck("Putar suara video", s.LiveWallpaperSound, v => { s.LiveWallpaperSound = v; Rebuild(); },
                hint: "Bawaan bisu. Suara hanya dari video di layar utama.");
            if (s.LiveWallpaperSound)
                AddSlider("Volume", 0, 1, 0.05, s.LiveWallpaperVolume, v => s.LiveWallpaperVolume = v, Pct);

            AddHeader("Jeda otomatis (hemat baterai & CPU)");
            AddCheck("Jeda saat tertutup jendela penuh", s.LiveWallpaperPauseCovered, v => s.LiveWallpaperPauseCovered = v,
                hint: "Video berhenti di monitor yang tertutup aplikasi maximize / fullscreen.");
            AddCheck("Jeda saat memakai baterai", s.LiveWallpaperPauseOnBattery, v => s.LiveWallpaperPauseOnBattery = v);
            AddCheck("Jeda saat mode game", s.LiveWallpaperPauseGame, v => s.LiveWallpaperPauseGame = v);

            AddHeader("Hemat memori");
            AddCheck("Lepas memori saat lama dijeda", s.LiveWallpaperReleaseWhenPaused, v => { s.LiveWallpaperReleaseWhenPaused = v; Rebuild(); },
                hint: "Kalau video dijeda cukup lama (desktop tertutup aplikasi), pemutarnya ditutup dan frame terakhir ditampilkan sebagai gambar diam. Hemat ±500 MB RAM. Saat desktop terlihat lagi video dimuat ulang (±0,3 dtk, tanpa layar hitam). Hanya untuk mode loop mulus.");
            if (s.LiveWallpaperReleaseWhenPaused)
                AddSlider("Lepas setelah dijeda", 5, 300, 5, s.LiveWallpaperReleaseAfterSec, v => s.LiveWallpaperReleaseAfterSec = (int)v, v => $"{v:0} s");
            if (LiveWallpaperManager.Status.Length > 0) AddNote(LiveWallpaperManager.Status);
        }) { Group = GDesktop, Desc = "Video bergerak di belakang ikon desktop — per monitor, suara opsional, jeda otomatis.",
             IsOn = () => s.LiveWallpaperEnabled });

        _pages.Add(new Page("\uE8A9", Color.FromRgb(0x5E, 0x5C, 0xE6), "Widget", null, () =>
        {
            void Rebuild() => Dispatcher.BeginInvoke(new Action(() => ShowPage(_page)));
            AddCheck("Tampilkan widget di desktop", s.WidgetsEnabled, v =>
            {
                s.WidgetsEnabled = v;
                if (v && s.Widgets == null) s.Widgets = WidgetManager.Defaults();
                if (!v) WidgetManager.SetEditMode(false);
                Rebuild();
            }, hint: "Jam, cuaca, musik, dan performa di desktop. Widget selalu di belakang aplikasi — terlihat saat desktop terbuka (juga saat Win+D) dan berhenti total saat tertutup jendela penuh.");
            if (!s.WidgetsEnabled) return;

            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 10, 0, 0) };
            Border editBtn = null!;
            editBtn = MakeButton(WidgetManager.EditMode ? L.T("Selesai mengatur posisi") : L.T("Atur posisi widget"), primary: true, () =>
            {
                WidgetManager.SetEditMode(!WidgetManager.EditMode);
                if (editBtn.Child is TextBlock tb) tb.Text = WidgetManager.EditMode ? L.T("Selesai mengatur posisi") : L.T("Atur posisi widget");
            });
            tools.Children.Add(editBtn);
            var reset = MakeButton(L.T("Susun ulang otomatis"), primary: false, WidgetManager.ResetLayout);
            reset.Margin = new Thickness(10, 0, 0, 0);
            tools.Children.Add(reset);
            _group = null;
            _panel.Children.Add(tools);
            AddNote("Seperti macOS: widget menempel ke grid dan widget lain otomatis menyingkir saat kamu menggeser. Bisa pindah monitor. Klik kanan widget untuk menu cepat.");
            AddSlider("Ukuran grid", 120, 220, 2, s.WidgetCellSize, v => s.WidgetCellSize = v, v => $"{v:0} px",
                hint: "Seperti widget iPhone: semua widget — teks, ikon, foto, sudut, dan jaraknya — ikut membesar/mengecil bersama grid.");
            AddSlider("Jarak dari tepi layar", 0, 160, 4, s.WidgetScreenMargin, v => s.WidgetScreenMargin = v, v => $"{v:0} px",
                hint: "Grid tidak sampai ke ujung layar. Area dock di bawah selalu dikosongkan, dan grid ditaruh di tengah.");

            var monitors = Monitors.All();
            string[] monNames = monitors.Select((m, i) => i == 0 ? L.T("Layar utama") : L.T("Monitor") + " " + (i + 1)).ToArray();
            string[] kinds = { "Jam", "Cuaca", "Musik", "Performa", "Catatan" };
            var fontList = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Where(n => !n.Contains("Icons") && !n.Contains("MDL2"))
                .Distinct().OrderBy(n => n).ToArray();
            string[] fontOptions = new[] { L.T("Ikut font app") }.Concat(fontList).ToArray();
            var list = s.Widgets ?? new List<WidgetConfig>();
            foreach (var cfg in list.ToList())
            {
                string where = monitors.Count > 1 ? " · " + monNames[Math.Clamp(cfg.Monitor, 0, monNames.Length - 1)] : "";
                AddHeader(L.T(kinds[Math.Clamp(cfg.Kind, 0, kinds.Length - 1)]) + where);
                AddCheck("Tampilkan", cfg.Visible, v => cfg.Visible = v);
                if (monitors.Count > 1)
                    AddCombo("Monitor", monNames, Math.Clamp(cfg.Monitor, 0, monNames.Length - 1), i => { cfg.Monitor = i; cfg.Col = cfg.Row = -1; Rebuild(); }, translate: false);
                AddCombo("Ukuran", new[] { "Kecil", "Sedang", "Besar", "Melebar" }, Math.Clamp(cfg.Size, 0, 3), i => cfg.Size = i);
                AddCombo("Bentuk", new[] { "Kotak membulat", "Lingkaran", "Pil" }, Math.Clamp(cfg.Shape, 0, 2), i => cfg.Shape = i,
                    hint: "Lingkaran hanya untuk ukuran kecil & besar; di ukuran lain menjadi pil.");
                AddCombo("Gaya", new[] { "Kaca", "Pekat", "Minimal" }, Math.Clamp(cfg.Style, 0, 2), i => { cfg.Style = i; Rebuild(); });
                if (cfg.Style != 2)
                {
                    AddCombo("Warna latar", new[] { "Gelap (bawaan)", "Ikut wallpaper", "Pilih sendiri" }, Math.Clamp(cfg.BgMode, 0, 2),
                        i => { cfg.BgMode = i; Rebuild(); },
                        hint: "Warna teks otomatis menyesuaikan: latar terang → teks gelap, supaya selalu terbaca.");
                    if (cfg.BgMode == 2) AddColor("Warna latar pilihan", cfg.BgColor, v => cfg.BgColor = v,
                        new[] { "#1C1C1E", "#2C2C2E", "#F2F2F7", "#0B1F3A", "#1E1033", "#0F2A1E", "#3A1010", "#FFD60A" });
                }
                AddCombo("Warna", new[] { "Ikut warna aksen", "Ikut wallpaper", "Pilih sendiri" }, Math.Clamp(cfg.ColorMode, 0, 2), i => { cfg.ColorMode = i; Rebuild(); });
                if (cfg.ColorMode == 2) AddColor("Warna pilihan", cfg.Color, v => cfg.Color = v);
                AddCombo("Font", fontOptions, Math.Max(0, Array.IndexOf(fontList, cfg.Font) + 1), i => cfg.Font = i == 0 ? "" : fontList[i - 1],
                    translate: false, fontOf: i => i == 0 ? null : PreviewFont(fontList[i - 1]));
                AddCombo("Ketebalan huruf", new[] { "Bawaan", "Tipis", "Normal", "Tebal" }, Math.Clamp(cfg.FontWeightMode, 0, 3), i => cfg.FontWeightMode = i);
                AddCombo("Warna teks", new[] { "Putih", "Ikut warna widget" }, Math.Clamp(cfg.TextColor, 0, 1), i => cfg.TextColor = i);
                AddCombo("Cahaya ambient", new[] { "Mati", "Ikut warna widget", "Ikut lagu yang diputar" }, Math.Clamp(cfg.Ambient, 0, 2),
                    i => { cfg.Ambient = i; Rebuild(); },
                    hint: "Cahaya lembut di sekeliling widget. \"Ikut lagu\": warnanya dari cover lagu yang sedang diputar, dan padam saat tidak ada lagu.");
                if (cfg.Ambient != 0)
                    AddSlider("Kekuatan cahaya", 0.2, 1, 0.05, cfg.AmbientStrength, v => cfg.AmbientStrength = v, Pct);
                if (cfg.Kind == 4) AddNoteBox(cfg);
                if (cfg.Kind == 1 && s.WeatherCity.Trim().Length == 0)
                    AddNote("Isi kota di Pengaturan → Kalender, cuaca & timer supaya widget cuaca terisi.");
                var remove = MakeButton(L.T("Hapus widget ini"), primary: false, () =>
                {
                    list.Remove(cfg);
                    Settings.NotifyChanged();
                    Rebuild();
                });
                remove.HorizontalAlignment = HorizontalAlignment.Left;
                remove.Margin = new Thickness(2, 8, 0, 0);
                _group = null;
                _panel.Children.Add(remove);
            }

            AddHeader("Tambah widget");
            var adds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 4, 0, 0) };
            for (int k = 0; k < kinds.Length; k++)
            {
                int kind = k;
                var b = MakeButton("+ " + L.T(kinds[k]), primary: false, () =>
                {
                    s.Widgets ??= new List<WidgetConfig>();
                    s.Widgets.Add(new WidgetConfig { Kind = kind, Size = kind is 0 or 2 or 4 ? 1 : 0 });
                    Settings.NotifyChanged();
                    Rebuild();
                });
                if (k > 0) b.Margin = new Thickness(8, 0, 0, 0);
                adds.Children.Add(b);
            }
            _group = null;
            _panel.Children.Add(adds);

            AddHeader("Gerakan");
            AddCheck("Widget bergeser halus", s.WidgetAnimateMove, v => s.WidgetAnimateMove = v,
                hint: "Widget yang menyingkir atau dilepas bergeser dengan animasi, bukan langsung lompat ke tempat baru.");
        }) { Group = GDesktop, Desc = "Jam, cuaca, musik, dan performa di desktop — ukuran, bentuk, gaya, dan warna bisa diatur.",
             IsOn = () => s.WidgetsEnabled });

        _pages.Add(new Page("", Color.FromRgb(0x0A, 0x84, 0xFF), "Dock", null, () =>
        {
            void Rebuild() => Dispatcher.BeginInvoke(new Action(() => ShowPage(_page)));
            AddHeader("Umum");
            AddCheck("Aktifkan dock", s.DockEnabled, v => s.DockEnabled = v, hint: "Baris aplikasi di bawah layar seperti di Mac. Perlu buka ulang Dynamic Island.");
            AddCheck("Sembunyikan taskbar Windows", s.HideWindowsTaskbar, v => s.HideWindowsTaskbar = v, hint: "Taskbar dikembalikan otomatis saat Dynamic Island ditutup.");
            AddCheck("Sembunyikan juga taskbar di monitor kedua", s.HideSecondaryTaskbars, v => s.HideSecondaryTaskbars = v,
                hint: "Mati (bawaan) = taskbar di monitor tambahan tetap ada, karena dock hanya muncul di layar utama. Taskbar itu ikut mode sembunyi otomatis: muncul saat pointer ke tepi bawah monitor tersebut.");
            AddCheck("Tampilkan aplikasi yang sedang terbuka", s.DockShowRunning, v => s.DockShowRunning = v);
            AddHeader("Tampilan");
            AddSlider("Ukuran ikon", 28, 72, 1, s.DockIconSize, v => s.DockIconSize = v, Rel(d.DockIconSize));
            AddCombo("Bentuk ikon", new[] { "Kotak membulat (bawaan)", "Lingkaran", "Kaca", "Ikon asli tanpa kotak" },
                Math.Clamp(s.DockIconShape, 0, 3), i => s.DockIconShape = i);
            AddCombo("Warna ikon", new[] { "Warna asli (bawaan)", "Monokrom (abu-abu)", "Satu warna pilihan" },
                Math.Clamp(s.IconColorMode, 0, 2), i => { s.IconColorMode = i; Rebuild(); }, hint: "Berlaku untuk ikon di dock dan di menu dock.");
            if (s.IconColorMode == 2)
                AddColor("Warna pilihan untuk ikon", s.IconTintColor, v => s.IconTintColor = v);
            AddHeader("Gerakan ikon");
            AddCheck("Ikon membesar & memantul", s.DockMagnify, v => { s.DockMagnify = v; Rebuild(); }, hint: "Ikon membesar mengikuti pointer dan memantul saat aplikasi dibuka, seperti di Mac.");
            if (s.DockMagnify)
            {
                AddCheck("Pembesaran ikon halus", s.DockMagnifySmooth, v => s.DockMagnifySmooth = v,
                    hint: "Ikon membesar dan mengecil dengan gerak pegas (mengikuti pengaturan Animasi), tidak langsung lompat.");
                AddCheck("Pantulan seperti bola", s.DockBounceRealistic, v => s.DockBounceRealistic = v,
                    hint: "Saat aplikasi dibuka, ikon memantul seperti bola jatuh: tiap pantulan makin rendah dan makin cepat.");
            }
            AddHeader("Preview jendela");
            AddSlider("Jeda sebelum preview jendela muncul", 100, 3000, 50, s.DockPreviewDelayMs, v => s.DockPreviewDelayMs = (int)v, Ms,
                hint: "Preview muncul saat pointer diam di atas ikon aplikasi yang terbuka.");
            AddHeader("Sembunyi otomatis");
            AddCheck("Sembunyi saat ada jendela maximize", s.DockSmart, v => s.DockSmart = v, hint: "Dock muncul lagi saat pointer diarahkan ke tepi bawah layar.");
            AddCheck("Seluruh tepi bawah bisa dipakai", s.DockRevealFullWidth, v => s.DockRevealFullWidth = v, hint: "Mati = hanya tepi bawah tepat di bawah dock.");
            AddHeader("Menu dock");
            AddCheck("Tombol kiri dock membuka menu Dynamic Island", s.DockMenu, v => { s.DockMenu = v; Dispatcher.BeginInvoke(new Action(() => ShowPage(_page))); },
                hint: "Dock memanjang ke atas jadi menu: cari aplikasi, aplikasi yang disematkan, semua aplikasi, dan tombol power. Mati = tombol itu membuka Start Windows seperti dulu.");
            AddCheck("Tombol Windows juga membuka menu ini", s.WinKeyOpensMenu, v => s.WinKeyOpensMenu = v,
                hint: "Menggantikan Start Windows saat tombol Windows ditekan sendirian. Kombinasi seperti Win+E, Win+D, dan Win+Shift+S tetap normal. Start Windows masih bisa dibuka dengan Ctrl+Esc.");
            AddCheck("Ikon di menu bergaya sama dengan dock", s.MenuIconsLikeDock, v => s.MenuIconsLikeDock = v,
                hint: "Bentuk (kotak membulat / lingkaran / kaca) dan warna latar ikon di menu mengikuti ikon dock. Mati = ikon polos tanpa kotak.");
            AddCheck("Bagian Phone Link", s.DockMenuPhoneLink, v => s.DockMenuPhoneLink = v,
                hint: "Menampilkan HP yang tersambung lewat Bluetooth (dengan sisa baterai) dan tombol untuk membuka Phone Link.");
            AddNote("Sematkan aplikasi ke menu: buka menu, klik kanan aplikasinya, lalu pilih \"Sematkan ke menu\". Tekan Esc atau klik di luar untuk menutup.");
        }) { Group = GDesktop, Desc = "Pengganti taskbar Windows di bawah layar, plus menu aplikasi yang muncul dari dock.", IsOn = () => s.DockEnabled });

        _pages.Add(new Page("", Color.FromRgb(0xFF, 0x9F, 0x0A), "Panel volume & kecerahan", null, () =>
        {
            AddCheck("Aktifkan panel samping", s.EnableEdgePanels, v => s.EnableEdgePanels = v,
                hint: "Arahkan pointer ke tepi kiri / kanan layar untuk mengatur volume dan kecerahan.");
            AddCheck("Tampilkan angka persen", s.ShowPanelPercent, v => s.ShowPanelPercent = v);
            AddHeader("Kapan panel muncul");
            AddSlider("Jeda di desktop", 0, 5000, 50, s.EdgeDelayDesktopMs, v => s.EdgeDelayDesktopMs = (int)v, Ms);
            AddSlider("Jeda saat ada jendela terbuka", 0, 5000, 50, s.EdgeWindowDelayMs, v => s.EdgeWindowDelayMs = (int)v, Ms);
            AddSlider("Jeda saat fullscreen", 0, 5000, 50, s.EdgeDelayFullscreenMs, v => s.EdgeDelayFullscreenMs = (int)v, Ms,
                hint: "Jeda lebih lama mencegah panel muncul tanpa sengaja saat main game.");
            AddSlider("Bagian tepi yang aktif", 10, 100, 5, s.EdgeZonePercent, v => s.EdgeZonePercent = v, v => $"{v:0}{L.T("% tengah layar")}",
                hint: "Seberapa panjang bagian tepi kiri / kanan (dihitung dari tengah) yang memunculkan panel.");
            AddSlider("Toleransi dari tepi layar", 0, 20, 1, s.EdgeTolerancePx, v => s.EdgeTolerancePx = (int)v, v => v == 0 ? L.T("Harus mentok") : $"{v:0}",
                hint: "Seberapa dekat pointer harus ke tepi. Juga berlaku untuk notch dan dock.");
            AddSlider("Hilang setelah pointer pergi", 100, 3000, 50, s.EdgeHideDelayMs, v => s.EdgeHideDelayMs = (int)v, Ms);
            AddHeader("Mengatur");
            AddSlider("Perubahan per scroll", 1, 20, 1, s.ScrollStepPercent, v => s.ScrollStepPercent = (int)v, v => $"{v:0}%",
                hint: "Scroll di panel untuk mengubah volume / kecerahan.");
            AddSlider("Jeda sebelum daftar aplikasi muncul", 0, 3000, 50, s.AppListOpenDelayMs, v => s.AppListOpenDelayMs = (int)v, Ms,
                hint: "Daftar volume per aplikasi (Spotify, Chrome, dll.) di panel volume.");
            AddHeader("Tombol volume & kecerahan di keyboard");
            Pv(PreviewKind.Hud);
            AddCombo("Tampilkan di", new[] { "Panel samping", "Notch (island)", "Keduanya" },
                Math.Clamp(s.VolumeHudMode, 0, 2), i => s.VolumeHudMode = i);
            Pv(null);
            AddCheck("Tampilkan saat tombol ditekan", s.ShowPanelOnKeyChange, v => s.ShowPanelOnKeyChange = v);
            AddCheck("Ganti pop-up volume Windows", s.ReplaceVolumeOsd, v => s.ReplaceVolumeOsd = v, hint: "Pop-up volume bawaan Windows diganti dengan tampilan Dynamic Island.");
            AddCheck("Sembunyikan pop-up volume & kecerahan Windows", s.HideNativeOsd, v => s.HideNativeOsd = v, hint: "Termasuk saat menekan F2 / F3.");
            AddSlider("Perubahan per tombol volume", 1, 10, 1, s.KeyVolumeStepPercent, v => s.KeyVolumeStepPercent = (int)v, v => $"{v:0}%");
        }) { Group = GDesktop, Desc = "Panel di tepi kiri / kanan layar untuk mengatur volume dan kecerahan.", IsOn = () => s.EnableEdgePanels });

        _pages.Add(new Page("", Color.FromRgb(0xFF, 0xD6, 0x0A), "Efek cahaya", PreviewKind.Spotlight, () =>
        {
            AddHeader("Cahaya pointer");
            Pv(PreviewKind.Spotlight);
            AddNote("Cahaya lembut yang mengikuti pointer saat diarahkan ke island, dock, atau panel.");
            AddCheck("Di island", s.HoverSpotlight, v => s.HoverSpotlight = v);
            AddCheck("Di dock", s.SpotlightDock, v => s.SpotlightDock = v);
            AddCheck("Di panel volume & kecerahan", s.SpotlightPanels, v => s.SpotlightPanels = v);
            AddCombo("Bentuk", new[] { "Bulat (bawaan)", "Oval lebar", "Cincin", "Liquid glass (berubah bentuk saat pointer bergerak)" },
                Math.Clamp(s.PointerLightShape, 0, 3), i => s.PointerLightShape = i);
            AddColor("Warna", s.PointerLightColor, v => s.PointerLightColor = v);
            AddSlider("Ukuran", 0.4, 2.5, 0.05, s.PointerLightSize, v => s.PointerLightSize = v, Pct);
            AddSlider("Kekuatan", 0.1, 1, 0.05, s.SpotlightIntensity, v => s.SpotlightIntensity = v, Pct);
            AddHeader("Ambient light");
            Pv(PreviewKind.Media);
            AddCheck("Cahaya mengikuti warna cover lagu", s.AmbientLight, v => s.AmbientLight = v,
                hint: "Saat musik diputar, pinggiran island bercahaya dengan warna cover lagunya.");
            AddSlider("Kekuatan", 0.1, 1, 0.05, s.AmbientIntensity, v => s.AmbientIntensity = v, Pct);
            AddSlider("Kecerahan warna", 0.5, 2, 0.05, s.AmbientBrightness, v => s.AmbientBrightness = v, Pct);
            AddCheck("Juga di dock", s.AmbientDock, v => s.AmbientDock = v);
            AddCheck("Juga di panel volume & kecerahan", s.AmbientPanels, v => s.AmbientPanels = v);
            AddCheck("Redupkan sebentar saat island bergerak", s.SmoothGlow, v => s.SmoothGlow = v, hint: "Membuat animasi buka-tutup lebih halus.");
        }) { Group = GDesktop, Desc = "Cahaya yang mengikuti pointer dan cahaya yang mengikuti warna lagu.",
             IsOn = () => s.HoverSpotlight || s.SpotlightDock || s.SpotlightPanels || s.AmbientLight });

        _pages.Add(new Page("", Color.FromRgb(0x8E, 0x8E, 0x93), "Bingkai layar", null, () =>
        {
            AddCheck("Aktifkan bingkai layar", s.BezelEnabled, v => s.BezelEnabled = v,
                hint: "Strip hitam tipis di atas layar dan sudut layar membulat, supaya notch terlihat menyatu seperti di MacBook.");
            AddSlider("Tebal strip atas", 0, 20, 1, s.BezelTopThickness, v => s.BezelTopThickness = v, Rel(d.BezelTopThickness));
            AddSlider("Lengkungan sudut layar", 0, 40, 1, s.ScreenCornerRadius, v => s.ScreenCornerRadius = v, Rel(d.ScreenCornerRadius));
            AddSlider("Kepekatan", 0.05, 1, 0.05, s.BezelOpacity, v => s.BezelOpacity = v, Pct);
            AddCheck("Sembunyikan saat fullscreen", s.BezelHideInFullscreen, v => s.BezelHideInFullscreen = v);
        }) { Group = GDesktop, Desc = "Sudut layar membulat dan strip atas ala MacBook.", IsOn = () => s.BezelEnabled });

        _pages.Add(new Page("", Color.FromRgb(0x63, 0x63, 0x66), "Shortcut & pencarian", null, () =>
        {
            AddHeader("Shortcut keyboard");
            AddCheck("Aktifkan shortcut keyboard", s.HotkeysEnabled, v => { s.HotkeysEnabled = v; Dispatcher.BeginInvoke(new Action(() => ShowPage(_page))); });
            AddHotkey("Buka / tutup island", s.HotkeyToggle, v => s.HotkeyToggle = v, s.HotkeysEnabled);
            AddHotkey("Halaman berikutnya", s.HotkeyNext, v => s.HotkeyNext = v, s.HotkeysEnabled);
            AddHotkey("Halaman sebelumnya", s.HotkeyPrev, v => s.HotkeyPrev = v, s.HotkeysEnabled);
            AddHotkey("Buka pengaturan", s.HotkeySettings, v => s.HotkeySettings = v, s.HotkeysEnabled);
            AddNote("Cara mengganti: klik kotaknya, lalu tekan kombinasi tombol baru (harus pakai Ctrl, Alt, atau Shift). Backspace = kosongkan, Esc = batal.");
            AddHeader("Pencarian cepat");
            AddCheck("Aktifkan pencarian cepat", s.SearchEnabled, v => { s.SearchEnabled = v; Dispatcher.BeginInvoke(new Action(() => ShowPage(_page))); },
                hint: "Seperti Spotlight di Mac: cari aplikasi, file terbaru, hitung cepat, atau langsung cari di Google.");
            AddHotkey("Tombol untuk membuka", s.HotkeySearch, v => s.HotkeySearch = v, s.SearchEnabled);
            var tryBtn = MakeButton(L.T("Coba sekarang"), primary: false, SearchWindow.Toggle, icon: "");
            tryBtn.HorizontalAlignment = HorizontalAlignment.Left;
            tryBtn.Margin = new Thickness(2, 10, 0, 0);
            _group = null;
            _panel.Children.Add(tryBtn);
            AddNote("Ketik nama aplikasi atau file lalu tekan Enter. Bisa juga hitung cepat (mis. 12*3+5) — Enter menyalin hasilnya.");
        }) { Group = GDesktop, Desc = "Kombinasi tombol untuk membuka island dan pencarian cepat.", IsOn = () => s.HotkeysEnabled || s.SearchEnabled });

        // ================================================================ SISTEM

        _pages.Add(new Page("", Color.FromRgb(0x8E, 0x8E, 0x93), "Sistem", null, () =>
        {
            AddHeader("Tentang");
            AddRow(LabelRow("Versi", new TextBlock { Text = VersionText(), FontSize = 13, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center }));
            AddRow(LabelRow("Dibuat", new TextBlock { Text = BuildDate(), FontSize = 13, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center }));
            AddRow(LabelRow("Developer", new TextBlock { Text = "Vincent", FontSize = 13, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center }));
            AddHeader("Update");
            AddCheck("Cek update otomatis", s.UpdateCheck, v => s.UpdateCheck = v,
                hint: "Saat app dibuka lalu sekali sehari, ke GitHub. Hanya memberi tahu — tidak meng-install sendiri.");
            var updStatus = new TextBlock
            {
                Text = UpdateService.Last?.Message ?? (L.T("Versi sekarang: ") + VersionText()),
                FontSize = 12.5, Foreground = Secondary, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0), MaxWidth = 360,
            };
            var updButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 10, 0, 0) };
            updButtons.Children.Add(MakeButton(L.T("Cek sekarang"), primary: false, async () =>
            {
                updStatus.Text = L.T("Mengecek…");
                var r = await UpdateService.CheckAsync();
                updStatus.Text = r.Message;
            }));
            var dl = MakeButton(L.T("Buka halaman download"), primary: false, () => UpdateService.OpenDownloadPage());
            dl.Margin = new Thickness(10, 0, 0, 0);
            updButtons.Children.Add(dl);
            updButtons.Children.Add(updStatus);
            _group = null;
            _panel.Children.Add(updButtons);
            AddHeader("Error & pemulihan");
            AddCheck("Buka ulang otomatis kalau crash", s.AutoRecover, v => s.AutoRecover = v,
                hint: "Kalau Dynamic Island tiba-tiba berhenti, taskbar Windows dikembalikan lalu Dynamic Island dibuka lagi. Berhenti mencoba kalau crash 3x dalam 5 menit.");
            AddNote("Saat ini menyala, Task Manager menampilkan 2 DynamicIsland.exe — yang kecil adalah penjaganya, itu normal.");
            var logBtn = MakeButton(L.T("Buka log error"), primary: false, CrashGuard.OpenLog, icon: "");
            logBtn.HorizontalAlignment = HorizontalAlignment.Left;
            logBtn.Margin = new Thickness(2, 10, 0, 0);
            _group = null;
            _panel.Children.Add(logBtn);
            AddNote(L.T("Log error: ") + CrashGuard.LogPath);
            Border copyBtn = null!;
            copyBtn = MakeButton(L.T("Salin laporan error"), primary: false, () =>
            {
                bool ok = ErrorReport.CopyToClipboard();
                if (copyBtn.Child is TextBlock tb)
                {
                    tb.Text = ok ? "✓ " + L.T("Tersalin — tempel di WhatsApp") : L.T("Gagal menyalin, coba lagi");
                    var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    back.Tick += (_, _) => { back.Stop(); tb.Text = L.T("Salin laporan error"); };
                    back.Start();
                }
            });
            copyBtn.HorizontalAlignment = HorizontalAlignment.Left;
            copyBtn.Margin = new Thickness(2, 10, 0, 0);
            _group = null;
            _panel.Children.Add(copyBtn);
            AddNote("Menyalin versi app, versi Windows, dan isi log error ke clipboard, supaya mudah dikirim ke developer.");
            AddHeader("Bantuan");
            var welcomeBtn = MakeButton(L.T("Tampilkan layar sambutan"), primary: false, App.ShowWelcome);
            welcomeBtn.HorizontalAlignment = HorizontalAlignment.Left;
            welcomeBtn.Margin = new Thickness(2, 4, 0, 0);
            _group = null;
            _panel.Children.Add(welcomeBtn);
            AddNote("Cara membuka Pengaturan, menu dock, menutup app, dan mengembalikan taskbar.");
            AddHeader("Reset");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 4, 0, 0) };
            buttons.Children.Add(MakeButton(L.T("Kembalikan semua ke default"), primary: false, () =>
            {
                Settings.ResetToDefault();
                _pages.Clear();
                _index.Clear();
                DefinePages();
                ShowPage(_page);
            }));
            var close = MakeButton(L.T("Tutup"), primary: true, Close);
            close.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(close);
            _group = null;
            _panel.Children.Add(buttons);
            AddNote(L.T("File pengaturan: ") + Settings.FilePath);
            AddHeader("Hapus");
            var removeBtn = MakeButton(L.T("Hapus dari laptop ini"), primary: false, () =>
            {
                var answer = MessageBox.Show(this,
                    L.T("Dynamic Island akan dihapus dari laptop ini:") + "\n\n" +
                    "• " + L.T("taskbar dan pop-up volume Windows dikembalikan") + "\n" +
                    "• " + L.T("tidak lagi berjalan saat Windows menyala") + "\n" +
                    "• " + L.T("aplikasi ditutup, lalu foldernya dibuka supaya bisa kamu hapus") + "\n\n" +
                    L.T("Pengaturanmu tetap tersimpan di: ") + System.IO.Path.GetDirectoryName(Settings.FilePath) + "\n\n" +
                    L.T("Lanjutkan?"),
                    "Dynamic Island", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer == MessageBoxResult.Yes) App.UninstallFromThisPc();
            });
            removeBtn.HorizontalAlignment = HorizontalAlignment.Left;
            removeBtn.Margin = new Thickness(2, 4, 0, 0);
            _group = null;
            _panel.Children.Add(removeBtn);
            AddNote("Mengembalikan taskbar, menghapus dari startup, lalu menutup aplikasi. Ada konfirmasi dulu.");
        }) { Group = GSistem, Desc = "Versi aplikasi, pemulihan error, bantuan, reset, dan hapus aplikasi." });
    }

    // =====================================================================
    // Pembuat kontrol
    // =====================================================================

    static string Ms(double v) => v < 1000 ? $"{v:0} ms" : $"{v / 1000:0.0#} {L.T("detik")}";
    static string Pct(double v) => $"{v * 100:0}%";

    void AddHeader(string text)
    {
        _group = null;
        _panel.Children.Add(new TextBlock
        {
            Text = L.T(text), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Secondary,
            Margin = new Thickness(14, 20, 0, 7),
        });
    }

    void AddNote(string text)
    {
        _group = null;
        _panel.Children.Add(new TextBlock
        {
            Text = L.T(text), Foreground = Secondary, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 7, 14, 2),
        });
    }

    /// <summary>Satu baris di dalam kartu (kartu baru dibuat otomatis; antar baris diberi garis tipis).</summary>
    Border AddRow(UIElement content)
    {
        if (_group == null)
        {
            _group = new StackPanel();
            _panel.Children.Add(new Border
            {
                Background = Card, CornerRadius = new CornerRadius(12), Child = _group,
                BorderBrush = CardBorder, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 2),
            });
        }
        if (_group.Children.Count > 0)
            _group.Children.Add(new Border { Height = 1, Background = Separator, Margin = new Thickness(14, 0, 0, 0) });
        var row = new Border { Padding = new Thickness(14, 10, 14, 10), Child = content, Background = Brushes.Transparent };
        _group.Children.Add(row);
        return row;
    }

    static TextBlock Label(string text) => new()
    {
        Text = L.T(text), FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Keterangan kecil abu-abu di bawah label (menjelaskan apa yang dilakukan pengaturan itu).</summary>
    static TextBlock Hint(string text) => new()
    {
        Text = L.T(text), FontSize = 11.5, Foreground = Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
    };

    /// <summary>Label di kiri (+ keterangan kecil kalau ada), kontrol di kanan.</summary>
    static Grid LabelRow(string label, UIElement control, bool translate = true, string? hint = null)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = Label(label);
        if (!translate) l.Text = label;
        if (string.IsNullOrEmpty(hint))
        {
            l.Margin = new Thickness(0, 0, 14, 0);
            g.Children.Add(l);
        }
        else
        {
            var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(l);
            left.Children.Add(Hint(hint));
            g.Children.Add(left);
        }
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    void AddCheck(string label, bool value, Action<bool> set, bool translate = true, string? hint = null)
    {
        var pv = _pv;
        var sw = new ToggleSwitch(value);
        sw.Toggled += on => { set(on); Settings.NotifyChanged(); Fire(pv); };
        var row = AddRow(LabelRow(label, sw, translate, hint));
        row.Cursor = Cursors.Hand;
        row.MouseLeftButtonUp += (sender, e) => { if (!e.Handled) sw.Flip(); }; // klik di mana saja pada baris
    }

    void AddSlider(string label, double min, double max, double step, double value,
                   Action<double> set, Func<double, string> format, string? hint = null)
    {
        var pv = _pv;
        var valueText = new TextBlock { Text = format(value), FontSize = 12.5, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
            SmallChange = step, LargeChange = step * 5, TickFrequency = step,
            IsSnapToTickEnabled = true, Margin = new Thickness(0, 6, 0, 0), Cursor = Cursors.Hand,
        };
        slider.ValueChanged += (sender, e) =>
        {
            set(e.NewValue);
            valueText.Text = format(e.NewValue);
            Settings.NotifyChanged();
            Fire(pv);
        };
        var stack = new StackPanel();
        stack.Children.Add(LabelRow(label, valueText, hint: hint));
        stack.Children.Add(slider);
        AddRow(stack);
    }

    void AddCombo(string label, string[] options, int selected, Action<int> set, bool translate = true, Func<int, FontFamily?>? fontOf = null, string? hint = null)
    {
        var pv = _pv;
        var shown = translate ? Array.ConvertAll(options, L.T) : options;
        int current = Math.Clamp(selected, 0, Math.Max(0, shown.Length - 1));
        var text = new TextBlock
        {
            Text = shown.Length > 0 ? shown[current] : "", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220,
        };
        if (fontOf != null && shown.Length > 0 && fontOf(current) is FontFamily cf) text.FontFamily = cf;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(text);
        content.Children.Add(new TextBlock { Text = "\uE70D", FontFamily = IconFont, FontSize = 9, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0) });
        var btn = new Border
        {
            Background = ControlBg, CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 5, 9, 5),
            Child = content, Cursor = Cursors.Hand,
        };
        btn.MouseEnter += (sender, args) => btn.Background = ControlHover;
        btn.MouseLeave += (sender, args) => btn.Background = ControlBg;
        btn.MouseLeftButtonUp += (sender, e) =>
        {
            e.Handled = true;
            OpenPicker(btn, shown, current, i =>
            {
                current = i;
                text.Text = shown[i];
                if (fontOf != null) text.FontFamily = fontOf(i) ?? FontFamily;
                set(i);
                Settings.NotifyChanged();
                Fire(pv);
            }, fontOf);
        };
        AddRow(LabelRow(label, btn, hint: hint));
    }

    /// <summary>Daftar pilihan gelap yang muncul di bawah tombol (bisa di-scroll untuk daftar panjang).</summary>
    void OpenPicker(FrameworkElement anchor, string[] items, int selected, Action<int> pick, Func<int, FontFamily?>? fontOf = null)
    {
        var list = new StackPanel();
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, VerticalOffset = 2,
        };
        for (int i = 0; i < items.Length; i++)
        {
            int index = i;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock
            {
                Text = i == selected ? "\uE73E" : "", FontFamily = IconFont, FontSize = 11, Foreground = Accent,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var t = new TextBlock { Text = items[i], FontSize = 12.5, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
            if (fontOf?.Invoke(i) is FontFamily itemFont) { t.FontFamily = itemFont; t.FontSize = 13.5; } // pratinjau: nama font ditulis dengan font itu
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            var row = new Border { Child = g, Padding = new Thickness(8, 6, 12, 6), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            row.MouseEnter += (sender, args) => row.Background = Accent;
            row.MouseLeave += (sender, args) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (sender, args) => { popup.IsOpen = false; if (index != selected) pick(index); };
            list.Children.Add(row);
        }
        var sv = SmoothScroll(new ScrollViewer { Content = list, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (_scrollBarStyle != null) sv.Resources.Add(typeof(ScrollBar), _scrollBarStyle);
        popup.Child = new Border
        {
            Background = Frozen(Color.FromRgb(0x2C, 0x2C, 0x2E)), BorderBrush = Frozen(Color.FromRgb(0x48, 0x48, 0x4A)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(4),
            MinWidth = Math.Max(200, anchor.ActualWidth), MaxWidth = 340, Child = sv, Margin = new Thickness(0, 0, 10, 10),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.5 },
        };
        popup.IsOpen = true;
        if (selected >= 0 && selected < list.Children.Count)
            Dispatcher.BeginInvoke(new Action(() => ((FrameworkElement)list.Children[selected]).BringIntoView()),
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    static readonly Dictionary<string, FontFamily?> _previewFonts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Font untuk pratinjau nama font. Font simbol (Wingdings, Webdings, Marlett, …) tidak bisa dibaca,
    /// jadi namanya tetap ditulis dengan font biasa (null).
    /// </summary>
    static FontFamily? PreviewFont(string name)
    {
        if (_previewFonts.TryGetValue(name, out var cached)) return cached;
        FontFamily? result = null;
        try
        {
            var ff = new FontFamily(name);
            result = ff;
            foreach (var tf in ff.GetTypefaces())
            {
                if (tf.TryGetGlyphTypeface(out var g)) { if (g.Symbol) result = null; break; }
            }
        }
        catch { result = null; }
        _previewFonts[name] = result;
        return result;
    }

    void AddText(string label, string value, Action<string> set, string? hint = null)
    {
        var pv = _pv;
        var box = new TextBox { Text = value, Margin = new Thickness(0, 7, 0, 0) };
        void Commit()
        {
            string v = box.Text.Trim();
            set(v);
            Settings.NotifyChanged();
            Fire(pv);
        }
        box.LostFocus += (sender, args) => Commit();
        box.KeyDown += (sender, e) => { if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); } };
        var stack = new StackPanel();
        stack.Children.Add(Label(label));
        if (!string.IsNullOrEmpty(hint)) stack.Children.Add(Hint(hint));
        stack.Children.Add(box);
        AddRow(stack);
    }

    /// <summary>Pilihan warna: lingkaran warna siap pakai, tombol pelangi (color wheel) untuk warna bebas, dan kotak kode hex.</summary>
    void AddColor(string label, string value, Action<string> set, string[]? presets = null, string? hint = null)
    {
        var pv = _pv;
        presets ??= Swatches;
        var box = new TextBox { Text = value, Width = 92, VerticalContentAlignment = VerticalAlignment.Center };
        var swatchPanel = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        var rings = new List<(Border ring, string hex)>();

        void Mark(string hex)
        {
            foreach (var (ring, h) in rings)
                ring.BorderBrush = string.Equals(h, hex, StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Transparent;
        }
        void Apply(string hex)
        {
            if (!TryColor(hex, out _)) { box.Text = value; return; }
            value = hex.ToUpperInvariant();
            box.Text = value;
            Mark(value);
            set(value);
            Settings.NotifyChanged();
            Fire(pv);
        }

        foreach (var hex in presets)
        {
            TryColor(hex, out var c);
            var ring = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(2),
                Padding = new Thickness(2), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, ToolTip = hex,
                Child = new Border
                {
                    CornerRadius = new CornerRadius(9), Background = Frozen(c),
                    BorderBrush = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                },
            };
            string h = hex;
            ring.MouseLeftButtonUp += (sender, e) => { e.Handled = true; Apply(h); };
            rings.Add((ring, hex));
            swatchPanel.Children.Add(ring);
        }
        // tombol pelangi: buka color wheel untuk memilih warna apa saja
        var wheelBtn = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Padding = new Thickness(2), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, ToolTip = L.T("Pilih warna lain (color wheel)"),
            Child = new Border
            {
                CornerRadius = new CornerRadius(9), Background = RainbowBrush,
                BorderBrush = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = "+", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Bold,
                                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) },
            },
        };
        wheelBtn.MouseEnter += (sender, e) => wheelBtn.BorderBrush = ControlHover;
        wheelBtn.MouseLeave += (sender, e) => wheelBtn.BorderBrush = Brushes.Transparent;
        wheelBtn.MouseLeftButtonUp += (sender, e) =>
        {
            e.Handled = true;
            TryColor(value, out var start);
            OpenColorWheel(wheelBtn, start, c => Apply($"#{c.R:X2}{c.G:X2}{c.B:X2}"));
        };
        swatchPanel.Children.Add(wheelBtn);

        Mark(value);
        box.LostFocus += (sender, args) => { if (!string.Equals(box.Text.Trim(), value, StringComparison.OrdinalIgnoreCase)) Apply(box.Text.Trim()); };
        box.KeyDown += (sender, e) => { if (e.Key == Key.Enter) { Apply(box.Text.Trim()); Keyboard.ClearFocus(); } };

        var stack = new StackPanel();
        stack.Children.Add(LabelRow(label, box, hint: hint));
        stack.Children.Add(swatchPanel);
        AddRow(stack);
    }

    // =====================================================================
    // Color wheel
    // =====================================================================

    static readonly Brush RainbowBrush = MakeRainbow();

    static Brush MakeRainbow()
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        string[] cols = { "#FF453A", "#FF9F0A", "#FFD60A", "#34C759", "#64D2FF", "#0A84FF", "#BF5AF2" };
        for (int i = 0; i < cols.Length; i++)
            b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(cols[i]), i / (double)(cols.Length - 1)));
        b.Freeze();
        return b;
    }

    const int WheelSize = 180;
    static System.Windows.Media.Imaging.BitmapSource? _wheelBitmap;

    /// <summary>Lingkaran warna: sudut = warna (hue), jarak dari tengah = kepekatan (saturasi). Dibuat sekali saja.</summary>
    static System.Windows.Media.Imaging.BitmapSource WheelBitmap()
    {
        if (_wheelBitmap != null) return _wheelBitmap;
        int n = WheelSize;
        double r = n / 2.0;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double dx = x + 0.5 - r, dy = y + 0.5 - r;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > r) continue;
                double hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                var c = FromHsv(hue, Math.Min(1, dist / r), 1);
                double a = Math.Clamp(r - dist, 0, 1); // tepi halus
                int i = (y * n + x) * 4;
                px[i] = (byte)(c.B * a); px[i + 1] = (byte)(c.G * a); px[i + 2] = (byte)(c.R * a); px[i + 3] = (byte)(255 * a);
            }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(n, n, 96, 96, PixelFormats.Pbgra32, null, px, n * 4);
        bmp.Freeze();
        return _wheelBitmap = bmp;
    }

    static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }

    /// <summary>
    /// Popup color wheel di bawah <paramref name="anchor"/>: klik/geser di lingkaran untuk warna,
    /// geser bar di bawahnya untuk terang-gelap. Warna langsung dipakai (pratinjau langsung di island).
    /// </summary>
    void OpenColorWheel(FrameworkElement anchor, Color start, Action<Color> changed)
    {
        var (h, sat, val) = ToHsv(start);
        if (val < 0.05) val = 1; // dari hitam: mulai dari terang supaya lingkarannya terlihat
        double radius = WheelSize / 2.0;

        var wheelImage = new Image { Source = WheelBitmap(), Width = WheelSize, Height = WheelSize };
        var darken = new System.Windows.Shapes.Ellipse { Width = WheelSize, Height = WheelSize, Fill = Brushes.Black, IsHitTestVisible = false };
        var marker = new System.Windows.Shapes.Ellipse
        {
            Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2.5, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.6 },
        };
        var wheel = new Canvas { Width = WheelSize, Height = WheelSize, Background = Brushes.Transparent, Cursor = Cursors.Cross };
        wheel.Children.Add(wheelImage);
        wheel.Children.Add(darken);
        wheel.Children.Add(marker);

        // bar terang-gelap
        const double barW = WheelSize;
        var barFill = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        var barStopHi = new GradientStop(Colors.White, 1);
        barFill.GradientStops.Add(new GradientStop(Colors.Black, 0));
        barFill.GradientStops.Add(barStopHi);
        var barThumb = new Border
        {
            Width = 14, Height = 22, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.White, BorderThickness = new Thickness(2.5),
            IsHitTestVisible = false, Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.6 },
        };
        var bar = new Canvas { Width = barW, Height = 22, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 12, 0, 0) };
        bar.Children.Add(new Border { Width = barW, Height = 12, CornerRadius = new CornerRadius(6), Background = barFill, Margin = new Thickness(0, 5, 0, 0) });
        bar.Children.Add(barThumb);

        var swatch = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), BorderBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1) };
        var hexText = new TextBlock { FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontFamily = new FontFamily("Consolas, Segoe UI") };
        var info = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        info.Children.Add(swatch);
        info.Children.Add(hexText);

        void Render(bool notify)
        {
            double a = h * Math.PI / 180, d = sat * radius;
            Canvas.SetLeft(marker, radius + Math.Cos(a) * d - 8);
            Canvas.SetTop(marker, radius + Math.Sin(a) * d - 8);
            darken.Opacity = 1 - val;
            barStopHi.Color = FromHsv(h, sat, 1);
            Canvas.SetLeft(barThumb, val * (barW - 14));
            var c = FromHsv(h, sat, val);
            swatch.Background = new SolidColorBrush(c);
            marker.Fill = new SolidColorBrush(c);
            hexText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            if (notify) changed(c);
        }

        void PickWheel(Point p)
        {
            double dx = p.X - radius, dy = p.Y - radius;
            h = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
            sat = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / radius);
            Render(true);
        }
        void PickBar(Point p)
        {
            val = Math.Clamp((p.X - 7) / (barW - 14), 0, 1);
            Render(true);
        }
        wheel.MouseLeftButtonDown += (sender, e) => { e.Handled = true; wheel.CaptureMouse(); PickWheel(e.GetPosition(wheel)); };
        wheel.MouseMove += (sender, e) => { if (wheel.IsMouseCaptured) PickWheel(e.GetPosition(wheel)); };
        wheel.MouseLeftButtonUp += (sender, e) => { e.Handled = true; wheel.ReleaseMouseCapture(); };
        bar.MouseLeftButtonDown += (sender, e) => { e.Handled = true; bar.CaptureMouse(); PickBar(e.GetPosition(bar)); };
        bar.MouseMove += (sender, e) => { if (bar.IsMouseCaptured) PickBar(e.GetPosition(bar)); };
        bar.MouseLeftButtonUp += (sender, e) => { e.Handled = true; bar.ReleaseMouseCapture(); };

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = L.T("Klik atau geser di lingkaran untuk memilih warna"), FontSize = 11.5, Foreground = Secondary, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap, MaxWidth = WheelSize });
        body.Children.Add(wheel);
        body.Children.Add(new TextBlock { Text = L.T("Terang — gelap"), FontSize = 11.5, Foreground = Secondary, Margin = new Thickness(0, 12, 0, -6) });
        body.Children.Add(bar);
        body.Children.Add(info);

        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, VerticalOffset = 4,
            Child = new Border
            {
                Background = Frozen(Color.FromRgb(0x2C, 0x2C, 0x2E)), BorderBrush = Frozen(Color.FromRgb(0x48, 0x48, 0x4A)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14),
                Child = body, Margin = new Thickness(0, 0, 10, 10),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.5 },
            },
        };
        Render(false);
        popup.IsOpen = true;
    }

    static bool TryColor(string hex, out Color c)
    {
        c = Colors.Transparent;
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return false;
            c = (Color)ColorConverter.ConvertFromString(hex.Trim());
            return true;
        }
        catch { return false; }
    }

    /// <summary>Kartu tema dengan gambar mini notch (warna, transparansi, tombol, aksen) — klik untuk memakai.</summary>
    void AddThemes()
    {
        var s = Settings.Current;
        var wrap = new WrapPanel();
        foreach (var theme in Themes.All)
        {
            bool active = theme.Matches(s);
            TryColor(theme.IslandColor, out var island);
            island.A = (byte)Math.Round(255 * theme.IslandOpacity);
            TryColor(theme.ToggleOnColor, out var toggle);
            TryColor(theme.AccentColor, out var accent);
            TryColor(theme.ChargeColor, out var charge);

            // gambar mini: latar "wallpaper", notch di atas dengan isi kecil
            var notchContent = new Grid { Margin = new Thickness(8, 0, 8, 0) };
            notchContent.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = Frozen(toggle), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
            notchContent.Children.Add(new Border { Width = 26, Height = 3, CornerRadius = new CornerRadius(1.5), Background = Frozen(accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            notchContent.Children.Add(new Border { Width = 12, Height = 3, CornerRadius = new CornerRadius(1.5), Background = Frozen(charge), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            double r = theme.BottomRadius * 0.7;
            var notch = new Border
            {
                Width = 96, Height = 24, Background = Frozen(island), CornerRadius = new CornerRadius(0, 0, r, r),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Child = notchContent,
                BorderBrush = theme.EdgeHighlight ? Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) : null,
                BorderThickness = theme.EdgeHighlight ? new Thickness(1, 0, 1, 1) : new Thickness(0),
            };
            var wallpaper = new Border
            {
                Height = 64, CornerRadius = new CornerRadius(8), ClipToBounds = true,
                Background = new LinearGradientBrush(Color.FromRgb(0x3A, 0x5F, 0x4A), Color.FromRgb(0x1E, 0x2E, 0x45), 45),
                Child = notch,
            };
            // tanda "dipakai": centang biru di pojok gambar
            var check = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Accent,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 5, 5),
                Visibility = active ? Visibility.Visible : Visibility.Collapsed,
                Child = new TextBlock { Text = "\uE73E", FontFamily = IconFont, FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var picture = new Grid();
            picture.Children.Add(wallpaper);
            picture.Children.Add(check);
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 7, 0, 0) };
            title.Children.Add(new TextBlock { Text = L.T(theme.Name), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            if (active)
                title.Children.Add(new Border
                {
                    Background = Frozen(Color.FromArgb(0x33, 0x0A, 0x84, 0xFF)), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = L.T("Dipakai"), FontSize = 10.5, Foreground = Accent, FontWeight = FontWeights.SemiBold },
                });
            var card = new StackPanel();
            card.Children.Add(picture);
            card.Children.Add(title);
            card.Children.Add(new TextBlock { Text = L.T(theme.Description), FontSize = 11, Foreground = Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 1, 0, 0) });
            var item = new Border
            {
                Width = 150, Padding = new Thickness(6), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(12),
                Background = Card, BorderThickness = new Thickness(2), Cursor = Cursors.Hand, Child = card,
                BorderBrush = active ? Accent : CardBorder,
            };
            var t = theme;
            item.MouseEnter += (sender, args) => { if (!t.Matches(Settings.Current)) item.BorderBrush = ControlHover; };
            item.MouseLeave += (sender, args) => { if (!t.Matches(Settings.Current)) item.BorderBrush = CardBorder; };
            item.MouseLeftButtonUp += (sender, args) =>
            {
                t.Apply(Settings.Current);
                Settings.NotifyChanged();
                App.Island?.Preview(PreviewKind.Control);
                Dispatcher.BeginInvoke(new Action(() => { double y = _scroll.VerticalOffset; ShowPage(_page); _scroll.ScrollToVerticalOffset(y); _scrollTarget = y; }));
            };
            wrap.Children.Add(item);
        }
        _group = null;
        _panel.Children.Add(wrap);
    }

    /// <summary>Kotak perekam shortcut: klik, lalu tekan kombinasinya.</summary>
    void AddHotkey(string label, string value, Action<string> set, bool enabled)
    {
        var box = new TextBox
        {
            Text = value, IsReadOnly = true, IsEnabled = enabled, Width = 180, Cursor = Cursors.Hand,
            IsReadOnlyCaretVisible = false, TextAlignment = TextAlignment.Center,
        };
        var warn = new TextBlock { FontSize = 11.5, Foreground = Warning, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        void ShowWarn()
        {
            bool failed = enabled && Hotkeys.Failed.Contains(box.Text);
            warn.Text = failed ? L.T("Shortcut ini sudah dipakai aplikasi lain, pilih kombinasi lain.") : "";
            warn.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        }
        string before = value;
        string waiting = L.T("Tekan kombinasi tombol…");
        box.GotKeyboardFocus += (sender, args) => { before = box.Text; box.Text = waiting; };
        box.LostKeyboardFocus += (sender, args) => { if (box.Text == waiting) box.Text = before; };
        box.PreviewKeyDown += (sender, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (Hotkeys.IsModifier(key)) return; // tunggu tombol utamanya
            var mods = Keyboard.Modifiers;
            string text;
            if (key == Key.Escape && mods == ModifierKeys.None) text = before;
            else if ((key == Key.Back || key == Key.Delete) && mods == ModifierKeys.None) text = "";
            else
            {
                text = Hotkeys.Format(mods, key);
                if (!Hotkeys.TryParse(text, out uint hkMods, out uint hkKey)) return; // tanpa Ctrl/Alt/Shift/Win: abaikan
            }
            box.Text = before = text;
            set(text);
            Settings.NotifyChanged(); // island langsung mendaftarkan ulang shortcut-nya
            ShowWarn();
            Keyboard.ClearFocus();
        };
        ShowWarn();
        var stack = new StackPanel { Opacity = enabled ? 1 : 0.45 };
        stack.Children.Add(LabelRow(label, box));
        stack.Children.Add(warn);
        AddRow(stack);
    }

    /// <summary>Kotak teks beberapa baris untuk widget Catatan (bisa juga diketik langsung di desktop: klik dua kali).</summary>
    void AddNoteBox(WidgetConfig cfg)
    {
        var box = new TextBox
        {
            Text = cfg.NoteText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76, MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 7, 0, 0), MaxLength = 2000,
        };
        box.LostFocus += (_, _) =>
        {
            if (box.Text == cfg.NoteText) return;
            cfg.NoteText = box.Text.TrimEnd();
            Settings.NotifyChanged();
            Settings.Save();
        };
        var stack = new StackPanel();
        stack.Children.Add(Label(L.T("Isi catatan")));
        stack.Children.Add(Hint(L.T("Bisa juga langsung diketik di desktop: klik dua kali widget catatannya.")));
        stack.Children.Add(box);
        AddRow(stack);
    }

    /// <summary>Pilih gambar / folder: thumbnail kecil + nama file + tombol "Pilih…" (dan "×" untuk mengosongkan).</summary>
    static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp" };

    void AddPathPicker(string label, string value, bool folder, Action<string> set, string? hint = null, bool clearable = false, bool video = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bool isImage = ImageExt.Contains(System.IO.Path.GetExtension(value).ToLowerInvariant());
        if (!folder && isImage && System.IO.File.Exists(value) && WallpaperService.Decode(value, 160) is { } thumb)
        {
            row.Children.Add(new Border
            {
                Width = 72, Height = 42, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 10, 0), ClipToBounds = true,
                Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill },
            });
        }
        string shown = value.Length == 0 ? L.T("Belum dipilih") : System.IO.Path.GetFileName(value.TrimEnd('\\', '/'));
        row.Children.Add(new TextBlock
        {
            Text = shown, ToolTip = value.Length > 0 ? value : null, FontSize = 12.5, Foreground = Secondary,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        });
        void Commit(string v)
        {
            set(v);
            Settings.NotifyChanged();
            Dispatcher.BeginInvoke(new Action(() => ShowPage(_page)));
        }
        row.Children.Add(MakeButton(L.T("Pilih…"), primary: false, () =>
        {
            string? picked = folder ? PickFolder(value) : PickImage(value, video);
            if (picked != null) Commit(picked);
        }));
        if (clearable && value.Length > 0)
        {
            var clear = MakeButton("×", primary: false, () => Commit(""));
            clear.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(clear);
        }
        AddRow(LabelRow(label, row, hint: hint));
    }

    string? PickImage(string current, bool video = false)
    {
        var d = new OpenFileDialog
        {
            Title = video ? L.T("Pilih video atau gambar") : L.T("Pilih gambar wallpaper"),
            Filter = video
                ? L.T("Video & gambar") + "|*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.jpg;*.jpeg;*.png;*.bmp;*.webp"
                : L.T("Gambar") + "|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.webp",
        };
        try
        {
            string? dir = current.Length > 0 ? System.IO.Path.GetDirectoryName(current) : null;
            d.InitialDirectory = dir != null && System.IO.Directory.Exists(dir) ? dir
                : Environment.GetFolderPath(video ? Environment.SpecialFolder.MyVideos : Environment.SpecialFolder.MyPictures);
        }
        catch { }
        return d.ShowDialog(this) == true ? d.FileName : null;
    }

    string? PickFolder(string current)
    {
        var d = new OpenFolderDialog { Title = L.T("Pilih folder wallpaper") };
        try
        {
            d.InitialDirectory = current.Length > 0 && System.IO.Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        }
        catch { }
        return d.ShowDialog(this) == true ? d.FolderName : null;
    }

    /// <summary>Tombol membulat (biru = utama, abu-abu = biasa).</summary>
    static Border MakeButton(string text, bool primary, Action click, string? icon = null)
    {
        Brush normal = primary ? Accent : ControlBg;
        Brush hover = primary ? Frozen(Color.FromRgb(0x2B, 0x95, 0xFF)) : ControlHover;
        var b = new Border
        {
            Background = normal, CornerRadius = new CornerRadius(7), Padding = new Thickness(14, 6, 14, 6), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon != null ? WithIcon(icon, text, 12.5)
                                 : new TextBlock { Text = text, FontSize = 12.5, Foreground = Brushes.White },
        };
        b.MouseEnter += (sender, args) => b.Background = hover;
        b.MouseLeave += (sender, args) => b.Background = normal;
        b.MouseLeftButtonUp += (sender, e) => { e.Handled = true; click(); };
        return b;
    }

    /// <summary>Teks dengan ikon di depannya (ikon pakai font ikon, teks pakai font biasa).</summary>
    static TextBlock WithIcon(string icon, string text, double size)
    {
        var tb = new TextBlock { FontSize = size, Foreground = Brushes.White };
        tb.Inlines.Add(new System.Windows.Documents.Run(icon) { FontFamily = IconFont, BaselineAlignment = BaselineAlignment.Center });
        tb.Inlines.Add(new System.Windows.Documents.Run("  " + text));
        return tb;
    }

    /// <summary>Nomor versi dari file proyek (DynamicIsland.csproj → &lt;Version&gt;).</summary>
    public static string VersionText()
    {
        var v = typeof(SettingsWindow).Assembly.GetName().Version;
        return v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>Tanggal program ini di-build (dari waktu file aplikasinya).</summary>
    static string BuildDate()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (path == null) return "—";
            return System.IO.File.GetLastWriteTime(path).ToString("d MMMM yyyy, HH:mm", L.Culture);
        }
        catch { return "—"; }
    }

    static void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (enable && Environment.ProcessPath != null) key.SetValue("DynamicIsland", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("DynamicIsland", throwOnMissingValue: false);
        }
        catch { }
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // =====================================================================
    // Toggle switch ala iOS / macOS
    // =====================================================================

    sealed class ToggleSwitch : Border
    {
        static readonly Color OnColor = Color.FromRgb(0x34, 0xC7, 0x59);
        static readonly Color OffColor = Color.FromRgb(0x48, 0x48, 0x4A);
        readonly TranslateTransform _move = new();
        readonly SolidColorBrush _bg;
        bool _on;

        public event Action<bool>? Toggled;

        public ToggleSwitch(bool on)
        {
            _on = on;
            Width = 42;
            Height = 25;
            CornerRadius = new CornerRadius(12.5);
            Padding = new Thickness(2);
            Cursor = Cursors.Hand;
            VerticalAlignment = VerticalAlignment.Center;
            _bg = new SolidColorBrush(on ? OnColor : OffColor);
            Background = _bg;
            _move.X = on ? 17 : 0;
            Child = new Border
            {
                Width = 21, Height = 21, CornerRadius = new CornerRadius(10.5), Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _move,
                Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.35 },
            };
            MouseLeftButtonUp += (sender, e) => { e.Handled = true; Flip(); };
        }

        public void Flip()
        {
            _on = !_on;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_on ? 17 : 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            _bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(_on ? OnColor : OffColor, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            Toggled?.Invoke(_on);
        }
    }
}
