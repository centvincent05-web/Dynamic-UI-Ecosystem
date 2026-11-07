using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Services;

/// <summary>
/// Ikon aplikasi resolusi tinggi (sampai 256 px) lewat Windows Shell, lengkap dengan transparansi,
/// plus warna dominan ikon untuk mengisi latar kotak dock.
/// </summary>
public static class IconLoader
{
    public readonly record struct IconInfo(ImageSource? Image, Color Dominant);

    static readonly Dictionary<string, IconInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static IconInfo Get(string path, int size = 96)
    {
        string key = path + "|" + size;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        IconInfo result = new(null, Color.FromRgb(0x2C, 0x2C, 0x2E));
        try
        {
            var bmp = LoadShellImage(path, size);
            if (bmp != null) result = new IconInfo(bmp, DominantColor(bmp));
        }
        catch { }
        if (result.Image == null)
        {
            var fallback = WindowTracker.IconFor(path); // ikon 32 px biasa
            if (fallback is BitmapSource bs) result = new IconInfo(fallback, DominantColor(bs));
        }
        if (Cache.Count > 300) Cache.Clear(); // jangan biarkan cache ikon terus membesar (pencarian, notifikasi, dock)
        Cache[key] = result;
        return result;
    }

    // ---------- Thumbnail isi file (Shelf) ----------

    static readonly Dictionary<string, BitmapSource?> ThumbCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Thumbnail isi file dari Windows (sama seperti di File Explorer): halaman pertama PDF/Word/PowerPoint,
    /// gambar, frame video. Null kalau Windows tidak punya thumbnail untuk file itu (pakai ikon biasa).
    /// Aman dipanggil dari thread latar; hasilnya di-cache per ukuran & waktu ubah file.
    /// </summary>
    public static BitmapSource? GetThumbnail(string path, int size)
    {
        string stamp;
        try { stamp = System.IO.File.GetLastWriteTimeUtc(path).Ticks.ToString(); } catch { stamp = "0"; }
        string key = path + "|" + size + "|" + stamp;
        lock (ThumbCache)
            if (ThumbCache.TryGetValue(key, out var hit)) return hit;
        BitmapSource? bmp = null;
        try { bmp = LoadShellImage(path, size, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK); } catch { }
        lock (ThumbCache)
        {
            if (ThumbCache.Count > 80) ThumbCache.Clear(); // thumbnail besar: cache dibatasi
            ThumbCache[key] = bmp;
        }
        return bmp;
    }

    /// <summary>Ikon tanpa cache, aman dipanggil dari thread latar (STA). Dipakai daftar aplikasi menu dock.</summary>
    public static BitmapSource? LoadIcon(string path, int size)
    {
        try { return LoadShellImage(path, size); } catch { return null; }
    }

    // ---------- Shell image factory ----------

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4, SIIGBF_THUMBNAILONLY = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    static BitmapSource? LoadShellImage(string path, int size, int flags = SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK)
    {
        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
        bool thumb = (flags & SIIGBF_THUMBNAILONLY) != 0;
        if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out IntPtr hbm) != 0 || hbm == IntPtr.Zero)
            return null;
        try
        {
            if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, // negatif = baris atas dulu
                    biPlanes = 1, biBitCount = 32, biCompression = 0,
                },
            };
            var pixels = new byte[w * h * 4];
            IntPtr hdc = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(hdc, hbm, 0, (uint)h, pixels, ref bmi, 0) == 0) return null;
            }
            finally { ReleaseDC(IntPtr.Zero, hdc); }

            if (thumb)
            {
                // thumbnail foto/dokumen sering tanpa saluran alpha (semua 0) → akan tampil transparan; jadikan pekat
                bool anyAlpha = false;
                for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
                if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            }

            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
            src.Freeze();
            return src;
        }
        finally { DeleteObject(hbm); }
    }

    /// <summary>Rata-rata warna piksel yang tidak transparan.</summary>
    public static Color DominantColor(BitmapSource src)
    {
        try
        {
            var conv = src.Format == PixelFormats.Pbgra32 || src.Format == PixelFormats.Bgra32
                ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            long r = 0, g = 0, b = 0, n = 0;
            for (int i = 0; i < px.Length; i += 16) // tiap 4 piksel cukup
            {
                byte a = px[i + 3];
                if (a < 200) continue;
                b += px[i]; g += px[i + 1]; r += px[i + 2]; n++;
            }
            if (n == 0) return Color.FromRgb(0x2C, 0x2C, 0x2E);
            return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
        }
        catch { return Color.FromRgb(0x2C, 0x2C, 0x2E); }
    }
}
