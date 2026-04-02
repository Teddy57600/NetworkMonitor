using Cronos;

namespace NetworkMonitor;

class CronSchedule : ISchedule
{
    private readonly CronExpression _cron;
    private readonly string _expression;

    public CronSchedule(string expression)
    {
        _expression = expression;
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _cron = parts.Length == 6
            ? CronExpression.Parse(expression, CronFormat.IncludeSeconds)
            : CronExpression.Parse(expression);
    }

    public string Description => CronDescription.ToFrench(_expression);

    public async Task WaitForNextAsync(CancellationToken ct)
    {
        var next = _cron.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        if (next is null) return;

        var delay = next.Value - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);
    }
}
