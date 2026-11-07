using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.UI;

/// <summary>
/// Preview jendela di atas ikon dock: gambar langsung (live) tiap jendela lewat DWM thumbnail,
/// klik untuk membuka, ✕ untuk menutup, plus tombol Sematkan / Lepas.
/// </summary>
public sealed class DockPreviewWindow : Window
{
    const double CardW = 220, ThumbH = 124;

    static readonly Brush CardBg = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E));
    static readonly Brush CardHover = Frozen(Color.FromRgb(0x2C, 0x2C, 0x2E));
    static readonly Brush Subtle = Frozen(Color.FromRgb(0xB4, 0xB2, 0xA9));
    static readonly Brush ChipBg = Frozen(Color.FromRgb(0x2C, 0x2C, 0x2E));

    readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock _title = new() { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190, Margin = new Thickness(0, 0, 10, 0) };
    readonly Image _icon = new() { Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    readonly List<(IntPtr thumb, Border slot, IntPtr src)> _thumbs = new();
    IntPtr _hwnd;
    double _anchorX, _anchorBottom;

    public DockApp? CurrentApp { get; private set; }

    /// <summary>Pengguna menyematkan / melepas atau menutup jendela: dock perlu disusun ulang.</summary>
    public event Action? DockChanged;

    public DockPreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false; // DWM thumbnail butuh jendela biasa (bukan layered)
        Background = Frozen(Color.FromRgb(0x0E, 0x0E, 0x10));
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Title = "Preview";

        // judul di kiri, tombol ikon kecil di kanan: tidak bisa saling tumpang tindih
        var header = new DockPanel { Margin = new Thickness(4, 0, 0, 10), LastChildFill = true };
        DockPanel.SetDock(_buttons, Dock.Right);
        header.Children.Add(_buttons);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_icon);
        left.Children.Add(_title);
        header.Children.Add(left);

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(header);
        root.Children.Add(_cards);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd);
            int round = 2; // DWMWCP_ROUND: sudut membulat khas Windows 11
            DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
        };
        SizeChanged += (_, _) => { Reposition(); Dispatcher.BeginInvoke(new Action(UpdateThumbs), DispatcherPriority.Render); };
        LocationChanged += (_, _) => UpdateThumbs();
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClearThumbs(); };
    }

    /// <param name="anchorX">Tengah ikon (DIP, koordinat layar).</param>
    /// <param name="anchorBottom">Batas bawah preview = atas dock (DIP).</param>
    public void ShowFor(DockApp app, double anchorX, double anchorBottom)
    {
        CurrentApp = app;
        _anchorX = anchorX;
        _anchorBottom = anchorBottom;
        Build(app);
        if (!IsVisible) Show();
        Reposition();
        Dispatcher.BeginInvoke(new Action(UpdateThumbs), DispatcherPriority.Render);
    }

    void Build(DockApp app)
    {
        ClearThumbs();
        FontFamily = IslandWindow.UiFont();
        _cards.Children.Clear();
        _buttons.Children.Clear();

        string? path = app.LaunchPath ?? app.ExePath;
        _icon.Source = path != null ? IconLoader.Get(path, 32).Image : null;
        _title.Text = app.Name;

        _buttons.Children.Add(Chip(app.Pinned ? "\uE77A" : "\uE718", app.Pinned ? L.T("Lepas dari dock") : L.T("Sematkan ke dock"), () =>
        {
            if (app.Pinned) WindowTracker.Unpin(app); else WindowTracker.Pin(app);
            app.Pinned = !app.Pinned;
            DockChanged?.Invoke();
            Build(app);
        }));
        _buttons.Children.Add(Chip("\uE710", L.T("Buka jendela baru"), () => { WindowTracker.Launch(app); Hide(); }));

        if (app.Windows.Count == 0)
        {
            _cards.Children.Add(new TextBlock
            {
                Text = app.Pinned ? L.T("Belum dibuka · klik ikon untuk membuka") : L.T("Tidak ada jendela"),
                Foreground = Subtle, FontSize = 12, Margin = new Thickness(4, 0, 4, 2),
            });
            return;
        }

        foreach (var hwnd in app.Windows) _cards.Children.Add(BuildCard(hwnd));
    }

    static readonly Brush CloseHover = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0x45, 0x3A));

    FrameworkElement BuildCard(IntPtr hwnd)
    {
        var title = new TextBlock
        {
            Text = Native.WindowTitle(hwnd), Foreground = Brushes.White, FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 26, 0),
        };
        // area klik dibuat penuh (latar transparan), bukan hanya garis huruf ✕ yang tipis
        var close = new Border
        {
            Width = 24, Height = 22, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "✕", Foreground = Subtle, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        close.MouseEnter += (_, _) => close.Background = CloseHover;
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, e) =>
        {
            WindowTracker.CloseWindow(hwnd);
            e.Handled = true;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            t.Tick += (_, _) => { t.Stop(); DockChanged?.Invoke(); };
            t.Start();
        };

        var top = new Grid { Height = 24 };
        top.Children.Add(title);
        top.Children.Add(close);

        // tempat gambar DWM thumbnail
        var slot = new Border
        {
            Height = ThumbH, Margin = new Thickness(0, 4, 0, 0), CornerRadius = new CornerRadius(6),
            Background = Frozen(Color.FromRgb(0x08, 0x08, 0x0A)),
        };
        // label kecil di pojok untuk jendela yang sedang minimize (gambarnya tetap tampil)
        if (Native.IsIconic(hwnd))
        {
            title.Text = "⤓ " + title.Text;
            title.ToolTip = L.T("Sedang di-minimize");
        }

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(slot);

        var card = new Border
        {
            Width = CardW, Padding = new Thickness(6), Margin = new Thickness(0, 0, 8, 0),
            CornerRadius = new CornerRadius(12), Background = CardBg, Child = stack, Cursor = Cursors.Hand,
        };
        card.MouseEnter += (_, _) => card.Background = CardHover;
        card.MouseLeave += (_, _) => card.Background = CardBg;
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
            Native.SwitchToThisWindow(hwnd, true);
            Hide();
        };

        _thumbs.Add((IntPtr.Zero, slot, hwnd));
        return card;
    }

    /// <summary>Tombol ikon bulat kecil (keterangan muncul sebagai tooltip).</summary>
    Border Chip(string glyph, string tooltip, Action onClick)
    {
        var chip = new Border
        {
            Background = ChipBg, CornerRadius = new CornerRadius(13), Width = 26, Height = 26,
            Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = tooltip,
            Child = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        chip.MouseEnter += (_, _) => chip.Opacity = 0.8;
        chip.MouseLeave += (_, _) => chip.Opacity = 1;
        chip.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
        return chip;
    }

    void Reposition()
    {
        double screenW = SystemParameters.PrimaryScreenWidth;
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        if (double.IsNaN(w) || double.IsNaN(h)) return;
        Left = Math.Clamp(_anchorX - w / 2, 8, screenW - w - 8);
        Top = _anchorBottom - h - 10;
    }

    // ---------- DWM thumbnail ----------

    void UpdateThumbs()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        for (int i = 0; i < _thumbs.Count; i++)
        {
            var (thumb, slot, src) = _thumbs[i];
            if (slot.ActualWidth <= 0) continue;
            if (thumb == IntPtr.Zero)
            {
                if (DwmRegisterThumbnail(_hwnd, src, out thumb) != 0) continue;
                _thumbs[i] = (thumb, slot, src);
            }

            Point p = slot.TransformToAncestor(this).Transform(new Point(0, 0));
            double sw = slot.ActualWidth, sh = slot.ActualHeight;

            // pertahankan rasio jendela asli di dalam kotak
            double ratio = 16.0 / 9;
            if (Native.IsIconic(src))
            {
                // jendela minimize: pakai ukuran jendela saat terbuka (bukan ukuran ikon minimize)
                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (GetWindowPlacement(src, ref wp))
                {
                    int nw = wp.rcNormalPosition.Right - wp.rcNormalPosition.Left;
                    int nh = wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top;
                    if ((wp.flags & 0x2) != 0) // WPF_RESTORETOMAXIMIZED: sebelum minimize ia maximize
                    {
                        var m = Native.PrimaryMonitorRect();
                        nw = m.Right - m.Left; nh = m.Bottom - m.Top;
                    }
                    if (nw > 0 && nh > 0) ratio = (double)nw / nh;
                }
            }
            else if (DwmQueryThumbnailSourceSize(thumb, out var size) == 0 && size.cy > 0)
            {
                ratio = (double)size.cx / size.cy;
            }
            double tw = sw, th = sw / ratio;
            if (th > sh) { th = sh; tw = sh * ratio; }
            double x = p.X + (sw - tw) / 2, y = p.Y + (sh - th) / 2;

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new Native.RECT
                {
                    Left = (int)Math.Round(x * dpi), Top = (int)Math.Round(y * dpi),
                    Right = (int)Math.Round((x + tw) * dpi), Bottom = (int)Math.Round((y + th) * dpi),
                },
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = false,
            };
            DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    void ClearThumbs()
    {
        foreach (var (thumb, _, _) in _thumbs)
            if (thumb != IntPtr.Zero) DwmUnregisterThumbnail(thumb);
        _thumbs.Clear();
    }

    const int DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public Native.RECT rcDestination;
        public Native.RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)] struct PSIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public Native.POINT ptMinPosition, ptMaxPosition;
        public Native.RECT rcNormalPosition;
    }

    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);

    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out PSIZE size);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
