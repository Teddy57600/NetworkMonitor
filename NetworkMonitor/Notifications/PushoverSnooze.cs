using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

static class PushoverSnooze
{
    public static DateTime GetSnoozeUntil(string key) => StateStore.GetSnoozeUntil(key);
    public static bool IsSnoozed(string key) => DateTime.UtcNow < GetSnoozeUntil(key);
    public static void ClearSnooze(string key) => StateStore.ClearSnooze(key);

    public static void StartWatching(string key, string receipt, ILogger logger, CancellationToken ct)
    {
        _ = Task.Run(() => WatchAsync(key, receipt, logger, ct), CancellationToken.None);
    }

    private static async Task WatchAsync(string key, string receipt, ILogger logger, CancellationToken ct)
    {
        var config = AppConfigProvider.Current;
        var token = config.PushoverToken;
        var url = $"https://api.pushover.net/1/receipts/{receipt}.json?token={token}";
        var deadline = DateTime.UtcNow.AddSeconds(300);

        using var client = new HttpClient();

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);

                var json = await client.GetStringAsync(url, ct);
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("acknowledged", out var ackProp) && ackProp.GetInt32() == 1)
                {
                    var snoozeUntil = DateTime.UtcNow.AddDays(config.SnoozeDays);
                    StateStore.SetSnooze(key, snoozeUntil);
                    logger.LogInformation("🔕 Notification acquittée [{Key}] — notifications suspendues jusqu'au {Until:dd/MM/yyyy HH:mm} UTC", key, snoozeUntil);
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Impossible de vérifier le reçu Pushover {Receipt}", receipt);
            }
        }
    }
}
