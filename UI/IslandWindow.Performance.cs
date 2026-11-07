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

/// <summary>Island: halaman performa (grafik seperti Task Manager).</summary>
public partial class IslandWindow
{
    // ---------- halaman performa ----------

    sealed class PerfCard
    {
        public readonly double[] History = new double[60];
        public int Count;
        public TextBlock Value = null!, Sub = null!;
        public System.Windows.Shapes.Path Line = null!, Area = null!;
        public void Push(double v)
        {
            Array.Copy(History, 1, History, 0, History.Length - 1);
            History[^1] = Math.Clamp(v, 0, 100);
            Count = Math.Min(Count + 1, History.Length);
        }
    }

    readonly Dictionary<string, PerfCard> _perf = new();
    double _netPeak = 1024 * 1024; // skala grafik jaringan (otomatis menyesuaikan)

    static readonly (string key, string name, Color color)[] PerfItems =
    {
        ("cpu", "CPU", Color.FromRgb(0x3A, 0xA0, 0xFF)),
        ("ram", "Memori", Color.FromRgb(0xB3, 0x7C, 0xFF)),
        ("gpu", "GPU", Color.FromRgb(0xFF, 0x6B, 0x9A)),
        ("disk", "Disk", Color.FromRgb(0x4C, 0xD9, 0x64)),
        ("net", "Jaringan", Color.FromRgb(0xFF, 0x9F, 0x0A)),
        ("battery", "Baterai", Color.FromRgb(0x34, 0xC7, 0x59)),
    };

    void BuildPerf()
    {
        if (_perf.Count > 0) return;
        PerfGrid.Children.Clear();
        foreach (var (key, name, color) in PerfItems)
        {
            var c = new PerfCard();
            var brush = new SolidColorBrush(color); brush.Freeze();
            var fill = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B)); fill.Freeze();
            c.Value = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
            c.Sub = new TextBlock { Foreground = Subtle, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis };
            c.Area = new System.Windows.Shapes.Path { Fill = fill, Stretch = Stretch.Fill };
            c.Line = new System.Windows.Shapes.Path { Stroke = brush, StrokeThickness = 1.4, Stretch = Stretch.Fill, StrokeLineJoin = PenLineJoin.Round };
            var head = new Grid();
            head.Children.Add(new TextBlock { Text = L.T(name), Foreground = brush, FontSize = 11.5, FontWeight = FontWeights.SemiBold });
            head.Children.Add(c.Value);
            var graph = new Grid { Height = 30, Margin = new Thickness(0, 4, 0, 2), ClipToBounds = true };
            graph.Children.Add(c.Area);
            graph.Children.Add(c.Line);
            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(graph);
            stack.Children.Add(c.Sub);
            PerfGrid.Children.Add(new Border
            {
                Background = NotifRowBg, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 7, 10, 6),
                Margin = new Thickness(3), Child = stack,
            });
            _perf[key] = c;
        }
    }

    /// <summary>Dipanggil tiap detik saat halaman performa terbuka.</summary>
    void UpdatePerf()
    {
        BuildPerf();
        double cpu = Math.Clamp(SystemStats.CpuPercent(), 0, 100);
        SetPerf("cpu", cpu, $"{cpu:0}%", $"{Environment.ProcessorCount} {L.T("thread")}");

        var (used, total) = SystemStats.RamGb();
        double ramPct = total > 0 ? used / total * 100 : 0;
        SetPerf("ram", ramPct, $"{ramPct:0}%", $"{used:0.0} / {total:0.0} GB");

        double gpu = SystemStats.GpuPercent();
        SetPerf("gpu", Math.Max(0, gpu), gpu < 0 ? "–" : $"{gpu:0}%", L.T("Mesin 3D"));

        double disk = SystemStats.DiskActivePercent();
        var (diskUsed, free) = SystemStats.Disk();
        SetPerf("disk", Math.Max(0, disk), disk < 0 ? "–" : $"{disk:0}%", $"C: {diskUsed}% · {free:0} GB {L.T("tersisa")}");

        var (down, up) = SystemStats.NetworkSpeed();
        double sum = down + up;
        _netPeak = Math.Max(256 * 1024, Math.Max(sum, _netPeak * 0.98)); // skala turun pelan-pelan
        SetPerf("net", sum / _netPeak * 100, SystemStats.FormatSpeed(sum), $"↓ {SystemStats.FormatSpeed(down)}  ↑ {SystemStats.FormatSpeed(up)}");

        var b = SystemStats.Battery();
        SetPerf("battery", b.HasBattery ? b.Percent : 100, b.HasBattery ? $"{b.Percent}%" : "AC",
            b.HasBattery ? (b.Charging ? L.T("Mengisi daya") : L.T("Tidak mengisi daya")) : L.T("Tanpa baterai"));
    }

    void SetPerf(string key, double value, string text, string sub)
    {
        if (!_perf.TryGetValue(key, out var c)) return;
        c.Push(value);
        c.Value.Text = text;
        c.Sub.Text = sub;
        // grafik 60 detik terakhir; titik tak terlihat di (0,0) dan (59,100) menjaga skala tetap 0-100%
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        int n = c.History.Length;
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(0, 0), false, false);
            g.LineTo(new Point(0, 0), false, false);
            g.BeginFigure(new Point(n - 1, 100), false, false);
            g.LineTo(new Point(n - 1, 100), false, false);
            g.BeginFigure(new Point(0, 100 - c.History[0]), false, false);
            for (int i = 1; i < n; i++) g.LineTo(new Point(i, 100 - c.History[i]), true, true);
        }
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(0, 0), false, false);
            g.LineTo(new Point(0, 0), false, false);
            g.BeginFigure(new Point(0, 100), true, true);
            for (int i = 0; i < n; i++) g.LineTo(new Point(i, 100 - c.History[i]), false, false);
            g.LineTo(new Point(n - 1, 100), false, false);
        }
        line.Freeze(); area.Freeze();
        c.Line.Data = line;
        c.Area.Data = area;
    }
}
