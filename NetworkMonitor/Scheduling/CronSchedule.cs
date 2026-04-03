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

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset from) =>
        _cron.GetNextOccurrence(from, TimeZoneInfo.Local);
}
