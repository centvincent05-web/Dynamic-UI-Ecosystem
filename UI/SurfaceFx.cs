using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicIsland.Core;

namespace DynamicIsland.UI;

/// <summary>
/// Efek yang sama dengan island untuk dock dan panel volume/kecerahan:
/// cahaya yang mengikuti pointer, dan semburat ambient light dari warna cover lagu.
/// Lapisannya dipotong mengikuti bentuk jendela, dan tidak menghalangi klik.
/// </summary>
public sealed class SurfaceFx
{
    /// <summary>Lapisan efek: taruh di atas bentuk latar, di bawah isi (ikon / slider).</summary>
    public Canvas Layer { get; } = new() { IsHitTestVisible = false };

    readonly PointerLight _pointer;
    readonly Rectangle _tint = new() { Opacity = 0, IsHitTestVisible = false };
    readonly GradientStop _tint0 = new(Colors.Transparent, 0), _tint1 = new(Colors.Transparent, 1);
    readonly Func<bool> _spotOn, _ambientOn;
    readonly FrameworkElement _host;

    public SurfaceFx(FrameworkElement host, Func<bool> spotlightOn, Func<bool> ambientOn)
    {
        _host = host;
        _spotOn = spotlightOn;
        _ambientOn = ambientOn;
        _pointer = new PointerLight(spotlightOn);
        var tintBrush = new RadialGradientBrush();
        tintBrush.GradientStops.Add(_tint0);
        tintBrush.GradientStops.Add(_tint1);
        _tint.Fill = tintBrush;
        Layer.Children.Add(_tint);
        Layer.Children.Add(_pointer.Visual);

        host.MouseEnter += (_, e) => { Move(e.GetPosition(Layer)); Fade(true); };
        host.MouseLeave += (_, _) => Fade(false);
        host.MouseMove += (_, e) => Move(e.GetPosition(Layer));

        IslandWindow.AmbientChanged += ApplyAmbient;
        Settings.Changed += ApplyAmbient;
        ApplyAmbient();
    }

    /// <summary>Dua-duanya mati: lapisan efek dilepas dari tampilan sepenuhnya (tidak digambar, tidak dipotong).</summary>
    bool Active => _spotOn() || _ambientOn();

    void UpdateActive()
    {
        var v = Active ? Visibility.Visible : Visibility.Collapsed;
        if (Layer.Visibility != v) Layer.Visibility = v;
    }

    /// <summary>Bentuk jendela berubah: lapisan efek dipotong mengikuti bentuknya.</summary>
    public void SetShape(Geometry geometry, double width, double height)
    {
        if (!Active) return;
        Layer.Width = width;
        Layer.Height = height;
        Layer.Clip = geometry;
        _tint.Width = width;
        _tint.Height = height;
    }

    void Move(Point p)
    {
        if (Layer.Visibility != Visibility.Visible) return;
        _pointer.Move(p);
    }

    void Fade(bool show)
    {
        if (show && Layer.Visibility != Visibility.Visible) return;
        _pointer.Fade(show);
    }

    /// <summary>Warna ambient dari island (cover lagu) atau mati.</summary>
    public void ApplyAmbient()
    {
        UpdateActive();
        var color = IslandWindow.AmbientColor;
        bool on = _ambientOn() && color is Color;
        if (!on)
        {
            _tint.BeginAnimation(UIElement.OpacityProperty, null);
            _tint.Opacity = 0;
            return;
        }
        var c = color!.Value;
        _tint0.Color = Color.FromArgb(170, c.R, c.G, c.B);
        _tint1.Color = Color.FromArgb(0, c.R, c.G, c.B);
        _tint.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(IslandWindow.AmbientStrength, TimeSpan.FromMilliseconds(500)));
    }
}
