using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>Chat singkat dengan Gemini lewat Gemini API (API key milik pengguna sendiri).</summary>
public sealed class GeminiClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    public readonly List<(bool user, string text)> History = new();

    public bool HasKey => !string.IsNullOrWhiteSpace(Settings.Current.GeminiApiKey);

    public async Task<string> AskAsync(string prompt)
    {
        var s = Settings.Current;
        History.Add((true, prompt));
        if (History.Count > 20) History.RemoveRange(0, History.Count - 20);
        var contents = History.Select(h => new
        {
            role = h.user ? "user" : "model",
            parts = new[] { new { text = h.text } },
        }).ToArray();
        var body = JsonSerializer.Serialize(new
        {
            contents,
            systemInstruction = new
            {
                parts = new[] { new { text = "Jawab singkat dan jelas (maksimal beberapa paragraf pendek), dalam bahasa yang dipakai pengguna. Jangan gunakan tabel." } },
            },
        });
        string model = string.IsNullOrWhiteSpace(s.GeminiModel) ? "gemini-flash-latest" : s.GeminiModel.Trim();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent");
        req.Headers.Add("x-goog-api-key", s.GeminiApiKey.Trim());
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        string answer;
        try
        {
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!resp.IsSuccessStatusCode)
            {
                string msg = doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : resp.ReasonPhrase ?? "";
                History.RemoveAt(History.Count - 1);
                return "⚠ " + msg;
            }
            var sb = new StringBuilder();
            foreach (var cand in doc.RootElement.GetProperty("candidates").EnumerateArray().Take(1))
                if (cand.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
                    foreach (var part in parts.EnumerateArray())
                        if (part.TryGetProperty("text", out var t) && !(part.TryGetProperty("thought", out var th) && th.GetBoolean()))
                            sb.Append(t.GetString());
            answer = Clean(sb.ToString().Trim());
        }
        catch (Exception ex)
        {
            History.RemoveAt(History.Count - 1);
            return "⚠ " + ex.Message;
        }
        History.Add((false, answer));
        return answer;
    }

    /// <summary>Rapikan markdown sederhana supaya enak dibaca sebagai teks biasa.</summary>
    static string Clean(string s) =>
        s.Replace("**", "").Replace("__", "").Replace("```", "").Replace("\n* ", "\n• ").Replace("\n- ", "\n• ").Replace("### ", "").Replace("## ", "").Replace("# ", "");

    public void Clear() => History.Clear();
}
