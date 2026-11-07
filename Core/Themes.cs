using System;
using System.Collections.Generic;
using System.Linq;

namespace DynamicIsland.Core;

/// <summary>
/// Tema siap pakai: sekali klik mengganti warna, bentuk, dan gaya animasi.
/// Setelah dipilih, semua nilainya tetap bisa diubah sendiri di Pengaturan.
/// </summary>
public sealed record Theme(string Name, string Description, IReadOnlyDictionary<string, object> Values)
{
    public string IslandColor => (string)Values[nameof(Settings.IslandColor)];
    public double IslandOpacity => (double)Values[nameof(Settings.IslandOpacity)];
    public string ToggleOnColor => (string)Values[nameof(Settings.ToggleOnColor)];
    public string AccentColor => (string)Values[nameof(Settings.AccentColor)];
    public string ChargeColor => (string)Values[nameof(Settings.ChargeColor)];
    public bool EdgeHighlight => (bool)Values[nameof(Settings.EdgeHighlight)];
    public double BottomRadius => (double)Values[nameof(Settings.CollapsedBottomRadius)];

    public void Apply(Settings s)
    {
        foreach (var (name, value) in Values)
            typeof(Settings).GetProperty(name)?.SetValue(s, value);
    }

    /// <summary>Semua nilai tema ini masih sama dengan pengaturan sekarang (belum diubah sendiri).</summary>
    public bool Matches(Settings s) => Values.All(kv =>
    {
        var cur = typeof(Settings).GetProperty(kv.Key)?.GetValue(s);
        return cur switch
        {
            string a => string.Equals(a, (string)kv.Value, StringComparison.OrdinalIgnoreCase),
            double d => Math.Abs(d - Convert.ToDouble(kv.Value)) < 0.001,
            _ => Equals(cur, kv.Value),
        };
    });
}

public static class Themes
{
    static Theme Make(string name, string desc,
        string island, double opacity, bool edge, string accent, string toggleOn,
        string charge, string download, string timer,
        double bottom, double expandedBottom, double shoulder,
        double stiffness, double damping, bool cover, bool ambient) =>
        new(name, desc, new Dictionary<string, object>
        {
            [nameof(Settings.IslandColor)] = island,
            [nameof(Settings.IslandOpacity)] = opacity,
            [nameof(Settings.EdgeHighlight)] = edge,
            [nameof(Settings.AccentColor)] = accent,
            [nameof(Settings.ToggleOnColor)] = toggleOn,
            [nameof(Settings.ChargeColor)] = charge,
            [nameof(Settings.DownloadColor)] = download,
            [nameof(Settings.TimerColor)] = timer,
            [nameof(Settings.CollapsedBottomRadius)] = bottom,
            [nameof(Settings.ExpandedBottomRadius)] = expandedBottom,
            [nameof(Settings.ShoulderRadius)] = shoulder,
            [nameof(Settings.AnimStiffness)] = stiffness,
            [nameof(Settings.AnimDamping)] = damping,
            [nameof(Settings.CoverColors)] = cover,
            [nameof(Settings.AmbientLight)] = ambient,
        });

    /// <summary>Tampilan bawaan aplikasi (nilai default di Settings).</summary>
    static Theme Default()
    {
        var d = new Settings();
        return Make("Bawaan", "Tampilan awal Dynamic Island",
            d.IslandColor, d.IslandOpacity, d.EdgeHighlight, d.AccentColor, d.ToggleOnColor,
            d.ChargeColor, d.DownloadColor, d.TimerColor,
            d.CollapsedBottomRadius, d.ExpandedBottomRadius, d.ShoulderRadius,
            d.AnimStiffness, d.AnimDamping, d.CoverColors, d.AmbientLight);
    }

    public static readonly Theme[] All =
    {
        Default(),
        // iPhone: hitam pekat, tombol biru, animasi sedikit memantul
        Make("Apple", "Hitam pekat seperti iPhone, tombol biru, gerakan memantul",
            "#000000", 1.0, false, "#FFFFFF", "#0A84FF", "#34C759", "#0A84FF", "#FF9F0A",
            17, 30, 10, 260, 22, true, true),
        // Glass: gelap translucent dengan garis tepi tipis, tombol putih
        Make("Glass", "Gelap translucent dengan garis tepi tipis, tombol putih",
            "#1C1C1E", 0.8, true, "#5DCAA5", "#FFFFFF", "#34C759", "#0A84FF", "#FF9F0A",
            17, 28, 10, 210, 26, true, false),
        // Minimal: tanpa warna, sudut lebih kecil, gerakan tegas tanpa memantul
        Make("Minimal", "Hitam-putih tanpa warna, sudut kecil, gerakan tegas",
            "#000000", 0.92, false, "#8E8E93", "#E5E5EA", "#FFFFFF", "#FFFFFF", "#FFFFFF",
            12, 22, 6, 320, 36, false, false),
        // Neon: ungu-biru menyala, sudut besar, gerakan lebih lentur
        Make("Neon", "Ungu & biru menyala, sudut membulat, gerakan lentur",
            "#0B0B1A", 0.85, true, "#64D2FF", "#BF5AF2", "#30D158", "#64D2FF", "#FF375F",
            20, 32, 12, 180, 18, true, true),
    };
}
