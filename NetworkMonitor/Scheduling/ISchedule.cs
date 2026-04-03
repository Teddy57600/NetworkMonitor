namespace NetworkMonitor;

interface ISchedule
{
    string Description { get; }
    DateTimeOffset? GetNextOccurrence(DateTimeOffset from);
}
