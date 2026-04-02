using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

internal class Program
{
    private const string DefaultStartupSound = "cosmic";
    private const string DefaultShutdownSound = "falling";

    static async Task Main(string[] args)
    {
        var ips = ParsePingTargets();
        var version = GetApplicationVersion();
        var shutdownReason = "arrêt normal";

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

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdownReason = "arrêt manuel demandé depuis la console";
            cts.Cancel();
        };
        using var sigTermRegistration = RegisterShutdownSignal(PosixSignal.SIGTERM, "arrêt Docker / système (SIGTERM)", cts, logger, reason => shutdownReason = reason);
        using var sigIntRegistration = RegisterShutdownSignal(PosixSignal.SIGINT, "interruption terminal (SIGINT)", cts, logger, reason => shutdownReason = reason);

        logger.LogInformation("NetworkMonitor démarré — version {Version} — {Schedule}", version, schedule.Description);
        await PushoverClient.SendAsync(
            "🚀 NetworkMonitor démarré",
            BuildLifecycleMessage("✨ Service opérationnel", version, schedule.Description, ips.Count, tcpMonitors.Count),
            0,
            logger,
            cts.Token,
            GetStartupPushoverSound(),
            html: true);

        try
        {
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
        }
        catch (Exception ex)
        {
            shutdownReason = $"erreur inattendue : {ex.GetType().Name}";
            logger.LogError(ex, "Arrêt du programme suite à une exception non gérée.");
            throw;
        }
        finally
        {
            logger.LogInformation("NetworkMonitor arrêté. Motif : {Reason}", shutdownReason);
            await PushoverClient.SendAsync(
                "🛑 NetworkMonitor arrêté",
                BuildLifecycleMessage("🌙 Service arrêté", version, schedule.Description, ips.Count, tcpMonitors.Count, shutdownReason),
                0,
                logger,
                CancellationToken.None,
                GetShutdownPushoverSound(),
                html: true);
        }
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

    private static string BuildLifecycleMessage(string status, string version, string scheduleDescription, int ipCount, int tcpCount, string? shutdownReason = null)
    {
        var reasonLine = string.IsNullOrWhiteSpace(shutdownReason)
            ? string.Empty
            : $"\n📍 Motif : {shutdownReason}";

        return $"<b>{status}</b>\n📦 Version : {version}\n🕒 Rythme : {scheduleDescription}\n🖧 IP surveillées : {ipCount}\n🔌 Ports TCP surveillés : {tcpCount}{reasonLine}";
    }

    private static string GetStartupPushoverSound()
    {
        var sound = Environment.GetEnvironmentVariable("PUSHOVER_STARTUP_SOUND");
        return string.IsNullOrWhiteSpace(sound) ? DefaultStartupSound : sound;
    }

    private static string GetShutdownPushoverSound()
    {
        var sound = Environment.GetEnvironmentVariable("PUSHOVER_SHUTDOWN_SOUND");
        return string.IsNullOrWhiteSpace(sound) ? DefaultShutdownSound : sound;
    }

    private static PosixSignalRegistration? RegisterShutdownSignal(PosixSignal signal, string reason, CancellationTokenSource cts, ILogger logger, Action<string> setShutdownReason)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return null;

        return PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;

            if (cts.IsCancellationRequested)
                return;

            setShutdownReason(reason);
            logger.LogInformation("Signal {Signal} reçu, arrêt propre en cours. Motif : {Reason}", context.Signal, reason);
            cts.Cancel();
        });
    }
}