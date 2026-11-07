using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.UI;

/// <summary>Island: titik mikrofon/kamera, pop-up perangkat Bluetooth, shortcut keyboard.</summary>
public partial class IslandWindow
{
    PrivacyMonitor? _privacy;
    BluetoothWatcher? _bluetooth;
    bool _alertDevice;

    public void AttachDevices(PrivacyMonitor privacy, BluetoothWatcher bluetooth)
    {
        _privacy = privacy;
        _bluetooth = bluetooth;
        privacy.Changed += UpdatePrivacy;
        bluetooth.Connected += ShowDeviceAlert;
        ApplyDeviceSettings();
    }

    /// <summary>Nyalakan / matikan pemantau sesuai pengaturan (yang mati benar-benar berhenti).</summary>
    void ApplyDeviceSettings()
    {
        var s = Settings.Current;
        if (_privacy != null)
        {
            bool need = s.PrivacyDots || s.CameraDot || s.CallIndicator || s.RecordIndicator;
            if (need && !_privacy.Running) _privacy.Start();
            else if (!need && _privacy.Running) _privacy.Stop();
        }
        if (_bluetooth != null)
        {
            if (s.BluetoothPopup && !_bluetooth.Running) _bluetooth.Start();
            else if (!s.BluetoothPopup && _bluetooth.Running) _bluetooth.Stop();
        }
        UpdatePrivacy();
    }

    // =====================================================================
    // Titik mikrofon (oranye) / kamera (hijau)
    // =====================================================================

    void UpdatePrivacy()
    {
        var st = Settings.Current;
        bool demo = PrivacyPreviewActive; // pratinjau dari Pengaturan: tampilkan kedua titik sebentar
        bool cam = demo || (st.CameraDot && _privacy != null && _privacy.CameraInUse);
        bool mic = demo || (st.PrivacyDots && _privacy != null && _privacy.MicInUse);
        bool any = cam || mic;
        bool wasShown = MiniPrivacy.Visibility == Visibility.Visible;
        MiniCamDot.Visibility = cam ? Visibility.Visible : Visibility.Collapsed;
        MiniMicDot.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
        MiniMicDot.Margin = new Thickness(cam ? 3 : 0, 0, 0, 0);
        MiniPrivacy.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (any)
        {
            MiniPrivacy.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            MiniPrivacyShift.X = MiniPrivacy.DesiredSize.Width + 6;
            var tip = new List<string>();
            if (cam && !demo) tip.Add(L.T("Kamera") + ": " + string.Join(", ", _privacy!.CameraApps));
            if (mic && !demo) tip.Add(L.T("Mikrofon") + ": " + string.Join(", ", _privacy!.MicApps));
            MiniPrivacy.ToolTip = tip.Count > 0 ? string.Join("\n", tip) : null;
            if (!wasShown)
                MiniPrivacy.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        }
        else MiniPrivacyShift.X = 0;
        UpdateLiveActivity();
        FitMini();
    }

    // =====================================================================
    // Panggilan (hijau) / rekam layar (merah) dengan durasi
    // =====================================================================

    static readonly string[] CallApps =
    {
        "discord", "whatsapp", "zoom", "teams", "skype", "telegram", "slack", "signal", "messenger", "line",
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", // panggilan lewat web (Google Meet, dll.)
    };
    static readonly Brush CallGreen = MakeFrozen(Color.FromRgb(0x34, 0xC7, 0x59));
    static readonly Brush CallGreenBg = MakeFrozen(Color.FromArgb(0x33, 0x34, 0xC7, 0x59));
    static readonly Brush RecRed = MakeFrozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    static readonly Brush RecRedBg = MakeFrozen(Color.FromArgb(0x33, 0xFF, 0x45, 0x3A));
    DispatcherTimer? _liveActivityTimer;
    DateTime _liveSince;
    long _callPreviewUntil;

    void UpdateLiveActivity()
    {
        var s = Settings.Current;
        bool demo = Environment.TickCount64 < _callPreviewUntil;
        DateTime? recSince = null, callSince = null;
        if (_privacy != null && s.RecordIndicator && _privacy.CaptureInUse)
            recSince = _privacy.CaptureSince.Values.DefaultIfEmpty(DateTime.Now).Min();
        if (_privacy != null && s.CallIndicator)
        {
            // ikon Discord di kiri notch sudah menandai panggilan Discord: jangan tampil dobel
            var calls = _privacy.MicSince.Where(kv => CallApps.Any(a => kv.Key.Contains(a, StringComparison.OrdinalIgnoreCase))
                                                     && !(s.DiscordInIsland && kv.Key.Contains("discord", StringComparison.OrdinalIgnoreCase))).ToList();
            if (calls.Count > 0) callSince = calls.Min(kv => kv.Value);
        }
        if (demo && recSince == null && callSince == null) callSince = DateTime.Now.AddSeconds(-42);

        bool rec = recSince != null;
        bool any = rec || callSince != null;
        bool wasShown = MiniLive.Visibility == Visibility.Visible;
        if (!any)
        {
            MiniLive.Visibility = Visibility.Collapsed;
            MiniLiveShift.X = 0;
            _liveActivityTimer?.Stop();
            return;
        }
        _liveSince = (rec ? recSince : callSince)!.Value;
        var fore = rec ? RecRed : CallGreen;
        MiniLive.Background = rec ? RecRedBg : CallGreenBg;
        MiniLiveIcon.Text = rec ? "\uE7C8" : "\uE717"; // titik rekam / telepon
        MiniLiveIcon.Foreground = MiniLiveText.Foreground = fore;
        MiniLive.ToolTip = rec ? L.T("Merekam layar") + ": " + string.Join(", ", _privacy?.CaptureApps ?? Array.Empty<string>())
                               : L.T("Dalam panggilan") + (demo ? "" : ": " + string.Join(", ", _privacy!.MicApps));
        MiniLive.Visibility = Visibility.Visible;
        TickLiveActivity();
        if (!wasShown) MiniLive.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        if (_liveActivityTimer == null)
        {
            _liveActivityTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _liveActivityTimer.Tick += (sender, args) =>
            {
                if (Environment.TickCount64 >= _callPreviewUntil && _callPreviewUntil != 0) { _callPreviewUntil = 0; UpdateLiveActivity(); return; }
                TickLiveActivity();
            };
        }
        _liveActivityTimer.Start();
    }

    void TickLiveActivity()
    {
        var d = DateTime.Now - _liveSince;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        MiniLiveText.Text = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");
        MiniLive.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double shift = -(MiniLive.DesiredSize.Width + 6);
        if (Math.Abs(shift - MiniLiveShift.X) > 0.5) { MiniLiveShift.X = shift; FitMini(); }
    }

    // =====================================================================
    // Perangkat Bluetooth tersambung
    // =====================================================================

    void ShowDeviceAlert(string name, int? battery, string glyph) => ShowDeviceAlert(name, battery, glyph, force: false);

    void ShowDeviceAlert(string name, int? battery, string glyph, bool force)
    {
        if ((!Settings.Current.BluetoothPopup && !force) || _suspended || _expanded) return;
        _alertNotif = true; // memakai tampilan notifikasi kecil yang sama
        _alertDevice = true;
        _alertPlug = false;
        _alertDownload = false;
        _alertHud = false;
        _alertW = NotifPeekWidth;
        _alertH = 60;
        NotifPeekIcon.Source = null;
        NotifPeekGlyph.Text = glyph;
        NotifPeekGlyph.Visibility = Visibility.Visible;
        NotifPeekApp.Text = "Bluetooth · " + L.T("Tersambung");
        NotifPeekTitle.Text = name;
        NotifPeekBody.Text = battery is int b ? $"{L.T("Baterai")} {b}%" : L.T("Tersambung");
        NotifPeekBody.Foreground = battery is int low && low <= 20 ? AlarmRed : PeekBodyBrush;
        NotifPeekBody.Visibility = battery != null ? Visibility.Visible : Visibility.Collapsed;
        BeginAlert();
    }

    static readonly Brush PeekBodyBrush = MakeFrozen(Color.FromRgb(0xDD, 0xDD, 0xDD));

    /// <summary>Kembalikan tampilan notifikasi kecil ke mode notifikasi aplikasi.</summary>
    void ResetPeekForNotification()
    {
        _alertDevice = false;
        NotifPeekGlyph.Visibility = Visibility.Collapsed;
        NotifPeekBody.Foreground = PeekBodyBrush;
    }

    // =====================================================================
    // Shortcut keyboard (RegisterHotKey: tidak memakai hook, tidak membebani)
    // =====================================================================

    const int WM_HOTKEY = 0x0312;
    enum HotkeyId { Toggle = 1, Next, Prev, OpenSettings, Search }

    HwndSource? _hwndSource;
    string _hotkeySignature = "";
    readonly List<int> _registeredHotkeys = new();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    void InitHotkeys(IntPtr hwnd)
    {
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(HotkeyProc);
        ApplyHotkeys();
    }

    void ApplyHotkeys()
    {
        if (_hwndSource == null) return;
        var s = Settings.Current;
        string sig = (s.HotkeysEnabled ? $"{s.HotkeyToggle}|{s.HotkeyNext}|{s.HotkeyPrev}|{s.HotkeySettings}" : "")
                   + (s.SearchEnabled ? "|search:" + s.HotkeySearch : "");
        if (sig == _hotkeySignature) return; // tidak berubah: jangan daftar ulang
        _hotkeySignature = sig;
        IntPtr h = _hwndSource.Handle;
        foreach (int id in _registeredHotkeys) UnregisterHotKey(h, id);
        _registeredHotkeys.Clear();
        Hotkeys.Failed.Clear();
        // pencarian cepat punya tombolnya sendiri (tidak ikut "Aktifkan shortcut keyboard")
        if (s.SearchEnabled) Register(h, HotkeyId.Search, s.HotkeySearch);
        if (!s.HotkeysEnabled) return;
        Register(h, HotkeyId.Toggle, s.HotkeyToggle);
        Register(h, HotkeyId.Next, s.HotkeyNext);
        Register(h, HotkeyId.Prev, s.HotkeyPrev);
        Register(h, HotkeyId.OpenSettings, s.HotkeySettings);
    }

    void Register(IntPtr h, HotkeyId id, string text)
    {
        if (!Hotkeys.TryParse(text, out uint mods, out uint vk)) return;
        if (RegisterHotKey(h, (int)id, mods | Hotkeys.ModNoRepeat, vk)) _registeredHotkeys.Add((int)id);
        else Hotkeys.Failed.Add(text);
    }

    // =====================================================================
    // Volume / kecerahan di notch
    // =====================================================================

    /// <summary>Tampilkan bar volume (atau kecerahan) di notch; tekan lagi = diperpanjang, tanpa animasi ulang.</summary>
    public void ShowHud(bool brightness, double value, bool muted = false)
    {
        if (_suspended || _expanded) return;
        value = Math.Clamp(value, 0, 1);
        _hudValue = muted ? 0 : value;
        HudIcon.Text = brightness ? "\uE706"
            : muted || value <= 0 ? "\uE74F" : value < 0.34 ? "\uE993" : value < 0.67 ? "\uE994" : "\uE995";
        HudText.Text = muted ? "mute" : Math.Round(value * 100) + "%";
        double t = _alertClock.Elapsed.TotalSeconds;
        if (_alertActive && _alertHud && t < _hudOutStart)
        {
            _hudOutStart = t + 1.3; // masih tampil: cukup perpanjang
            FrameLoop.Add(_animTick);
            return;
        }
        bool wasShown = _alertActive && _alertHud;
        _alertHud = true;
        _alertNotif = false;
        _alertPlug = false;
        _alertDownload = false;
        _alertW = 300; _alertH = 40;
        _hudOutStart = 1.6;
        if (!wasShown) _hudShown = _hudValue;
        BeginAlert();
    }

    // =====================================================================
    // Gestur touchpad: geser 2 jari ke samping (WM_MOUSEHWHEEL) = ganti lagu
    // =====================================================================

    const int WM_MOUSEHWHEEL = 0x020E;
    int _hWheel;
    long _hWheelAt, _lastSwipe;

    bool OnHorizontalWheel(IntPtr wParam)
    {
        var s = Settings.Current;
        if (!s.TouchpadGestures || !HasMedia || _suspended) return false;
        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        long now = Environment.TickCount64;
        if (now - _hWheelAt > 400 || (_hWheel != 0 && Math.Sign(_hWheel) != Math.Sign(delta))) _hWheel = 0;
        _hWheelAt = now;
        _hWheel += delta;
        if (Math.Abs(_hWheel) < 240 || now - _lastSwipe < 800) return true; // satu gesekan = satu lagu
        bool next = _hWheel > 0;
        if (s.GestureReverse) next = !next;
        _hWheel = 0;
        _lastSwipe = now;
        if (next) _media.Next(); else _media.Previous();
        Nudge(next ? -1 : 1);
        if (_expanded && _mode != IslandMode.Media) SetMode(IslandMode.Media, animate: true); // tetap di player musik
        return true;
    }

    readonly TranslateTransform _nudge = new();

    /// <summary>Isi notch sedikit bergeser ke arah geser lalu kembali (tanda gestur diterima).</summary>
    void Nudge(int dir)
    {
        if (Mini.RenderTransform != _nudge) Mini.RenderTransform = _nudge;
        var a = new DoubleAnimation(0, dir * 10, TimeSpan.FromMilliseconds(110))
        {
            AutoReverse = true, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        _nudge.BeginAnimation(TranslateTransform.XProperty, a);
    }

    IntPtr HotkeyProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEHWHEEL) { handled = OnHorizontalWheel(wParam); return IntPtr.Zero; }
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        handled = true;
        switch ((HotkeyId)wParam.ToInt32())
        {
            case HotkeyId.Toggle:
                if (_expanded) CloseByKey();
                else OpenByKey(HasMedia ? IslandMode.Media : IslandMode.Control);
                break;
            case HotkeyId.Next:
            case HotkeyId.Prev:
                if (!_expanded) { OpenByKey(HasMedia ? IslandMode.Media : IslandMode.Control); break; }
                var pages = Pages();
                int idx = Math.Max(0, Array.IndexOf(pages, _mode));
                int dir = (HotkeyId)wParam.ToInt32() == HotkeyId.Next ? 1 : -1;
                _peeking = false;
                SetMode(pages[(idx + dir + pages.Length) % pages.Length], animate: true);
                KeepOpenAfterKey();
                break;
            case HotkeyId.OpenSettings:
                App.OpenSettings();
                break;
            case HotkeyId.Search:
                if (!_suspended) SearchWindow.Toggle();
                break;
        }
        return IntPtr.Zero;
    }

    const int KeyOpenMs = 5000; // dibuka lewat keyboard: tetap terbuka sebentar walaupun mouse di tempat lain

    void OpenByKey(IslandMode mode)
    {
        if (_suspended) return;
        if (IsTucked) { _edgeRevealed = true; ApplyPresence(); }
        _peeking = false;
        Expand(mode);
        KeepOpenAfterKey();
    }

    void KeepOpenAfterKey()
    {
        if (Root.IsMouseOver) return;
        Start(_collapseTimer, Math.Max(KeyOpenMs, Settings.Current.CollapseDelayMs));
        if (_edgeRevealed) Start(_edgeRevealTimer, Math.Max(KeyOpenMs, Settings.Current.CollapseDelayMs) + 600);
    }

    void CloseByKey()
    {
        _collapseTimer.Stop();
        Collapse();
        if (_edgeRevealed && !Root.IsMouseOver) Start(_edgeRevealTimer, 600);
    }
}
