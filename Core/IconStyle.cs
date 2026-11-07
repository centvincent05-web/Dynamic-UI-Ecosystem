using System;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Core;

/// <summary>
/// Warna ikon dock & menu dock (Pengaturan → Dock → Warna ikon):
/// 0 = warna asli (bawaan), 1 = monokrom (abu-abu), 2 = satu warna pilihan.
/// Hasil olahan disimpan per gambar, jadi tiap ikon hanya diolah sekali per pilihan warna.
/// </summary>
public static class IconStyle
{
    sealed class Entry { public int Mode; public string Tint = ""; public BitmapSource? Result; }
    static readonly ConditionalWeakTable<BitmapSource, Entry> Cache = new();

    public static ImageSource? Apply(ImageSource? src)
    {
        var s = Settings.Current;
        if (s.IconColorMode == 0 || src is not BitmapSource bs) return src;
        string tint = s.IconTintColor ?? "";
        if (Cache.TryGetValue(bs, out var e) && e.Mode == s.IconColorMode && e.Tint == tint && e.Result != null) return e.Result;
        var result = Recolor(bs, s.IconColorMode, ParseTint(tint));
        if (result == null) return src;
        Cache.AddOrUpdate(bs, new Entry { Mode = s.IconColorMode, Tint = tint, Result = result });
        return result;
    }

    /// <summary>Warna latar kotak ikon (dari warna dominan ikon) ikut pilihan warna.</summary>
    public static Color ApplyColor(Color c)
    {
        var s = Settings.Current;
        switch (s.IconColorMode)
        {
            case 1:
                byte l = Luma(c.R, c.G, c.B);
                return Color.FromRgb(l, l, l);
            case 2:
                return ParseTint(s.IconTintColor ?? "");
            default:
                return c;
        }
    }

    static BitmapSource? Recolor(BitmapSource src, int mode, Color tint)
    {
        try
        {
            BitmapSource conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            for (int i = 0; i < px.Length; i += 4)
            {
                if (px[i + 3] == 0) continue;
                byte l = Luma(px[i + 2], px[i + 1], px[i]);
                if (mode == 1)
                {
                    px[i] = px[i + 1] = px[i + 2] = l;
                }
                else
                {
                    // gelap-terang ikon tetap terlihat, warnanya diganti warna pilihan
                    double f = 0.30 + 0.70 * l / 255.0;
                    px[i] = (byte)Math.Min(255, tint.B * f + 40 * (l / 255.0));
                    px[i + 1] = (byte)Math.Min(255, tint.G * f + 40 * (l / 255.0));
                    px[i + 2] = (byte)Math.Min(255, tint.R * f + 40 * (l / 255.0));
                }
            }
            var result = BitmapSource.Create(w, h, src.DpiX > 0 ? src.DpiX : 96, src.DpiY > 0 ? src.DpiY : 96, PixelFormats.Bgra32, null, px, w * 4);
            result.Freeze();
            return result;
        }
        catch { return null; }
    }

    static byte Luma(byte r, byte g, byte b) => (byte)Math.Clamp(0.299 * r + 0.587 * g + 0.114 * b, 0, 255);

    static Color ParseTint(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(hex) ? "#0A84FF" : hex); }
        catch { return Color.FromRgb(0x0A, 0x84, 0xFF); }
    }
}
