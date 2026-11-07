using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.UI;

/// <summary>Bagian yang bisa dipratinjau dari Pengaturan.</summary>
public enum PreviewKind
{
    Mini, Media, Control, Calendar, Notifications, Performance, Shelf, Clipboard, Gemini,
    Charge, Download, Timer, NotifPeek, Bluetooth, Privacy, Hud, Call, Spotlight,
}

/// <summary>
/// Island: pratinjau langsung saat pengaturan diubah. Island dipaksa muncul (juga saat ada jendela
/// fullscreen/maximize), membuka halaman yang sedang diatur atau memutar animasinya, lalu kembali normal.
/// </summary>
public partial class IslandWindow
{
    const int PreviewHoldMs = 3000;
    long _privacyPreviewUntil;
    DispatcherTimer? _privacyPreviewTimer, _spotPreviewTimer;

    public void Preview(PreviewKind kind)
    {
        if (_suspended) return; // mode game: jangan ganggu
        RevealForPreview();
        switch (kind)
        {
            case PreviewKind.Mini:
                if (_expanded) { _collapseTimer.Stop(); Collapse(); }
                break;
            case PreviewKind.Media: OpenForPreview(IslandMode.Media); break;
            case PreviewKind.Control: OpenForPreview(IslandMode.Control); break;
            case PreviewKind.Calendar: OpenForPreview(IslandMode.Calendar); break;
            case PreviewKind.Notifications: OpenForPreview(IslandMode.Notifications); break;
            case PreviewKind.Performance: OpenForPreview(IslandMode.Performance); break;
            case PreviewKind.Shelf: OpenForPreview(IslandMode.Shelf); break;
            case PreviewKind.Clipboard: OpenForPreview(IslandMode.Clipboard); break;
            case PreviewKind.Gemini: OpenForPreview(IslandMode.Gemini); break;
            case PreviewKind.Charge:
                if (!PrepareAlertPreview()) break;
                var b = SystemStats.Battery();
                ShowChargeAlert(true, b.HasBattery ? b.Percent : 80, force: true);
                break;
            case PreviewKind.Download:
                if (!PrepareAlertPreview()) break;
                ShowRimAlert(DownloadBlue, "", L.T("Download selesai"), "contoh-file.zip");
                break;
            case PreviewKind.Timer:
                if (!PrepareAlertPreview()) break;
                ShowRimAlert(TimerOrange, "", L.T("Timer"), "25:00");
                break;
            case PreviewKind.NotifPeek:
                if (!PrepareAlertPreview()) break;
                ShowNotificationAlert(new NotificationItem(0, "", "Dynamic Island", L.T("Contoh notifikasi"),
                    L.T("Begini notifikasi baru tampil di notch."), "", DateTime.Now));
                break;
            case PreviewKind.Bluetooth:
                if (!PrepareAlertPreview()) break;
                ShowDeviceAlert(L.T("Earbuds (contoh)"), 80, "", force: true);
                break;
            case PreviewKind.Spotlight:
                OpenForPreview(IslandMode.Control);
                if (!Root.IsMouseOver)
                {
                    // pointer tidak di island: tunjukkan cahayanya di tengah sebentar
                    MoveSpotlight(new Point(_cx, Math.Max(40, _pageH.Value / 2)));
                    FadeSpotlight(true);
                    _spotPreviewTimer ??= OneShot(() => { if (!Root.IsMouseOver) FadeSpotlight(false); });
                    Start(_spotPreviewTimer, PreviewHoldMs - 300);
                }
                break;
            case PreviewKind.Call:
                if (_expanded) { _collapseTimer.Stop(); Collapse(); }
                _callPreviewUntil = Environment.TickCount64 + PreviewHoldMs;
                UpdateLiveActivity();
                break;
            case PreviewKind.Hud:
                if (_expanded) { _collapseTimer.Stop(); Collapse(); }
                ShowHud(false, _volume.Muted ? 0 : _volume.Master, _volume.Muted);
                break;
            case PreviewKind.Privacy:
                if (_expanded) { _collapseTimer.Stop(); Collapse(); }
                _privacyPreviewUntil = Environment.TickCount64 + PreviewHoldMs;
                UpdatePrivacy();
                _privacyPreviewTimer ??= OneShot(UpdatePrivacy);
                Start(_privacyPreviewTimer, PreviewHoldMs + 50);
                break;
        }
    }

    bool PrivacyPreviewActive => Environment.TickCount64 < _privacyPreviewUntil;

    /// <summary>Notch tersembunyi (fullscreen / maximize / selalu sembunyi): turunkan sebentar.</summary>
    void RevealForPreview()
    {
        if (!_tucked) return;
        _edgeRevealed = true;
        ApplyPresence();
        Start(_edgeRevealTimer, PreviewHoldMs + 900);
    }

    void OpenForPreview(IslandMode mode)
    {
        if (!_expanded) Expand(mode);
        else if (_mode != mode) SetMode(Array.IndexOf(Pages(), mode) >= 0 ? mode : _mode, animate: true);
        if (!Root.IsMouseOver) Start(_collapseTimer, PreviewHoldMs);
    }

    /// <summary>Animasi notifikasi butuh notch kecil; jangan putar ulang selagi masih berjalan.</summary>
    bool PrepareAlertPreview()
    {
        if (_alertActive) return false;
        if (_expanded) { _collapseTimer.Stop(); Collapse(); }
        return true;
    }

    /// <summary>Notifikasi kecil dengan garis berwarna penuh di tepi notch (dipakai pratinjau download / timer).</summary>
    void ShowRimAlert(Brush brush, string glyph, string label, string detail)
    {
        _alertPlug = false;
        _alertDownload = true;
        _alertHud = false;
        _alertNotif = false;
        _alertW = 300; _alertH = 40;
        AlertBolt.Text = glyph;
        AlertBolt.Foreground = brush;
        AlertBolt.Visibility = Visibility.Visible;
        AlertLabel.Text = label;
        AlertLabel.Foreground = Brushes.White;
        AlertPercent.Text = detail;
        AlertPercent.Foreground = Subtle;
        AlertBattery.Visibility = Visibility.Collapsed;
        SetRimBrush(brush);
        BeginAlert();
    }
}
