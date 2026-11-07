using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>Cuaca saat ini dari Open-Meteo (gratis, tanpa login, tanpa API key). Diperbarui tiap 30 menit.</summary>
public sealed class WeatherService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly Dispatcher _ui;
    readonly DispatcherTimer _timer;
    string _cityResolved = "";
    double _lat, _lon;

    public double? Temperature { get; private set; }
    public int Code { get; private set; } = -1;
    public string City { get; private set; } = "";
    public event Action? Changed;
    /// <summary>Pemakai lain selain kalender (mis. widget cuaca di desktop). True = tetap ambil data cuaca.</summary>
    public static Func<bool>? ExtraDemand { get; set; }

    public WeatherService(Dispatcher ui)
    {
        _ui = ui;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(30) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public void Refresh()
    {
        var s = Settings.Current;
        string city = (s.WeatherCity ?? "").Trim();
        bool wanted = s.CalendarWeather || ExtraDemand?.Invoke() == true; // widget cuaca juga butuh data
        if (!wanted || city.Length == 0) { Temperature = null; Changed?.Invoke(); return; }
        Task.Run(async () =>
        {
            try
            {
                if (!city.Equals(_cityResolved, StringComparison.OrdinalIgnoreCase))
                {
                    string g = await Http.GetStringAsync(
                        $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=id&name={Uri.EscapeDataString(city)}").ConfigureAwait(false);
                    using var gd = JsonDocument.Parse(g);
                    if (!gd.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return;
                    var r = res[0];
                    _lat = r.GetProperty("latitude").GetDouble();
                    _lon = r.GetProperty("longitude").GetDouble();
                    _cityResolved = city;
                    City = r.GetProperty("name").GetString() ?? city;
                }
                string f = await Http.GetStringAsync(string.Format(CultureInfo.InvariantCulture,
                    "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,weather_code", _lat, _lon)).ConfigureAwait(false);
                using var fd = JsonDocument.Parse(f);
                var cur = fd.RootElement.GetProperty("current");
                double t = cur.GetProperty("temperature_2m").GetDouble();
                int code = cur.GetProperty("weather_code").GetInt32();
                _ui.BeginInvoke(new Action(() => { Temperature = t; Code = code; Changed?.Invoke(); }));
            }
            catch { }
        });
    }

    /// <summary>Ikon & keterangan dari kode cuaca WMO.</summary>
    public static (string icon, string text) Describe(int code) => code switch
    {
        0 => ("☀", "Cerah"),
        1 or 2 => ("🌤", "Cerah berawan"),
        3 => ("☁", "Berawan"),
        45 or 48 => ("🌫", "Berkabut"),
        >= 51 and <= 57 => ("🌦", "Gerimis"),
        >= 61 and <= 67 => ("🌧", "Hujan"),
        >= 71 and <= 77 => ("🌨", "Salju"),
        >= 80 and <= 82 => ("🌧", "Hujan lokal"),
        >= 95 => ("⛈", "Badai petir"),
        _ => ("🌡", ""),
    };
}
