using System;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland.Core;

/// <summary>
/// Membuat bentuk "notch" yang menempel ke tepi layar:
/// tepi atas rata, dua sudut sambungan melengkung CEKUNG (tanpa ujung lancip),
/// lalu sudut bawah membulat.
///
///  ____                         ____   &lt;- tepi layar (y = 0)
///      \_                     _/
///        |                   |          &lt;- shoulder (cekung)
///        |                   |
///         \_________________/           &lt;- bottom radius (cembung)
/// </summary>
public static class NotchGeometry
{
    /// <summary>
    /// Separuh garis tepi notch, mulai dari tengah bawah → sudut bawah → sisi → shoulder (tepi layar).
    /// Dipakai untuk animasi garis yang "menelusuri" notch. Mengembalikan panjang garisnya juga.
    /// </summary>
    public static Geometry BuildHalfOutline(double centerX, double width, double height,
                                            double shoulder, double bottomRadius, bool left, out double length)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        double s = Math.Clamp(shoulder, 0, height);
        double r = Math.Clamp(bottomRadius, 0, Math.Min(width / 2, Math.Max(0, height - s)));
        double dir = left ? -1 : 1;
        double xe = centerX + dir * width / 2; // tepi sisi
        var sweepBottom = left ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
        var sweepShoulder = left ? SweepDirection.Counterclockwise : SweepDirection.Clockwise;

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(centerX, height), false, false);
            c.LineTo(new Point(xe - dir * r, height), true, false);
            if (r > 0) c.ArcTo(new Point(xe, height - r), new Size(r, r), 0, false, sweepBottom, true, false);
            c.LineTo(new Point(xe, s), true, false);
            if (s > 0) c.ArcTo(new Point(xe + dir * s, 0), new Size(s, s), 0, false, sweepShoulder, true, false);
        }
        g.Freeze();
        length = (width / 2 - r) + Math.PI * r / 2 + Math.Max(0, height - r - s) + Math.PI * s / 2;
        return g;
    }

    /// <param name="centerX">Titik tengah notch di sumbu X.</param>
    /// <param name="width">Lebar badan notch (tanpa shoulder).</param>
    /// <param name="height">Tinggi notch dari tepi layar.</param>
    /// <param name="shoulder">Radius lengkungan cekung di sambungan.</param>
    /// <param name="bottomRadius">Radius sudut bawah.</param>
    /// <param name="transform">Opsional: untuk memutar bentuk (panel kiri/kanan).</param>
    public static Geometry Build(double centerX, double width, double height,
                                 double shoulder, double bottomRadius, Transform? transform = null, bool closed = true)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        double s = Math.Clamp(shoulder, 0, height);
        double r = Math.Clamp(bottomRadius, 0, Math.Min(width / 2, Math.Max(0, height - s)));

        double x0 = centerX - width / 2;
        double x1 = centerX + width / 2;

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x0 - s, 0), isFilled: closed, isClosed: closed); // closed=false: hanya garis tepi (tanpa tepi atas)
            if (s > 0)
                c.ArcTo(new Point(x0, s), new Size(s, s), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(x0, height - r), true, false);
            if (r > 0)
                c.ArcTo(new Point(x0 + r, height), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(x1 - r, height), true, false);
            if (r > 0)
                c.ArcTo(new Point(x1, height - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(x1, s), true, false);
            if (s > 0)
                c.ArcTo(new Point(x1 + s, 0), new Size(s, s), 0, false, SweepDirection.Clockwise, true, false);
        }

        if (transform != null) g.Transform = transform;
        g.Freeze();
        return g;
    }
}
