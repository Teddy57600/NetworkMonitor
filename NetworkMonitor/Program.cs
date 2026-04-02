using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

internal class Program
{
    static async Task Main(string[] args)
    {
        var ips = ParsePingTargets();
        var version = GetApplicationVersion();

        using var loggerFactory = LoggerFactory.Create(builder =>
            builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddSimpleConsole(o => o.TimestampFormat = "dd/MM/yyyy HH:mm:ss ")
                .AddProvider(new FileLoggerProvider(Path.Combine(StateStore.DataDir, "logs"))));

        var logger = loggerFactory.CreateLogger<Program>();
        if (ips.Count == 0)
            logger.LogWarning("Aucune IP à monitorer n'est configurée via PING_TARGETS.");

        var monitors = ips.ToDictionary(
            ip => ip,
            ip => new MonitorState(ip, loggerFactory.CreateLogger<MonitorState>()));
        
        var tcpMonitors = ParseTcpTargets()
            .Select(t => new TcpPortMonitorState(t.Host, t.Port, loggerFactory.CreateLogger<TcpPortMonitorState>()))
            .ToList();
        if (tcpMonitors.Count == 0)
            logger.LogWarning("Aucun endpoint host:port à monitorer n'est configuré via TCP_TARGETS.");

        var schedule = BuildSchedule();

        logger.LogInformation("NetworkMonitor démarré — version {Version} — {Schedule}", version, schedule.Description);

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

    private static IReadOnlyList<string> ParsePingTargets()
    {
        var raw = Environment.GetEnvironmentVariable("PING_TARGETS");
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static string GetApplicationVersion()
    {
        var version = Environment.GetEnvironmentVariable("APP_VERSION");
        return string.IsNullOrWhiteSpace(version) ? "inconnue" : version;
    }
}