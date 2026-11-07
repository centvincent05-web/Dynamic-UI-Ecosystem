using System;
using System.Media;
using System.Windows.Threading;

namespace DynamicIsland.Services;

/// <summary>Timer hitung mundur. Hanya berdetak (1x per detik) saat sedang berjalan.</summary>
public sealed class TimerService
{
    readonly DispatcherTimer _tick;
    DateTime _endUtc;

    public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(25);
    public TimeSpan Remaining { get; private set; } = TimeSpan.FromMinutes(25);
    public bool IsRunning { get; private set; }
    /// <summary>Waktu habis dan belum ditutup pengguna (lonceng merah).</summary>
    public bool IsRinging { get; private set; }
    public DateTime EndsAtLocal => _endUtc.ToLocalTime();

    /// <summary>Tampilkan di notch kecil: sedang berjalan, dijeda di tengah jalan, atau berbunyi.</summary>
    public bool IsActive => IsRunning || IsRinging || (Remaining < Duration && Remaining > TimeSpan.Zero);

    public event Action? Changed;
    public event Action? Finished;

    public TimerService()
    {
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => OnTick();
    }

    public void SetMinutes(double minutes)
    {
        minutes = Math.Clamp(Math.Round(minutes), 1, 180);
        Stop();
        Duration = TimeSpan.FromMinutes(minutes);
        Remaining = Duration;
        IsRinging = false;
        Changed?.Invoke();
    }

    public void Start()
    {
        if (IsRunning) return;
        if (Remaining <= TimeSpan.Zero) Remaining = Duration;
        IsRinging = false;
        _endUtc = DateTime.UtcNow + Remaining;
        IsRunning = true;
        _tick.Start();
        Changed?.Invoke();
    }

    public void Pause()
    {
        if (!IsRunning) return;
        Remaining = _endUtc - DateTime.UtcNow;
        if (Remaining < TimeSpan.Zero) Remaining = TimeSpan.Zero;
        Stop();
        Changed?.Invoke();
    }

    public void Toggle()
    {
        if (IsRunning) Pause();
        else Start();
    }

    public void Reset()
    {
        Stop();
        Remaining = Duration;
        IsRinging = false;
        Changed?.Invoke();
    }

    /// <summary>Matikan lonceng setelah waktu habis.</summary>
    public void Dismiss()
    {
        if (!IsRinging) return;
        IsRinging = false;
        Remaining = Duration;
        Changed?.Invoke();
    }

    void Stop()
    {
        IsRunning = false;
        _tick.Stop();
    }

    int _lastShownSecond = -1;

    void OnTick()
    {
        Remaining = _endUtc - DateTime.UtcNow;
        if (Remaining <= TimeSpan.Zero)
        {
            Remaining = TimeSpan.Zero;
            Stop();
            IsRinging = true;
            try { SystemSounds.Exclamation.Play(); } catch { }
            Changed?.Invoke();
            Finished?.Invoke();
            return;
        }
        int sec = (int)Math.Ceiling(Remaining.TotalSeconds);
        if (sec == _lastShownSecond) return; // perbarui tampilan hanya saat detik berganti
        _lastShownSecond = sec;
        Changed?.Invoke();
    }

    public static string Format(TimeSpan t)
    {
        int total = (int)Math.Ceiling(Math.Max(0, t.TotalSeconds));
        int h = total / 3600, m = total / 60 % 60, s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }
}
