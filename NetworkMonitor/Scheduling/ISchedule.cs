namespace NetworkMonitor;

interface ISchedule
{
    string Description { get; }
    Task WaitForNextAsync(CancellationToken ct);
}
