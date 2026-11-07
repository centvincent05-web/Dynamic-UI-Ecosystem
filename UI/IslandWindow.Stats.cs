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

/// <summary>Island: stat laptop di control center.</summary>
public partial class IslandWindow
{
    // ---------- stat laptop di control center ----------

    static readonly string[] StatOrder = { "cpu", "gpu", "ram", "disk", "battery" };
    readonly Dictionary<string, (TextBlock value, FrameworkElement chip)> _stats = new();
    Border? _statBatFill;
    string _statsBuilt = "";

    static readonly Dictionary<string, string> StatIcons = new()
    {
        // ikon kecil 14x14 digambar sendiri (garis), supaya rapi di semua versi Windows
        ["cpu"] = "M4,4 H10 V10 H4 Z M6,6 H8 V8 H6 Z M2,5.5 H4 M2,8.5 H4 M10,5.5 H12 M10,8.5 H12 M5.5,2 V4 M8.5,2 V4 M5.5,10 V12 M8.5,10 V12",
        ["gpu"] = "M1.5,4 H12.5 V10 H1.5 Z M7.5,7 m-1.8,0 a1.8,1.8 0 1 0 3.6,0 a1.8,1.8 0 1 0 -3.6,0 M3,10 V12 M5,10 V12 M3.5,5.8 V8.2",
        ["ram"] = "M1.5,4.5 H12.5 V9.5 H1.5 Z M3.5,6 H5.5 V8 H3.5 Z M8.5,6 H10.5 V8 H8.5 Z M3,9.5 V11.5 M5.5,9.5 V11.5 M8.5,9.5 V11.5 M11,9.5 V11.5",
        ["disk"] = "M2.5,3 H11.5 A1.5,1.5 0 0 1 13,4.5 V9.5 A1.5,1.5 0 0 1 11.5,11 H2.5 A1.5,1.5 0 0 1 1,9.5 V4.5 A1.5,1.5 0 0 1 2.5,3 Z M10.2,7 m-0.9,0 a0.9,0.9 0 1 0 1.8,0 a0.9,0.9 0 1 0 -1.8,0 M3.5,8.5 H7",
    };

    List<string> SelectedStats()
    {
        var picked = (Settings.Current.StatItems ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return StatOrder.Where(k => picked.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Susun ulang ikon stat sesuai pilihan di Settings (separuh kiri jam, separuh kanan).</summary>
    void BuildStats()
    {
        var items = SelectedStats();
        string sig = string.Join(",", items);
        if (sig == _statsBuilt) return;
        _statsBuilt = sig;
        StatsLeft.Children.Clear();
        StatsRight.Children.Clear();
        _stats.Clear();
        _statBatFill = null;
        int leftCount = (items.Count + 1) / 2;
        if (items.Count > 0 && items[^1] == "battery" && items.Count % 2 == 1) leftCount = items.Count / 2 + 1;
        for (int i = 0; i < items.Count; i++)
        {
            string key = items[i];
            FrameworkElement icon;
            if (key == "battery")
            {
                var g = new Grid { Width = 19, Height = 10, VerticalAlignment = VerticalAlignment.Center };
                g.Children.Add(new Border { Width = 17, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2.5), BorderThickness = new Thickness(1.1), BorderBrush = Subtle });
                _statBatFill = new Border { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 2, 0, 2), CornerRadius = new CornerRadius(1.2), Background = Brushes.White, Width = 0 };
                g.Children.Add(_statBatFill);
                g.Children.Add(new Border { Width = 1.6, Height = 4, HorizontalAlignment = HorizontalAlignment.Right, CornerRadius = new CornerRadius(0.8), Background = Subtle });
                icon = g;
            }
            else
            {
                icon = new Path
                {
                    Data = Geometry.Parse(StatIcons[key]), Stroke = Subtle, StrokeThickness = 1.1, Width = 14, Height = 14,
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            var value = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            var chip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            chip.Children.Add(icon);
            chip.Children.Add(value);
            bool left = i < leftCount;
            chip.Margin = left ? new Thickness(0, 0, 11, 0) : new Thickness(11, 0, 0, 0);
            (left ? StatsLeft : StatsRight).Children.Add(chip);
            _stats[key] = (value, chip);
        }
    }

    void UpdateStats(SystemStats.BatteryInfo b)
    {
        BuildStats();
        foreach (var (key, (value, chip)) in _stats)
        {
            switch (key)
            {
                case "cpu":
                    value.Text = $"{Math.Clamp(SystemStats.CpuPercent(), 0, 100):0}%";
                    chip.ToolTip = "CPU";
                    break;
                case "gpu":
                    double gpu = SystemStats.GpuPercent();
                    value.Text = gpu < 0 ? "–" : $"{gpu:0}%";
                    chip.ToolTip = "GPU";
                    break;
                case "ram":
                    value.Text = SystemStats.RamPercent() + "%";
                    chip.ToolTip = L.T("Memori (RAM)");
                    break;
                case "disk":
                    var (pct, free) = SystemStats.Disk();
                    value.Text = pct + "%";
                    chip.ToolTip = $"Disk C: · {free:0} GB " + L.T("tersisa");
                    break;
                case "battery":
                    if (!b.HasBattery) { value.Text = "AC"; break; }
                    value.Text = (b.Charging ? "⚡" : "") + b.Percent + "%";
                    var c = b.Charging ? ChargeGreen : b.Percent <= 20 ? AlarmRed : Brushes.White;
                    value.Foreground = c;
                    if (_statBatFill != null) { _statBatFill.Width = 13 * b.Percent / 100.0; _statBatFill.Background = c; }
                    chip.ToolTip = L.T("Baterai");
                    break;
            }
        }
    }
}
