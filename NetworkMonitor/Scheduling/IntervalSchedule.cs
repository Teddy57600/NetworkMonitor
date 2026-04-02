namespace NetworkMonitor;

class IntervalSchedule(int seconds) : ISchedule
{
    public string Description => $"intervalle toutes les {seconds}s";

    public Task WaitForNextAsync(CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(seconds), ct);
}
