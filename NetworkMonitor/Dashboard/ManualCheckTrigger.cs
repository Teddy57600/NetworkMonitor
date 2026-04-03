namespace NetworkMonitor;

sealed class ManualCheckTrigger
{
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private int _requested;

    public void Request()
    {
        Interlocked.Exchange(ref _requested, 1);

        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _requested, 0) == 1)
            return true;

        var signaled = await _signal.WaitAsync(timeout, ct);
        if (!signaled)
            return false;

        Interlocked.Exchange(ref _requested, 0);
        return true;
    }
}
