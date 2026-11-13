using System.Net.Http.Json;
using System.Text;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// Sends alerts to your phone / Discord: a Discord channel webhook and/or ntfy (free push app for Android/iPhone,
/// https://ntfy.sh - subscribe to your topic in the app). Used for shinies and for problems found by the system check.
/// </summary>
public static class NotifyService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<string, DateTime> Recent = new();

    public static bool Configured
    {
        get
        {
            var s = AppSettings.Current;
            return !string.IsNullOrWhiteSpace(s.DiscordWebhookUrl) || !string.IsNullOrWhiteSpace(s.NtfyTopic);
        }
    }

    /// <summary>Sends to every configured service. Never throws; returns a short result text (for the Test button).</summary>
    public static async Task<string> SendAsync(string title, string message, bool important = false, string? imagePath = null)
    {
        var s = AppSettings.Current;
        if (!Configured) return "No notification service is set up (Settings → Notifications).";
        // don't spam the same message (e.g. a problem that keeps coming back)
        var key = title + "|" + message;
        lock (Recent)
        {
            if (Recent.TryGetValue(key, out var t) && (DateTime.Now - t).TotalMinutes < 5) return "Skipped (sent less than 5 minutes ago).";
            Recent[key] = DateTime.Now;
        }
        var results = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.DiscordWebhookUrl))
        {
            try
            {
                HttpResponseMessage r;
                var payload = new { username = "ShinyForge", content = (important && s.DiscordMentionEveryone ? "@everyone " : "") + $"**{title}**\n{message}" };
                if (imagePath != null && File.Exists(imagePath))
                {
                    using var form = new MultipartFormDataContent();
                    form.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload_json");
                    form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(imagePath)), "file", Path.GetFileName(imagePath));
                    r = await Http.PostAsync(s.DiscordWebhookUrl.Trim(), form);
                }
                else
                    r = await Http.PostAsJsonAsync(s.DiscordWebhookUrl.Trim(), payload);
                results.Add(r.IsSuccessStatusCode ? "Discord: sent" : $"Discord: error {(int)r.StatusCode}");
            }
            catch (Exception e) { results.Add("Discord: " + e.Message); }
        }
        if (!string.IsNullOrWhiteSpace(s.NtfyTopic))
        {
            try
            {
                var server = string.IsNullOrWhiteSpace(s.NtfyServer) ? "https://ntfy.sh" : s.NtfyServer.TrimEnd('/');
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{server}/{Uri.EscapeDataString(s.NtfyTopic.Trim())}")
                {
                    Content = new StringContent(message, Encoding.UTF8),
                };
                req.Headers.Add("Title", Ascii(title));
                req.Headers.Add("Priority", important ? "urgent" : "default");
                req.Headers.Add("Tags", important ? "sparkles" : "warning");
                var r = await Http.SendAsync(req);
                results.Add(r.IsSuccessStatusCode ? "ntfy: sent" : $"ntfy: error {(int)r.StatusCode}");
            }
            catch (Exception e) { results.Add("ntfy: " + e.Message); }
        }
        var text = string.Join(", ", results);
        AppPaths.Log($"notify '{title}': {text}");
        return text;
    }

    /// <summary>Fire-and-forget version.</summary>
    public static void Send(string title, string message, bool important = false, string? imagePath = null) =>
        _ = Task.Run(() => SendAsync(title, message, important, imagePath));

    private static string Ascii(string s) => new(s.Select(c => c < 128 ? c : c == 'é' ? 'e' : '*').ToArray());

    /// <summary>Hooks the system check: problems are sent too (if enabled in Settings).</summary>
    public static void Attach()
    {
        HealthMonitor.Problem += item =>
        {
            if (!AppSettings.Current.NotifyProblems || item.State == HealthState.Ok) return;
            if (item.State == HealthState.Warning && !AppSettings.Current.NotifyWarnings) return;
            Send($"ShinyForge {(item.State == HealthState.Error ? "problem" : "warning")}: {item.Feature}", item.Message);
        };
    }
}
