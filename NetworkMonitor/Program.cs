using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

internal class Program
{
    static async Task Main(string[] args)
    {
        var startedAt = DateTimeOffset.Now;
        var monitorCollectionsLock = new object();

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
        var manualCheckTrigger = new ManualCheckTrigger();
        using var cts = new CancellationTokenSource();

        var monitors = CreatePingMonitors(config.PingTargets, loggerFactory);
        var tcpMonitors = CreateTcpMonitors(config.TcpTargets, loggerFactory);
        var httpMonitors = CreateHttpMonitors(config.HttpTargets, loggerFactory);
        var dnsMonitors = CreateDnsMonitors(config.DnsTargets, loggerFactory);
        var tlsMonitors = CreateTlsMonitors(config.TlsTargets, loggerFactory);
        var dnsRecordMonitors = CreateDnsRecordMonitors(config.DnsRecordTargets, loggerFactory);
        var schedule = BuildSchedule(config);
        var dashboardLogger = loggerFactory.CreateLogger("DashboardWeb");
        var dashboardApp = await EnsureDashboardStateAsync(
            null,
            config.DashboardEnabled,
            () => BuildDashboardSnapshot(startedAt, monitorCollectionsLock, monitors, tcpMonitors, httpMonitors, dnsMonitors, tlsMonitors, dnsRecordMonitors),
            manualCheckTrigger,
            dashboardLogger,
            cts.Token);

        LogActiveConfiguration(logger, config);

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
            BuildLifecycleMessage("✨ Service opérationnel", version, schedule.Description, monitors.Count, tcpMonitors.Count, httpMonitors.Count, dnsMonitors.Count, tlsMonitors.Count, dnsRecordMonitors.Count),
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
                    lock (monitorCollectionsLock)
                    {
                        SyncPingMonitors(monitors, config.PingTargets, loggerFactory, logger);
                        SyncTcpMonitors(tcpMonitors, config.TcpTargets, loggerFactory, logger);
                        SyncHttpMonitors(httpMonitors, config.HttpTargets, loggerFactory, logger);
                        SyncDnsMonitors(dnsMonitors, config.DnsTargets, loggerFactory, logger);
                        SyncTlsMonitors(tlsMonitors, config.TlsTargets, loggerFactory, logger);
                        SyncDnsRecordMonitors(dnsRecordMonitors, config.DnsRecordTargets, loggerFactory, logger);
                    }
                    schedule = BuildSchedule(config);
                    dashboardApp = await EnsureDashboardStateAsync(
                        dashboardApp,
                        config.DashboardEnabled,
                        () => BuildDashboardSnapshot(startedAt, monitorCollectionsLock, monitors, tcpMonitors, httpMonitors, dnsMonitors, tlsMonitors, dnsRecordMonitors),
                        manualCheckTrigger,
                        dashboardLogger,
                        cts.Token);
                    configVersion = AppConfigProvider.Version;
                    LogActiveConfiguration(logger, config);
                }

                MonitorState[] pingMonitorBatch;
                TcpPortMonitorState[] tcpMonitorBatch;
                HttpEndpointMonitorState[] httpMonitorBatch;
                DnsMonitorState[] dnsMonitorBatch;
                TlsMonitorState[] tlsMonitorBatch;
                DnsRecordMonitorState[] dnsRecordMonitorBatch;
                lock (monitorCollectionsLock)
                {
                    pingMonitorBatch = monitors.Values.ToArray();
                    tcpMonitorBatch = tcpMonitors.Values.ToArray();
                    httpMonitorBatch = httpMonitors.Values.ToArray();
                    dnsMonitorBatch = dnsMonitors.Values.ToArray();
                    tlsMonitorBatch = tlsMonitors.Values.ToArray();
                    dnsRecordMonitorBatch = dnsRecordMonitors.Values.ToArray();
                }

                foreach (var monitor in pingMonitorBatch)
                    await monitor.Check(cts.Token);

                foreach (var tcpMonitor in tcpMonitorBatch)
                    await tcpMonitor.Check(cts.Token);

                foreach (var httpMonitor in httpMonitorBatch)
                    await httpMonitor.Check(cts.Token);

                foreach (var dnsMonitor in dnsMonitorBatch)
                    await dnsMonitor.Check(cts.Token);

                foreach (var tlsMonitor in tlsMonitorBatch)
                    await tlsMonitor.Check(cts.Token);

                foreach (var dnsRecordMonitor in dnsRecordMonitorBatch)
                    await dnsRecordMonitor.Check(cts.Token);

                try
                {
                    await WaitForNextAsync(schedule, logger, configVersion, manualCheckTrigger, cts.Token);
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
            if (dashboardApp is not null)
            {
                await dashboardApp.StopAsync(CancellationToken.None);
                await dashboardApp.DisposeAsync();
            }
            AppConfigProvider.RefreshIfChanged(logger);
            config = AppConfigProvider.Current;
            version = config.AppVersion;
            schedule = BuildSchedule(config);
            logger.LogInformation("NetworkMonitor arrêté. Motif : {Reason}", shutdownReason);
            await PushoverClient.SendAsync(
                "🛑 NetworkMonitor arrêté",
                BuildLifecycleMessage("🌙 Service arrêté", version, schedule.Description, monitors.Count, tcpMonitors.Count, httpMonitors.Count, dnsMonitors.Count, tlsMonitors.Count, dnsRecordMonitors.Count, shutdownReason),
                0,
                null,
                logger,
                CancellationToken.None,
                config.ShutdownSound,
                html: true);
        }
    }

    private static DashboardSnapshot BuildDashboardSnapshot(DateTimeOffset startedAt, object collectionsLock, Dictionary<string, MonitorState> monitors, Dictionary<string, TcpPortMonitorState> tcpMonitors, Dictionary<string, HttpEndpointMonitorState> httpMonitors, Dictionary<string, DnsMonitorState> dnsMonitors, Dictionary<string, TlsMonitorState> tlsMonitors, Dictionary<string, DnsRecordMonitorState> dnsRecordMonitors)
    {
        MonitorState[] pingMonitors;
        TcpPortMonitorState[] tcpMonitorStates;
        HttpEndpointMonitorState[] httpMonitorStates;
        DnsMonitorState[] dnsMonitorStates;
        TlsMonitorState[] tlsMonitorStates;
        DnsRecordMonitorState[] dnsRecordMonitorStates;
        lock (collectionsLock)
        {
            pingMonitors = monitors.Values.ToArray();
            tcpMonitorStates = tcpMonitors.Values.ToArray();
            httpMonitorStates = httpMonitors.Values.ToArray();
            dnsMonitorStates = dnsMonitors.Values.ToArray();
            tlsMonitorStates = tlsMonitors.Values.ToArray();
            dnsRecordMonitorStates = dnsRecordMonitors.Values.ToArray();
        }

        var pingSnapshots = pingMonitors
            .Select(monitor => monitor.GetDashboardSnapshot())
            .OrderBy(snapshot => snapshot.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var tcpSnapshots = tcpMonitorStates
            .Select(monitor => monitor.GetDashboardSnapshot())
            .OrderBy(snapshot => snapshot.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var httpSnapshots = httpMonitorStates
            .Select(monitor => monitor.GetDashboardSnapshot())
            .OrderBy(snapshot => snapshot.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var dnsSnapshots = dnsMonitorStates
            .Select(monitor => monitor.GetDashboardSnapshot())
            .OrderBy(snapshot => snapshot.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var tlsSnapshots = tlsMonitorStates
            .Select(monitor => monitor.GetDashboardSnapshot())
            .OrderBy(snapshot => snapshot.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var dnsRecordSnapshots = dnsRecordMonitorStates
            .Select(monitor => monitor.GetDashboardSnapshot())
            .OrderBy(snapshot => snapshot.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var allSnapshots = pingSnapshots.Concat(tcpSnapshots).Concat(httpSnapshots).Concat(dnsSnapshots).Concat(tlsSnapshots).Concat(dnsRecordSnapshots).ToArray();
        var recentIncidents = StateStore.GetRecentIncidents(20)
            .Select(incident => new DashboardIncidentSnapshot
            {
                Id = incident.Id,
                Key = incident.Key,
                Type = incident.Type,
                DisplayName = incident.DisplayName,
                StartedAt = incident.StartedAt,
                ResolvedAt = incident.ResolvedAt,
                IsOpen = incident.ResolvedAt is null
            })
            .ToArray();
        var config = AppConfigProvider.Current;

        return new DashboardSnapshot
        {
            GeneratedAt = DateTimeOffset.Now,
            StartedAt = startedAt,
            Version = config.AppVersion,
            Schedule = BuildSchedule(config).Description,
            DefaultSnoozeDays = config.SnoozeDays,
            ConfigPath = AppConfigProvider.ConfigPath,
            ConfigVersion = AppConfigProvider.Version,
            TimeZone = TimeZoneInfo.Local.Id,
            RefreshIntervalSeconds = config.DashboardRefreshSeconds,
            Summary = new DashboardSummary
            {
                Total = allSnapshots.Length,
                Up = allSnapshots.Count(snapshot => !snapshot.IsDown),
                Down = allSnapshots.Count(snapshot => snapshot.IsDown),
                Snoozed = allSnapshots.Count(snapshot => snapshot.SnoozeUntil.HasValue)
            },
            PingMonitors = pingSnapshots,
            TcpMonitors = tcpSnapshots,
            HttpMonitors = httpSnapshots,
            DnsMonitors = dnsSnapshots,
            TlsMonitors = tlsSnapshots,
            DnsRecordMonitors = dnsRecordSnapshots,
            RecentIncidents = recentIncidents
        };
    }

    private static async Task<WebApplication?> EnsureDashboardStateAsync(
        WebApplication? dashboardApp,
        bool dashboardEnabled,
        Func<DashboardSnapshot> snapshotFactory,
        ManualCheckTrigger manualCheckTrigger,
        ILogger logger,
        CancellationToken ct)
    {
        if (dashboardEnabled)
        {
            if (dashboardApp is not null)
                return dashboardApp;

            logger.LogInformation("Activation du tableau de bord web.");
            return await DashboardWebServer.StartAsync(snapshotFactory, manualCheckTrigger, logger, ct);
        }

        if (dashboardApp is null)
            return null;

        logger.LogInformation("Désactivation du tableau de bord web.");
        await dashboardApp.StopAsync(CancellationToken.None);
        await dashboardApp.DisposeAsync();
        return null;
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

    private static Dictionary<string, HttpEndpointMonitorState> CreateHttpMonitors(IReadOnlyList<HttpTargetConfig> targets, ILoggerFactory loggerFactory)
    {
        return targets.ToDictionary(
            target => target.Url,
            target => new HttpEndpointMonitorState(target, loggerFactory.CreateLogger<HttpEndpointMonitorState>()),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, DnsMonitorState> CreateDnsMonitors(IReadOnlyList<DnsTargetConfig> targets, ILoggerFactory loggerFactory)
    {
        return targets.ToDictionary(
            BuildDnsKey,
            target => new DnsMonitorState(target, loggerFactory.CreateLogger<DnsMonitorState>()),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, TlsMonitorState> CreateTlsMonitors(IReadOnlyList<TlsTargetConfig> targets, ILoggerFactory loggerFactory)
    {
        return targets.ToDictionary(
            target => BuildTlsKey(target.Host, target.Port),
            target => new TlsMonitorState(target, loggerFactory.CreateLogger<TlsMonitorState>()),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, DnsRecordMonitorState> CreateDnsRecordMonitors(IReadOnlyList<DnsRecordTargetConfig> targets, ILoggerFactory loggerFactory)
    {
        return targets.ToDictionary(
            BuildDnsRecordKey,
            target => new DnsRecordMonitorState(target, loggerFactory.CreateLogger<DnsRecordMonitorState>()),
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

    private static void SyncHttpMonitors(Dictionary<string, HttpEndpointMonitorState> monitors, IReadOnlyList<HttpTargetConfig> configuredTargets, ILoggerFactory loggerFactory, ILogger logger)
    {
        var desiredTargets = configuredTargets.ToDictionary(target => target.Url, StringComparer.OrdinalIgnoreCase);

        foreach (var key in monitors.Keys.Except(desiredTargets.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            monitors.Remove(key);
            logger.LogInformation("Monitor HTTP supprimé suite au rechargement de la configuration : {Target}", key);
        }

        foreach (var target in desiredTargets)
        {
            if (monitors.ContainsKey(target.Key))
            {
                monitors[target.Key] = new HttpEndpointMonitorState(target.Value, loggerFactory.CreateLogger<HttpEndpointMonitorState>());
                logger.LogInformation("Monitor HTTP mis à jour suite au rechargement de la configuration : {Target}", target.Key);
                continue;
            }

            monitors[target.Key] = new HttpEndpointMonitorState(target.Value, loggerFactory.CreateLogger<HttpEndpointMonitorState>());
            logger.LogInformation("Monitor HTTP ajouté suite au rechargement de la configuration : {Target}", target.Key);
        }
    }

    private static void SyncDnsMonitors(Dictionary<string, DnsMonitorState> monitors, IReadOnlyList<DnsTargetConfig> configuredTargets, ILoggerFactory loggerFactory, ILogger logger)
    {
        var desiredTargets = configuredTargets.ToDictionary(BuildDnsKey, StringComparer.OrdinalIgnoreCase);

        foreach (var key in monitors.Keys.Except(desiredTargets.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            monitors.Remove(key);
            logger.LogInformation("Monitor DNS supprimé suite au rechargement de la configuration : {Target}", key);
        }

        foreach (var target in desiredTargets)
        {
            if (monitors.ContainsKey(target.Key))
            {
                monitors[target.Key] = new DnsMonitorState(target.Value, loggerFactory.CreateLogger<DnsMonitorState>());
                logger.LogInformation("Monitor DNS mis à jour suite au rechargement de la configuration : {Target}", target.Key);
                continue;
            }

            monitors[target.Key] = new DnsMonitorState(target.Value, loggerFactory.CreateLogger<DnsMonitorState>());
            logger.LogInformation("Monitor DNS ajouté suite au rechargement de la configuration : {Target}", target.Key);
        }
    }

    private static void SyncTlsMonitors(Dictionary<string, TlsMonitorState> monitors, IReadOnlyList<TlsTargetConfig> configuredTargets, ILoggerFactory loggerFactory, ILogger logger)
    {
        var desiredTargets = configuredTargets.ToDictionary(target => BuildTlsKey(target.Host, target.Port), StringComparer.OrdinalIgnoreCase);

        foreach (var key in monitors.Keys.Except(desiredTargets.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            monitors.Remove(key);
            logger.LogInformation("Monitor TLS supprimé suite au rechargement de la configuration : {Target}", key);
        }

        foreach (var target in desiredTargets)
        {
            if (monitors.ContainsKey(target.Key))
            {
                monitors[target.Key] = new TlsMonitorState(target.Value, loggerFactory.CreateLogger<TlsMonitorState>());
                logger.LogInformation("Monitor TLS mis à jour suite au rechargement de la configuration : {Target}", target.Key);
                continue;
            }

            monitors[target.Key] = new TlsMonitorState(target.Value, loggerFactory.CreateLogger<TlsMonitorState>());
            logger.LogInformation("Monitor TLS ajouté suite au rechargement de la configuration : {Target}", target.Key);
        }
    }

    private static void SyncDnsRecordMonitors(Dictionary<string, DnsRecordMonitorState> monitors, IReadOnlyList<DnsRecordTargetConfig> configuredTargets, ILoggerFactory loggerFactory, ILogger logger)
    {
        var desiredTargets = configuredTargets.ToDictionary(BuildDnsRecordKey, StringComparer.OrdinalIgnoreCase);

        foreach (var key in monitors.Keys.Except(desiredTargets.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            monitors.Remove(key);
            logger.LogInformation("Monitor DNS record supprimé suite au rechargement de la configuration : {Target}", key);
        }

        foreach (var target in desiredTargets)
        {
            if (monitors.ContainsKey(target.Key))
            {
                monitors[target.Key] = new DnsRecordMonitorState(target.Value, loggerFactory.CreateLogger<DnsRecordMonitorState>());
                logger.LogInformation("Monitor DNS record mis à jour suite au rechargement de la configuration : {Target}", target.Key);
                continue;
            }

            monitors[target.Key] = new DnsRecordMonitorState(target.Value, loggerFactory.CreateLogger<DnsRecordMonitorState>());
            logger.LogInformation("Monitor DNS record ajouté suite au rechargement de la configuration : {Target}", target.Key);
        }
    }

    private static ISchedule BuildSchedule(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ScheduleCron))
            return new CronSchedule(config.ScheduleCron);

        return new IntervalSchedule(config.ScheduleIntervalSeconds);
    }

    private static async Task WaitForNextAsync(ISchedule schedule, ILogger logger, int configVersion, ManualCheckTrigger manualCheckTrigger, CancellationToken ct)
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

            var delay = remaining > TimeSpan.FromSeconds(30)
                ? TimeSpan.FromSeconds(30)
                : remaining;

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var configWait = AppConfigProvider.WaitForPotentialChangeAsync(logger, delay, waitCts.Token);
            var manualCheckWait = manualCheckTrigger.WaitAsync(delay, waitCts.Token);
            var completedTask = await Task.WhenAny(configWait, manualCheckWait);
            waitCts.Cancel();
            await completedTask;

            if (completedTask == manualCheckWait && manualCheckWait.Result)
            {
                logger.LogInformation("Cycle de vérification immédiat demandé depuis le tableau de bord web.");
                return;
            }
        }
    }

    private static void LogActiveConfiguration(ILogger logger, AppConfig config)
    {
        if (config.PingTargets.Count == 0)
            logger.LogWarning("Aucune IP à monitorer n'est configurée.");

        if (config.TcpTargets.Count == 0)
            logger.LogWarning("Aucun endpoint host:port à monitorer n'est configuré.");

        if (config.HttpTargets.Count == 0)
            logger.LogWarning("Aucun endpoint HTTP/HTTPS à monitorer n'est configuré.");

        if (config.DnsTargets.Count == 0)
            logger.LogWarning("Aucun hôte DNS à monitorer n'est configuré.");

        if (config.TlsTargets.Count == 0)
            logger.LogWarning("Aucune cible TLS à monitorer n'est configurée.");

        if (config.DnsRecordTargets.Count == 0)
            logger.LogWarning("Aucun check DNS record à monitorer n'est configuré.");

        logger.LogInformation(
            "Configuration active — YAML: {YamlPath} — Dashboard: {DashboardEnabled} — Ping: {PingCount} — TCP: {TcpCount} — HTTP: {HttpCount} — DNS: {DnsCount} — TLS: {TlsCount} — DNS Record: {DnsRecordCount} — Schedule: {Schedule}",
            AppConfigProvider.ConfigPath,
            config.DashboardEnabled ? "activé" : "désactivé",
            config.PingTargets.Count,
            config.TcpTargets.Count,
            config.HttpTargets.Count,
            config.DnsTargets.Count,
            config.TlsTargets.Count,
            config.DnsRecordTargets.Count,
            BuildSchedule(config).Description);

        foreach (var dnsTarget in config.DnsTargets.OrderBy(GetDnsTargetLogName, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(dnsTarget.ReverseLookupIp))
            {
                logger.LogInformation(
                    "Monitor DNS actif — PTR {Ip} → {HostName}",
                    dnsTarget.ReverseLookupIp,
                    string.IsNullOrWhiteSpace(dnsTarget.ExpectedHost) ? "<tout hostname>" : dnsTarget.ExpectedHost);
                continue;
            }

            logger.LogInformation(
                "Monitor DNS actif — {Host} → {ExpectedAddress}",
                dnsTarget.Host,
                string.IsNullOrWhiteSpace(dnsTarget.ExpectedAddress) ? "<toute adresse>" : dnsTarget.ExpectedAddress);
        }
    }

    private static string BuildLifecycleMessage(string status, string version, string scheduleDescription, int pingCount, int tcpCount, int httpCount, int dnsCount, int tlsCount, int dnsRecordCount, string? shutdownReason = null)
    {
        var reasonLine = string.IsNullOrWhiteSpace(shutdownReason)
            ? string.Empty
            : $"\n📍 Motif : {shutdownReason}";

        return $"<b>{status}</b>\n📦 Version : {version}\n🕒 Rythme : {scheduleDescription}\n🖧 IP surveillées : {pingCount}\n🔌 Ports TCP surveillés : {tcpCount}\n🌐 Endpoints HTTP surveillés : {httpCount}\n🧭 Hôtes DNS surveillés : {dnsCount}\n🔐 Cibles TLS surveillées : {tlsCount}\n📚 DNS records surveillés : {dnsRecordCount}{reasonLine}";
    }

    private static string BuildTcpKey(string host, int port) => $"{host}:{port}";

    private static string BuildTlsKey(string host, int port) => $"{host}:{port}";

    private static string BuildDnsRecordKey(DnsRecordTargetConfig target) => $"{target.RecordType}:{target.Host}:{target.ExpectedValue}:{target.ContainsText}";

    private static string BuildDnsKey(DnsTargetConfig target) => !string.IsNullOrWhiteSpace(target.ReverseLookupIp)
        ? $"PTR:{target.ReverseLookupIp}"
        : $"A:{target.Host}";

    private static string GetDnsTargetLogName(DnsTargetConfig target) => !string.IsNullOrWhiteSpace(target.ReverseLookupIp)
        ? target.ReverseLookupIp!
        : target.Host;

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