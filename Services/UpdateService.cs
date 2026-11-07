using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>
/// Pemberitahuan update lewat GitHub Releases (repo Vincent). Hanya MEMBERI TAHU dan membuka halaman download,
/// tidak meng-install otomatis. Cek saat app dibuka (setelah 30 dtk) lalu kira-kira sekali sehari.
/// Cara rilis: buat Release di GitHub dengan tag "v0.9.1" dan lampirkan DynamicIsland-paket.zip.
/// </summary>
public static class UpdateService
{
    public const string Repo = "centvincent05-web/Dynamic-UI-Ecosystem";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";

    static readonly HttpClient Http = CreateClient();
    static DispatcherTimer? _timer;
    static string _notifiedVersion = "";

    /// <summary>Hasil cek terakhir (null = belum pernah cek di sesi ini).</summary>
    public static Result? Last { get; private set; }

    /// <summary>Ada versi baru: (versi, alamat halaman download). Dipanggil di thread UI.</summary>
    public static event Action<string, string>? UpdateAvailable;

    public sealed record Result(bool Ok, bool Newer, string Latest, string Url, string Message);

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub mewajibkan User-Agent; cukup nama app + versi (tanpa data pribadi)
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"DynamicIsland/{CrashGuard.AppVersion()}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static void Start(Dispatcher ui)
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(3); // setelah cek pertama: periksa tiap 3 jam apakah sudah sehari
            var s = Settings.Current;
            if (!s.UpdateCheck) return;
            // cek pertama di tiap sesi selalu jalan; berikutnya hanya kalau sudah ±sehari
            bool first = Last == null;
            if (!first && DateTime.Now - s.UpdateLastCheck < TimeSpan.FromHours(20)) return;
            await CheckAsync();
        };
        _timer.Start();
    }

    /// <summary>Cek sekarang. Aman dipanggil dari thread UI; event UpdateAvailable dipanggil di thread UI.</summary>
    public static async Task<Result> CheckAsync()
    {
        Result r;
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest").ConfigureAwait(true);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                r = new Result(false, false, "", ReleasesPage, L.T("Belum ada rilis di GitHub (atau repo-nya private)."));
            else if (!resp.IsSuccessStatusCode)
                r = new Result(false, false, "", ReleasesPage, L.T("Gagal cek update") + $" (HTTP {(int)resp.StatusCode}).");
            else
            {
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
                bool newer = IsNewer(tag, CrashGuard.AppVersion());
                string latest = tag.TrimStart('v', 'V');
                r = new Result(true, newer, latest, url,
                    newer ? L.T("Versi baru tersedia: ") + latest : L.T("Sudah versi terbaru") + $" ({CrashGuard.AppVersion()}).");
            }
            Settings.Current.UpdateLastCheck = DateTime.Now;
            Settings.Save();
        }
        catch (Exception)
        {
            r = new Result(false, false, "", ReleasesPage, L.T("Gagal cek update (tidak ada internet?)."));
        }
        Last = r;
        if (r.Newer && r.Latest != _notifiedVersion)
        {
            _notifiedVersion = r.Latest;
            UpdateAvailable?.Invoke(r.Latest, r.Url);
        }
        return r;
    }

    /// <summary>"v0.10.0" lebih baru dari "0.9.0"? Tag yang tidak bisa dibaca dianggap tidak lebih baru.</summary>
    public static bool IsNewer(string tag, string current)
    {
        static Version? Parse(string s)
        {
            s = s.Trim().TrimStart('v', 'V');
            int dash = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (dash > 0) s = s[..dash];
            if (!s.Contains('.')) s += ".0";
            return Version.TryParse(s, out var v) ? v : null;
        }
        var a = Parse(tag);
        var b = Parse(current);
        if (a == null || b == null) return false;
        static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), 0);
        return Norm(a) > Norm(b);
    }

    public static void OpenDownloadPage(string? url = null)
    {
        try { Process.Start(new ProcessStartInfo(url ?? Last?.Url ?? ReleasesPage) { UseShellExecute = true }); } catch { }
    }
}
