using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.UI;

/// <summary>
/// Pencarian cepat ala Spotlight: muncul di bawah notch, cari aplikasi, file yang baru dibuka,
/// hitung cepat (kalkulator), atau langsung cari di Google. Enter = buka, Esc = tutup.
/// </summary>
public sealed class SearchWindow : Window
{
    enum Kind { App, File, Calc, Web }
    sealed record Entry(Kind Kind, string Title, string Subtitle, string Target, string Glyph = "");

    static SearchWindow? _instance;

    // indeks aplikasi & file terbaru (dibaca di thread latar, dipakai ulang beberapa menit)
    static List<Entry> _apps = new(), _files = new();
    static DateTime _indexedAt = DateTime.MinValue;
    static int _indexing;

    readonly TextBox _box = new();
    readonly TextBlock _placeholder;
    readonly StackPanel _results = new() { Margin = new Thickness(0, 4, 0, 0) };
    readonly Border _frame;
    readonly TranslateTransform _drop = new();
    List<Entry> _shown = new();
    int _selected;
    bool _closing;

    static readonly Brush Secondary = Frozen(Color.FromRgb(0x98, 0x98, 0x9D));
    static readonly Brush SelectedBg = Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Buka (atau tutup kalau sedang terbuka) — dipanggil dari shortcut.</summary>
    public static void Toggle()
    {
        if (_instance is { IsVisible: true }) { _instance.HideAnimated(); return; }
        _instance ??= new SearchWindow();
        _instance.Open();
    }

    SearchWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        FontFamily = new FontFamily(string.IsNullOrWhiteSpace(Settings.Current.FontName) ? "Segoe UI Variable Text" : Settings.Current.FontName + ", Segoe UI");
        Foreground = Brushes.White;

        _box.Background = Brushes.Transparent;
        _box.BorderThickness = new Thickness(0);
        _box.Foreground = Brushes.White;
        _box.CaretBrush = Brushes.White;
        _box.FontSize = 19;
        _box.VerticalContentAlignment = VerticalAlignment.Center;
        _box.TextChanged += (sender, args) => { _placeholder!.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Search(); };
        _box.PreviewKeyDown += OnKey;

        _placeholder = new TextBlock
        {
            Text = L.T("Cari aplikasi, file, atau hitung…"), FontSize = 19, Foreground = Secondary,
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0),
        };
        var inputGrid = new Grid();
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition());
        inputGrid.Children.Add(new TextBlock { Text = "\uE721", FontFamily = IconFont, FontSize = 18, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 12, 0) });
        var boxHost = new Grid();
        boxHost.Children.Add(_box);
        boxHost.Children.Add(_placeholder);
        Grid.SetColumn(boxHost, 1);
        inputGrid.Children.Add(boxHost);

        var stack = new StackPanel();
        stack.Children.Add(inputGrid);
        stack.Children.Add(_results);

        var s = Settings.Current;
        Color bg = Color.FromRgb(0x1C, 0x1C, 0x1E);
        try { bg = (Color)ColorConverter.ConvertFromString(s.IslandColor); } catch { }
        bg.A = (byte)Math.Round(255 * Math.Clamp(Math.Max(s.IslandOpacity, 0.88), 0, 1)); // sedikit lebih pekat supaya teks mudah dibaca
        _frame = new Border
        {
            Background = Frozen(bg), CornerRadius = new CornerRadius(18), Padding = new Thickness(16, 12, 12, 10),
            BorderBrush = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Margin = new Thickness(18, 6, 18, 26), Child = stack, RenderTransform = _drop,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.5 },
        };
        Content = _frame;

        Deactivated += (sender, args) => HideAnimated();
    }

    void Open()
    {
        var s = Settings.Current;
        double screenW = SystemParameters.PrimaryScreenWidth;
        Left = (screenW - Width) / 2;
        Top = s.CollapsedHeight + 4;
        _closing = false;
        _box.Text = "";
        Search();
        EnsureIndex();
        Opacity = 0;
        Show();
        Activate();
        _box.Focus();
        Keyboard.Focus(_box);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        _drop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
    }

    void HideAnimated()
    {
        if (_closing || !IsVisible) return;
        _closing = true;
        var a = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(110));
        a.Completed += (sender, args) => { Hide(); _closing = false; };
        BeginAnimation(OpacityProperty, a);
    }

    // =====================================================================
    // Keyboard
    // =====================================================================

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                HideAnimated();
                break;
            case Key.Down:
                e.Handled = true;
                Select(Math.Min(_shown.Count - 1, _selected + 1));
                break;
            case Key.Up:
                e.Handled = true;
                Select(Math.Max(0, _selected - 1));
                break;
            case Key.Enter:
                e.Handled = true;
                if (_selected >= 0 && _selected < _shown.Count) Run(_shown[_selected]);
                break;
        }
    }

    // =====================================================================
    // Hasil
    // =====================================================================

    void Search()
    {
        string q = _box.Text.Trim();
        var list = new List<Entry>();
        if (q.Length > 0)
        {
            if (TryCalc(q, out string result))
                list.Add(new Entry(Kind.Calc, "= " + result, L.T("Enter untuk menyalin hasil"), result, "\uE8EF"));
            list.AddRange(Rank(_apps, q, 0).Concat(Rank(_files, q, -15))
                .OrderByDescending(x => x.score).Take(7).Select(x => x.entry));
            list.Add(new Entry(Kind.Web, string.Format(L.T("Cari \"{0}\" di Google"), q), "google.com", q, "\uE774"));
        }
        _shown = list;
        _selected = 0;
        _results.Children.Clear();
        _results.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < list.Count; i++) _results.Children.Add(MakeRow(list[i], i));
        Select(0);
    }

    static IEnumerable<(Entry entry, int score)> Rank(List<Entry> source, string q, int bonus)
    {
        foreach (var e in source)
        {
            int sc = Score(e.Title, q);
            if (sc > 0) yield return (e, sc + bonus);
        }
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

    FrameworkElement MakeRow(Entry e, int index)
    {
        FrameworkElement icon;
        if (e.Kind is Kind.App or Kind.File)
        {
            ImageSource? img = null;
            try { img = IconLoader.Get(e.Kind == Kind.App ? "shell:AppsFolder\\" + e.Target : e.Target, 48).Image; } catch { }
            icon = new Image { Source = img, Width = 26, Height = 26, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        }
        else
        {
            icon = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Background = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock { Text = e.Glyph, FontFamily = IconFont, FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
        }
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var title = new TextBlock { Text = e.Title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var sub = new TextBlock { Text = e.Subtitle, FontSize = 11.5, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(icon);
        Grid.SetColumn(title, 1);
        g.Children.Add(title);
        Grid.SetColumn(sub, 2);
        g.Children.Add(sub);
        var row = new Border { Child = g, Padding = new Thickness(8, 6, 10, 6), CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.MouseEnter += (sender, args) => Select(index);
        row.MouseLeftButtonUp += (sender, args) => Run(e);
        return row;
    }

    void Select(int index)
    {
        _selected = index;
        for (int i = 0; i < _results.Children.Count; i++)
            ((Border)_results.Children[i]).Background = i == index ? SelectedBg : Brushes.Transparent;
    }

    void Run(Entry e)
    {
        try
        {
            switch (e.Kind)
            {
                case Kind.App:
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + e.Target) { UseShellExecute = true });
                    break;
                case Kind.File:
                    Process.Start(new ProcessStartInfo(e.Target) { UseShellExecute = true });
                    break;
                case Kind.Calc:
                    Clipboard.SetText(e.Target);
                    break;
                case Kind.Web:
                    Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString(e.Target)) { UseShellExecute = true });
                    break;
            }
        }
        catch { }
        HideAnimated();
    }

    // =====================================================================
    // Kalkulator
    // =====================================================================

    static readonly Regex MathOnly = new(@"^[0-9\s\.\,\+\-\*\/\(\)x×÷%:]+$", RegexOptions.Compiled);

    static bool TryCalc(string q, out string result)
    {
        result = "";
        if (!MathOnly.IsMatch(q) || !q.Any(char.IsDigit) || !q.Any(c => "+-*/x×÷%:".Contains(c))) return false;
        string expr = q.Replace('×', '*').Replace('x', '*').Replace('÷', '/').Replace(':', '/').Replace(',', '.');
        // 50% → (50/100)
        expr = Regex.Replace(expr, @"(\d+(\.\d+)?)\s*%", "($1/100)");
        try
        {
            var value = new System.Data.DataTable().Compute(expr, null);
            double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d)) return false;
            result = Math.Round(d, 10).ToString("0.##########", CultureInfo.InvariantCulture);
            return true;
        }
        catch { return false; }
    }

    // =====================================================================
    // Indeks aplikasi (Start menu, termasuk aplikasi Store) & file terbaru
    // =====================================================================

    static void EnsureIndex()
    {
        if (DateTime.Now - _indexedAt < TimeSpan.FromMinutes(5)) return;
        if (Interlocked.Exchange(ref _indexing, 1) == 1) return;
        var t = new Thread(() =>
        {
            try
            {
                var apps = ReadApps();
                var files = ReadRecentFiles();
                if (apps.Count > 0) _apps = apps;
                _files = files;
                _indexedAt = DateTime.Now;
            }
            catch { }
            finally { Interlocked.Exchange(ref _indexing, 0); }
            _instance?.Dispatcher.BeginInvoke(new Action(() => { if (_instance.IsVisible) _instance.Search(); }));
        }) { IsBackground = true, Name = "SearchIndex" };
        t.SetApartmentState(ApartmentState.STA); // objek shell Windows butuh STA
        t.Start();
    }

    /// <summary>Semua aplikasi di "shell:AppsFolder" (daftar All apps di Start).</summary>
    static List<Entry> ReadApps()
    {
        var list = new List<Entry>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return list;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string path = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new Entry(Kind.App, name, L.T("Aplikasi"), path));
            }
        }
        catch { }
        return list.GroupBy(e => e.Title, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>File yang baru dibuka (folder Recent Windows), yang terbaru dulu.</summary>
    static List<Entry> ReadRecentFiles()
    {
        var list = new List<Entry>();
        try
        {
            string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            var dir = new DirectoryInfo(recent);
            foreach (var f in dir.EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc).Take(300))
            {
                string name = Path.GetFileNameWithoutExtension(f.Name);
                if (name.Length == 0) continue;
                list.Add(new Entry(Kind.File, name, L.T("File terbaru"), f.FullName));
            }
        }
        catch { }
        return list;
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
