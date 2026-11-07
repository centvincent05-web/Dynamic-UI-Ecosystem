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

/// <summary>Island: shelf, riwayat clipboard, chat Gemini, jadwal, cuaca, pomodoro.</summary>
public partial class IslandWindow
{
    // =====================================================================
    // Shelf · Clipboard · Gemini · jadwal · cuaca · pomodoro
    // =====================================================================

    ClipboardHistory? _clip;
    CalendarFeed? _calendar;
    WeatherService? _weather;
    readonly GeminiClient _gemini = new();
    bool _dragOver, _geminiBusy, _geminiTyping;

    public void AttachExtras(ClipboardHistory clip, CalendarFeed calendar, WeatherService weather)
    {
        _clip = clip;
        _calendar = calendar;
        _weather = weather;
        clip.Attach(this);
        clip.Changed += () => { if (_mode == IslandMode.Clipboard) RebuildClip(); };
        calendar.Changed += UpdateCalendarExtras;
        calendar.Reminder += e => ShowNotificationAlert(new NotificationItem(0, "", "Google Calendar",
            e.Title, $"{e.Start:HH:mm} · {L.T("dimulai")} {Math.Max(0, (int)Math.Round((e.Start - DateTime.Now).TotalMinutes))} {L.T("menit lagi")}", "", DateTime.Now));
        weather.Changed += () => UpdateClock(reschedule: false);
        CalEventsBox.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenUrl("https://calendar.google.com/calendar/r"); };

        // ---- shelf: tarik file ke notch ----
        Root.AllowDrop = true;
        Root.DragEnter += (_, e) =>
        {
            if (!Settings.Current.PageShelf || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            _dragOver = true;
            _collapseTimer.Stop();
            if (!_expanded) Expand(IslandMode.Shelf); else if (_mode != IslandMode.Shelf) SetMode(IslandMode.Shelf, animate: true);
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        Root.DragOver += (_, e) => { if (_dragOver) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        Root.DragLeave += (_, _) => { _dragOver = false; if (!Root.IsMouseOver) Start(_collapseTimer, Settings.Current.CollapseDelayMs + 800); };
        Root.Drop += (_, e) =>
        {
            _dragOver = false;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                var list = Settings.Current.ShelfItems ??= new List<string>();
                foreach (var f in files) { list.Remove(f); list.Insert(0, f); }
                if (list.Count > 30) list.RemoveRange(30, list.Count - 30);
                Settings.Save();
                RebuildShelf();
            }
            e.Handled = true;
        };
        ShelfClearButton.Click += (_, _) => { Settings.Current.ShelfItems.Clear(); Settings.Save(); RebuildShelf(); };
        // thumbnail / pratinjau Shelf dinyalakan-dimatikan di Pengaturan: susun ulang kotaknya
        bool lastThumb = Settings.Current.ShelfThumbnails, lastHover = Settings.Current.ShelfHoverPreview;
        Settings.Changed += () =>
        {
            var st = Settings.Current;
            if (st.ShelfThumbnails == lastThumb && st.ShelfHoverPreview == lastHover) return;
            lastThumb = st.ShelfThumbnails; lastHover = st.ShelfHoverPreview;
            RebuildShelf();
        };

        // ---- clipboard ----
        ClipClearButton.Click += (_, _) => _clip?.Clear();

        // ---- gemini ----
        GeminiInput.PreviewMouseLeftButtonDown += (_, _) => BeginGeminiTyping();
        GeminiInput.TextChanged += (_, _) => GeminiPlaceholder.Visibility = GeminiInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        GeminiInput.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; SendGemini(); }
            else if (e.Key == Key.Escape) { e.Handled = true; EndGeminiTyping(); }
        };
        GeminiSend.Click += (_, _) => SendGemini();
        GeminiNewButton.Click += (_, _) => { _gemini.Clear(); GeminiMessages.Children.Clear(); };
        GeminiKeyButton.Click += (_, _) => OpenUrl("https://aistudio.google.com/apikey");
        // scroll di daftar panjang: gulir dulu, pindah halaman setelah mentok
        foreach (var sv in new[] { ShelfScroll, ClipScroll, GeminiScroll }) HookListScroll(sv);

        PomodoroButton.Click += (_, _) => TogglePomodoro();
        UpdateCalendarExtras();
        UpdateDots();
    }

    void HookListScroll(ScrollViewer sv)
    {
        sv.PreviewMouseWheel += (sender, e) =>
        {
            bool down = e.Delta < 0;
            bool canScroll = down ? sv.VerticalOffset < sv.ScrollableHeight - 0.5 : sv.VerticalOffset > 0.5;
            if (canScroll)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * 0.4);
                _pageWheel = 0;
            }
            else OnPageWheel(sender, e);
            e.Handled = true;
        };
    }

    // ---------- shelf ----------

    /// <summary>Isi halaman berubah (mis. shelf / clipboard dari kosong jadi ada isi): tinggi island menyesuaikan.</summary>
    void RefitPage(IslandMode mode)
    {
        if (!_expanded || _mode != mode) return;
        var (w, h) = Dims(mode);
        if (Math.Abs(_pageH.Target - h) < 0.5) return;
        GrowWindow(w, h);
        _pageH.Target = h;
        FrameLoop.Add(_animTick);
    }

    int _shelfGen;

    void RebuildShelf()
    {
        HideShelfPreview();
        int gen = ++_shelfGen;
        bool thumbs = Settings.Current.ShelfThumbnails, hoverPreview = Settings.Current.ShelfHoverPreview;
        var pendingThumbs = new List<(Image img, string path)>();
        Dispatcher.BeginInvoke(new Action(() => RefitPage(IslandMode.Shelf)));
        var items = (Settings.Current.ShelfItems ?? new List<string>()).Where(f => System.IO.File.Exists(f) || System.IO.Directory.Exists(f)).ToList();
        ShelfTiles.Children.Clear();
        ShelfEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfClearButton.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var path in items)
        {
            var icon = IconLoader.Get(path, 64).Image;
            var name = new TextBlock
            {
                Text = System.IO.Path.GetFileName(path.TrimEnd('\\')), Foreground = Brushes.White, FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, MaxWidth = 82, Margin = new Thickness(0, 4, 0, 0),
            };
            var close = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = TileOff, Opacity = 0, Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = "✕", Foreground = Brushes.White, FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var stack = new StackPanel { Margin = new Thickness(6, 6, 6, 4) };
            var img = new Image { Source = icon, Width = 40, Height = 40, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            stack.Children.Add(img);
            if (thumbs && System.IO.File.Exists(path)) pendingThumbs.Add((img, path));
            stack.Children.Add(name);
            var grid = new Grid();
            grid.Children.Add(stack);
            grid.Children.Add(close);
            var tile = new Border
            {
                Width = 92, Height = 84, CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Margin = new Thickness(2), Child = grid, ToolTip = hoverPreview ? null : path,
            };
            var p = path;
            tile.MouseEnter += (_, _) => { tile.Background = NotifRowBg; close.Opacity = 1; if (hoverPreview) ScheduleShelfPreview(tile, path); };
            tile.MouseLeave += (_, _) => { tile.Background = Brushes.Transparent; close.Opacity = 0; HideShelfPreview(); };
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Settings.Current.ShelfItems.Remove(p); Settings.Save(); RebuildShelf(); };
            // tarik keluar ke aplikasi lain; klik untuk membuka
            Point? down = null;
            tile.PreviewMouseLeftButtonDown += (_, e) => { down = e.GetPosition(tile); HideShelfPreview(); };
            tile.MouseMove += (_, e) =>
            {
                if (down == null || e.LeftButton != MouseButtonState.Pressed) return;
                var d = e.GetPosition(tile) - down.Value;
                if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
                down = null;
                try { DragDrop.DoDragDrop(tile, new DataObject(DataFormats.FileDrop, new[] { p }), DragDropEffects.Copy | DragDropEffects.Move); } catch { }
            };
            tile.MouseLeftButtonUp += (_, e) =>
            {
                if (down == null) return;
                down = null;
                e.Handled = true;
                OpenUrl(p);
            };
            ShelfTiles.Children.Add(tile);
        }
        if (pendingThumbs.Count > 0) LoadShelfThumbs(gen, pendingThumbs);
    }

    /// <summary>
    /// Ambil thumbnail isi file di thread latar (dokumen/video bisa lambat), lalu ganti ikon satu per satu.
    /// Thread STA karena sebagian penyedia thumbnail Windows (Office, PDF) membutuhkannya.
    /// </summary>
    void LoadShelfThumbs(int gen, List<(Image img, string path)> items)
    {
        var t = new System.Threading.Thread(() =>
        {
            foreach (var (img, path) in items)
            {
                if (gen != _shelfGen) return; // Shelf sudah disusun ulang
                var bmp = IconLoader.GetThumbnail(path, 160);
                if (bmp == null) continue;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != _shelfGen) return;
                    img.Source = bmp;
                    img.Width = 72; img.Height = 48; // isi file: lebih lebar dari ikon, nama tetap muat di kotak
                }));
            }
        }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal };
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
    }

    // ---------- pratinjau besar Shelf (ala Quick Look) ----------

    System.Windows.Controls.Primitives.Popup? _shelfPreview;
    DispatcherTimer? _shelfPreviewTimer;
    int _shelfPreviewGen;

    void ScheduleShelfPreview(FrameworkElement tile, string path)
    {
        HideShelfPreview();
        _shelfPreviewTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _shelfPreviewTimer.Tick -= OnPreviewTick;
        _pendingPreview = (tile, path);
        _shelfPreviewTimer.Tick += OnPreviewTick;
        _shelfPreviewTimer.Start();
    }

    (FrameworkElement tile, string path)? _pendingPreview;

    void OnPreviewTick(object? sender, EventArgs e)
    {
        _shelfPreviewTimer?.Stop();
        if (_pendingPreview is not { } p || !p.tile.IsMouseOver || _dragOver) return;
        ShowShelfPreview(p.tile, p.path);
    }

    void HideShelfPreview()
    {
        _shelfPreviewTimer?.Stop();
        _pendingPreview = null;
        _shelfPreviewGen++;
        if (_shelfPreview != null) { _shelfPreview.IsOpen = false; _shelfPreview = null; }
    }

    void ShowShelfPreview(FrameworkElement tile, string path)
    {
        const double maxSide = 300;
        bool isDir = System.IO.Directory.Exists(path);
        var image = new Image
        {
            Source = IconLoader.Get(path, 256).Image, Width = 96, Height = 96, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 10),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        string name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        string detail;
        try
        {
            if (isDir)
            {
                int n = 0;
                foreach (var _ in System.IO.Directory.EnumerateFileSystemEntries(path)) if (++n >= 999) break;
                detail = L.T("Folder") + " · " + n + L.T(" item");
            }
            else
            {
                var fi = new System.IO.FileInfo(path);
                string ext = fi.Extension.TrimStart('.').ToUpperInvariant();
                detail = (ext.Length > 0 ? ext + " · " : "") + FormatSize(fi.Length) + " · " +
                         L.T("Diubah ") + fi.LastWriteTime.ToString("d MMM yyyy, HH:mm", L.Culture);
            }
        }
        catch { detail = ""; }

        var font = new FontFamily(Settings.Current.FontName);
        var stack = new StackPanel { MaxWidth = maxSide };
        stack.Children.Add(image);
        stack.Children.Add(new TextBlock
        {
            Text = name, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = font,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (detail.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = detail, Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), FontSize = 11, FontFamily = font,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0),
            });

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 12, 14, 12), MinWidth = 180,
            Child = stack, Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.55 },
        };
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = tile, Placement = System.Windows.Controls.Primitives.PlacementMode.Custom,
            AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
            StaysOpen = true, IsHitTestVisible = false, Focusable = false, Child = card,
        };
        // di tengah, tepat di bawah kotak file
        popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => new[]
        {
            new System.Windows.Controls.Primitives.CustomPopupPlacement(
                new Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height - 4),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
        };
        _shelfPreview = popup;
        popup.IsOpen = true;

        // isi file (halaman dokumen, gambar, video) dimuat di latar, lalu ikon diganti
        if (!isDir)
        {
            int gen = _shelfPreviewGen;
            var t = new System.Threading.Thread(() =>
            {
                var bmp = IconLoader.GetThumbnail(path, 400);
                if (bmp == null) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != _shelfPreviewGen) return; // sudah ditutup / pindah file
                    double scale = Math.Min(maxSide / bmp.PixelWidth, 220.0 / bmp.PixelHeight);
                    scale = Math.Min(scale, 1.0);
                    image.Source = bmp;
                    image.Width = Math.Max(96, bmp.PixelWidth * scale);
                    image.Height = Math.Max(64, bmp.PixelHeight * scale);
                }));
            }) { IsBackground = true };
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
        }
    }

    static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes; int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }

    // ---------- clipboard ----------

    void RebuildClip()
    {
        Dispatcher.BeginInvoke(new Action(() => RefitPage(IslandMode.Clipboard)));
        ClipRows.Children.Clear();
        var items = _clip?.Items ?? Array.Empty<string>();
        ClipEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipClearButton.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var text in items)
        {
            string preview = text.Replace("\r", "").Replace("\n", " ⏎ ").Trim();
            var label = new TextBlock
            {
                Text = preview, Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34, Margin = new Thickness(0, 0, 22, 0),
            };
            var close = new TextBlock
            {
                Text = "✕", Foreground = Subtle, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand, Opacity = 0, Background = Brushes.Transparent, Padding = new Thickness(4, 0, 0, 0),
            };
            var grid = new Grid();
            grid.Children.Add(label);
            grid.Children.Add(close);
            var row = new Border
            {
                Background = NotifRowBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 5), Child = grid, Cursor = Cursors.Hand, ToolTip = L.T("Klik untuk menyalin lagi"),
            };
            var t = text;
            row.MouseEnter += (_, _) => close.Opacity = 1;
            row.MouseLeave += (_, _) => close.Opacity = 0;
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; _clip?.Remove(t); };
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _clip?.Copy(t);
                label.Text = "✓ " + L.T("Disalin");
                var timer = OneShot(RebuildClip);
                Start(timer, 900);
            };
            ClipRows.Children.Add(row);
        }
    }

    // ---------- gemini ----------

    void UpdateGeminiState()
    {
        bool key = _gemini.HasKey;
        GeminiNoKey.Visibility = key ? Visibility.Collapsed : Visibility.Visible;
        GeminiScroll.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
        GeminiInput.IsEnabled = key;
    }

    void BeginGeminiTyping()
    {
        if (_geminiTyping) return;
        _geminiTyping = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetNoActivate(hwnd, false); // izinkan island menerima ketikan
        Activate();
        GeminiInput.Focus();
    }

    void EndGeminiTyping()
    {
        if (!_geminiTyping) return;
        _geminiTyping = false;
        Keyboard.ClearFocus();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && PasswordPanel.Visibility != Visibility.Visible) Native.SetNoActivate(hwnd, true);
    }

    async void SendGemini()
    {
        string q = GeminiInput.Text.Trim();
        if (q.Length == 0 || _geminiBusy || !_gemini.HasKey) return;
        GeminiInput.Text = "";
        AddGeminiBubble(q, user: true);
        var thinking = AddGeminiBubble("…", user: false);
        _geminiBusy = true;
        string answer = await _gemini.AskAsync(q);
        _geminiBusy = false;
        thinking.Text = answer;
        GeminiScroll.ScrollToEnd();
    }

    TextBlock AddGeminiBubble(string text, bool user)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = Brushes.White, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 18,
        };
        var bubble = new Border
        {
            Child = tb, CornerRadius = new CornerRadius(14), Padding = new Thickness(11, 7, 11, 7), Margin = new Thickness(user ? 50 : 0, 0, user ? 0 : 30, 6),
            Background = user ? TileOff : Brushes.Transparent, HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        // klik kanan: salin jawaban
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = L.T("Salin") };
        copy.Click += (_, _) => { try { Clipboard.SetText(tb.Text); } catch { } };
        menu.Items.Add(copy);
        bubble.ContextMenu = menu;
        GeminiMessages.Children.Add(bubble);
        GeminiScroll.ScrollToEnd();
        return tb;
    }

    // ---------- kalender: jadwal & cuaca ----------

    double CalendarExtra => Settings.Current.CalendarEvents && !string.IsNullOrWhiteSpace(Settings.Current.CalendarIcsUrl) ? 36 : 0;

    public void UpdateCalendarExtras()
    {
        bool show = CalendarExtra > 0;
        CalEventsBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && _calendar != null)
        {
            var now = DateTime.Now;
            var upcoming = _calendar.Today.Where(e => e.AllDay || e.End > now).Take(4).ToList();
            CalEvents.Text = _calendar.Error != null && _calendar.Today.Count == 0 ? "⚠ " + L.T("Gagal membaca kalender")
                : upcoming.Count == 0 ? "📅  " + L.T("Tidak ada acara lagi hari ini")
                : "📅  " + string.Join("   ·   ", upcoming.Select(e => (e.AllDay ? L.T("Seharian") : e.Start.ToString("HH:mm")) + "  " + e.Title));
        }
        var s = Settings.Current;
        PlaceView(CalendarView, s.CalendarWidth, s.CalendarHeight + CalendarExtra);
        if (_expanded && _mode == IslandMode.Calendar) GrowWindow(s.CalendarWidth, s.CalendarHeight + CalendarExtra);
        if (_expanded && _mode == IslandMode.Calendar) SetMode(IslandMode.Calendar, animate: true);
    }

    string WeatherPrefix()
    {
        if (_weather?.Temperature is not double t || !Settings.Current.CalendarWeather) return "";
        var (icon, _) = WeatherService.Describe(_weather.Code);
        return $"{icon} {t:0}° {_weather.City}  ·  ";
    }

    // ---------- pomodoro ----------

    int _pomoPhase = -1; // -1 = mati; genap = fokus, ganjil = istirahat
    int _pomoRound;

    void TogglePomodoro()
    {
        if (_pomoPhase >= 0)
        {
            _pomoPhase = -1;
            _timer.Reset();
            UpdateTimer();
            return;
        }
        _pomoPhase = 0;
        _pomoRound = 1;
        _timer.SetMinutes(25);
        _timer.Start();
        UpdateTimer();
    }

    /// <summary>Waktu habis saat pomodoro: lanjut otomatis ke istirahat / fokus berikutnya.</summary>
    bool PomodoroNext()
    {
        if (_pomoPhase < 0 || !Settings.Current.PomodoroEnabled) return false;
        bool wasFocus = _pomoPhase % 2 == 0;
        _pomoPhase++;
        int minutes;
        string title, body;
        if (wasFocus)
        {
            bool longBreak = _pomoRound % 4 == 0;
            minutes = longBreak ? 15 : 5;
            title = L.T("Waktunya istirahat");
            body = $"{minutes} {L.T("menit")} · {L.T("putaran")} {_pomoRound}/4";
        }
        else
        {
            _pomoRound++;
            minutes = 25;
            title = L.T("Waktunya fokus lagi");
            body = $"25 {L.T("menit")} · {L.T("putaran")} {(_pomoRound - 1) % 4 + 1}/4";
        }
        ShowNotificationAlert(new NotificationItem(0, "", "🍅 Pomodoro", title, body, "", DateTime.Now));
        var next = OneShot(() => { _timer.Dismiss(); _timer.SetMinutes(minutes); _timer.Start(); UpdateTimer(); });
        Start(next, 1200);
        return true;
    }
}
