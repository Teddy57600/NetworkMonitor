using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

internal class Program
{
    static async Task Main(string[] args)
    {
        var ips = new[] {
            "1.1.1.1",
            "89.187.7.222"
        };

        using var loggerFactory = LoggerFactory.Create(builder =>
            builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddSimpleConsole(o => o.TimestampFormat = "dd/MM/yyyy HH:mm:ss ")
                .AddProvider(new FileLoggerProvider(Path.Combine(StateStore.DataDir, "logs"))));

        var logger = loggerFactory.CreateLogger<Program>();
        var monitors = ips.ToDictionary(
            ip => ip,
            ip => new MonitorState(ip, loggerFactory.CreateLogger<MonitorState>()));
        var tcpMonitors = ParseTcpTargets()
            .Select(t => new TcpPortMonitorState(t.Host, t.Port, loggerFactory.CreateLogger<TcpPortMonitorState>()))
            .ToList();
        var schedule = BuildSchedule();

        logger.LogInformation("NetworkMonitor démarré — {Schedule}", schedule.Description);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        while (!cts.Token.IsCancellationRequested)
        {
            foreach (var monitor in monitors.Values)
                await monitor.Check(cts.Token);

            foreach (var tcpMonitor in tcpMonitors)
                await tcpMonitor.Check(cts.Token);

            try
            {
                await schedule.WaitForNextAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("NetworkMonitor arrêté.");
    }

    private static IReadOnlyList<(string Host, int Port)> ParseTcpTargets()
    {
        var raw = Environment.GetEnvironmentVariable("TCP_TARGETS");
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var targets = new List<(string, int)>();
        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var lastColon = entry.LastIndexOf(':');
            if (lastColon > 0 && int.TryParse(entry[(lastColon + 1)..], out var port))
                targets.Add((entry[..lastColon], port));
        }
        return targets;
    }

    private static ISchedule BuildSchedule()
    {
        var cronExpr = Environment.GetEnvironmentVariable("SCHEDULE_CRON");
        if (!string.IsNullOrWhiteSpace(cronExpr))
            return new CronSchedule(cronExpr);

        var intervalStr = Environment.GetEnvironmentVariable("SCHEDULE_INTERVAL_SECONDS");
        int seconds = int.TryParse(intervalStr, out var s) && s > 0 ? s : 10;
        return new IntervalSchedule(seconds);
    }
}