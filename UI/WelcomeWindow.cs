using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DynamicIsland.Core;

namespace DynamicIsland.UI;

/// <summary>
/// Layar sambutan (muncul sekali saat pertama kali dijalankan, bisa dibuka lagi dari Pengaturan → Sistem):
/// cara membuka Pengaturan, menu dock, menutup app, dan mengembalikan taskbar.
/// </summary>
public sealed class WelcomeWindow : Window
{
    static readonly Brush Bg = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E));
    static readonly Brush Card = Frozen(Color.FromRgb(0x2C, 0x2C, 0x2E));
    static readonly Brush CardBorder = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    static readonly Brush Secondary = Frozen(Color.FromRgb(0x98, 0x98, 0x9D));
    static readonly Brush Accent = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF));
    static readonly Brush AccentHover = Frozen(Color.FromRgb(0x2B, 0x95, 0xFF));
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public WelcomeWindow()
    {
        var s = Settings.Current;
        Title = L.T("Selamat datang") + " · Dynamic Island";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Bg;
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        ShowInTaskbar = true;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        SourceInitialized += (_, _) => DarkTitleBar(new WindowInteropHelper(this).Handle);

        var root = new StackPanel { Margin = new Thickness(26, 22, 26, 24) };
        root.Children.Add(new TextBlock
        {
            Text = L.T("Selamat datang di Dynamic Island"), FontSize = 22, FontWeight = FontWeights.SemiBold,
        });
        root.Children.Add(new TextBlock
        {
            Text = L.T("Beberapa hal yang perlu kamu tahu sebelum mulai:"), FontSize = 13, Foreground = Secondary,
            Margin = new Thickness(0, 4, 0, 16), TextWrapping = TextWrapping.Wrap,
        });

        // menu dock: isi tergantung pengaturan yang sedang menyala
        string menuText = !s.DockEnabled
            ? L.T("Dock sedang mati. Bisa dinyalakan di Pengaturan → Dock.")
            : s.DockMenu
                ? (s.WinKeyOpensMenu
                    ? L.T("Klik tombol paling kiri di dock, atau tekan tombol Windows. Start Windows yang asli tetap bisa dibuka dengan Ctrl+Esc.")
                    : L.T("Klik tombol paling kiri di dock untuk membuka menu: cari aplikasi, aplikasi yang disematkan, dan tombol power."))
                : L.T("Tombol paling kiri di dock membuka Start Windows seperti biasa.");

        root.Children.Add(Item("", Color.FromRgb(0x8E, 0x8E, 0x93), L.T("Membuka Pengaturan"),
            L.T("Klik kanan notch di atas layar → Pengaturan. Bisa juga lewat ikon gerigi saat island terbuka atau di menu dock.")));
        root.Children.Add(Item("", Color.FromRgb(0x0A, 0x84, 0xFF), L.T("Menu dock"), menuText));
        root.Children.Add(Item("", Color.FromRgb(0xFF, 0x45, 0x3A), L.T("Menutup aplikasi"),
            L.T("Klik kanan notch → Keluar. Taskbar Windows otomatis kembali saat aplikasi ditutup.")));
        root.Children.Add(Item("", Color.FromRgb(0x34, 0xC7, 0x59), L.T("Taskbar tidak muncul lagi?"),
            L.T("Jalankan kembalikan-taskbar.bat di folder aplikasi. Untuk berhenti memakai Dynamic Island: Pengaturan → Sistem → Hapus dari laptop ini.")));

        root.Children.Add(new TextBlock
        {
            Text = L.T("Layar ini bisa dibuka lagi dari Pengaturan → Sistem."), FontSize = 11.5, Foreground = Secondary,
            Margin = new Thickness(2, 12, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        var ok = new Border
        {
            Background = Accent, CornerRadius = new CornerRadius(8), Padding = new Thickness(22, 8, 22, 8), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0),
            Child = new TextBlock { Text = L.T("Mengerti"), FontSize = 13.5, Foreground = Brushes.White },
        };
        ok.MouseEnter += (_, _) => ok.Background = AccentHover;
        ok.MouseLeave += (_, _) => ok.Background = Accent;
        ok.MouseLeftButtonUp += (_, e) => { e.Handled = true; Close(); };
        root.Children.Add(ok);

        KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Escape) Close(); };
        Content = root;
    }

    static Border Item(string glyph, Color color, string title, string text)
    {
        var icon = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = Frozen(color),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 14, 0),
            Child = new TextBlock
            {
                Text = glyph, FontFamily = IconFont, FontSize = 15, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var words = new StackPanel();
        words.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold });
        words.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(icon);
        Grid.SetColumn(words, 1);
        g.Children.Add(words);
        return new Border
        {
            Background = Card, BorderBrush = CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 8), Child = g,
        };
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static void DarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
            int round = 2;
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        }
        catch { }
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
