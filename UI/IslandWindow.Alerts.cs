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

/// <summary>Island: notifikasi charger & download, garis di tepi notch.</summary>
public partial class IslandWindow
{
    public void AttachDownloads(DownloadMonitor d)
    {
        _downloads = d;
        d.Changed += () => { UpdateMiniInfo(); FrameLoop.Add(_animTick); };
        d.Completed += name => ShowDownloadAlert(name);
    }

    /// <summary>Animasi garis biru download (halus mengejar persen; tanpa persen: segmen berjalan).</summary>
    bool StepDownload(double dt)
    {
        var st = Settings.Current;
        bool download = _downloads?.IsActive == true && st.DownloadProgress && !_suspended;
        // timer berjalan: garis oranye = sisa waktu, makin pendek seiring waktu (download lebih diutamakan)
        bool timer = !download && st.TimerRim && !_suspended && _timer.IsRunning && _timer.Duration.TotalSeconds > 0;
        bool active = download || timer;
        var brush = download ? DownloadBlue : TimerOrange;
        if (active && _progressBrush != brush) { _progressBrush = brush; _dlShown = timer ? 1 : 0; }
        double toOp = active ? 1 : 0;
        _dlOpacity += (toOp - _dlOpacity) * Math.Min(1, dt * 6);
        if (Math.Abs(toOp - _dlOpacity) < 0.002) _dlOpacity = toOp;
        double? prog = download ? _downloads!.Progress
                     : timer ? Math.Clamp(_timer.Remaining.TotalSeconds / _timer.Duration.TotalSeconds, 0, 1) : null;
        _dlIndeterminate = download && prog == null;
        bool moving = _dlOpacity != toOp;
        if (prog is double target)
        {
            _dlShown += (target - _dlShown) * Math.Min(1, dt * 5);
            moving |= Math.Abs(target - _dlShown) > 0.002;
        }
        if (_dlIndeterminate) { _dlPhase = (_dlPhase + dt / 1.6) % 1; moving = true; }
        if (!active && _dlOpacity == 0) _dlShown = 0;
        return moving;
    }

    static double EaseOutCubic(double t) { t = MathEx.Clamp01(t); return 1 - Math.Pow(1 - t, 3); }
    static double EaseInOutCubic(double t)
    {
        t = MathEx.Clamp01(t);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }
    /// <summary>Keluar dengan sedikit "lewat" lalu balik (overshoot lembut, seperti iPhone).</summary>
    static double EaseOutBack(double t)
    {
        t = MathEx.Clamp01(t);
        const double c1 = 1.2, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    /// <summary>Hitung semua nilai animasi charger dari waktu yang sudah berjalan.</summary>
    bool StepChargeAlert()
    {
        double t = _alertClock.Elapsed.TotalSeconds;
        const double inDur = 0.6, outDur = 0.6;
        double outStart = AlertOutStart;

        // lebar notifikasi: masuk dengan overshoot lembut, keluar kurva S
        _alertA = t < inDur ? EaseOutBack(t / inDur)
                : t < outStart ? 1
                : 1 - EaseInOutCubic((t - outStart) / outDur);

        if (_alertNotif || _alertHud)
        {
            _rimProgress = 0; // notifikasi aplikasi / volume: tanpa garis tepi
            _rimOpacity = 0;
            _lineProgress = 1;
        }
        else if (_alertDownload)
        {
            // download selesai: garis biru penuh lalu memudar
            _rimProgress = 1;
            _rimOpacity = t < 1.8 ? EaseOutCubic(t / 0.3) : 1 - EaseInOutCubic((t - 1.8) / 0.8);
            _lineProgress = 1;
        }
        else if (_alertPlug)
        {
            // garis hijau tumbuh dari tengah bawah menyusuri tepi notch ke kedua sisi, lalu memudar
            _rimProgress = EaseInOutCubic((t - 0.15) / 1.1);
            _rimOpacity = t < 1.8 ? 1 : 1 - EaseInOutCubic((t - 1.8) / 0.8);
            // garis kecil di bawah notch ikut tumbuh saat notch kembali ke bentuk awal
            _lineProgress = EaseOutCubic((t - outStart - 0.2) / 0.8);
        }
        else
        {
            // dicabut: garis penuh lalu mundur perlahan ke tengah (progress mundur)
            _rimProgress = 1 - EaseInOutCubic((t - 0.35) / 1.4);
            _rimOpacity = EaseOutCubic(t / 0.25);
            _lineProgress = 1;
        }

        if (t >= AlertTotal)
        {
            _alertActive = false;
            _alertA = 0;
            _rimProgress = 0;
            _rimOpacity = 0;
            _lineProgress = 1;
            _alertClock.Stop();
            return false;
        }
        return true;
    }

    void DrawChargeAlert(double ae, double p, double bh)
    {
        double op = Smooth(MathEx.Clamp01((ae - 0.45) / 0.55)) * Smooth(MathEx.Clamp01(1 - p * 2.2));
        // notifikasi aplikasi memakai tampilan sendiri (ikon + judul + isi)
        double opNotif = _alertNotif ? op : 0;
        NotifPeek.Opacity = opNotif;
        NotifPeek.Visibility = opNotif <= 0.001 ? Visibility.Hidden : Visibility.Visible;
        if (opNotif > 0.001)
        {
            NotifPeek.Width = _alertW;
            NotifPeek.Height = bh;
            Canvas.SetLeft(NotifPeek, _cx - _alertW / 2);
            Canvas.SetTop(NotifPeek, 0);
        }
        if (Settings.Current.NotifPeekAnimated) AnimatePeekParts(MathEx.Clamp01((ae - 0.45) / 0.55));
        else if (_peekPartsUsed) AnimatePeekParts(1);
        if (_alertNotif) op = 0;
        // volume / kecerahan
        double opHud = _alertHud ? op : 0;
        HudPeek.Opacity = opHud;
        HudPeek.Visibility = opHud <= 0.001 ? Visibility.Hidden : Visibility.Visible;
        if (opHud > 0.001)
        {
            HudPeek.Width = _alertW;
            HudPeek.Height = bh;
            Canvas.SetLeft(HudPeek, _cx - _alertW / 2);
            Canvas.SetTop(HudPeek, 0);
            _hudShown += (_hudValue - _hudShown) * 0.35; // bar mengejar nilai baru dengan halus
            if (Math.Abs(_hudValue - _hudShown) < 0.002) _hudShown = _hudValue;
            double track = HudTrack.ActualWidth > 0 ? HudTrack.ActualWidth : _alertW - 120;
            HudFill.Width = Math.Max(0, track * _hudShown);
        }
        if (_alertHud) op = 0;
        ChargeAlert.Opacity = op;
        ChargeAlert.Visibility = op <= 0.001 ? Visibility.Hidden : Visibility.Visible;
        if (op <= 0.001) return;
        ChargeAlert.Width = _alertW;
        ChargeAlert.Height = bh;
        Canvas.SetLeft(ChargeAlert, _cx - _alertW / 2);
        Canvas.SetTop(ChargeAlert, 0);
        // isi baterai mengalir naik saat dicolok; saat dicabut tetap di persentasenya
        double fill = _alertPlug ? Math.Max(_rimProgress, 0) : 1;
        if (_alertDownload) fill = 0;
        AlertBatFill.Width = Math.Max(0, 19.4 * _alertPercent / 100.0 * fill);
        // petir sedikit berdenyut saat muncul
        double pulse = 1 + 0.22 * Math.Sin(Math.PI * MathEx.Clamp01(_alertClock.Elapsed.TotalSeconds / 0.7));
        AlertBoltScale.ScaleX = AlertBoltScale.ScaleY = pulse;
    }

    /// <summary>Garis hijau di dalam tepi notch, menelusuri dari tengah bawah ke kedua sisi (dash yang memanjang).</summary>
    void DrawChargeRim(double w, double h, double r, double p)
    {
        if (!_alertActive) { DrawDownloadRim(w, h, r, p); return; }
        double op = _rimOpacity * Smooth(MathEx.Clamp01(1 - p * 2.2));
        if (op <= 0.001 || _rimProgress <= 0.001)
        {
            if (ChargeRimLayer.Visibility != Visibility.Hidden) ChargeRimLayer.Visibility = Visibility.Hidden;
            return;
        }
        var s = Settings.Current;
        var left = NotchGeometry.BuildHalfOutline(_cx, w, h, s.ShoulderRadius, r, true, out double len);
        var right = NotchGeometry.BuildHalfOutline(_cx, w, h, s.ShoulderRadius, r, false, out _);
        RimL.Data = RimGlowL.Data = left;
        RimR.Data = RimGlowR.Data = right;
        double shown = len * _rimProgress;
        SetDash(RimL, shown); SetDash(RimR, shown);
        SetDash(RimGlowL, shown); SetDash(RimGlowR, shown);
        ChargeRimLayer.Visibility = Visibility.Visible;
        ChargeRimLayer.Opacity = op;
    }

    /// <summary>Garis biru download: panjang = persen, atau segmen berjalan kalau persen tidak diketahui.</summary>
    void DrawDownloadRim(double w, double h, double r, double p)
    {
        double op = _dlOpacity * Smooth(MathEx.Clamp01(1 - p * 2.2));
        if (op <= 0.001 || (!_dlIndeterminate && _dlShown <= 0.001))
        {
            if (ChargeRimLayer.Visibility != Visibility.Hidden) ChargeRimLayer.Visibility = Visibility.Hidden;
            return;
        }
        SetRimBrush(_progressBrush);
        var s = Settings.Current;
        var left = NotchGeometry.BuildHalfOutline(_cx, w, h, s.ShoulderRadius, r, true, out double len);
        var right = NotchGeometry.BuildHalfOutline(_cx, w, h, s.ShoulderRadius, r, false, out _);
        RimL.Data = RimGlowL.Data = left;
        RimR.Data = RimGlowR.Data = right;
        foreach (var path in new[] { RimL, RimR, RimGlowL, RimGlowR })
        {
            if (_dlIndeterminate)
            {
                double seg = len * 0.3, x = _dlPhase * (len + seg) - seg, t = path.StrokeThickness;
                path.StrokeDashArray = new DoubleCollection { seg / t, len * 3 / t };
                path.StrokeDashOffset = -x / t;
            }
            else SetDash(path, len * _dlShown);
        }
        ChargeRimLayer.Visibility = Visibility.Visible;
        ChargeRimLayer.Opacity = op;
    }

    /// <summary>Tampilkan hanya sepanjang <paramref name="shown"/> piksel dari awal garis.</summary>
    static void SetDash(System.Windows.Shapes.Path path, double shown)
    {
        double t = path.StrokeThickness;
        path.StrokeDashArray = new DoubleCollection { Math.Max(0.0001, shown / t), 100000 };
        path.StrokeDashOffset = 0;
    }

    // ---------- notifikasi masuk beranimasi (opsi "NotifPeekAnimated") ----------
    bool _peekPartsUsed;
    ScaleTransform? _peekIconScale;
    TranslateTransform? _peekIconMove;
    TranslateTransform[]? _peekTextMove;

    /// <summary>p 0→1 = notifikasi muncul. Ikon "pop" (sedikit melewati ukuran) sambil meluncur dari kiri; teks menyusul bergantian dari bawah.</summary>
    void AnimatePeekParts(double p)
    {
        _peekPartsUsed = p < 1;
        if (_peekIconScale == null)
        {
            var host = (FrameworkElement)((FrameworkElement)NotifPeekIcon.Parent).Parent;
            _peekIconScale = new ScaleTransform(1, 1);
            _peekIconMove = new TranslateTransform();
            var g = new TransformGroup();
            g.Children.Add(_peekIconScale);
            g.Children.Add(_peekIconMove);
            host.RenderTransformOrigin = new Point(0.5, 0.5);
            host.RenderTransform = g;
            _peekTextMove = new TranslateTransform[3];
            var texts = new FrameworkElement[] { NotifPeekApp, NotifPeekTitle, NotifPeekBody };
            for (int i = 0; i < 3; i++) { _peekTextMove[i] = new TranslateTransform(); texts[i].RenderTransform = _peekTextMove[i]; }
        }
        double ei = MathEx.Clamp01(p / 0.7);
        double pop = EaseOutBack(ei);
        _peekIconScale.ScaleX = _peekIconScale.ScaleY = 0.55 + 0.45 * pop;
        _peekIconMove!.X = -14 * (1 - EaseOutCubic(ei));
        var parts = new FrameworkElement[] { NotifPeekApp, NotifPeekTitle, NotifPeekBody };
        double[] delay = { 0.18, 0.28, 0.38 };
        for (int i = 0; i < 3; i++)
        {
            double t = EaseOutCubic(MathEx.Clamp01((p - delay[i]) / 0.55));
            parts[i].Opacity = t;
            _peekTextMove![i].Y = 7 * (1 - t);
        }
    }

}
