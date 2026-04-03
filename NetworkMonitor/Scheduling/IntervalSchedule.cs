namespace NetworkMonitor;

class IntervalSchedule(int seconds) : ISchedule
{
    public string Description => $"intervalle toutes les {seconds}s";

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset from) =>
        from.AddSeconds(seconds);
}
