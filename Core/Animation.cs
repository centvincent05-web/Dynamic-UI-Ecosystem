using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace DynamicIsland.Core;

/// <summary>Simulasi pegas sederhana: memberi efek memantul halus seperti di iPhone.</summary>
public sealed class Spring
{
    public double Value;
    public double Velocity;
    public double Target;

    /// <summary>Maju satu langkah. Mengembalikan false kalau pegas sudah diam.</summary>
    public bool Step(double dt, double stiffness, double damping)
    {
        double remaining = dt;
        while (remaining > 0)
        {
            double h = Math.Min(remaining, 1.0 / 240.0); // sub-step biar stabil
            double accel = stiffness * (Target - Value) - damping * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
            remaining -= h;
        }

        if (Math.Abs(Target - Value) < 0.0005 && Math.Abs(Velocity) < 0.005)
        {
            Value = Target;
            Velocity = 0;
            return false;
        }
        return true;
    }

    public bool IsSettled => Value == Target && Velocity == 0;
}

/// <summary>
/// Satu loop frame bersama. Hanya terpasang ke render loop WPF selama ada animasi yang berjalan,
/// jadi saat diam aplikasi tidak menggambar ulang apa pun (CPU ~0%).
/// </summary>
public static class FrameLoop
{
    static readonly List<Func<double, bool>> Subscribers = new();
    static bool _hooked;
    static TimeSpan _last;
    static Func<double, bool>[] _snapshot = Array.Empty<Func<double, bool>>();
    static bool _dirty = true;

    /// <summary>True selama ada animasi yang sedang berjalan.</summary>
    public static bool IsActive => _hooked;

    /// <summary>Tambahkan callback per frame. Callback mengembalikan false untuk berhenti.</summary>
    public static void Add(Func<double, bool> tick)
    {
        if (!Subscribers.Contains(tick)) { Subscribers.Add(tick); _dirty = true; }
        if (_hooked) return;
        _hooked = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    public static void Remove(Func<double, bool> tick) { if (Subscribers.Remove(tick)) _dirty = true; }

    static void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_last == TimeSpan.Zero) { _last = now; return; }
        if (now == _last) return; // event bisa terpanggil dua kali di frame yang sama
        // batas fps animasi (0 = ikuti refresh rate layar)
        int limit = Settings.Current.AnimationFpsLimit;
        if (limit > 0 && (now - _last).TotalSeconds < 1.0 / limit - 0.002) return;
        double dt = Math.Clamp((now - _last).TotalSeconds, 0, 0.05);
        _last = now;

        // salinan daftar hanya dibuat ulang kalau berubah (tidak membuat sampah memori tiap frame)
        if (_dirty) { _snapshot = Subscribers.ToArray(); _dirty = false; }
        foreach (var tick in _snapshot)
        {
            bool keep;
            try { keep = tick(dt); } catch { keep = false; }
            if (!keep && Subscribers.Remove(tick)) _dirty = true;
        }

        if (Subscribers.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }
}

public static class MathEx
{
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    public static double Clamp01(double v) => Math.Clamp(v, 0, 1);
}
