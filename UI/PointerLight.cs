using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicIsland.Core;

namespace DynamicIsland.UI;

/// <summary>
/// Cahaya yang mengikuti pointer (island, dock, panel samping). Warna, bentuk, ukuran, dan kekuatannya
/// diatur di Pengaturan → Tampilan → Cahaya pointer. Bentuk "Liquid glass" ikut berubah bentuk saat
/// pointer bergerak: memanjang ke arah gerakan lalu kembali bulat saat diam.
/// </summary>
public sealed class PointerLight
{
    public enum Shape { Round, Wide, Ring, Liquid }

    /// <summary>Elemen yang ditaruh pemilik di kanvas yang dipotong mengikuti bentuknya.</summary>
    public Ellipse Visual { get; } = new() { IsHitTestVisible = false, Opacity = 0 };

    readonly Func<bool> _on;
    readonly RadialGradientBrush _brush = new();
    readonly ScaleTransform _stretch = new(1, 1);
    readonly RotateTransform _turn = new();
    readonly Func<double, bool> _tick;
    double _size = 220, _w = 220, _h = 220;
    Point _target, _pos;
    Vector _vel;
    bool _hasPos;

    public PointerLight(Func<bool> on)
    {
        _on = on;
        _tick = Step;
        Visual.Fill = _brush;
        var g = new TransformGroup();
        g.Children.Add(_stretch);
        g.Children.Add(_turn);
        Visual.RenderTransform = g;
        Visual.RenderTransformOrigin = new Point(0.5, 0.5);
        ApplyStyle();
        Settings.Changed += ApplyStyle;
    }

    static Shape CurrentShape => (Shape)Math.Clamp(Settings.Current.PointerLightShape, 0, 3);

    /// <summary>Warna, bentuk, ukuran & kekuatan dari Pengaturan.</summary>
    public void ApplyStyle()
    {
        var s = Settings.Current;
        Color c = Colors.White;
        try { c = (Color)ColorConverter.ConvertFromString(s.PointerLightColor); } catch { }
        double k = Math.Clamp(s.SpotlightIntensity, 0.05, 1);
        Color A(double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha * k, 0, 255)), c.R, c.G, c.B);
        var shape = CurrentShape;
        _size = 220 * Math.Clamp(s.PointerLightSize, 0.4, 2.5);
        _w = shape == Shape.Wide ? _size * 1.7 : _size;
        _h = shape == Shape.Wide ? _size * 0.75 : _size;
        Visual.Width = _w;
        Visual.Height = _h;

        _brush.GradientStops.Clear();
        switch (shape)
        {
            case Shape.Ring: // cincin cahaya, tengahnya kosong
                _brush.GradientStops.Add(new GradientStop(A(0), 0));
                _brush.GradientStops.Add(new GradientStop(A(8), 0.5));
                _brush.GradientStops.Add(new GradientStop(A(80), 0.72));
                _brush.GradientStops.Add(new GradientStop(A(14), 0.86));
                _brush.GradientStops.Add(new GradientStop(A(0), 1));
                break;
            case Shape.Liquid: // seperti tetesan kaca: tengah samar, tepinya memantulkan cahaya
                _brush.GradientStops.Add(new GradientStop(A(34), 0));
                _brush.GradientStops.Add(new GradientStop(A(16), 0.55));
                _brush.GradientStops.Add(new GradientStop(A(95), 0.78));
                _brush.GradientStops.Add(new GradientStop(A(22), 0.88));
                _brush.GradientStops.Add(new GradientStop(A(0), 1));
                break;
            default: // bulat / oval: terang di tengah, memudar ke tepi (seperti sebelumnya)
                _brush.GradientStops.Add(new GradientStop(A(90), 0));
                _brush.GradientStops.Add(new GradientStop(A(30), 0.45));
                _brush.GradientStops.Add(new GradientStop(A(0), 1));
                break;
        }
        if (shape != Shape.Liquid) { _stretch.ScaleX = _stretch.ScaleY = 1; _turn.Angle = 0; }
    }

    public void Move(Point p)
    {
        if (!_on()) return;
        _target = p;
        if (CurrentShape != Shape.Liquid || !_hasPos)
        {
            _pos = p;
            _hasPos = true;
            Place();
            if (CurrentShape != Shape.Liquid) return;
        }
        FrameLoop.Add(_tick); // liquid glass: mengejar pointer dengan halus & berubah bentuk
    }

    public void Fade(bool show)
    {
        if (show && !_on()) return;
        if (!show) _hasPos = false;
        Visual.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 220 : 380)));
    }

    void Place()
    {
        Canvas.SetLeft(Visual, _pos.X - _w / 2);
        Canvas.SetTop(Visual, _pos.Y - _h / 2);
    }

    bool Step(double dt)
    {
        if (CurrentShape != Shape.Liquid) return false;
        dt = Math.Clamp(dt, 0.001, 0.05);
        var before = _pos;
        double follow = 1 - Math.Exp(-dt * 16); // mengejar pointer sedikit tertinggal (kesan cair)
        _pos += (_target - _pos) * follow;
        var v = (_pos - before) / dt;
        _vel += (v - _vel) * Math.Min(1, dt * 12);
        double speed = _vel.Length;
        // makin cepat bergerak, makin memanjang searah gerakan (dan sedikit menipis)
        double stretch = 1 + Math.Min(speed / 1800, 0.65);
        _stretch.ScaleX = stretch;
        _stretch.ScaleY = 1 / Math.Sqrt(stretch);
        if (speed > 20) _turn.Angle = Math.Atan2(_vel.Y, _vel.X) * 180 / Math.PI;
        Place();
        bool moving = (_target - _pos).Length > 0.3 || speed > 5;
        if (!moving) { _vel = new Vector(); _stretch.ScaleX = _stretch.ScaleY = 1; }
        return moving;
    }
}
