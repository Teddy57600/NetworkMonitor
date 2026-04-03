using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

internal class Program
{
    static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddSimpleConsole(o => o.TimestampFormat = "dd/MM/yyyy HH:mm:ss ")
                .AddProvider(new FileLoggerProvider(Path.Combine(StateStore.DataDir, "logs"))));

        var logger = loggerFactory.CreateLogger<Program>();
        AppConfigProvider.RefreshIfChanged(logger);

        var config = AppConfigProvider.Current;
        var version = config.AppVersion;
        var shutdownReason = "arrêt normal";
        var configVersion = AppConfigProvider.Version;

        var monitors = CreatePingMonitors(config.PingTargets, loggerFactory);
        var tcpMonitors = CreateTcpMonitors(config.TcpTargets, loggerFactory);
        var schedule = BuildSchedule(config);

        LogActiveConfiguration(logger, config);

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
            BuildLifecycleMessage("✨ Service opérationnel", version, schedule.Description, monitors.Count, tcpMonitors.Count),
            0,
            null,
            logger,
            cts.Token,
            config.StartupSound,
            html: true);

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                AppConfigProvider.RefreshIfChanged(logger);
                if (configVersion != AppConfigProvider.Version)
                {
                    config = AppConfigProvider.Current;
                    version = config.AppVersion;
                    SyncPingMonitors(monitors, config.PingTargets, loggerFactory, logger);
                    SyncTcpMonitors(tcpMonitors, config.TcpTargets, loggerFactory, logger);
                    schedule = BuildSchedule(config);
                    configVersion = AppConfigProvider.Version;
                    LogActiveConfiguration(logger, config);
                }

                foreach (var monitor in monitors.Values)
                    await monitor.Check(cts.Token);

                foreach (var tcpMonitor in tcpMonitors.Values)
                    await tcpMonitor.Check(cts.Token);

                try
                {
                    await WaitForNextAsync(schedule, logger, configVersion, cts.Token);
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
            AppConfigProvider.RefreshIfChanged(logger);
            config = AppConfigProvider.Current;
            version = config.AppVersion;
            schedule = BuildSchedule(config);
            logger.LogInformation("NetworkMonitor arrêté. Motif : {Reason}", shutdownReason);
            await PushoverClient.SendAsync(
                "🛑 NetworkMonitor arrêté",
                BuildLifecycleMessage("🌙 Service arrêté", version, schedule.Description, monitors.Count, tcpMonitors.Count, shutdownReason),
                0,
                null,
                logger,
                CancellationToken.None,
                config.ShutdownSound,
                html: true);
        }
    }

    private static Dictionary<string, MonitorState> CreatePingMonitors(IReadOnlyList<string> ips, ILoggerFactory loggerFactory)
    {
        return ips.ToDictionary(
            ip => ip,
            ip => new MonitorState(ip, loggerFactory.CreateLogger<MonitorState>()),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, TcpPortMonitorState> CreateTcpMonitors(IReadOnlyList<TcpTargetConfig> targets, ILoggerFactory loggerFactory)
    {
        return targets.ToDictionary(
            target => BuildTcpKey(target.Host, target.Port),
            target => new TcpPortMonitorState(target.Host, target.Port, loggerFactory.CreateLogger<TcpPortMonitorState>()),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void SyncPingMonitors(Dictionary<string, MonitorState> monitors, IReadOnlyList<string> configuredIps, ILoggerFactory loggerFactory, ILogger logger)
    {
        var desiredIps = new HashSet<string>(configuredIps, StringComparer.OrdinalIgnoreCase);

        foreach (var ip in monitors.Keys.Except(desiredIps, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            monitors.Remove(ip);
            logger.LogInformation("Monitor ping supprimé suite au rechargement de la configuration : {Ip}", ip);
        }

        foreach (var ip in desiredIps)
        {
            if (monitors.ContainsKey(ip))
                continue;

            monitors[ip] = new MonitorState(ip, loggerFactory.CreateLogger<MonitorState>());
            logger.LogInformation("Monitor ping ajouté suite au rechargement de la configuration : {Ip}", ip);
        }
    }

    private static void SyncTcpMonitors(Dictionary<string, TcpPortMonitorState> monitors, IReadOnlyList<TcpTargetConfig> configuredTargets, ILoggerFactory loggerFactory, ILogger logger)
    {
        var desiredTargets = configuredTargets.ToDictionary(
            target => BuildTcpKey(target.Host, target.Port),
            StringComparer.OrdinalIgnoreCase);

        foreach (var key in monitors.Keys.Except(desiredTargets.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            monitors.Remove(key);
            logger.LogInformation("Monitor TCP supprimé suite au rechargement de la configuration : {Target}", key);
        }

        foreach (var target in desiredTargets)
        {
            if (monitors.ContainsKey(target.Key))
                continue;

            monitors[target.Key] = new TcpPortMonitorState(target.Value.Host, target.Value.Port, loggerFactory.CreateLogger<TcpPortMonitorState>());
            logger.LogInformation("Monitor TCP ajouté suite au rechargement de la configuration : {Target}", target.Key);
        }
    }

    private static ISchedule BuildSchedule(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ScheduleCron))
            return new CronSchedule(config.ScheduleCron);

        return new IntervalSchedule(config.ScheduleIntervalSeconds);
    }

    private static async Task WaitForNextAsync(ISchedule schedule, ILogger logger, int configVersion, CancellationToken ct)
    {
        var next = schedule.GetNextOccurrence(DateTimeOffset.Now);
        if (next is null)
            return;

        while (!ct.IsCancellationRequested)
        {
            AppConfigProvider.RefreshIfChanged(logger);
            if (configVersion != AppConfigProvider.Version)
                return;

            var remaining = next.Value - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero)
                return;

            await AppConfigProvider.WaitForPotentialChangeAsync(logger, remaining, ct);
        }
    }

    private static void LogActiveConfiguration(ILogger logger, AppConfig config)
    {
        if (config.PingTargets.Count == 0)
            logger.LogWarning("Aucune IP à monitorer n'est configurée.");

        if (config.TcpTargets.Count == 0)
            logger.LogWarning("Aucun endpoint host:port à monitorer n'est configuré.");

        logger.LogInformation(
            "Configuration active — YAML: {YamlPath} — Ping: {PingCount} — TCP: {TcpCount} — Schedule: {Schedule}",
            AppConfigProvider.ConfigPath,
            config.PingTargets.Count,
            config.TcpTargets.Count,
            BuildSchedule(config).Description);
    }

    private static string BuildLifecycleMessage(string status, string version, string scheduleDescription, int ipCount, int tcpCount, string? shutdownReason = null)
    {
        var reasonLine = string.IsNullOrWhiteSpace(shutdownReason)
            ? string.Empty
            : $"\n📍 Motif : {shutdownReason}";

        return $"<b>{status}</b>\n📦 Version : {version}\n🕒 Rythme : {scheduleDescription}\n🖧 IP surveillées : {ipCount}\n🔌 Ports TCP surveillés : {tcpCount}{reasonLine}";
    }

    private static string BuildTcpKey(string host, int port) => $"{host}:{port}";

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