using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

public sealed record CalendarEvent(string Title, DateTime Start, DateTime End, bool AllDay);

/// <summary>
/// Jadwal dari Google Calendar lewat "alamat rahasia format iCal" (tanpa login / OAuth).
/// Diperbarui tiap 15 menit; mengingatkan beberapa menit sebelum acara dimulai.
/// </summary>
public sealed class CalendarFeed
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly Dispatcher _ui;
    readonly DispatcherTimer _refresh, _remind;
    readonly HashSet<string> _reminded = new();
    List<CalendarEvent> _all = new();
    DateTime _todayFor;

    public List<CalendarEvent> Today { get; private set; } = new();
    public string? Error { get; private set; }
    public event Action? Changed;
    public event Action<CalendarEvent>? Reminder;

    public CalendarFeed(Dispatcher ui)
    {
        _ui = ui;
        _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(15) };
        _refresh.Tick += (_, _) => Refresh();
        _remind = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _remind.Tick += (_, _) => CheckReminders();
        Refresh(); // timer hanya berjalan kalau kalender dipakai (lihat Refresh)
    }

    public void Refresh()
    {
        string url = Settings.Current.CalendarIcsUrl?.Trim() ?? "";
        if (!Settings.Current.CalendarEvents || url.Length == 0)
        {
            _refresh.Stop();
            _remind.Stop();
            _all.Clear(); Today = new(); Error = null; Changed?.Invoke();
            return;
        }
        if (!_refresh.IsEnabled) _refresh.Start();
        if (!_remind.IsEnabled) _remind.Start();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        Task.Run(async () =>
        {
            List<CalendarEvent>? list = null;
            string? err = null;
            try { list = Parse(await Http.GetStringAsync(url).ConfigureAwait(false)); }
            catch (Exception ex) { err = ex.Message; }
            _ui.BeginInvoke(new Action(() =>
            {
                Error = err;
                if (list != null) _all = list;
                UpdateToday();
            }));
        });
    }

    void UpdateToday()
    {
        var day = DateTime.Today;
        _todayFor = day;
        Today = _all.Where(e => e.Start < day.AddDays(1) && e.End > day).OrderBy(e => !e.AllDay).ThenBy(e => e.Start).ToList();
        Changed?.Invoke();
    }

    void CheckReminders()
    {
        if (_todayFor != DateTime.Today) UpdateToday(); // ganti hari
        int minutes = Settings.Current.ReminderMinutes;
        if (minutes <= 0) return;
        var now = DateTime.Now;
        foreach (var e in Today)
        {
            if (e.AllDay) continue;
            double until = (e.Start - now).TotalMinutes;
            string key = e.Title + e.Start.ToString("o");
            if (until <= minutes && until > -1 && _reminded.Add(key)) Reminder?.Invoke(e);
        }
    }

    // ---------- iCal ----------

    static List<CalendarEvent> Parse(string ics)
    {
        // gabungkan baris lanjutan (diawali spasi / tab)
        var lines = new List<string>();
        foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if ((raw.StartsWith(" ") || raw.StartsWith("\t")) && lines.Count > 0) lines[^1] += raw[1..];
            else lines.Add(raw);
        }
        var result = new List<CalendarEvent>();
        Dictionary<string, string>? ev = null;
        var exdates = new List<DateTime>();
        foreach (var line in lines)
        {
            if (line == "BEGIN:VEVENT") { ev = new(StringComparer.OrdinalIgnoreCase); exdates.Clear(); continue; }
            if (line == "END:VEVENT" && ev != null) { Expand(ev, exdates, result); ev = null; continue; }
            if (ev == null) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon], value = line[(colon + 1)..];
            string key = name.Split(';')[0];
            if (key == "EXDATE") { foreach (var v in value.Split(',')) if (ParseDate(name, v, out var d, out _)) exdates.Add(d); continue; }
            ev[key] = value;
            ev[key + "#params"] = name;
        }
        return result;
    }

    static void Expand(Dictionary<string, string> ev, List<DateTime> exdates, List<CalendarEvent> result)
    {
        if (!ev.TryGetValue("DTSTART", out var ds) || !ParseDate(ev["DTSTART#params"], ds, out var start, out bool allDay)) return;
        DateTime end = start.AddHours(allDay ? 24 : 1);
        if (ev.TryGetValue("DTEND", out var de) && ParseDate(ev["DTEND#params"], de, out var e2, out _)) end = e2;
        if (ev.TryGetValue("STATUS", out var st) && st == "CANCELLED") return;
        string title = Unescape(ev.TryGetValue("SUMMARY", out var t) ? t : "(tanpa judul)");
        var len = end - start;
        var from = DateTime.Today.AddDays(-1);
        var to = DateTime.Today.AddDays(2);

        if (!ev.TryGetValue("RRULE", out var rule))
        {
            if (start < to && end > from) result.Add(new CalendarEvent(title, start, end, allDay));
            return;
        }
        // acara berulang (DAILY / WEEKLY / MONTHLY / YEARLY, INTERVAL, COUNT, UNTIL, BYDAY untuk mingguan)
        var parts = rule.Split(';').Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        string freq = parts.GetValueOrDefault("FREQ", "");
        int interval = int.TryParse(parts.GetValueOrDefault("INTERVAL", "1"), out var iv) ? Math.Max(1, iv) : 1;
        int count = int.TryParse(parts.GetValueOrDefault("COUNT", "0"), out var c) ? c : 0;
        DateTime until = DateTime.MaxValue;
        if (parts.TryGetValue("UNTIL", out var u) && ParseDate("UNTIL", u, out var ud, out _)) until = ud.Date.AddDays(1);
        var byDay = parts.TryGetValue("BYDAY", out var bd) && freq == "WEEKLY"
            ? bd.Split(',').Select(x => x.Length >= 2 ? x[^2..] : x).ToHashSet() : null;
        string[] codes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

        int n = 0;
        for (var occ = start; occ < to && occ < until && n < 5000; )
        {
            var candidates = new List<DateTime>();
            if (byDay != null)
            {
                var weekStart = occ.Date.AddDays(-(int)occ.DayOfWeek);
                for (int d = 0; d < 7; d++)
                {
                    var day = weekStart.AddDays(d) + start.TimeOfDay;
                    if (byDay.Contains(codes[d]) && day >= start) candidates.Add(day);
                }
            }
            else candidates.Add(occ);
            foreach (var cnd in candidates)
            {
                if (cnd >= until) break;
                n++;
                if (count > 0 && n > count) return;
                if (cnd + len > from && cnd < to && !exdates.Contains(cnd)) result.Add(new CalendarEvent(title, cnd, cnd + len, allDay));
            }
            occ = freq switch
            {
                "DAILY" => occ.AddDays(interval),
                "WEEKLY" => occ.AddDays(7 * interval),
                "MONTHLY" => occ.AddMonths(interval),
                "YEARLY" => occ.AddYears(interval),
                _ => DateTime.MaxValue,
            };
            if (occ == DateTime.MaxValue) break;
        }
    }

    /// <summary>20261104T090000Z (UTC), 20261104T090000 (waktu lokal), atau VALUE=DATE:20261104 (seharian).</summary>
    static bool ParseDate(string param, string v, out DateTime dt, out bool allDay)
    {
        v = v.Trim();
        allDay = v.Length == 8 || param.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) && !v.Contains('T');
        if (allDay) return DateTime.TryParseExact(v[..Math.Min(8, v.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);
        bool utc = v.EndsWith("Z");
        if (!DateTime.TryParseExact(v.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return false;
        if (utc) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
        return true;
    }

    static string Unescape(string s) => s.Replace("\\n", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
}
