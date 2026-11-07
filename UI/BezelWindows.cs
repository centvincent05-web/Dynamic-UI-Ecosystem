using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using DynamicIsland.Core;

namespace DynamicIsland.UI;

/// <summary>
/// Bingkai layar: strip tipis di tepi atas + empat sudut layar membulat.
/// Dibuat dari 5 jendela kecil yang tembus klik (bukan satu jendela selayar penuh),
/// jadi hemat RAM dan tidak pernah menghalangi klik.
/// </summary>
public sealed class BezelWindows
{
    enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

    readonly Window _strip;
    readonly Rectangle _stripRect = new();
    readonly Dictionary<Corner, (Window win, Path path)> _corners = new();
    bool _hiddenForFullscreen;

    public BezelWindows()
    {
        _strip = MakeWindow(_stripRect);
        foreach (Corner c in Enum.GetValues<Corner>())
        {
            var path = new Path();
            _corners[c] = (MakeWindow(path), path);
        }
        Settings.Changed += Apply;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(Apply));
        Apply();
    }

    /// <summary>Dipanggil saat kondisi layar berubah.</summary>
    public void OnScreenState(bool fullscreen, bool maximized)
    {
        _hiddenForFullscreen = fullscreen && Settings.Current.BezelHideInFullscreen;
        Apply();
    }

    void Apply()
    {
        var s = Settings.Current;
        bool visible = s.BezelEnabled && !_hiddenForFullscreen;
        double sw = SystemParameters.PrimaryScreenWidth;
        double sh = SystemParameters.PrimaryScreenHeight;
        double t = s.BezelTopThickness;
        double r = s.ScreenCornerRadius;
        var brush = IslandWindow.ParseBrush("#000000", s.BezelOpacity);

        // strip atas
        _stripRect.Fill = brush;
        _stripRect.Width = sw;
        _stripRect.Height = Math.Max(t, 1);
        Place(_strip, 0, 0, sw, Math.Max(t, 1), visible && t > 0);

        // sudut membulat
        foreach (var (corner, (win, path)) in _corners)
        {
            path.Fill = brush;
            path.Data = CornerGeometry(r, corner);
            double x = corner is Corner.TopLeft or Corner.BottomLeft ? 0 : sw - r;
            double y = corner is Corner.TopLeft or Corner.TopRight ? t : sh - r;
            Place(win, x, y, r, r, visible && r > 0);
        }
    }

    /// <summary>Area di luar seperempat lingkaran (bagian yang menutupi pojok layar).</summary>
    static Geometry CornerGeometry(double r, Corner corner)
    {
        r = Math.Max(r, 1);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            // versi kiri atas, lalu dicerminkan untuk sudut lain
            c.BeginFigure(new Point(0, 0), true, true);
            c.LineTo(new Point(r, 0), false, false);
            c.ArcTo(new Point(0, r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, false, false);
        }
        double sx = corner is Corner.TopRight or Corner.BottomRight ? -1 : 1;
        double sy = corner is Corner.BottomLeft or Corner.BottomRight ? -1 : 1;
        g.Transform = new ScaleTransform(sx, sy, r / 2, r / 2);
        g.Freeze();
        return g;
    }

    static Window MakeWindow(UIElement content)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            IsHitTestVisible = false,
            Focusable = false,
            Content = content,
            Title = "Bezel",
        };
        w.SourceInitialized += (_, _) => Native.MakeClickThrough(new WindowInteropHelper(w).Handle);
        return w;
    }

    static void Place(Window w, double x, double y, double width, double height, bool visible)
    {
        w.Left = x;
        w.Top = y;
        w.Width = width;
        w.Height = height;
        if (visible && !w.IsVisible) w.Show();
        else if (!visible && w.IsVisible) w.Hide();
    }

    public void Close()
    {
        _strip.Close();
        foreach (var (win, _) in _corners.Values) win.Close();
    }
}
