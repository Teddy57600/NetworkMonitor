using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

static class PushoverClient
{
    public static async Task SendAsync(string title, string message, int priority, ILogger logger, CancellationToken ct = default, string? sound = null, bool html = false)
    {
        if (PushoverSnooze.IsSnoozed)
        {
            logger.LogDebug("🔕 Notification ignorée (snooze actif jusqu'au {Until:dd/MM/yyyy HH:mm} UTC) : {Title}", PushoverSnooze.SnoozeUntil, title);
            return;
        }

        try
        {
            using var client = new HttpClient();

            var data = new Dictionary<string, string>
            {
                ["token"] = Environment.GetEnvironmentVariable("PUSHOVER_TOKEN")!,
                ["user"] = Environment.GetEnvironmentVariable("PUSHOVER_USER")!,
                ["title"] = title,
                ["message"] = message,
                ["priority"] = priority.ToString()
            };

            if (!string.IsNullOrWhiteSpace(sound))
                data["sound"] = sound;

            if (html)
                data["html"] = "1";

            if (priority == 2)
            {
                data["retry"] = "30";
                data["expire"] = "300";
            }

            var response = await client.PostAsync("https://api.pushover.net/1/messages.json",
                new FormUrlEncodedContent(data), ct);

            logger.LogDebug("Notification Pushover envoyée : {Title} (HTTP {StatusCode})", title, (int)response.StatusCode);

            if (priority == 2 && response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("receipt", out var receiptProp))
                {
                    var receipt = receiptProp.GetString();
                    if (!string.IsNullOrEmpty(receipt))
                        PushoverSnooze.StartWatching(receipt, logger, ct);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Échec de l'envoi de la notification Pushover : {Title}", title);
        }
    }
}
