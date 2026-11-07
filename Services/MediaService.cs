using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Services;

/// <summary>
/// Membaca media yang sedang diputar lewat API resmi Windows (sama dengan yang dipakai
/// pop-up media di Windows 11). Berbasis event: tidak ada polling.
/// Bekerja untuk Spotify, YouTube/YouTube Music di Chrome, Edge, Apple Music, dll.
/// </summary>
public sealed class MediaService
{
    readonly Dispatcher _ui;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    string _lastTrackKey = "";

    public event Action? Changed;
    public event Action? TrackChanged;

    public bool HasSession { get; private set; }
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string SourceAppId { get; private set; } = "";
    public string SourceName => FriendlySourceName(SourceAppId);
    public BitmapSource? Art { get; private set; }
    public bool IsPlaying { get; private set; }
    public TimeSpan Duration { get; private set; }
    TimeSpan _position;
    DateTime _positionStampUtc;

    public MediaService(Dispatcher ui) => _ui = ui;

    /// <summary>Posisi lagu saat ini (diinterpolasi secara lokal, tanpa bertanya ke Windows tiap detik).</summary>
    public TimeSpan Position
    {
        get
        {
            var p = _position;
            if (IsPlaying) p += DateTime.UtcNow - _positionStampUtc;
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }

    public async Task InitAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => Post(AttachCurrent);
        AttachCurrent();
    }

    void AttachCurrent()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaProperties;
            _session.PlaybackInfoChanged -= OnPlaybackInfo;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }

        _session = _manager?.GetCurrentSession();
        HasSession = _session != null;

        if (_session == null)
        {
            Title = Artist = SourceAppId = "";
            Art = null;
            IsPlaying = false;
            Duration = TimeSpan.Zero;
            _lastTrackKey = "";
            Changed?.Invoke();
            return;
        }

        _session.MediaPropertiesChanged += OnMediaProperties;
        _session.PlaybackInfoChanged += OnPlaybackInfo;
        _session.TimelinePropertiesChanged += OnTimeline;

        SourceAppId = _session.SourceAppUserModelId ?? "";
        RefreshPlayback();
        RefreshTimeline();
        _ = RefreshPropertiesAsync();
    }

    void OnMediaProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e)
        => Post(() => _ = RefreshPropertiesAsync());

    void OnPlaybackInfo(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e)
        => Post(() => { RefreshPlayback(); RefreshTimeline(); Changed?.Invoke(); });

    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e)
        => Post(() => { RefreshTimeline(); Changed?.Invoke(); });

    async Task RefreshPropertiesAsync()
    {
        var session = _session;
        if (session == null) return;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (session != _session || props == null) return;

            BitmapSource? art = null;
            if (props.Thumbnail != null)
                art = await LoadThumbnailAsync(props.Thumbnail);

            Post(() =>
            {
                if (session != _session) return;
                Title = props.Title ?? "";
                Artist = props.Artist ?? "";
                Art = art;
                string key = Title + "|" + Artist;
                bool trackChanged = key != _lastTrackKey && Title.Length > 0;
                _lastTrackKey = key;
                Changed?.Invoke();
                if (trackChanged) TrackChanged?.Invoke();
            });
        }
        catch { /* sesi bisa hilang sewaktu-waktu */ }
    }

    void RefreshPlayback()
    {
        try
        {
            var info = _session?.GetPlaybackInfo();
            IsPlaying = info != null &&
                        info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch { IsPlaying = false; }
    }

    void RefreshTimeline()
    {
        try
        {
            var t = _session?.GetTimelineProperties();
            if (t == null) return;
            Duration = t.EndTime - t.StartTime;
            _position = t.Position;
            _positionStampUtc = t.LastUpdatedTime.UtcDateTime;
            // Beberapa aplikasi tidak memperbarui stempel waktu; jangan sampai melompat jauh.
            if (DateTime.UtcNow - _positionStampUtc > TimeSpan.FromHours(1))
                _positionStampUtc = DateTime.UtcNow;
        }
        catch { }
    }

    static async Task<BitmapSource?> LoadThumbnailAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var ras = await reference.OpenReadAsync();
            using var stream = ras.AsStream();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 128; // cukup untuk tampilan, hemat RAM
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ---------- Kontrol ----------
    public void PlayPause() { try { _ = _session?.TryTogglePlayPauseAsync(); } catch { } }
    public void Next() { try { _ = _session?.TrySkipNextAsync(); } catch { } }
    public void Previous() { try { _ = _session?.TrySkipPreviousAsync(); } catch { } }

    public void Seek(TimeSpan position)
    {
        try { _ = _session?.TryChangePlaybackPositionAsync(position.Ticks); } catch { }
    }

    void Post(Action a)
    {
        if (_ui.CheckAccess()) a();
        else _ui.BeginInvoke(a);
    }

    public static string FriendlySourceName(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return "";
        string id = appId.ToLowerInvariant();
        if (id.Contains("chrome")) return "Google Chrome";
        if (id.Contains("msedge")) return "Microsoft Edge";
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("firefox") || id.Contains("308046b0af4a39cb")) return "Firefox";
        if (id.Contains("applemusic") || id.Contains("appleinc")) return "Apple Music";
        if (id.Contains("zunemusic")) return "Media Player";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("opera")) return "Opera";
        if (id.Contains("vlc")) return "VLC";

        string name = appId;
        int bang = name.LastIndexOf('!');
        if (bang >= 0) name = name[(bang + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }
}
