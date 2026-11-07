using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicIsland.Core;

public enum VisualizerMode { Real, Fake, Off }

/// <summary>
/// Semua pengaturan aplikasi. Disimpan di %AppData%\DynamicIsland\settings.json.
/// Bisa diubah lewat menu Pengaturan (klik kanan island) atau edit file JSON saat aplikasi tertutup.
/// </summary>
public sealed class Settings
{
    // ---------- Bentuk island ----------
    /// <summary>Lebar notch kecil saat diam (jam + baterai).</summary>
    public double CollapsedWidth { get; set; } = 150;
    /// <summary>Lebar notch kecil saat musik diputar (cover + jam + bar musik).</summary>
    public double CollapsedActiveWidth { get; set; } = 215;
    public double CollapsedHeight { get; set; } = 34;
    /// <summary>Ukuran saat hover di cover lagu (player musik).</summary>
    public double MediaWidth { get; set; } = 370;
    public double MediaHeight { get; set; } = 160;
    /// <summary>Ukuran control center (hover di jam).</summary>
    public double ControlWidth { get; set; } = 440;
    public double ControlHeight { get; set; } = 326;
    /// <summary>Ukuran halaman kalender + timer.</summary>
    public double CalendarWidth { get; set; } = 450;
    public double CalendarHeight { get; set; } = 240;
    /// <summary>Ukuran daftar Wi-Fi / Bluetooth.</summary>
    public double NetListWidth { get; set; } = 440;
    public double NetListHeight { get; set; } = 330;
    public double NotifWidth { get; set; } = 440;
    public double NotifHeight { get; set; } = 320;
    /// <summary>Halaman notifikasi Windows di island.</summary>
    public bool NotificationsEnabled { get; set; } = true;
    /// <summary>Notifikasi baru muncul sebentar di notch.</summary>
    public bool NotifPeek { get; set; } = true;
    /// <summary>Aplikasi yang notifikasinya tidak dimunculkan di notch (tetap ada di halaman notifikasi).</summary>
    public List<string> NotifMutedApps { get; set; } = new();
    /// <summary>Aplikasi yang pernah mengirim notifikasi (id → nama), untuk daftar di Pengaturan.</summary>
    public Dictionary<string, string> NotifKnownApps { get; set; } = new();

    public bool IsNotifMuted(string appId) => NotifMutedApps.Exists(x => string.Equals(x, appId, StringComparison.OrdinalIgnoreCase));

    public void SetNotifMuted(string appId, bool muted)
    {
        NotifMutedApps.RemoveAll(x => string.Equals(x, appId, StringComparison.OrdinalIgnoreCase));
        if (muted) NotifMutedApps.Add(appId);
    }
    public double PerfWidth { get; set; } = 440;
    public double PerfHeight { get; set; } = 330;
    // halaman island yang ditampilkan (bisa dimatikan supaya tidak sesak)
    public bool PageMusic { get; set; } = true;
    public bool PageControl { get; set; } = true;
    public bool PageCalendar { get; set; } = true;
    public bool PagePerformance { get; set; } = true;
    /// <summary>Font untuk island, panel samping, dan dock.</summary>
    public string FontName { get; set; } = "Segoe UI Variable Text";
    /// <summary>Ikon Discord di kiri notch saat sedang dalam panggilan suara (musik tetap di kanan).</summary>
    public bool DiscordInIsland { get; set; } = false;
    /// <summary>Titik hijau (kamera) / oranye (mikrofon) di notch saat ada aplikasi yang memakainya.</summary>
    public bool PrivacyDots { get; set; } = true;   // titik mikrofon (oranye)
    /// <summary>Titik kamera (hijau). Bawaan mati: Windows versi baru tidak lagi memberi tahu aplikasi lain.</summary>
    public bool CameraDot { get; set; } = false;
    /// <summary>Pop-up di notch saat perangkat Bluetooth tersambung, beserta sisa baterainya.</summary>
    public bool BluetoothPopup { get; set; } = true;
    /// <summary>Pil hijau + durasi di notch saat sedang dalam panggilan (Discord, WhatsApp, Zoom, Meet, …).</summary>
    public bool CallIndicator { get; set; } = true;
    /// <summary>Pil merah + durasi di notch saat layar sedang direkam.</summary>
    public bool RecordIndicator { get; set; } = false;
    /// <summary>Pencarian cepat ala Spotlight (aplikasi, file terbaru, kalkulator, Google).</summary>
    public bool SearchEnabled { get; set; } = true;
    public string HotkeySearch { get; set; } = "Alt+Space";

    // ---------- shortcut keyboard (bawaan: mati) ----------
    /// <summary>Geser 2 jari ke samping di island = lagu berikutnya / sebelumnya.</summary>
    public bool TouchpadGestures { get; set; } = true;
    /// <summary>Cahaya lembut yang mengikuti pointer saat hover di island.</summary>
    public bool HoverSpotlight { get; set; } = true;
    /// <summary>Saat terbuka, island lebih pekat (94%) supaya isinya terbaca di atas wallpaper yang ramai.</summary>
    public bool ExpandedSolid { get; set; } = false;
    /// <summary>Cahaya ambient disembunyikan sebentar selama island bergerak, supaya animasinya tidak patah-patah.</summary>
    public bool SmoothGlow { get; set; } = false;
    /// <summary>Jendela island dibesarkan sebelum membuka & dikecilkan beberapa detik setelah menutup (opsional).</summary>
    public bool PrewarmWindow { get; set; } = false;
    public double SpotlightIntensity { get; set; } = 0.4;
    /// <summary>Cahaya pointer: warna, bentuk (0 bulat, 1 oval lebar, 2 cincin, 3 liquid glass), ukuran (1 = 100%).</summary>
    public string PointerLightColor { get; set; } = "#FFFFFF";
    public int PointerLightShape { get; set; } = 0;
    public double PointerLightSize { get; set; } = 1.0;
    // cahaya pointer & ambient light di area lain (bawaan: hanya island, seperti sebelumnya)
    public bool SpotlightDock { get; set; } = false;
    public bool SpotlightPanels { get; set; } = false;
    public bool AmbientDock { get; set; } = false;
    public bool AmbientPanels { get; set; } = false;
    /// <summary>Kecerahan warna ambient light: 1 = seperti sebelumnya.</summary>
    public double AmbientBrightness { get; set; } = 1.0;
    /// <summary>Saat tombol volume/kecerahan ditekan: 0 = panel samping, 1 = di notch, 2 = keduanya.</summary>
    public int VolumeHudMode { get; set; } = 0;
    [System.Text.Json.Serialization.JsonIgnore] public bool HudSide => VolumeHudMode != 1;
    [System.Text.Json.Serialization.JsonIgnore] public bool HudNotch => VolumeHudMode != 0;
    public bool GestureReverse { get; set; } = false;
    public bool HotkeysEnabled { get; set; } = false;
    public string HotkeyToggle { get; set; } = "Ctrl+Alt+Space";
    public string HotkeyNext { get; set; } = "Ctrl+Alt+Right";
    public string HotkeyPrev { get; set; } = "Ctrl+Alt+Left";
    public string HotkeySettings { get; set; } = "Ctrl+Alt+S";

    // ---------- halaman tambahan ----------
    public bool PageShelf { get; set; } = true;
    /// <summary>Kotak file di Shelf menampilkan isi asli (halaman PDF/Word, gambar, video) bukan ikon. Bawaan mati.</summary>
    public bool ShelfThumbnails { get; set; } = true;
    /// <summary>Pratinjau besar saat pointer diam di atas file di Shelf (ala Quick Look). Bawaan mati.</summary>
    public bool ShelfHoverPreview { get; set; } = true;
    public List<string> ShelfItems { get; set; } = new();
    public bool PageClipboard { get; set; } = true;
    public bool PageGemini { get; set; } = true;
    public string GeminiApiKey { get; set; } = "";
    public string GeminiModel { get; set; } = "gemini-flash-latest";
    public double ShelfWidth { get; set; } = 440;
    public double ShelfHeight { get; set; } = 250;
    public double ClipWidth { get; set; } = 440;
    public double ClipHeight { get; set; } = 320;
    public double GeminiWidth { get; set; } = 460;
    public double GeminiHeight { get; set; } = 360;

    // ---------- kalender ----------
    public bool CalendarEvents { get; set; } = true;
    /// <summary>Google Calendar → Setelan → "Alamat rahasia dalam format iCal".</summary>
    public string CalendarIcsUrl { get; set; } = "";
    public int ReminderMinutes { get; set; } = 10;
    public bool CalendarWeather { get; set; } = true;
    public string WeatherCity { get; set; } = "Jakarta";
    /// <summary>Timer mode Pomodoro: 25 menit fokus, 5 menit istirahat, 15 menit tiap 4 putaran.</summary>
    public bool PomodoroEnabled { get; set; } = true;
    /// <summary>FPS bar musik saat notch kecil (bar-nya kecil, tidak perlu setinggi saat terbuka).</summary>
    public int MiniVisualizerFps { get; set; } = 30;
    /// <summary>Batas FPS semua animasi: 0 = ikuti refresh rate layar.</summary>
    public int AnimationFpsLimit { get; set; } = 0;
    /// <summary>Radius lengkungan cekung di sambungan dengan tepi layar.</summary>
    public double ShoulderRadius { get; set; } = 10;
    public double CollapsedBottomRadius { get; set; } = 17;
    public double ExpandedBottomRadius { get; set; } = 28;

    // ---------- Animasi (pegas) ----------
    /// <summary>Makin besar = makin cepat/kencang.</summary>
    public double AnimStiffness { get; set; } = 210;
    /// <summary>Makin kecil = makin memantul.</summary>
    public double AnimDamping { get; set; } = 26;
    /// <summary>Kecepatan muncul dari tepi (panel samping, dock, notch tersembunyi). 1 = seperti versi lama.</summary>
    public double EdgeRevealSpeed { get; set; } = 1.0;

    /// <summary>Pegas untuk muncul dari tepi: lebih kencang, tingkat pantulnya sama dengan animasi biasa.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public (double k, double c) RevealSpring
    {
        get
        {
            double m = Math.Clamp(EdgeRevealSpeed, 1, 4);
            return (AnimStiffness * m, AnimDamping * Math.Sqrt(m));
        }
    }
    public int HoverExpandDelayMs { get; set; } = 120;
    public int CollapseDelayMs { get; set; } = 450;
    public bool PeekOnTrackChange { get; set; } = true;
    public int PeekDurationMs { get; set; } = 2500;

    // ---------- Jam ----------
    public bool Use24Hour { get; set; } = true;
    public bool ShowSeconds { get; set; } = false;

    // ---------- Tampilan ----------
    public string AccentColor { get; set; } = "#5DCAA5";
    public string ChargeColor { get; set; } = "#34C759";
    public string DownloadColor { get; set; } = "#0A84FF";
    public string TimerColor { get; set; } = "#FF9F0A";
    /// <summary>Warna tombol / tile saat menyala (Wi-Fi, Bluetooth, Jangan ganggu, dll.).</summary>
    public string ToggleOnColor { get; set; } = "#FFFFFF";
    public string CameraDotColor { get; set; } = "#34C759";
    public string MicDotColor { get; set; } = "#FF9F0A";
    public string IslandColor { get; set; } = "#000000";
    /// <summary>0.2 - 1.0 (hanya latar, teks tetap jelas).</summary>
    public double IslandOpacity { get; set; } = 0.8;
    public string PanelColor { get; set; } = "#2A2A2C";
    public double PanelOpacity { get; set; } = 0.96;
    public bool ShowPanelPercent { get; set; } = false;

    // ---------- Bingkai layar (strip atas + sudut layar membulat) ----------
    public bool BezelEnabled { get; set; } = false;
    public double BezelTopThickness { get; set; } = 4;
    public double ScreenCornerRadius { get; set; } = 12;
    public double BezelOpacity { get; set; } = 1.0;
    public bool BezelHideInFullscreen { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VisualizerMode Visualizer { get; set; } = VisualizerMode.Real;
    public int VisualizerBars { get; set; } = 5;
    /// <summary>Batas frame bar musik per detik (hemat CPU).</summary>
    public int VisualizerFps { get; set; } = 30;

    // ---------- Panel tepi (volume kiri, kecerahan kanan) ----------
    public bool EnableEdgePanels { get; set; } = true;
    public int EdgeDelayDesktopMs { get; set; } = 0;
    public int EdgeWindowDelayMs { get; set; } = 0;
    public int EdgeDelayFullscreenMs { get; set; } = 1500;
    /// <summary>Persentase bagian tengah tepi layar yang aktif sebagai pemicu.</summary>
    public double EdgeZonePercent { get; set; } = 60;
    public int EdgeHideDelayMs { get; set; } = 700;
    public int ScrollStepPercent { get; set; } = 2;
    /// <summary>Toleransi (piksel) dari tepi layar untuk memicu panel/notch/dock.</summary>
    public int EdgeTolerancePx { get; set; } = 3;
    public bool ShowPanelOnKeyChange { get; set; } = true;
    /// <summary>Tangkap tombol volume keyboard: pop-up Windows tidak muncul, diganti panel Dynamic Island.</summary>
    public bool ReplaceVolumeOsd { get; set; } = true;
    public int KeyVolumeStepPercent { get; set; } = 2;
    /// <summary>Sembunyikan pop-up volume/kecerahan bawaan Windows (termasuk tombol F2/F3).</summary>
    public bool HideNativeOsd { get; set; } = true;
    public int AppListOpenDelayMs { get; set; } = 600;

    // ---------- Dock (pengganti taskbar) ----------
    public bool DockEnabled { get; set; } = true;
    /// <summary>Sembunyikan taskbar asli Windows selama dock aktif (dikembalikan saat aplikasi ditutup).</summary>
    public bool HideWindowsTaskbar { get; set; } = true;
    /// <summary>Ikut sembunyikan taskbar di monitor kedua dst. Bawaan mati: taskbar monitor kedua tetap tampil.</summary>
    public bool HideSecondaryTaskbars { get; set; } = false;
    /// <summary>Smart: dock sembunyi saat ada jendela maximize, muncul saat pointer ke tepi bawah.</summary>
    public bool DockSmart { get; set; } = true;
    public double DockIconSize { get; set; } = 38;
    public bool DockShowRunning { get; set; } = true;
    /// <summary>Lama hover di ikon dock sebelum preview jendela muncul.</summary>
    public int DockPreviewDelayMs { get; set; } = 700;
    /// <summary>Aplikasi yang disematkan (path .lnk/.exe). Kosong (null) = ikut taskbar Windows.</summary>
    public System.Collections.Generic.List<string>? DockPinned { get; set; } = null;

    // ---------- Menu dock (pengganti Start menu) ----------
    /// <summary>Tombol kiri dock membuka menu Dynamic Island (dock memanjang ke atas). Mati = membuka Start Windows seperti dulu.</summary>
    public bool DockMenu { get; set; } = true;
    /// <summary>Tombol Windows (ditekan sendirian) membuka menu dock, bukan Start Windows. Bawaan mati.</summary>
    public bool WinKeyOpensMenu { get; set; } = true;
    /// <summary>Bagian Phone Link (HP tersambung + tombol buka Phone Link) di menu dock. Bawaan mati.</summary>
    public bool DockMenuPhoneLink { get; set; } = false;
    /// <summary>Aplikasi yang disematkan di menu dock (ID dari shell:AppsFolder).</summary>
    public System.Collections.Generic.List<string> MenuPinned { get; set; } = new();
    /// <summary>Ikon di menu dock memakai bentuk & warna latar yang sama dengan ikon dock.</summary>
    public bool MenuIconsLikeDock { get; set; } = true;
    /// <summary>Warna ikon dock & menu: 0 = warna asli (bawaan), 1 = monokrom, 2 = satu warna pilihan.</summary>
    public int IconColorMode { get; set; } = 0;
    public string IconTintColor { get; set; } = "#0A84FF";

    // ---------- Sistem ----------
    /// <summary>Sembunyikan island saat nonton video / main game fullscreen.</summary>
    public bool HideInFullscreen { get; set; } = true;
    /// <summary>Mode game: saat fullscreen, semua fitur berhenti total (hook mouse dilepas, audio & animasi stop).</summary>
    public bool GameMode { get; set; } = true;
    /// <summary>Saat fullscreen di aplikasi ini (browser, pemutar video), notch langsung muncul saat pointer ke atas.
    /// Fullscreen di aplikasi lain (game) tidak memunculkan notch.</summary>
    /// <summary>Opsional: aplikasi yang tetap boleh memunculkan notch walau fullscreen (nama proses, pisahkan koma).</summary>
    public string FullscreenRevealList { get; set; } = "";
    /// <summary>Garis hijau di bawah notch saat mengisi daya.</summary>
    public bool ShowChargeLine { get; set; } = true;
    /// <summary>Animasi notch saat charger dicolok / dicabut.</summary>
    public bool ChargeAnimation { get; set; } = true;
    /// <summary>Garis progress download di sekeliling notch.</summary>
    public bool DownloadProgress { get; set; } = true;
    /// <summary>Garis tepi tipis ala macOS di island, panel, dan dock.</summary>
    public bool EdgeHighlight { get; set; } = true;
    /// <summary>Bar musik, progress, dan sumber lagu mengikuti warna cover.</summary>
    public bool CoverColors { get; set; } = true;
    /// <summary>Sisa waktu timer sebagai garis oranye di tepi notch.</summary>
    public bool TimerRim { get; set; } = true;
    /// <summary>Ikon dock membesar mengikuti pointer & memantul saat aplikasi dibuka (gaya macOS).</summary>
    public bool DockMagnify { get; set; } = true;
    /// <summary>Pembesaran ikon dock bergerak dengan pegas (masuk/keluar halus). Mati = cara lama.</summary>
    public bool DockMagnifySmooth { get; set; } = false;
    /// <summary>Pantulan ikon saat app dibuka seperti bola jatuh (makin rendah). Mati = cara lama.</summary>
    public bool DockBounceRealistic { get; set; } = false;
    /// <summary>Isi island muncul dari buram ke tajam saat membuka/berganti halaman.</summary>
    public bool IslandContentBlur { get; set; } = false;
    /// <summary>Bentuk notch sedikit meregang mengikuti kecepatan geraknya (kenyal).</summary>
    public bool IslandJelly { get; set; } = false;
    /// <summary>Notifikasi di notch: ikon "pop" & meluncur, teks menyusul bergantian.</summary>
    public bool NotifPeekAnimated { get; set; } = false;
    /// <summary>Widget yang menyingkir / dilepas bergeser beranimasi, bukan langsung lompat.</summary>
    public bool WidgetAnimateMove { get; set; } = false;
    /// <summary>Bentuk ikon dock: 0 = kotak membulat (bawaan), 1 = lingkaran, 2 = kaca, 3 = ikon asli.</summary>
    public int DockIconShape { get; set; } = 0;
    /// <summary>Bahasa tampilan: "id" (Indonesia) atau "en" (English).</summary>
    public string Language { get; set; } = "id";
    /// <summary>Stat laptop di control center (pisahkan koma): cpu, gpu, ram, disk, battery.</summary>
    public string StatItems { get; set; } = "cpu,ram,battery";
    /// <summary>Cahaya lembut di sekitar island mengikuti warna cover lagu.</summary>
    public bool AmbientLight { get; set; } = true;
    public double AmbientIntensity { get; set; } = 0.7;
    /// <summary>Pointer di tepi atas mana saja memunculkan notch (bukan hanya di tengah).</summary>
    public bool TopRevealFullWidth { get; set; } = true;
    /// <summary>Pointer di tepi bawah mana saja memunculkan dock (bukan hanya di bawah dock).</summary>
    public bool DockRevealFullWidth { get; set; } = true;
    /// <summary>Notch meluncur hilang saat ada jendela yang di-maximize.</summary>
    public bool HideWhenMaximized { get; set; } = true;
    /// <summary>Notch selalu tersembunyi (juga di desktop), baru muncul saat pointer ke tengah tepi atas.</summary>
    public bool AlwaysHidden { get; set; } = false;
    /// <summary>Saat notch tersembunyi, arahkan pointer ke tengah tepi atas untuk memunculkannya.</summary>
    public bool RevealOnTopEdge { get; set; } = true;
    /// <summary>Jeda muncul dari tepi atas (desktop / jendela maximize). Saat fullscreen notch tidak muncul.</summary>
    public int TopRevealDelayMs { get; set; } = 0;
    public double TopEdgeZoneWidth { get; set; } = 320;
    public bool RunAtStartup { get; set; } = false;
    /// <summary>Jalankan ulang otomatis kalau aplikasi crash (lewat proses penjaga kecil). Bawaan mati, seperti sebelumnya.</summary>
    public bool AutoRecover { get; set; } = false;

    // =====================================================================

    /// <summary>Versi ukuran default. Kalau naik, ukuran lama di settings.json diganti default baru.</summary>
    public int SizeVersion { get; set; } = 0; // 0 = file lama tanpa versi
    // ---------- Update (GitHub Releases) ----------
    /// <summary>Cek versi baru di GitHub saat app dibuka (lalu sekali sehari).</summary>
    public bool UpdateCheck { get; set; } = true;
    public DateTime UpdateLastCheck { get; set; } = DateTime.MinValue;

    // ---------- Wallpaper (Fase 1) — fitur nyala, tapi wallpaper baru diganti setelah gambar/folder dipilih ----------
    public bool WallpaperEnabled { get; set; } = true; // permintaan Vincent: bawaan nyala (aman: tidak mengubah apa pun sebelum gambar dipilih)
    /// <summary>0 = satu gambar, 1 = folder (berganti otomatis), 2 = siang/malam.</summary>
    public int WallpaperMode { get; set; } = 0;
    public string WallpaperImage { get; set; } = "";
    /// <summary>Gambar khusus untuk monitor lain (mode satu gambar). Kosong = sama dengan monitor utama.</summary>
    public string WallpaperImageOther { get; set; } = "";
    public string WallpaperFolder { get; set; } = "";
    public int WallpaperIntervalMin { get; set; } = 60;
    public bool WallpaperShuffle { get; set; } = false;
    /// <summary>Mode folder: tiap monitor mendapat gambar berbeda.</summary>
    public bool WallpaperPerMonitor { get; set; } = false;
    public string WallpaperDayImage { get; set; } = "";
    public string WallpaperNightImage { get; set; } = "";
    /// <summary>Siang/malam ikut matahari terbit & terbenam di kota (Pengaturan → Kalender, cuaca). Mati = jam tetap.</summary>
    public bool WallpaperBySun { get; set; } = true;
    public int WallpaperDayHour { get; set; } = 6;
    public int WallpaperNightHour { get; set; } = 18;
    /// <summary>0 Isi (Fill), 1 Pas (Fit), 2 Regang, 3 Tengah, 4 Ubin, 5 Bentang ke semua monitor.</summary>
    public int WallpaperFit { get; set; } = 0;
    public bool WallpaperFade { get; set; } = true;
    public bool WallpaperLockScreen { get; set; } = true; // permintaan Vincent: bawaan nyala
    /// <summary>Warna aksen island ikut warna utama wallpaper. Warna lama disimpan dan dikembalikan saat dimatikan.</summary>
    public bool AccentFromWallpaper { get; set; } = false;
    public string AccentBeforeWallpaper { get; set; } = "";
    /// <summary>Saat tidak ada lagu, cahaya ambient island ikut warna wallpaper.</summary>
    public bool AmbientFromWallpaper { get; set; } = false;
    // posisi rotasi (diisi otomatis)
    public int WallpaperIndex { get; set; } = -1;
    public DateTime WallpaperLastChange { get; set; } = DateTime.MinValue;
    public string WallpaperLastApplied { get; set; } = "";
    public double? WallpaperLat { get; set; }
    public double? WallpaperLon { get; set; }
    public string WallpaperGeoCity { get; set; } = "";

    // ---------- Live wallpaper (Fase 3) — bawaan mati, berjalan di proses terpisah "--wallpaper" ----------
    public bool LiveWallpaperEnabled { get; set; } = false;
    /// <summary>File video/gambar per monitor (indeks 0 = layar utama, lalu urut dari kiri). Kosong = sama dengan layar utama.</summary>
    public List<string>? LiveWallpaperFiles { get; set; } = null;
    /// <summary>0 isi layar, 1 pas (utuh), 2 regang.</summary>
    public int LiveWallpaperFit { get; set; } = 0;
    /// <summary>Suara video (hanya dari layar utama). Bawaan bisu.</summary>
    public bool LiveWallpaperSound { get; set; } = false;
    public double LiveWallpaperVolume { get; set; } = 0.3;
    public bool LiveWallpaperPauseCovered { get; set; } = true;
    public bool LiveWallpaperPauseOnBattery { get; set; } = true;
    public bool LiveWallpaperPauseGame { get; set; } = true;
    /// <summary>Loop tanpa jeda: dua pemutar bergantian (+RAM/VRAM ±1 video). Mati = pemutar tunggal (cara lama).</summary>
    public bool LiveWallpaperSeamlessLoop { get; set; } = true;
    /// <summary>Video dijeda lama (desktop tertutup) → tutup pemutar, tampilkan frame terakhir sebagai gambar (hemat ±500 MB).</summary>
    public bool LiveWallpaperReleaseWhenPaused { get; set; } = true;
    public int LiveWallpaperReleaseAfterSec { get; set; } = 20;
    /// <summary>Batas ukuran file video (MB). File yang lebih besar tidak dimuat.</summary>
    public int LiveWallpaperMaxMB { get; set; } = 500;

    // ---------- Widget desktop (Fase 2) — bawaan mati ----------
    public bool WidgetsEnabled { get; set; } = false;
    /// <summary>Ukuran satu sel grid widget (DIP). Semua widget, teks, dan gambar ikut diskalakan (bawaan 158 = seperti macOS).</summary>
    public double WidgetCellSize { get; set; } = 158;
    /// <summary>Jarak grid widget dari tepi layar (DIP). Area dock di bawah juga selalu dikosongkan.</summary>
    public double WidgetScreenMargin { get; set; } = 56;
    /// <summary>Daftar widget. Null = belum pernah dinyalakan (diisi jam, cuaca, musik, performa saat pertama kali).</summary>
    public List<WidgetConfig>? Widgets { get; set; } = null;

    /// <summary>Layar sambutan pertama kali sudah pernah ditampilkan.</summary>
    public bool WelcomeShown { get; set; } = false;
    public int LookVersion { get; set; } = 0; // 1 = tampilan translucent 80%
    const int CurrentSizeVersion = 5;

    [JsonIgnore]
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynamicIsland", "settings.json");

    public static Settings Current { get; private set; } = new();

    /// <summary>Dipanggil setiap ada pengaturan yang berubah (diterapkan langsung, tanpa restart).</summary>
    public static event Action? Changed;

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new Settings();
        }
        catch
        {
            Current = new Settings();
        }
        if (Current.SizeVersion < CurrentSizeVersion)
        {
            var d = new Settings();
            Current.CollapsedWidth = d.CollapsedWidth;
            Current.CollapsedActiveWidth = d.CollapsedActiveWidth;
            Current.CollapsedHeight = d.CollapsedHeight;
            Current.CollapsedBottomRadius = d.CollapsedBottomRadius;
            Current.ShoulderRadius = d.ShoulderRadius;
            Current.ExpandedBottomRadius = d.ExpandedBottomRadius;
            Current.MediaWidth = d.MediaWidth;
            Current.MediaHeight = d.MediaHeight;
            Current.ControlWidth = d.ControlWidth;
            Current.ControlHeight = d.ControlHeight;
            Current.CalendarWidth = d.CalendarWidth;
            Current.CalendarHeight = d.CalendarHeight;
            Current.SizeVersion = CurrentSizeVersion;
        }
        if (Current.LookVersion < 1)
        {
            Current.IslandOpacity = 0.8; // tampilan translucent ala macOS
            Current.LookVersion = 1;
        }
        if (Current.LookVersion < 2)
        {
            if (Current.DockIconSize >= 44) Current.DockIconSize = 38; // dock sedikit lebih kecil
            Current.LookVersion = 2;
        }
        if (Current.LookVersion < 3)
        {
            Current.BezelEnabled = false; // bingkai layar mati sebagai bawaan
            Current.LookVersion = 3;
        }
        if (Current.LookVersion < 5)
        {
            // kamera & rekam layar tidak terdeteksi di Windows versi baru: bawaan mati
            Current.CameraDot = false;
            Current.RecordIndicator = false;
        }
        if (Current.LookVersion < 4)
        {
            Current.ExpandedSolid = false; // transparansi saat terbuka sama dengan saat kecil (diatur sendiri di Pengaturan)
            Current.LookVersion = 4;
        }
        if (Current.LookVersion < 5) Current.LookVersion = 5;
        if (Current.LookVersion < 6)
        {
            // tepi kiri/kanan: bagian yang aktif diperlebar supaya pointer lebih gampang "masuk"
            if (Math.Abs(Current.EdgeZonePercent - 60) < 0.1) Current.EdgeZonePercent = 80;
            Current.LookVersion = 6;
        }
        if (Current.LookVersion < 7)
        {
            // kembali seperti dulu: seluruh tepi langsung memunculkan notch / dock / panel (area bisa diatur di Pengaturan);
            // fullscreen biasa (browser, video) juga langsung, game tetap diam total lewat Mode game
            Current.TopRevealFullWidth = true;
            Current.TopRevealDelayMs = 0;
            Current.EdgeZonePercent = 100;
            Current.EdgeDelayFullscreenMs = 0;
            Current.LookVersion = 7;
        }
        if (Current.LookVersion < 8)
        {
            Current.SmoothGlow = false;     // island kembali seperti sebelum direvisi; opsinya tetap ada di Pengaturan
            Current.PrewarmWindow = false;
            Current.LookVersion = 8;
        }
        if (Current.LookVersion < 9)
        {
            // panel samping kembali seperti versi lama: hanya 60% bagian tengah tepi layar,
            // dan saat fullscreen baru muncul setelah pointer diam 1,5 detik di tepi
            Current.EdgeZonePercent = 60;
            Current.EdgeDelayFullscreenMs = 1500;
            Current.LookVersion = 9;
        }
        if (Current.LookVersion < 10)
        {
            // permintaan Vincent: thumbnail & pratinjau Shelf (ala macOS) serta tombol Windows → menu dock jadi bawaan
            Current.ShelfThumbnails = true;
            Current.ShelfHoverPreview = true;
            Current.WinKeyOpensMenu = true;
            Current.LookVersion = 10;
        }
        if (Current.LookVersion < 12)
        {
            // permintaan Vincent (6 Nov): pengaturan wallpaper & lock screen bawaan nyala.
            // Aman: tanpa gambar/folder yang dipilih, wallpaper tidak diubah.
            Current.WallpaperEnabled = true;
            Current.WallpaperLockScreen = true;
        }
        if (Current.LookVersion < 11)
        {
            // keputusan Vincent: taskbar monitor kedua tampil sebagai bawaan (bisa ikut disembunyikan di Pengaturan → Dock)
            Current.HideSecondaryTaskbars = false;
            Current.LookVersion = 11;
        }
        if (Current.LookVersion < 12) Current.LookVersion = 12;
        Current.Sanitize();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
        }
        catch { /* best effort */ }
    }

    public static void ResetToDefault()
    {
        Current = new Settings { SizeVersion = CurrentSizeVersion };
        NotifyChanged();
    }

    public static void NotifyChanged()
    {
        Current.Sanitize();
        Changed?.Invoke();
    }

    void Sanitize()
    {
        CollapsedWidth = Math.Clamp(CollapsedWidth, 80, 600);
        CollapsedActiveWidth = Math.Clamp(CollapsedActiveWidth, CollapsedWidth, 700);
        IslandOpacity = Math.Clamp(IslandOpacity, 0.2, 1);
        PanelOpacity = Math.Clamp(PanelOpacity, 0.2, 1);
        BezelOpacity = Math.Clamp(BezelOpacity, 0.05, 1);
        BezelTopThickness = Math.Clamp(BezelTopThickness, 0, 20);
        ScreenCornerRadius = Math.Clamp(ScreenCornerRadius, 0, 40);
        TopRevealDelayMs = Math.Clamp(TopRevealDelayMs, 0, 5000);
        DockIconSize = Math.Clamp(DockIconSize, 28, 72);
        AmbientIntensity = Math.Clamp(AmbientIntensity, 0.1, 1);
        TopEdgeZoneWidth = Math.Clamp(TopEdgeZoneWidth, 60, 1000);
        CollapsedHeight = Math.Clamp(CollapsedHeight, 20, 80);
        MediaWidth = Math.Clamp(MediaWidth, CollapsedActiveWidth, 900);
        MediaHeight = Math.Clamp(MediaHeight, CollapsedHeight, 500);
        ControlWidth = Math.Clamp(ControlWidth, CollapsedActiveWidth, 900);
        ControlHeight = Math.Clamp(ControlHeight, CollapsedHeight, 600);
        CalendarWidth = Math.Clamp(CalendarWidth, CollapsedActiveWidth, 900);
        CalendarHeight = Math.Clamp(CalendarHeight, CollapsedHeight, 600);
        ShoulderRadius = Math.Clamp(ShoulderRadius, 0, 40);
        CollapsedBottomRadius = Math.Clamp(CollapsedBottomRadius, 0, CollapsedHeight);
        ExpandedBottomRadius = Math.Clamp(ExpandedBottomRadius, 0, MediaHeight / 2);
        AnimStiffness = Math.Clamp(AnimStiffness, 50, 1500);
        AnimDamping = Math.Clamp(AnimDamping, 5, 100);
        VisualizerBars = Math.Clamp(VisualizerBars, 3, 12);
        VisualizerFps = Math.Clamp(VisualizerFps, 15, 144);
        EdgeZonePercent = Math.Clamp(EdgeZonePercent, 10, 100);
        ScrollStepPercent = Math.Clamp(ScrollStepPercent, 1, 20);
        EdgeTolerancePx = Math.Clamp(EdgeTolerancePx, 0, 20);
        EdgeDelayDesktopMs = Math.Clamp(EdgeDelayDesktopMs, 0, 5000);
        EdgeWindowDelayMs = Math.Clamp(EdgeWindowDelayMs, 0, 5000);
        EdgeDelayFullscreenMs = Math.Clamp(EdgeDelayFullscreenMs, 0, 5000);
    }
}

/// <summary>Satu widget desktop. Posisi disimpan sebagai pecahan ruang kosong monitor (tahan ganti resolusi).</summary>
public sealed class WidgetConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>0 jam, 1 cuaca, 2 musik, 3 performa, 4 catatan.</summary>
    public int Kind { get; set; }
    /// <summary>0 = layar utama, 1.. = monitor lain (urut dari kiri).</summary>
    public int Monitor { get; set; }
    /// <summary>Posisi di grid (kolom/baris sel, dari kiri-atas). −1 = belum diatur → disusun otomatis.</summary>
    public int Col { get; set; } = -1;
    public int Row { get; set; } = -1;
    /// <summary>(Lama, tidak dipakai lagi sejak grid) posisi pecahan 0..1.</summary>
    public double X { get; set; } = -1;
    public double Y { get; set; } = -1;
    /// <summary>0 kecil, 1 sedang, 2 besar, 3 lebar.</summary>
    public int Size { get; set; }
    /// <summary>0 kotak membulat, 1 lingkaran, 2 pil.</summary>
    public int Shape { get; set; }
    /// <summary>0 kaca, 1 pekat, 2 minimal.</summary>
    public int Style { get; set; }
    /// <summary>0 ikut warna aksen, 1 ikut wallpaper, 2 pilihan sendiri.</summary>
    public int ColorMode { get; set; }
    public string Color { get; set; } = "#0A84FF";
    public bool Visible { get; set; } = true;
    /// <summary>Font widget. Kosong = ikut font app (Pengaturan → Warna & font).</summary>
    public string Font { get; set; } = "";
    /// <summary>0 bawaan, 1 tipis, 2 normal, 3 tebal.</summary>
    public int FontWeightMode { get; set; }
    /// <summary>0 putih, 1 ikut warna widget.</summary>
    public int TextColor { get; set; }
    /// <summary>Cahaya ambient di sekitar widget: 0 mati, 1 ikut warna widget, 2 ikut lagu yang diputar.</summary>
    public int Ambient { get; set; }
    public double AmbientStrength { get; set; } = 0.6;
    /// <summary>Warna latar (gaya kaca & pekat): 0 gelap bawaan, 1 ikut wallpaper, 2 pilih sendiri.</summary>
    public int BgMode { get; set; }
    public string BgColor { get; set; } = "#1C1C1E";
    /// <summary>Isi widget Catatan (Kind 4).</summary>
    public string NoteText { get; set; } = "";

    /// <summary>Ringkasan tampilan (bukan posisi, bukan isi catatan): kalau berubah, isi widget dibangun ulang.</summary>
    public string LookKey() => $"{Kind}|{Size}|{Shape}|{Style}|{ColorMode}|{Color}|{Font}|{FontWeightMode}|{TextColor}|{Ambient}|{AmbientStrength:0.00}|{BgMode}|{BgColor}";
}
