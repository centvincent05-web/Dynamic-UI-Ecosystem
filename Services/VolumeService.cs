using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DynamicIsland.Services;

/// <summary>Satu aplikasi yang mengeluarkan suara (bisa terdiri dari beberapa sesi audio).</summary>
public sealed class AppAudio
{
    public string Name = "";
    public string? ExePath;
    public ImageSource? Icon;
    public bool IsActive;
    public readonly List<SimpleAudioVolume> Volumes = new();

    public float Volume
    {
        get => Volumes.Count > 0 ? Volumes[0].Volume : 0;
        set { foreach (var v in Volumes) try { v.Volume = Math.Clamp(value, 0f, 1f); } catch { } }
    }

    public bool Muted
    {
        get => Volumes.Count > 0 && Volumes[0].Mute;
        set { foreach (var v in Volumes) try { v.Mute = value; } catch { } }
    }
}

/// <summary>Volume utama + volume per aplikasi lewat Windows Core Audio.</summary>
public sealed class VolumeService : IDisposable, IMMNotificationClient
{
    readonly Dispatcher _ui;
    readonly MMDeviceEnumerator _enumerator = new();
    MMDevice? _device;
    readonly Dictionary<string, (string name, ImageSource? icon)> _appCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Volume berubah (dari tombol keyboard, aplikasi lain, atau dari kita sendiri).</summary>
    public event Action? MasterChanged;

    public VolumeService(Dispatcher ui)
    {
        _ui = ui;
        AttachDefaultDevice();
        try { _enumerator.RegisterEndpointNotificationCallback(this); } catch { }
    }

    void AttachDefaultDevice()
    {
        try
        {
            if (_device != null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                _device.Dispose();
            }
        }
        catch { }

        try
        {
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
        }
        catch { _device = null; }
    }

    void OnVolumeNotification(AudioVolumeNotificationData data)
        => _ui.BeginInvoke(new Action(() => MasterChanged?.Invoke()));

    public bool Available => _device != null;

    public float Master
    {
        get { try { return _device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0; } catch { return 0; } }
        set { try { if (_device != null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f); } catch { } }
    }

    public bool Muted
    {
        get { try { return _device?.AudioEndpointVolume.Mute ?? false; } catch { return false; } }
        set { try { if (_device != null) _device.AudioEndpointVolume.Mute = value; } catch { } }
    }

    /// <summary>Daftar aplikasi yang punya sesi audio. Dipanggil hanya saat panel dibuka.</summary>
    public List<AppAudio> GetApps()
    {
        var result = new Dictionary<string, AppAudio>(StringComparer.OrdinalIgnoreCase);
        if (_device == null) return new List<AppAudio>();

        try
        {
            var manager = _device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;

            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                try
                {
                    if (s.IsSystemSoundsSession) continue;
                    if (s.State == AudioSessionState.AudioSessionStateExpired) continue;

                    int pid = (int)s.GetProcessID;
                    string? path = Native.ProcessPath(pid);
                    string key = path ?? ("pid:" + pid);

                    if (!result.TryGetValue(key, out var app))
                    {
                        var (name, icon) = Describe(path, pid, s.DisplayName);
                        app = new AppAudio { Name = name, ExePath = path, Icon = icon };
                        result[key] = app;
                    }

                    app.Volumes.Add(s.SimpleAudioVolume);
                    if (s.State == AudioSessionState.AudioSessionStateActive) app.IsActive = true;
                }
                catch { }
            }
        }
        catch { }

        return result.Values
            .OrderByDescending(a => a.IsActive)
            .ThenBy(a => a.Name)
            .ToList();
    }

    // ---------- Discord (untuk ikon di kiri notch) ----------

    AudioSessionControl? _discordSession;
    long _discordCheck;

    /// <summary>
    /// Discord sedang dalam panggilan suara (sesi audionya aktif) + tingkat suara saat ini (0-1).
    /// Pencarian sesi dilakukan paling sering tiap 1 detik; tingkat suara dibaca dari sesi yang tersimpan.
    /// </summary>
    public (bool active, float peak, string? exe) DiscordState()
    {
        long now = Environment.TickCount64;
        if (_device == null) return (false, 0, null);
        if (_discordSession == null || now - _discordCheck > 1000)
        {
            _discordCheck = now;
            _discordSession = null;
            _discordExe = null;
            try
            {
                var manager = _device.AudioSessionManager;
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    try
                    {
                        if (s.State != AudioSessionState.AudioSessionStateActive) continue;
                        string? path = Native.ProcessPath((int)s.GetProcessID);
                        if (path == null) continue;
                        string file = System.IO.Path.GetFileName(path);
                        if (!file.StartsWith("Discord", StringComparison.OrdinalIgnoreCase)) continue;
                        _discordSession = s;
                        _discordExe = path;
                        break;
                    }
                    catch { }
                }
            }
            catch { }
        }
        if (_discordSession == null) return (false, 0, null);
        float peak = 0;
        try { peak = _discordSession.AudioMeterInformation.MasterPeakValue; } catch { _discordSession = null; return (false, 0, null); }
        return (true, peak, _discordExe);
    }
    string? _discordExe;

    public static ImageSource? IconOf(string path) => LoadIcon(path);

    (string name, ImageSource? icon) Describe(string? path, int pid, string? displayName)
    {
        if (path != null && _appCache.TryGetValue(path, out var cached)) return cached;

        string name = "";
        if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith("@")) name = displayName;
        if (name.Length == 0 && path != null)
        {
            try { name = FileVersionInfo.GetVersionInfo(path).FileDescription ?? ""; } catch { }
        }
        if (name.Length == 0)
        {
            try { name = Process.GetProcessById(pid).ProcessName; } catch { name = "Aplikasi"; }
        }

        ImageSource? icon = path != null ? LoadIcon(path) : null;
        var entry = (name.Trim(), icon);
        if (path != null) _appCache[path] = entry;
        return entry;
    }

    static ImageSource? LoadIcon(string path)
    {
        try
        {
            var info = new Native.SHFILEINFO();
            Native.SHGetFileInfo(path, 0, ref info,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.SHFILEINFO>(),
                Native.SHGFI_ICON | Native.SHGFI_LARGEICON);
            if (info.hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { Native.DestroyIcon(info.hIcon); }
        }
        catch { return null; }
    }

    // ---------- Ganti perangkat output (misal colok headset) ----------
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia)
            _ui.BeginInvoke(new Action(() => { AttachDefaultDevice(); MasterChanged?.Invoke(); }));
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        try { _device?.Dispose(); } catch { }
        try { _enumerator.Dispose(); } catch { }
    }
}
