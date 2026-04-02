using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

static class PushoverSnooze
{
    private static DateTime _snoozeUntil = StateStore.SnoozeUntil;

    public static DateTime SnoozeUntil => _snoozeUntil;
    public static bool IsSnoozed => DateTime.UtcNow < _snoozeUntil;

    private static int SnoozeDays =>
        int.TryParse(Environment.GetEnvironmentVariable("SNOOZE_DAYS"), out var d) && d > 0 ? d : 1;

    public static void StartWatching(string receipt, ILogger logger, CancellationToken ct)
    {
        _ = Task.Run(() => WatchAsync(receipt, logger, ct), CancellationToken.None);
    }

    private static async Task WatchAsync(string receipt, ILogger logger, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("PUSHOVER_TOKEN")!;
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
                    _snoozeUntil = DateTime.UtcNow.AddDays(SnoozeDays);
                    StateStore.SetSnooze(_snoozeUntil);
                    logger.LogInformation("🔕 Notification acquittée — notifications suspendues jusqu'au {Until:dd/MM/yyyy HH:mm} UTC", _snoozeUntil);
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
