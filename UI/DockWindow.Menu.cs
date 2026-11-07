using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.UI;

/// <summary>
/// Menu dock: tombol kiri dock (atau tombol Windows, kalau dinyalakan) membuat dock MEMANJANG ke atas
/// dengan animasi pegas seperti island. Dock dan menu satu bentuk, dipisah garis tipis:
/// pencarian, aplikasi yang disematkan, semua aplikasi, Phone Link (opsional), akun & tombol power.
/// </summary>
public sealed partial class DockWindow
{
    const double MenuHeight = 470, PhoneColumn = 224, MenuTileW = 92, MenuTileH = 86;

    readonly Spring _menu = new();
    bool _menuOpen;
    double _menuW = 720, _baseWinH;
    int _popupsOpen; // menu klik kanan / menu power sedang terbuka

    Grid _menuPanel = null!;
    Border _menuSep = null!;
    TextBox _menuSearch = null!;
    TextBlock _menuPlaceholder = null!;
    StackPanel _menuBody = null!;
    ScrollViewer _menuScroll = null!;
    Border _phoneCard = null!;
    TextBlock _phoneName = null!, _phoneState = null!;
    readonly List<(Border tile, Action run)> _menuItems = new();
    DispatcherTimer _menuPoll = null!;
    StartKeyHook? _startKeys;

    // daftar "Semua aplikasi" disimpan supaya tidak disusun ulang tiap kali menu dibuka
    WrapPanel? _allAppsCache;
    object? _allAppsSource;
    int _allAppsIcons = -1;

    static readonly Brush MenuSecondary = Frozen(Color.FromRgb(0x98, 0x98, 0x9D));
    static readonly Brush MenuHover = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    static readonly Brush MenuSelected = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    static readonly Brush MenuField = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    static readonly Brush MenuCard = Frozen(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
    static readonly Brush MenuLine = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    static readonly FontFamily MenuIconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public bool IsMenuOpen => _menuOpen;

    // =====================================================================
    // Susun tampilan
    // =====================================================================

    void InitMenu()
    {
        _menuPanel = BuildMenuPanel();
        _menuSep = new Border { Height = 1, Background = MenuLine, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        int at = _root.Children.IndexOf(_edge);
        _root.Children.Insert(at < 0 ? _root.Children.Count : at, _menuPanel);
        _root.Children.Insert(at < 0 ? _root.Children.Count : at + 1, _menuSep);

        _menuPoll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(90) };
        _menuPoll.Tick += (_, _) =>
        {
            if (!_menuOpen) { _menuPoll.Stop(); return; }
            // klik di luar menu (termasuk saat Windows tidak memberi fokus ke menu) → tutup
            bool button = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (button && _popupsOpen == 0 && !_root.IsMouseOver) CloseMenu();
        };

        Deactivated += (_, _) => { if (_popupsOpen == 0) CloseMenu(); };
        PreviewKeyDown += OnMenuKey;
        AppCatalog.Updated += () => { if (_menuOpen) RebuildMenuApps(); };

        // siapkan daftar aplikasi + ikon di latar beberapa detik setelah aplikasi jalan,
        // supaya saat menu pertama kali dibuka isinya langsung ada
        var warm = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(20) };
        warm.Tick += (_, _) =>
        {
            warm.Stop();
            if (Settings.Current.DockMenu && !_suspended) AppCatalog.EnsureLoaded(Dispatcher, 48);
        };
        warm.Start();
    }

    Grid BuildMenuPanel()
    {
        var panel = new Grid { Visibility = Visibility.Collapsed, Background = Brushes.Transparent };
        TextElement.SetForeground(panel, Brushes.White);
        panel.ContextMenu = new ContextMenu();
        ContextMenuService.SetIsEnabled(panel, false); // klik kanan di area kosong menu: jangan buka menu dock
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var inner = new Thickness(20, 18, 20, 12);

        // ---- pencarian ----
        _menuSearch = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White,
            CaretBrush = Brushes.White, FontSize = 15, VerticalContentAlignment = VerticalAlignment.Center,
        };
        _menuPlaceholder = new TextBlock
        {
            Text = L.T("Cari aplikasi…"), FontSize = 15, Foreground = MenuSecondary, IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0),
        };
        _menuSearch.TextChanged += (_, _) =>
        {
            _menuPlaceholder.Visibility = _menuSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RebuildMenuApps();
        };
        var searchGrid = new Grid();
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition());
        searchGrid.Children.Add(new TextBlock { Text = "", FontFamily = MenuIconFont, FontSize = 15, Foreground = MenuSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var host = new Grid();
        host.Children.Add(_menuSearch);
        host.Children.Add(_menuPlaceholder);
        Grid.SetColumn(host, 1);
        searchGrid.Children.Add(host);
        var search = new Border
        {
            Background = MenuField, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 9, 14, 9),
            Child = searchGrid, Margin = new Thickness(inner.Left, inner.Top, inner.Right, 10), Cursor = Cursors.IBeam,
        };
        search.MouseLeftButtonDown += (_, _) => _menuSearch.Focus();
        panel.Children.Add(search);

        // ---- isi: daftar aplikasi + Phone Link ----
        var body = new Grid { Margin = new Thickness(inner.Left - 6, 0, inner.Right, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _menuBody = new StackPanel();
        _menuScroll = new ScrollViewer
        {
            Content = _menuBody, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
        body.Children.Add(_menuScroll);
        _phoneCard = BuildPhoneCard();
        Grid.SetColumn(_phoneCard, 1);
        body.Children.Add(_phoneCard);
        Grid.SetRow(body, 1);
        panel.Children.Add(body);

        // ---- bawah: akun + tombol ----
        var footer = new Grid { Margin = new Thickness(inner.Left, 8, inner.Right - 4, inner.Bottom) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string user = Environment.UserName;
        var avatar = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = MenuField, Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = user.Length > 0 ? user[..1].ToUpperInvariant() : "?", FontSize = 13, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var who = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(avatar);
        who.Children.Add(new TextBlock { Text = user, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        footer.Children.Add(who);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(MenuRoundButton("", L.T("Pengaturan Dynamic Island"), () => { CloseMenu(); App.OpenSettings(); }));
        Border power = null!;
        var powerMenu = BuildPowerMenu();
        // satu handler saja: dulu ada dua handler klik di tombol yang sama, dan handler kedua (yang membuka menu) tidak pernah jalan
        power = MenuRoundButton("\uE7E8", L.T("Power"), () =>
        {
            powerMenu.PlacementTarget = power;
            powerMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            powerMenu.IsOpen = true;
        });
        power.ContextMenu = powerMenu;
        buttons.Children.Add(power);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        panel.Children.Add(footer);
        return panel;
    }

    Border MenuRoundButton(string glyph, string tip, Action click)
    {
        var b = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Margin = new Thickness(6, 0, 0, 0), ToolTip = tip,
            Child = new TextBlock { Text = glyph, FontFamily = MenuIconFont, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        b.MouseEnter += (_, _) => b.Background = MenuHover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    ContextMenu TrackPopup(ContextMenu m)
    {
        m.Opened += (_, _) => _popupsOpen++;
        m.Closed += (_, _) => { _popupsOpen = Math.Max(0, _popupsOpen - 1); if (_menuOpen) Dispatcher.BeginInvoke(new Action(FocusMenu)); };
        return m;
    }

    ContextMenu BuildPowerMenu()
    {
        var m = new ContextMenu();
        void Add(string text, Action act)
        {
            var item = new MenuItem { Header = L.T(text) };
            item.Click += (_, _) => { CloseMenu(); act(); };
            m.Items.Add(item);
        }
        Add("Kunci", () => LockWorkStation());
        Add("Tidur (sleep)", PowerActions.Sleep); // Modern Standby: matikan layar (bukan hibernate)
        m.Items.Add(new Separator());
        Add("Mulai ulang", () => RunCmd("shutdown.exe", "/r /t 0"));
        Add("Matikan laptop", () => RunCmd("shutdown.exe", "/s /t 0"));
        m.Items.Add(new Separator());
        Add("Keluar dari akun", () => RunCmd("shutdown.exe", "/l"));
        return TrackPopup(m);
    }

    static void RunCmd(string file, string args)
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true }); } catch { }
    }

    // ---- Phone Link ----

    Border BuildPhoneCard()
    {
        _phoneName = new TextBlock { Text = L.T("HP kamu"), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 10, 0, 2) };
        _phoneState = new TextBlock { Text = "", FontSize = 12, Foreground = MenuSecondary, TextWrapping = TextWrapping.Wrap };
        var open = new Border
        {
            Background = MenuField, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Cursor = Cursors.Hand,
            Margin = new Thickness(0, 14, 0, 0),
            Child = new TextBlock { Text = L.T("Buka Phone Link"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center },
        };
        open.MouseEnter += (_, _) => open.Background = MenuSelected;
        open.MouseLeave += (_, _) => open.Background = MenuField;
        open.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseMenu(); OpenPhoneLink(); };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "", FontFamily = MenuIconFont, FontSize = 30, Foreground = MenuSecondary });
        stack.Children.Add(_phoneName);
        stack.Children.Add(_phoneState);
        stack.Children.Add(open);
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Pesan, panggilan, foto, dan kirim file ada di dalam Phone Link."), FontSize = 11.5, Foreground = MenuSecondary,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        });
        return new Border
        {
            Width = PhoneColumn - 14, Margin = new Thickness(14, 2, 0, 4), Padding = new Thickness(16), CornerRadius = new CornerRadius(16),
            Background = MenuCard, Child = stack, VerticalAlignment = VerticalAlignment.Top,
        };
    }

    static void OpenPhoneLink()
    {
        try { Process.Start(new ProcessStartInfo("ms-phonelink:") { UseShellExecute = true }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\Microsoft.YourPhone_8wekyb3d8bbwe!App") { UseShellExecute = true }); } catch { }
        }
    }

    async void RefreshPhone()
    {
        if (!Settings.Current.DockMenuPhoneLink) return;
        var info = await PhoneStatus.QueryAsync();
        if (info == null)
        {
            _phoneName.Text = L.T("HP kamu");
            _phoneState.Text = L.T("Belum ada HP yang dipasangkan lewat Bluetooth.");
            return;
        }
        _phoneName.Text = info.Name;
        string state = info.Connected ? L.T("Tersambung") : L.T("Tidak tersambung");
        if (info.Battery is int b) state += $" · {L.T("baterai")} {b}%";
        _phoneState.Text = state;
    }

    // =====================================================================
    // Isi daftar aplikasi
    // =====================================================================

    void RebuildMenuApps()
    {
        string q = _menuSearch.Text.Trim();
        _menuBody.Children.Clear();
        _menuItems.Clear();
        var apps = AppCatalog.Apps;

        if (q.Length == 0)
        {
            // disematkan
            var pinnedIds = Settings.Current.MenuPinned ?? new List<string>();
            var pinned = pinnedIds.Select(id => apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)))
                                  .Where(a => a != null).Cast<AppCatalog.App>().ToList();
            _menuBody.Children.Add(MenuHeader("Disematkan"));
            if (pinned.Count == 0)
                _menuBody.Children.Add(MenuHint(apps.Count == 0 ? "" : "Klik kanan aplikasi di bawah → \"Sematkan ke menu\" supaya muncul di sini."));
            else
            {
                var wrap = new WrapPanel();
                foreach (var a in pinned) wrap.Children.Add(MenuTile(a, track: false));
                _menuBody.Children.Add(wrap);
            }

            _menuBody.Children.Add(MenuHeader("Semua aplikasi"));
            if (apps.Count == 0)
                _menuBody.Children.Add(MenuHint("Memuat daftar aplikasi…"));
            else
            {
                int icons = apps.Count(a => a.Icon != null);
                if (_allAppsCache == null || !ReferenceEquals(_allAppsSource, apps) || _allAppsIcons != icons)
                {
                    _allAppsCache = new WrapPanel();
                    foreach (var a in apps) _allAppsCache.Children.Add(MenuTile(a, track: false));
                    _allAppsSource = apps;
                    _allAppsIcons = icons;
                }
                if (_allAppsCache.Parent is Panel oldParent) oldParent.Children.Remove(_allAppsCache);
                _menuBody.Children.Add(_allAppsCache);
            }
        }
        else
        {
            var found = apps.Select(a => (a, sc: Score(a.Name, q))).Where(x => x.sc > 0)
                            .OrderByDescending(x => x.sc).ThenBy(x => x.a.Name).Take(24).Select(x => x.a).ToList();
            _menuBody.Children.Add(MenuHeader("Hasil"));
            if (found.Count == 0) _menuBody.Children.Add(MenuHint("Tidak ada aplikasi yang cocok."));
            else
            {
                var wrap = new WrapPanel();
                foreach (var a in found) wrap.Children.Add(MenuTile(a, track: true));
                _menuBody.Children.Add(wrap);
            }
            _menuBody.Children.Add(WebRow(q));
        }
        if (q.Length > 0 && _menuItems.Count > 0) _menuItems[0].tile.Background = MenuSelected; // Enter = yang pertama
        _menuScroll.ScrollToTop();
    }

    TextBlock MenuHeader(string text) => new()
    {
        Text = L.T(text), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = MenuSecondary, Margin = new Thickness(8, 8, 0, 6),
    };

    TextBlock MenuHint(string text) => new()
    {
        Text = L.T(text), FontSize = 12, Foreground = MenuSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 8),
    };

    /// <summary>Ikon aplikasi di menu: polos, atau bergaya sama dengan dock (bentuk + warna latar), dengan warna ikon pilihan.</summary>
    static FrameworkElement MenuIcon(AppCatalog.App a)
    {
        var s = Settings.Current;
        var img = IconStyle.Apply(a.Icon);
        if (!s.MenuIconsLikeDock)
        {
            FrameworkElement plain = img != null
                ? new Image { Source = img, Width = 40, Height = 40, Stretch = Stretch.Uniform }
                : new Border
                {
                    Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = MenuField,
                    Child = new TextBlock { Text = a.Name[..1].ToUpperInvariant(), FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                };
            RenderOptions.SetBitmapScalingMode(plain, BitmapScalingMode.HighQuality);
            return plain;
        }
        const double t = 44;
        FrameworkElement content = img != null
            ? new Image { Source = img, Width = t, Height = t, Stretch = Stretch.Uniform }
            : new TextBlock { Text = a.Name[..1].ToUpperInvariant(), FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(content, BitmapScalingMode.HighQuality);
        var box = new Border { Width = t, Height = t, Child = content, Background = TileBg };
        ApplyShape(box, content, img != null ? IconStyle.ApplyColor(a.Dominant) : null, t);
        return box;
    }

    Border MenuTile(AppCatalog.App a, bool track)
    {
        var icon = MenuIcon(a);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock
        {
            Text = a.Name, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 5, 4, 0), MaxWidth = MenuTileW - 8,
        });
        var tile = new Border
        {
            Width = MenuTileW, Height = MenuTileH, CornerRadius = new CornerRadius(12), Background = Brushes.Transparent,
            Child = stack, Cursor = Cursors.Hand, ToolTip = a.Name,
        };
        ToolTipService.SetInitialShowDelay(tile, 600);
        void Launch() { CloseMenu(); AppCatalog.Launch(a); }
        tile.MouseEnter += (_, _) => { if (tile.Background != MenuSelected) tile.Background = MenuHover; };
        tile.MouseLeave += (_, _) => { if (tile.Background == MenuHover) tile.Background = Brushes.Transparent; };
        tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; Launch(); };

        // menu klik kanan dibuat saat dibutuhkan saja (ratusan kotak × menu = memori terbuang)
        tile.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var cm = new ContextMenu();
            bool isPinned = (Settings.Current.MenuPinned ?? new List<string>()).Contains(a.Id, StringComparer.OrdinalIgnoreCase);
            var pin = new MenuItem { Header = isPinned ? L.T("Lepas dari menu") : L.T("Sematkan ke menu") };
            pin.Click += (_, _) =>
            {
                var list = Settings.Current.MenuPinned ??= new List<string>();
                if (isPinned) list.RemoveAll(x => string.Equals(x, a.Id, StringComparison.OrdinalIgnoreCase));
                else list.Add(a.Id);
                Settings.Save();
                RebuildMenuApps();
            };
            cm.Items.Add(pin);
            var open = new MenuItem { Header = L.T("Buka") };
            open.Click += (_, _) => Launch();
            cm.Items.Add(open);
            TrackPopup(cm);
            cm.PlacementTarget = tile;
            cm.IsOpen = true;
        };

        if (track) _menuItems.Add((tile, new Action(Launch))); // hasil pencarian: Enter membuka yang pertama
        return tile;
    }

    Border WebRow(string q)
    {
        var text = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new Run("  ") { FontFamily = MenuIconFont, Foreground = MenuSecondary });
        text.Inlines.Add(new Run(string.Format(L.T("Cari \"{0}\" di Google"), q)));
        var row = new Border
        {
            Child = text, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(10), Margin = new Thickness(2, 10, 6, 6),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
        };
        void Go()
        {
            CloseMenu();
            try { Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString(q)) { UseShellExecute = true }); } catch { }
        }
        row.MouseEnter += (_, _) => { if (row.Background != MenuSelected) row.Background = MenuHover; };
        row.MouseLeave += (_, _) => { if (row.Background == MenuHover) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; Go(); };
        _menuItems.Add((row, new Action(Go)));
        return row;
    }

    /// <summary>Awal nama > awal kata > singkatan (mis. "vsc" = Visual Studio Code) > ada di tengah.</summary>
    static int Score(string name, string q)
    {
        var ci = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions o = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        if (ci.IsPrefix(name, q, o)) return 100 - Math.Min(20, name.Length - q.Length);
        var words = name.Split(new[] { ' ', '-', '_', '.', '(' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => ci.IsPrefix(w, q, o))) return 75;
        string initials = string.Concat(words.Select(w => char.ToLowerInvariant(w[0])));
        if (q.Length >= 2 && initials.StartsWith(q.ToLowerInvariant(), StringComparison.Ordinal)) return 60;
        if (ci.IndexOf(name, q, o) >= 0) return 45;
        return 0;
    }

    void OnMenuKey(object sender, KeyEventArgs e)
    {
        if (!_menuOpen) return;
        if (e.Key == Key.Escape) { e.Handled = true; CloseMenu(); }
        else if (e.Key == Key.Enter && _menuItems.Count > 0) { e.Handled = true; _menuItems[0].run(); }
    }

    // =====================================================================
    // Buka / tutup
    // =====================================================================

    public void ToggleMenu()
    {
        if (_menuOpen) CloseMenu(); else OpenMenu();
    }

    void OpenMenu()
    {
        var s = Settings.Current;
        if (_suspended || !s.DockMenu || _menuOpen) return;
        _menuOpen = true;
        _previewTimer.Stop();
        _preview.Hide();
        _hideTimer.Stop();

        bool phone = s.DockMenuPhoneLink;
        _phoneCard.Visibility = phone ? Visibility.Visible : Visibility.Collapsed;
        _menuW = Math.Min(phone ? 860 : 700, _winW - 40);

        UpdateWindowBounds();  // jendela dibesarkan dulu (menu masih 0, jadi tidak terlihat bedanya)
        ApplyPresence();       // dock tersembunyi (mode Smart): munculkan
        _menuSearch.Text = "";
        RebuildMenuApps();
        AppCatalog.EnsureLoaded(Dispatcher, 48);
        if (phone) RefreshPhone();

        _menu.Target = 1;
        FrameLoop.Add(_tick);
        FocusMenu();
        _menuPoll.Start();
    }

    public void CloseMenu()
    {
        if (!_menuOpen) return;
        _menuOpen = false;
        _menuPoll.Stop();
        _menu.Target = 0;
        FrameLoop.Add(_tick);
        try { Native.SetNoActivate(new WindowInteropHelper(this).Handle, true); } catch { }
        ApplyPresence();
        if (_edgeRevealed) StartTimer(_hideTimer, 900);
    }

    /// <summary>
    /// Menu selesai menutup: lepas ratusan kotak aplikasi dan kembalikan memorinya ke Windows
    /// (dibuat ulang saat menu dibuka lagi; ikon tetap disimpan jadi tetap cepat).
    /// </summary>
    void ReleaseMenuContent()
    {
        _menuBody.Children.Clear();
        _menuItems.Clear();
        _allAppsCache = null;
        _allAppsSource = null;
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) => { t.Stop(); if (!_menuOpen && App.Island?.IsBusy != true) MemoryTrim.Trim(); };
        t.Start();
    }

    /// <summary>Ambil fokus keyboard supaya bisa langsung mengetik di kotak pencarian.</summary>
    void FocusMenu()
    {
        if (!_menuOpen) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.SetNoActivate(hwnd, false);
        IntPtr fg = Native.GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? Native.GetWindowThreadProcessId(fg, out _) : 0;
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        SetForegroundWindow(hwnd);
        if (attached) AttachThreadInput(me, fgThread, false);
        Activate();
        _menuSearch.Focus();
        Keyboard.Focus(_menuSearch);
    }

    // =====================================================================
    // Ukuran jendela & gambar
    // =====================================================================

    /// <summary>Tinggi jendela: setinggi dock saja saat menu tertutup, setinggi dock + menu saat terbuka.</summary>
    void UpdateWindowBounds()
    {
        _baseWinH = _dockH + _tile * 0.5 + 16; // ruang di atas untuk ikon yang membesar
        double want = (_menuOpen || _menu.Value > 0.001) ? Math.Max(_baseWinH, _dockH + MenuHeight + 40) : _baseWinH;
        double screenW = SystemParameters.PrimaryScreenWidth, screenH = SystemParameters.PrimaryScreenHeight;
        double left = (screenW - _winW) / 2, top = screenH - want;
        bool sizeChanged = Math.Abs(want - _winH) > 0.5 || Math.Abs(Width - _winW) > 0.5;
        _winH = want;
        _root.Width = _layer.Width = _winW;
        _root.Height = _layer.Height = _winH;
        Redraw(); // isi digambar menempel ke bawah jendela yang baru

        var hwnd = new WindowInteropHelper(this).Handle;
        void Move()
        {
            if (hwnd != IntPtr.Zero && sizeChanged)
            {
                // pindah + ubah ukuran dalam SATU langkah
                var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(left * m.M11), (int)Math.Round(top * m.M22),
                    (int)Math.Round(_winW * m.M11), (int)Math.Round(_winH * m.M22), 0x0004 | 0x0010); // NOZORDER | NOACTIVATE
            }
            Width = _winW;
            Height = _winH;
            Left = left;
            Top = top;
        }
        // jendela tumbuh ke atas: tanpa ini Windows sempat menampilkan gambar dock LAMA di posisi atas yang baru
        // (dock "melayang" sepersekian detik). Disembunyikan sebentar oleh DWM sampai gambar barunya siap.
        if (hwnd != IntPtr.Zero && sizeChanged) WindowCloak.Run(hwnd, Dispatcher, Move);
        else Move();
    }

    /// <summary>Dipanggil dari Redraw: posisikan isi menu dan potong mengikuti bentuk yang sedang tumbuh.</summary>
    void LayoutMenu(double shapeW, double shapeH, double open)
    {
        if (open <= 0.01 && !_menuOpen)
        {
            if (_menuPanel.Visibility != Visibility.Collapsed) { _menuPanel.Visibility = Visibility.Collapsed; _menuSep.Visibility = Visibility.Collapsed; }
            return;
        }
        _menuPanel.Visibility = Visibility.Visible;
        _menuSep.Visibility = Visibility.Visible;
        double panelTop = _winH - _dockH - MenuHeight, panelLeft = _winW / 2 - _menuW / 2;
        _menuPanel.Width = _menuW;
        _menuPanel.Height = MenuHeight;
        Canvas.SetLeft(_menuPanel, panelLeft);
        Canvas.SetTop(_menuPanel, panelTop);
        // isi terlihat hanya di dalam bentuk yang sedang memanjang (seperti island membuka)
        double clipY = Math.Max(0, (_winH - shapeH) - panelTop);
        double clipX = Math.Max(0, (_menuW - shapeW) / 2);
        _menuPanel.Clip = new RectangleGeometry(new Rect(clipX, clipY, Math.Max(0, _menuW - clipX * 2), Math.Max(0, MenuHeight - clipY)));
        double fade = MathEx.Clamp01((open - 0.35) / 0.65);
        _menuPanel.Opacity = fade;
        _menuPanel.IsHitTestVisible = _menuOpen && open > 0.6;

        _menuSep.Width = Math.Max(0, Math.Min(shapeW, _menuW) - 40);
        Canvas.SetLeft(_menuSep, _winW / 2 - _menuSep.Width / 2);
        Canvas.SetTop(_menuSep, _winH - _dockH);
        _menuSep.Opacity = fade;
    }

    // =====================================================================
    // Tombol Windows
    // =====================================================================

    void UpdateStartKeyHook()
    {
        var s = Settings.Current;
        bool want = s.DockEnabled && s.DockMenu && s.WinKeyOpensMenu && !_suspended;
        if (want)
        {
            if (_startKeys == null)
            {
                _startKeys = new StartKeyHook(Dispatcher);
                _startKeys.WinPressedAlone += ToggleMenu;
            }
            _startKeys.Active = true;
            if (!_startKeys.IsRunning) _startKeys.Start();
        }
        else if (_startKeys != null)
        {
            _startKeys.Active = false;
            if (_startKeys.IsRunning) _startKeys.Stop();
        }
    }

    // =====================================================================
    // Native
    // =====================================================================

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool LockWorkStation();
}
