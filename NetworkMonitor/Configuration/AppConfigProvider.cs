using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

sealed class AppConfig
{
    public string AppVersion { get; init; } = "inconnue";
    public string PushoverToken { get; init; } = string.Empty;
    public string PushoverUser { get; init; } = string.Empty;
    public string StartupSound { get; init; } = "cosmic";
    public string ShutdownSound { get; init; } = "falling";
    public int SnoozeDays { get; init; } = 1;
    public string? ScheduleCron { get; init; }
    public int ScheduleIntervalSeconds { get; init; } = 10;
    public bool DashboardEnabled { get; init; } = true;
    public bool DashboardAuthEnabled { get; init; }
    public string DashboardAuthUsername { get; init; } = string.Empty;
    public string DashboardAuthPasswordHash { get; init; } = string.Empty;
    public int DashboardSessionHours { get; init; } = 12;
    public int DashboardRefreshSeconds { get; init; } = 5;
    public IReadOnlyList<string> PingTargets { get; init; } = [];
    public IReadOnlyList<TcpTargetConfig> TcpTargets { get; init; } = [];
    public IReadOnlyList<HttpTargetConfig> HttpTargets { get; init; } = [];
    public IReadOnlyList<DnsTargetConfig> DnsTargets { get; init; } = [];
}

sealed class TcpTargetConfig
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
}

sealed class HttpTargetConfig
{
    public string Url { get; init; } = string.Empty;
    public int? ExpectedStatusCode { get; init; }
    public string? ContainsText { get; init; }
}

sealed class DnsTargetConfig
{
    public string Host { get; init; } = string.Empty;
    public string? ExpectedAddress { get; init; }
    public string? ReverseLookupIp { get; init; }
    public string? ExpectedHost { get; init; }
}

sealed class ConfigMutationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

static class AppConfigProvider
{
    private static readonly TimeSpan WatcherFallbackPollingInterval = TimeSpan.FromSeconds(30);
    private static readonly object _lock = new();
    private static readonly SemaphoreSlim _changeSignal = new(0, int.MaxValue);
    private static YamlAppConfig _yamlConfig = new();
    private static DateTime _lastWriteTimeUtc = DateTime.MinValue;
    private static bool _fileExists;
    private static int _version;
    private static bool _reloadRequested = true;
    private static FileSystemWatcher? _watcher;
    private static string? _watchedDirectory;
    private static string? _watchedFileName;

    public static string ConfigPath =>
        Environment.GetEnvironmentVariable("CONFIG_YAML_PATH")
        ?? Path.Combine(StateStore.DataDir, "config.yaml");

    public static int Version
    {
        get
        {
            lock (_lock)
                return _version;
        }
    }

    public static AppConfig Current
    {
        get
        {
            lock (_lock)
                return BuildEffectiveConfig(_yamlConfig);
        }
    }

    public static void RefreshIfChanged(ILogger logger)
    {
        EnsureWatcher(logger);

        var path = ConfigPath;
        var exists = File.Exists(path);
        var lastWriteTimeUtc = exists ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

        lock (_lock)
        {
            if (!_reloadRequested && exists == _fileExists && lastWriteTimeUtc == _lastWriteTimeUtc)
                return;
        }

        YamlAppConfig yamlConfig = new();
        if (exists)
        {
            try
            {
                yamlConfig = ParseYaml(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Échec du chargement de la configuration YAML {Path}", path);
                return;
            }
        }

        lock (_lock)
        {
            _yamlConfig = yamlConfig;
            _fileExists = exists;
            _lastWriteTimeUtc = lastWriteTimeUtc;
            _reloadRequested = false;
            _version++;
        }

        if (exists)
            logger.LogInformation("Configuration YAML rechargée depuis {Path}", path);
        else
            logger.LogInformation("Configuration YAML absente, utilisation de la configuration par variables d'environnement.");
    }

    public static Task WaitForPotentialChangeAsync(ILogger logger, TimeSpan timeout, CancellationToken ct)
    {
        EnsureWatcher(logger);

        lock (_lock)
        {
            if (_reloadRequested)
                return Task.CompletedTask;
        }

        var delay = timeout < WatcherFallbackPollingInterval
            ? timeout
            : WatcherFallbackPollingInterval;

        return _changeSignal.WaitAsync(delay, ct);
    }

    public static string GetRawConfigContent()
    {
        var path = ConfigPath;
        if (File.Exists(path))
            return File.ReadAllText(path);

        lock (_lock)
            return BuildYamlContent(_yamlConfig);
    }

    public static ConfigMutationResult ReplaceRawConfig(string content)
    {
        try
        {
            var normalizedContent = content?.Replace("\r\n", "\n") ?? string.Empty;
            var parsed = string.IsNullOrWhiteSpace(normalizedContent)
                ? new YamlAppConfig()
                : ParseYaml(normalizedContent);

            lock (_lock)
                return SaveYamlConfig(parsed, "La configuration YAML a été mise à jour.");
        }
        catch (Exception ex)
        {
            return new ConfigMutationResult
            {
                Success = false,
                Message = $"Configuration YAML invalide : {ex.Message}"
            };
        }
    }

    public static ConfigMutationResult AddPingTarget(string target)
    {
        target = target.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            return new ConfigMutationResult
            {
                Success = false,
                Message = "La cible ping est obligatoire."
            };
        }

        lock (_lock)
        {
            if (BuildEffectiveConfig(_yamlConfig).PingTargets.Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                return new ConfigMutationResult
                {
                    Success = true,
                    Message = $"La cible ping '{target}' est déjà monitorée."
                };
            }

            var updated = CloneYamlConfig(_yamlConfig);
            updated.PingTargets = NormalizePingTargets([.. (updated.PingTargets ?? []), target]).ToList();
            return SaveYamlConfig(updated, $"La cible ping '{target}' a été ajoutée au fichier YAML.");
        }
    }

    public static string GetPingTargetSource(string target)
    {
        lock (_lock)
        {
            var inEnvironment = ParsePingTargetsFromEnvironment().Contains(target, StringComparer.OrdinalIgnoreCase);
            var inYaml = NormalizePingTargets(_yamlConfig.PingTargets ?? []).Contains(target, StringComparer.OrdinalIgnoreCase);
            return BuildSourceLabel(inEnvironment, inYaml);
        }
    }

    public static ConfigMutationResult RemovePingTarget(string target)
    {
        target = target.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            return new ConfigMutationResult
            {
                Success = false,
                Message = "La cible ping est obligatoire."
            };
        }

        lock (_lock)
        {
            var envTargets = ParsePingTargetsFromEnvironment();
            var yamlTargets = NormalizePingTargets(_yamlConfig.PingTargets ?? []);
            var inEnvironment = envTargets.Contains(target, StringComparer.OrdinalIgnoreCase);
            var inYaml = yamlTargets.Contains(target, StringComparer.OrdinalIgnoreCase);

            if (!inYaml)
            {
                return new ConfigMutationResult
                {
                    Success = false,
                    Message = inEnvironment
                        ? $"La cible ping '{target}' est fournie par variable d'environnement et ne peut pas être supprimée depuis l'interface web."
                        : $"La cible ping '{target}' n'existe pas dans le fichier YAML."
                };
            }

            var updated = CloneYamlConfig(_yamlConfig);
            updated.PingTargets = yamlTargets
                .Where(existing => !string.Equals(existing, target, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var successMessage = inEnvironment
                ? $"La cible ping '{target}' a été supprimée du YAML, mais reste monitorée via les variables d'environnement."
                : $"La cible ping '{target}' a été supprimée du fichier YAML.";

            return SaveYamlConfig(updated, successMessage);
        }
    }

    public static ConfigMutationResult AddTcpTarget(string host, int port)
    {
        host = host.Trim();
        if (string.IsNullOrWhiteSpace(host) || port <= 0)
        {
            return new ConfigMutationResult
            {
                Success = false,
                Message = "L'hôte TCP et un port strictement positif sont obligatoires."
            };
        }

        lock (_lock)
        {
            if (BuildEffectiveConfig(_yamlConfig).TcpTargets.Any(target => string.Equals(target.Host, host, StringComparison.OrdinalIgnoreCase) && target.Port == port))
            {
                return new ConfigMutationResult
                {
                    Success = true,
                    Message = $"L'endpoint TCP '{host}:{port}' est déjà monitoré."
                };
            }

            var updated = CloneYamlConfig(_yamlConfig);
            updated.TcpTargets = NormalizeTcpTargets([.. (updated.TcpTargets ?? []), new TcpTargetConfig { Host = host, Port = port }]).ToList();
            return SaveYamlConfig(updated, $"L'endpoint TCP '{host}:{port}' a été ajouté au fichier YAML.");
        }
    }

    public static ConfigMutationResult AddHttpTarget(string url, int? expectedStatusCode, string? containsText)
    {
        url = url.Trim();
        containsText = string.IsNullOrWhiteSpace(containsText) ? null : containsText.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return new ConfigMutationResult
            {
                Success = false,
                Message = "Une URL HTTP/HTTPS absolue valide est obligatoire."
            };
        }

        if (expectedStatusCode is <= 0)
            expectedStatusCode = null;

        lock (_lock)
        {
            if (BuildEffectiveConfig(_yamlConfig).HttpTargets.Any(target => string.Equals(target.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                return new ConfigMutationResult
                {
                    Success = true,
                    Message = $"L'endpoint HTTP '{url}' est déjà monitoré."
                };
            }

            var updated = CloneYamlConfig(_yamlConfig);
            updated.HttpTargets = NormalizeHttpTargets([
                .. (updated.HttpTargets ?? []),
                new HttpTargetConfig
                {
                    Url = url,
                    ExpectedStatusCode = expectedStatusCode,
                    ContainsText = containsText
                }
            ]).ToList();

            return SaveYamlConfig(updated, $"L'endpoint HTTP '{url}' a été ajouté au fichier YAML.");
        }
    }

    public static ConfigMutationResult AddDnsTarget(string host, string? expectedAddress)
    {
        host = host.Trim();
        expectedAddress = string.IsNullOrWhiteSpace(expectedAddress) ? null : expectedAddress.Trim();

        if (string.IsNullOrWhiteSpace(host))
        {
            return new ConfigMutationResult
            {
                Success = false,
                Message = "L'hôte DNS est obligatoire."
            };
        }

        lock (_lock)
        {
            if (BuildEffectiveConfig(_yamlConfig).DnsTargets.Any(target => string.Equals(target.Host, host, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(target.ReverseLookupIp)))
            {
                return new ConfigMutationResult
                {
                    Success = true,
                    Message = $"La cible DNS '{host}' est déjà monitorée."
                };
            }

            var updated = CloneYamlConfig(_yamlConfig);
            updated.DnsTargets = NormalizeDnsTargets([
                .. (updated.DnsTargets ?? []),
                new DnsTargetConfig
                {
                    Host = host,
                    ExpectedAddress = expectedAddress
                }
            ]).ToList();

            return SaveYamlConfig(updated, $"La cible DNS '{host}' a été ajoutée au fichier YAML.");
        }
    }

    public static ConfigMutationResult AddDnsReverseTarget(string ip, string? expectedHost)
    {
        ip = ip.Trim();
        expectedHost = string.IsNullOrWhiteSpace(expectedHost) ? null : expectedHost.Trim();

        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            return new ConfigMutationResult
            {
                Success = false,
                Message = "Une adresse IP valide est obligatoire pour le reverse DNS."
            };
        }

        lock (_lock)
        {
            if (BuildEffectiveConfig(_yamlConfig).DnsTargets.Any(target => string.Equals(target.ReverseLookupIp, ip, StringComparison.OrdinalIgnoreCase)))
            {
                return new ConfigMutationResult
                {
                    Success = true,
                    Message = $"Le reverse DNS '{ip}' est déjà monitoré."
                };
            }

            var updated = CloneYamlConfig(_yamlConfig);
            updated.DnsTargets = NormalizeDnsTargets([
                .. (updated.DnsTargets ?? []),
                new DnsTargetConfig
                {
                    ReverseLookupIp = ip,
                    ExpectedHost = expectedHost
                }
            ]).ToList();

            return SaveYamlConfig(updated, $"Le reverse DNS '{ip}' a été ajouté au fichier YAML.");
        }
    }

    public static string GetTcpTargetSource(string host, int port)
    {
        lock (_lock)
        {
            var inEnvironment = ParseTcpTargetsFromEnvironment().Any(target => string.Equals(target.Host, host, StringComparison.OrdinalIgnoreCase) && target.Port == port);
            var inYaml = NormalizeTcpTargets(_yamlConfig.TcpTargets ?? []).Any(target => string.Equals(target.Host, host, StringComparison.OrdinalIgnoreCase) && target.Port == port);
            return BuildSourceLabel(inEnvironment, inYaml);
        }
    }

    public static string GetHttpTargetSource(string url)
    {
        lock (_lock)
        {
            var inEnvironment = ParseHttpTargetsFromEnvironment().Any(target => string.Equals(target.Url, url, StringComparison.OrdinalIgnoreCase));
            var inYaml = NormalizeHttpTargets(_yamlConfig.HttpTargets ?? []).Any(target => string.Equals(target.Url, url, StringComparison.OrdinalIgnoreCase));
            return BuildSourceLabel(inEnvironment, inYaml);
        }
    }

    public static string GetDnsTargetSource(string host)
    {
        lock (_lock)
        {
            var inEnvironment = ParseDnsTargetsFromEnvironment().Any(target => string.Equals(target.Host, host, StringComparison.OrdinalIgnoreCase) || string.Equals(target.ReverseLookupIp, host, StringComparison.OrdinalIgnoreCase));
            var inYaml = NormalizeDnsTargets(_yamlConfig.DnsTargets ?? []).Any(target => string.Equals(target.Host, host, StringComparison.OrdinalIgnoreCase) || string.Equals(target.ReverseLookupIp, host, StringComparison.OrdinalIgnoreCase));
            return BuildSourceLabel(inEnvironment, inYaml);
        }
    }

    public static ConfigMutationResult RemoveTcpTarget(string host, int port)
    {
        host = host.Trim();
        if (string.IsNullOrWhiteSpace(host) || port <= 0)
        {
            return new ConfigMutationResult
            {
                Success = false,
                Message = "L'hôte TCP et un port strictement positif sont obligatoires."
            };
        }

        lock (_lock)
        {
            var envTargets = ParseTcpTargetsFromEnvironment();
            var yamlTargets = NormalizeTcpTargets(_yamlConfig.TcpTargets ?? []);
            var inEnvironment = envTargets.Any(target => string.Equals(target.Host, host, StringComparison.OrdinalIgnoreCase) && target.Port == port);
            var inYaml = yamlTargets.Any(target => string.Equals(target.Host, host, StringComparison.OrdinalIgnoreCase) && target.Port == port);

            if (!inYaml)
            {
                return new ConfigMutationResult
                {
                    Success = false,
                    Message = inEnvironment
                        ? $"L'endpoint TCP '{host}:{port}' est fourni par variable d'environnement et ne peut pas être supprimé depuis l'interface web."
                        : $"L'endpoint TCP '{host}:{port}' n'existe pas dans le fichier YAML."
                };
            }

            var updated = CloneYamlConfig(_yamlConfig);
            updated.TcpTargets = yamlTargets
                .Where(target => !string.Equals(target.Host, host, StringComparison.OrdinalIgnoreCase) || target.Port != port)
                .ToList();

            var successMessage = inEnvironment
                ? $"L'endpoint TCP '{host}:{port}' a été supprimé du YAML, mais reste monitoré via les variables d'environnement."
                : $"L'endpoint TCP '{host}:{port}' a été supprimé du fichier YAML.";

            return SaveYamlConfig(updated, successMessage);
        }
    }

    public static ConfigMutationResult RemoveHttpTarget(string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return new ConfigMutationResult
            {
                Success = false,
                Message = "Une URL HTTP/HTTPS absolue valide est obligatoire."
            };
        }

        lock (_lock)
        {
            var envTargets = ParseHttpTargetsFromEnvironment();
            var yamlTargets = NormalizeHttpTargets(_yamlConfig.HttpTargets ?? []);
            var inEnvironment = envTargets.Any(target => string.Equals(target.Url, url, StringComparison.OrdinalIgnoreCase));
            var inYaml = yamlTargets.Any(target => string.Equals(target.Url, url, StringComparison.OrdinalIgnoreCase));

            if (!inYaml)
            {
                return new ConfigMutationResult
                {
                    Success = false,
                    Message = inEnvironment
                        ? $"L'endpoint HTTP '{url}' est fourni par variable d'environnement et ne peut pas être supprimé depuis l'interface web."
                        : $"L'endpoint HTTP '{url}' n'existe pas dans le fichier YAML."
                };
            }

            var updated = CloneYamlConfig(_yamlConfig);
            updated.HttpTargets = yamlTargets
                .Where(target => !string.Equals(target.Url, url, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var successMessage = inEnvironment
                ? $"L'endpoint HTTP '{url}' a été supprimé du YAML, mais reste monitoré via les variables d'environnement."
                : $"L'endpoint HTTP '{url}' a été supprimé du fichier YAML.";

            return SaveYamlConfig(updated, successMessage);
        }
    }

    public static ConfigMutationResult RemoveDnsTarget(string host)
    {
        host = host.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            return new ConfigMutationResult
            {
                Success = false,
                Message = "L'hôte DNS est obligatoire."
            };
        }

        lock (_lock)
        {
            var envTargets = ParseDnsTargetsFromEnvironment();
            var yamlTargets = NormalizeDnsTargets(_yamlConfig.DnsTargets ?? []);
            var inEnvironment = envTargets.Any(target => string.Equals(target.Host, host, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(target.ReverseLookupIp));
            var inYaml = yamlTargets.Any(target => string.Equals(target.Host, host, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(target.ReverseLookupIp));

            if (!inYaml)
            {
                return new ConfigMutationResult
                {
                    Success = false,
                    Message = inEnvironment
                        ? $"La cible DNS '{host}' est fournie par variable d'environnement et ne peut pas être supprimée depuis l'interface web."
                        : $"La cible DNS '{host}' n'existe pas dans le fichier YAML."
                };
            }

            var updated = CloneYamlConfig(_yamlConfig);
            updated.DnsTargets = yamlTargets
                .Where(target => !string.Equals(target.Host, host, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(target.ReverseLookupIp))
                .ToList();

            var successMessage = inEnvironment
                ? $"La cible DNS '{host}' a été supprimée du YAML, mais reste monitorée via les variables d'environnement."
                : $"La cible DNS '{host}' a été supprimée du fichier YAML.";

            return SaveYamlConfig(updated, successMessage);
        }
    }

    public static ConfigMutationResult RemoveDnsReverseTarget(string ip)
    {
        ip = ip.Trim();
        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            return new ConfigMutationResult
            {
                Success = false,
                Message = "Une adresse IP valide est obligatoire pour le reverse DNS."
            };
        }

        lock (_lock)
        {
            var envTargets = ParseDnsTargetsFromEnvironment();
            var yamlTargets = NormalizeDnsTargets(_yamlConfig.DnsTargets ?? []);
            var inEnvironment = envTargets.Any(target => string.Equals(target.ReverseLookupIp, ip, StringComparison.OrdinalIgnoreCase));
            var inYaml = yamlTargets.Any(target => string.Equals(target.ReverseLookupIp, ip, StringComparison.OrdinalIgnoreCase));

            if (!inYaml)
            {
                return new ConfigMutationResult
                {
                    Success = false,
                    Message = inEnvironment
                        ? $"Le reverse DNS '{ip}' est fourni par variable d'environnement et ne peut pas être supprimé depuis l'interface web."
                        : $"Le reverse DNS '{ip}' n'existe pas dans le fichier YAML."
                };
            }

            var updated = CloneYamlConfig(_yamlConfig);
            updated.DnsTargets = yamlTargets
                .Where(target => !string.Equals(target.ReverseLookupIp, ip, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var successMessage = inEnvironment
                ? $"Le reverse DNS '{ip}' a été supprimé du YAML, mais reste monitoré via les variables d'environnement."
                : $"Le reverse DNS '{ip}' a été supprimé du fichier YAML.";

            return SaveYamlConfig(updated, successMessage);
        }
    }

    private static void EnsureWatcher(ILogger logger)
    {
        var path = ConfigPath;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            directory = Directory.GetCurrentDirectory();

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
            return;

        lock (_lock)
        {
            if (string.Equals(_watchedDirectory, directory, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_watchedFileName, fileName, StringComparison.OrdinalIgnoreCase)
                && _watcher is not null)
                return;

            try
            {
                Directory.CreateDirectory(directory);

                _watcher?.Dispose();
                _watcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.FileName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.CreationTime
                        | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };

                _watcher.Changed += OnConfigFileChanged;
                _watcher.Created += OnConfigFileChanged;
                _watcher.Deleted += OnConfigFileChanged;
                _watcher.Renamed += OnConfigFileRenamed;
                _watcher.Error += OnWatcherError;

                _watchedDirectory = directory;
                _watchedFileName = fileName;
                _reloadRequested = true;

                logger.LogInformation(
                    "Surveillance native du fichier YAML activée via FileSystemWatcher pour {Path} avec repli périodique toutes les {Seconds}s.",
                    path,
                    WatcherFallbackPollingInterval.TotalSeconds);
            }
            catch (Exception ex)
            {
                _watcher?.Dispose();
                _watcher = null;
                _watchedDirectory = directory;
                _watchedFileName = fileName;
                _reloadRequested = true;

                logger.LogWarning(ex,
                    "Impossible d'initialiser FileSystemWatcher pour {Path}. Le rechargement restera assuré par contrôle périodique.",
                    path);
            }
        }
    }

    private static void OnConfigFileChanged(object sender, FileSystemEventArgs e) => RequestReload();

    private static void OnConfigFileRenamed(object sender, RenamedEventArgs e) => RequestReload();

    private static void OnWatcherError(object sender, ErrorEventArgs e) => RequestReload();

    private static void RequestReload()
    {
        lock (_lock)
        {
            if (_reloadRequested)
                return;

            _reloadRequested = true;
        }

        PulseChangeSignal();
    }

    private static ConfigMutationResult SaveYamlConfig(YamlAppConfig updatedConfig, string successMessage)
    {
        var path = ConfigPath;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            directory = Directory.GetCurrentDirectory();

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            var yamlContent = BuildYamlContent(updatedConfig);
            File.WriteAllText(tempPath, yamlContent);

            try
            {
                File.Move(tempPath, path, true);
            }
            catch (IOException) when (File.Exists(path))
            {
                File.WriteAllText(path, yamlContent);
                File.Delete(tempPath);
            }

            _yamlConfig = updatedConfig;
            _fileExists = true;
            _lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            _reloadRequested = false;
            _version++;

            PulseChangeSignal();

            return new ConfigMutationResult
            {
                Success = true,
                Message = successMessage
            };
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }

            return new ConfigMutationResult
            {
                Success = false,
                Message = $"Impossible d'écrire la configuration YAML '{path}' : {ex.Message}"
            };
        }
    }

    private static void PulseChangeSignal()
    {
        try
        {
            _changeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private static YamlAppConfig CloneYamlConfig(YamlAppConfig source)
    {
        return new YamlAppConfig
        {
            AppVersion = source.AppVersion,
            PushoverToken = source.PushoverToken,
            PushoverUser = source.PushoverUser,
            StartupSound = source.StartupSound,
            ShutdownSound = source.ShutdownSound,
            SnoozeDays = source.SnoozeDays,
            ScheduleCron = source.ScheduleCron,
            ScheduleIntervalSeconds = source.ScheduleIntervalSeconds,
            DashboardEnabled = source.DashboardEnabled,
            DashboardAuthEnabled = source.DashboardAuthEnabled,
            DashboardAuthUsername = source.DashboardAuthUsername,
            DashboardAuthPasswordHash = source.DashboardAuthPasswordHash,
            DashboardAuthPasswordHashFile = source.DashboardAuthPasswordHashFile,
            DashboardSessionHours = source.DashboardSessionHours,
            DashboardRefreshSeconds = source.DashboardRefreshSeconds,
            PingTargets = source.PingTargets is null ? null : [.. source.PingTargets],
            TcpTargets = source.TcpTargets is null ? null : [.. source.TcpTargets.Select(target => new TcpTargetConfig { Host = target.Host, Port = target.Port })],
            HttpTargets = source.HttpTargets is null ? null : [.. source.HttpTargets.Select(target => new HttpTargetConfig { Url = target.Url, ExpectedStatusCode = target.ExpectedStatusCode, ContainsText = target.ContainsText })],
            DnsTargets = source.DnsTargets is null ? null : [.. source.DnsTargets.Select(target => new DnsTargetConfig { Host = target.Host, ExpectedAddress = target.ExpectedAddress, ReverseLookupIp = target.ReverseLookupIp, ExpectedHost = target.ExpectedHost })]
        };
    }

    private static string BuildYamlContent(YamlAppConfig config)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(config.AppVersion))
            lines.Add($"appVersion: {QuoteYaml(config.AppVersion)}");

        if (config.SnoozeDays is int snoozeDays)
            lines.Add($"snoozeDays: {snoozeDays}");

        AppendBlankLine(lines);
        AppendScalarSection(lines, "schedule", [
            ("cron", config.ScheduleCron, false),
            ("intervalSeconds", config.ScheduleIntervalSeconds?.ToString(), true)
        ]);

        AppendBlankLine(lines);
        AppendScalarSection(lines, "dashboard", [
            ("enabled", config.DashboardEnabled?.ToString().ToLowerInvariant(), true),
            ("authEnabled", config.DashboardAuthEnabled?.ToString().ToLowerInvariant(), true),
            ("authUsername", config.DashboardAuthUsername, false),
            ("authPasswordHash", config.DashboardAuthPasswordHash, false),
            ("authPasswordHashFile", config.DashboardAuthPasswordHashFile, false),
            ("sessionHours", config.DashboardSessionHours?.ToString(), true),
            ("refreshSeconds", config.DashboardRefreshSeconds?.ToString(), true)
        ]);

        AppendBlankLine(lines);
        AppendScalarSection(lines, "pushover", [
            ("token", config.PushoverToken, false),
            ("user", config.PushoverUser, false),
            ("startupSound", config.StartupSound, false),
            ("shutdownSound", config.ShutdownSound, false)
        ]);

        if ((config.PingTargets?.Count ?? 0) > 0 || (config.TcpTargets?.Count ?? 0) > 0 || (config.HttpTargets?.Count ?? 0) > 0 || (config.DnsTargets?.Count ?? 0) > 0)
        {
            AppendBlankLine(lines);
            lines.Add("monitoring:");

            if (config.PingTargets?.Count > 0)
            {
                lines.Add("  pingTargets:");
                foreach (var target in config.PingTargets)
                    lines.Add($"    - {QuoteYaml(target)}");
            }

            if (config.TcpTargets?.Count > 0)
            {
                lines.Add("  tcpTargets:");
                foreach (var target in config.TcpTargets)
                {
                    lines.Add($"    - host: {QuoteYaml(target.Host)}");
                    lines.Add($"      port: {target.Port}");
                }
            }

            if (config.HttpTargets?.Count > 0)
            {
                lines.Add("  httpTargets:");
                foreach (var target in config.HttpTargets)
                {
                    lines.Add($"    - url: {QuoteYaml(target.Url)}");
                    if (target.ExpectedStatusCode is int expectedStatusCode)
                        lines.Add($"      expectedStatusCode: {expectedStatusCode}");
                    if (!string.IsNullOrWhiteSpace(target.ContainsText))
                        lines.Add($"      containsText: {QuoteYaml(target.ContainsText!)}");
                }
            }

            if (config.DnsTargets?.Count > 0)
            {
                lines.Add("  dnsTargets:");
                foreach (var target in config.DnsTargets)
                {
                    if (!string.IsNullOrWhiteSpace(target.ReverseLookupIp))
                    {
                        lines.Add($"    - ip: {QuoteYaml(target.ReverseLookupIp)}");
                        if (!string.IsNullOrWhiteSpace(target.ExpectedHost))
                            lines.Add($"      expectedHost: {QuoteYaml(target.ExpectedHost)}");
                    }
                    else
                    {
                        lines.Add($"    - host: {QuoteYaml(target.Host)}");
                        if (!string.IsNullOrWhiteSpace(target.ExpectedAddress))
                            lines.Add($"      expectedAddress: {QuoteYaml(target.ExpectedAddress)}");
                    }
                }
            }
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static void AppendScalarSection(List<string> lines, string sectionName, IEnumerable<(string Key, string? Value, bool RenderRaw)> entries)
    {
        var sectionEntries = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Value)).ToArray();
        if (sectionEntries.Length == 0)
            return;

        lines.Add($"{sectionName}:");
        foreach (var entry in sectionEntries)
        {
            var renderedValue = entry.RenderRaw ? entry.Value! : QuoteYaml(entry.Value!);
            lines.Add($"  {entry.Key}: {renderedValue}");
        }
    }

    private static void AppendBlankLine(List<string> lines)
    {
        if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            lines.Add(string.Empty);
    }

    private static string QuoteYaml(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    private static string BuildSourceLabel(bool inEnvironment, bool inYaml)
    {
        return (inEnvironment, inYaml) switch
        {
            (true, true) => "ENV + YAML",
            (true, false) => "ENV",
            (false, true) => "YAML",
            _ => "Inconnue"
        };
    }

    private static AppConfig BuildEffectiveConfig(YamlAppConfig yamlConfig)
    {
        var environmentPingTargets = ParsePingTargetsFromEnvironment();
        var yamlPingTargets = yamlConfig.PingTargets is not null
            ? NormalizePingTargets(yamlConfig.PingTargets)
            : [];

        var environmentTcpTargets = ParseTcpTargetsFromEnvironment();
        var yamlTcpTargets = yamlConfig.TcpTargets is not null
            ? NormalizeTcpTargets(yamlConfig.TcpTargets)
            : [];

        var environmentHttpTargets = ParseHttpTargetsFromEnvironment();
        var yamlHttpTargets = yamlConfig.HttpTargets is not null
            ? NormalizeHttpTargets(yamlConfig.HttpTargets)
            : [];

        var environmentDnsTargets = ParseDnsTargetsFromEnvironment();
        var yamlDnsTargets = yamlConfig.DnsTargets is not null
            ? NormalizeDnsTargets(yamlConfig.DnsTargets)
            : [];

        return new AppConfig
        {
            AppVersion = FirstNonEmpty(yamlConfig.AppVersion, Environment.GetEnvironmentVariable("APP_VERSION")) ?? "inconnue",
            PushoverToken = FirstNonEmpty(yamlConfig.PushoverToken, Environment.GetEnvironmentVariable("PUSHOVER_TOKEN")) ?? string.Empty,
            PushoverUser = FirstNonEmpty(yamlConfig.PushoverUser, Environment.GetEnvironmentVariable("PUSHOVER_USER")) ?? string.Empty,
            StartupSound = FirstNonEmpty(yamlConfig.StartupSound, Environment.GetEnvironmentVariable("PUSHOVER_STARTUP_SOUND")) ?? "cosmic",
            ShutdownSound = FirstNonEmpty(yamlConfig.ShutdownSound, Environment.GetEnvironmentVariable("PUSHOVER_SHUTDOWN_SOUND")) ?? "falling",
            SnoozeDays = yamlConfig.SnoozeDays ?? ParsePositiveInt(Environment.GetEnvironmentVariable("SNOOZE_DAYS"), 1),
            ScheduleCron = FirstNonEmpty(yamlConfig.ScheduleCron, Environment.GetEnvironmentVariable("SCHEDULE_CRON")),
            ScheduleIntervalSeconds = yamlConfig.ScheduleIntervalSeconds ?? ParsePositiveInt(Environment.GetEnvironmentVariable("SCHEDULE_INTERVAL_SECONDS"), 10),
            DashboardEnabled = yamlConfig.DashboardEnabled ?? ParseBoolean(Environment.GetEnvironmentVariable("DASHBOARD_ENABLED"), true),
            DashboardAuthEnabled = yamlConfig.DashboardAuthEnabled ?? ParseBoolean(Environment.GetEnvironmentVariable("DASHBOARD_AUTH_ENABLED"), false),
            DashboardAuthUsername = FirstNonEmpty(yamlConfig.DashboardAuthUsername, Environment.GetEnvironmentVariable("DASHBOARD_AUTH_USERNAME")) ?? string.Empty,
            DashboardAuthPasswordHash = FirstNonEmpty(
                ReadSecretFile(FirstNonEmpty(yamlConfig.DashboardAuthPasswordHashFile, Environment.GetEnvironmentVariable("DASHBOARD_AUTH_PASSWORD_HASH_FILE"))),
                yamlConfig.DashboardAuthPasswordHash,
                Environment.GetEnvironmentVariable("DASHBOARD_AUTH_PASSWORD_HASH")) ?? string.Empty,
            DashboardSessionHours = yamlConfig.DashboardSessionHours ?? ParsePositiveInt(Environment.GetEnvironmentVariable("DASHBOARD_SESSION_HOURS"), 12),
            DashboardRefreshSeconds = yamlConfig.DashboardRefreshSeconds ?? ParsePositiveInt(Environment.GetEnvironmentVariable("DASHBOARD_REFRESH_SECONDS"), 5),
            PingTargets = MergePingTargets(environmentPingTargets, yamlPingTargets),
            TcpTargets = MergeTcpTargets(environmentTcpTargets, yamlTcpTargets),
            HttpTargets = MergeHttpTargets(environmentHttpTargets, yamlHttpTargets),
            DnsTargets = MergeDnsTargets(environmentDnsTargets, yamlDnsTargets)
        };
    }

    private static string? ReadSecretFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> MergePingTargets(IReadOnlyList<string> environmentTargets, IReadOnlyList<string> yamlTargets)
    {
        return environmentTargets
            .Concat(yamlTargets)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<TcpTargetConfig> MergeTcpTargets(IReadOnlyList<TcpTargetConfig> environmentTargets, IReadOnlyList<TcpTargetConfig> yamlTargets)
    {
        return environmentTargets
            .Concat(yamlTargets)
            .GroupBy(target => $"{target.Host}:{target.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<HttpTargetConfig> MergeHttpTargets(IReadOnlyList<HttpTargetConfig> environmentTargets, IReadOnlyList<HttpTargetConfig> yamlTargets)
    {
        return environmentTargets
            .Concat(yamlTargets)
            .GroupBy(target => target.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<DnsTargetConfig> MergeDnsTargets(IReadOnlyList<DnsTargetConfig> environmentTargets, IReadOnlyList<DnsTargetConfig> yamlTargets)
    {
        return environmentTargets
            .Concat(yamlTargets)
            .GroupBy(target => target.Host, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizePingTargets(IReadOnlyList<string> targets)
    {
        return targets
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Select(ip => ip.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<TcpTargetConfig> NormalizeTcpTargets(IReadOnlyList<TcpTargetConfig> targets)
    {
        return targets
            .Where(target => !string.IsNullOrWhiteSpace(target.Host) && target.Port > 0)
            .GroupBy(target => $"{target.Host}:{target.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<HttpTargetConfig> NormalizeHttpTargets(IReadOnlyList<HttpTargetConfig> targets)
    {
        return targets
            .Where(target => Uri.TryCreate(target.Url, UriKind.Absolute, out _))
            .GroupBy(target => target.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<DnsTargetConfig> NormalizeDnsTargets(IReadOnlyList<DnsTargetConfig> targets)
    {
        return targets
            .Where(target => !string.IsNullOrWhiteSpace(target.Host) || !string.IsNullOrWhiteSpace(target.ReverseLookupIp))
            .GroupBy(target => string.IsNullOrWhiteSpace(target.ReverseLookupIp) ? $"host:{target.Host}" : $"ip:{target.ReverseLookupIp}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<string> ParsePingTargetsFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("PING_TARGETS");
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<TcpTargetConfig> ParseTcpTargetsFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("TCP_TARGETS");
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var targets = new List<TcpTargetConfig>();
        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseHostPort(entry, out var target))
                targets.Add(target);
        }

        return NormalizeTcpTargets(targets);
    }

    private static IReadOnlyList<HttpTargetConfig> ParseHttpTargetsFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("HTTP_TARGETS");
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return NormalizeHttpTargets(raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(url => new HttpTargetConfig { Url = url.Trim() })
            .ToArray());
    }

    private static IReadOnlyList<DnsTargetConfig> ParseDnsTargetsFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("DNS_TARGETS");
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return NormalizeDnsTargets(raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(host => new DnsTargetConfig { Host = host.Trim() })
            .ToArray());
    }

    private static int ParsePositiveInt(string? rawValue, int defaultValue)
    {
        return int.TryParse(rawValue, out var value) && value > 0 ? value : defaultValue;
    }

    private static bool ParseBoolean(string? rawValue, bool defaultValue)
    {
        return bool.TryParse(rawValue, out var value) ? value : defaultValue;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static YamlAppConfig ParseYaml(string content)
    {
        var config = new YamlAppConfig();
        var lines = content.Replace("\r\n", "\n").Split('\n');

        for (int index = 0; index < lines.Length;)
        {
            var line = PrepareLine(lines[index]);
            if (line.IsEmpty)
            {
                index++;
                continue;
            }

            if (line.Indent != 0)
                throw new FormatException($"Ligne YAML inattendue à l'indentation {line.Indent + 1}.");

            if (TryParseScalar(line.Content, out var key, out var value))
            {
                ApplyTopLevelScalar(config, key, value);
                index++;
                continue;
            }

            if (!line.Content.EndsWith(':'))
                throw new FormatException($"Section YAML invalide : {line.Content}");

            var section = line.Content[..^1].Trim();
            index++;

            switch (section)
            {
                case "schedule":
                    ParseSchedule(lines, ref index, config);
                    break;
                case "pushover":
                    ParsePushover(lines, ref index, config);
                    break;
                case "dashboard":
                    ParseDashboard(lines, ref index, config);
                    break;
                case "monitoring":
                    ParseMonitoring(lines, ref index, config);
                    break;
                case "pingTargets":
                    config.PingTargets = ParseStringList(lines, ref index, 2);
                    break;
                case "tcpTargets":
                    config.TcpTargets = ParseTcpTargetList(lines, ref index, 2);
                    break;
                default:
                    SkipIndentedBlock(lines, ref index, 2);
                    break;
            }
        }

        return config;
    }

    private static void ApplyTopLevelScalar(YamlAppConfig config, string key, string value)
    {
        switch (key)
        {
            case "appVersion":
                config.AppVersion = Unquote(value);
                break;
            case "snoozeDays":
                config.SnoozeDays = ParseYamlPositiveInt(value, key);
                break;
        }
    }

    private static void ParseSchedule(string[] lines, ref int index, YamlAppConfig config)
    {
        ParseScalarBlock(lines, ref index, 2, (key, value) =>
        {
            switch (key)
            {
                case "cron":
                    config.ScheduleCron = Unquote(value);
                    break;
                case "intervalSeconds":
                    config.ScheduleIntervalSeconds = ParseYamlPositiveInt(value, key);
                    break;
            }
        });
    }

    private static void ParseDashboard(string[] lines, ref int index, YamlAppConfig config)
    {
        ParseScalarBlock(lines, ref index, 2, (key, value) =>
        {
            switch (key)
            {
                case "enabled":
                    config.DashboardEnabled = ParseYamlBoolean(value, key);
                    break;
                case "authEnabled":
                    config.DashboardAuthEnabled = ParseYamlBoolean(value, key);
                    break;
                case "authUsername":
                    config.DashboardAuthUsername = Unquote(value);
                    break;
                case "authPasswordHash":
                    config.DashboardAuthPasswordHash = Unquote(value);
                    break;
                case "authPasswordHashFile":
                    config.DashboardAuthPasswordHashFile = Unquote(value);
                    break;
                case "sessionHours":
                    config.DashboardSessionHours = ParseYamlPositiveInt(value, key);
                    break;
                case "refreshSeconds":
                    config.DashboardRefreshSeconds = ParseYamlPositiveInt(value, key);
                    break;
            }
        });
    }

    private static void ParsePushover(string[] lines, ref int index, YamlAppConfig config)
    {
        ParseScalarBlock(lines, ref index, 2, (key, value) =>
        {
            switch (key)
            {
                case "token":
                    config.PushoverToken = Unquote(value);
                    break;
                case "user":
                    config.PushoverUser = Unquote(value);
                    break;
                case "startupSound":
                    config.StartupSound = Unquote(value);
                    break;
                case "shutdownSound":
                    config.ShutdownSound = Unquote(value);
                    break;
            }
        });
    }

    private static void ParseMonitoring(string[] lines, ref int index, YamlAppConfig config)
    {
        while (index < lines.Length)
        {
            var line = PrepareLine(lines[index]);
            if (line.IsEmpty)
            {
                index++;
                continue;
            }

            if (line.Indent < 2)
                break;

            if (line.Indent != 2 || !line.Content.EndsWith(':'))
                throw new FormatException($"Bloc monitoring invalide : {line.Content}");

            var section = line.Content[..^1].Trim();
            index++;

            switch (section)
            {
                case "pingTargets":
                    config.PingTargets = ParseStringList(lines, ref index, 4);
                    break;
                case "tcpTargets":
                    config.TcpTargets = ParseTcpTargetList(lines, ref index, 4);
                    break;
                case "httpTargets":
                    config.HttpTargets = ParseHttpTargetList(lines, ref index, 4);
                    break;
                case "dnsTargets":
                    config.DnsTargets = ParseDnsTargetList(lines, ref index, 4);
                    break;
                default:
                    SkipIndentedBlock(lines, ref index, 4);
                    break;
            }
        }
    }

    private static void ParseScalarBlock(string[] lines, ref int index, int expectedIndent, Action<string, string> apply)
    {
        while (index < lines.Length)
        {
            var line = PrepareLine(lines[index]);
            if (line.IsEmpty)
            {
                index++;
                continue;
            }

            if (line.Indent < expectedIndent)
                break;

            if (line.Indent != expectedIndent || !TryParseScalar(line.Content, out var key, out var value))
                throw new FormatException($"Entrée YAML invalide : {line.Content}");

            apply(key, value);
            index++;
        }
    }

    private static List<string> ParseStringList(string[] lines, ref int index, int expectedIndent)
    {
        var items = new List<string>();

        while (index < lines.Length)
        {
            var line = PrepareLine(lines[index]);
            if (line.IsEmpty)
            {
                index++;
                continue;
            }

            if (line.Indent < expectedIndent)
                break;

            if (line.Indent != expectedIndent || !line.Content.StartsWith("- "))
                throw new FormatException($"Liste YAML invalide : {line.Content}");

            var value = Unquote(line.Content[2..].Trim());
            if (!string.IsNullOrWhiteSpace(value))
                items.Add(value);

            index++;
        }

        return items;
    }

    private static List<HttpTargetConfig> ParseHttpTargetList(string[] lines, ref int index, int expectedIndent)
    {
        var items = new List<HttpTargetConfig>();

        while (index < lines.Length)
        {
            var line = PrepareLine(lines[index]);
            if (line.IsEmpty)
            {
                index++;
                continue;
            }

            if (line.Indent < expectedIndent)
                break;

            if (line.Indent != expectedIndent || !line.Content.StartsWith("- "))
                throw new FormatException($"Liste httpTargets invalide : {line.Content}");

            var item = line.Content[2..].Trim();
            if (Uri.TryCreate(Unquote(item), UriKind.Absolute, out _))
            {
                items.Add(new HttpTargetConfig { Url = Unquote(item) });
                index++;
                continue;
            }

            var url = string.Empty;
            int? expectedStatusCode = null;
            string? containsText = null;

            if (TryParseScalar(item, out var inlineKey, out var inlineValue))
                ApplyHttpField(inlineKey, inlineValue, ref url, ref expectedStatusCode, ref containsText);
            else
                throw new FormatException($"Entrée httpTargets invalide : {line.Content}");

            index++;
            while (index < lines.Length)
            {
                var childLine = PrepareLine(lines[index]);
                if (childLine.IsEmpty)
                {
                    index++;
                    continue;
                }

                if (childLine.Indent <= expectedIndent)
                    break;

                if (childLine.Indent != expectedIndent + 2 || !TryParseScalar(childLine.Content, out var childKey, out var childValue))
                    throw new FormatException($"Entrée httpTargets invalide : {childLine.Content}");

                ApplyHttpField(childKey, childValue, ref url, ref expectedStatusCode, ref containsText);
                index++;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                throw new FormatException("Chaque entrée httpTargets doit contenir une URL absolue valide.");

            items.Add(new HttpTargetConfig
            {
                Url = url,
                ExpectedStatusCode = expectedStatusCode,
                ContainsText = containsText
            });
        }

        return items;
    }

    private static List<DnsTargetConfig> ParseDnsTargetList(string[] lines, ref int index, int expectedIndent)
    {
        var items = new List<DnsTargetConfig>();

        while (index < lines.Length)
        {
            var line = PrepareLine(lines[index]);
            if (line.IsEmpty)
            {
                index++;
                continue;
            }

            if (line.Indent < expectedIndent)
                break;

            if (line.Indent != expectedIndent || !line.Content.StartsWith("- "))
                throw new FormatException($"Liste dnsTargets invalide : {line.Content}");

            var item = line.Content[2..].Trim();
            var scalarHost = Unquote(item);
            if (!string.IsNullOrWhiteSpace(scalarHost) && !TryParseScalar(item, out _, out _))
            {
                items.Add(new DnsTargetConfig { Host = scalarHost });
                index++;
                continue;
            }

            var host = string.Empty;
            string? expectedAddress = null;
            string? reverseLookupIp = null;
            string? expectedHost = null;

            if (TryParseScalar(item, out var inlineKey, out var inlineValue))
                ApplyDnsField(inlineKey, inlineValue, ref host, ref expectedAddress, ref reverseLookupIp, ref expectedHost);
            else
                throw new FormatException($"Entrée dnsTargets invalide : {line.Content}");

            index++;
            while (index < lines.Length)
            {
                var childLine = PrepareLine(lines[index]);
                if (childLine.IsEmpty)
                {
                    index++;
                    continue;
                }

                if (childLine.Indent <= expectedIndent)
                    break;

                if (childLine.Indent != expectedIndent + 2 || !TryParseScalar(childLine.Content, out var childKey, out var childValue))
                    throw new FormatException($"Entrée dnsTargets invalide : {childLine.Content}");

                ApplyDnsField(childKey, childValue, ref host, ref expectedAddress, ref reverseLookupIp, ref expectedHost);
                index++;
            }

            if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(reverseLookupIp))
                throw new FormatException("Chaque entrée dnsTargets doit contenir host ou ip.");

            items.Add(new DnsTargetConfig
            {
                Host = host,
                ExpectedAddress = expectedAddress,
                ReverseLookupIp = reverseLookupIp,
                ExpectedHost = expectedHost
            });
        }

        return items;
    }

    private static List<TcpTargetConfig> ParseTcpTargetList(string[] lines, ref int index, int expectedIndent)
    {
        var items = new List<TcpTargetConfig>();

        while (index < lines.Length)
        {
            var line = PrepareLine(lines[index]);
            if (line.IsEmpty)
            {
                index++;
                continue;
            }

            if (line.Indent < expectedIndent)
                break;

            if (line.Indent != expectedIndent || !line.Content.StartsWith("- "))
                throw new FormatException($"Liste tcpTargets invalide : {line.Content}");

            var item = line.Content[2..].Trim();
            if (TryParseHostPort(Unquote(item), out var scalarTarget))
            {
                items.Add(scalarTarget);
                index++;
                continue;
            }

            var host = string.Empty;
            var port = 0;

            if (TryParseScalar(item, out var inlineKey, out var inlineValue))
                ApplyTcpField(inlineKey, inlineValue, ref host, ref port);
            else
                throw new FormatException($"Entrée tcpTargets invalide : {line.Content}");

            index++;
            while (index < lines.Length)
            {
                var childLine = PrepareLine(lines[index]);
                if (childLine.IsEmpty)
                {
                    index++;
                    continue;
                }

                if (childLine.Indent <= expectedIndent)
                    break;

                if (childLine.Indent != expectedIndent + 2 || !TryParseScalar(childLine.Content, out var childKey, out var childValue))
                    throw new FormatException($"Entrée tcpTargets invalide : {childLine.Content}");

                ApplyTcpField(childKey, childValue, ref host, ref port);
                index++;
            }

            if (string.IsNullOrWhiteSpace(host) || port <= 0)
                throw new FormatException("Chaque entrée tcpTargets doit contenir host et port.");

            items.Add(new TcpTargetConfig { Host = host, Port = port });
        }

        return items;
    }

    private static void ApplyTcpField(string key, string value, ref string host, ref int port)
    {
        switch (key)
        {
            case "host":
                host = Unquote(value);
                break;
            case "port":
                port = ParseYamlPositiveInt(value, key);
                break;
        }
    }

    private static void ApplyHttpField(string key, string value, ref string url, ref int? expectedStatusCode, ref string? containsText)
    {
        switch (key)
        {
            case "url":
                url = Unquote(value);
                break;
            case "expectedStatusCode":
                expectedStatusCode = ParseYamlPositiveInt(value, key);
                break;
            case "containsText":
                containsText = Unquote(value);
                break;
        }
    }

    private static void ApplyDnsField(string key, string value, ref string host, ref string? expectedAddress)
    {
        switch (key)
        {
            case "host":
                host = Unquote(value);
                break;
            case "expectedAddress":
                expectedAddress = Unquote(value);
                break;
        }
    }

    private static void ApplyDnsField(string key, string value, ref string host, ref string? expectedAddress, ref string? reverseLookupIp, ref string? expectedHost)
    {
        switch (key)
        {
            case "host":
                host = Unquote(value);
                break;
            case "expectedAddress":
                expectedAddress = Unquote(value);
                break;
            case "ip":
                reverseLookupIp = Unquote(value);
                break;
            case "expectedHost":
                expectedHost = Unquote(value);
                break;
        }
    }

    private static void SkipIndentedBlock(string[] lines, ref int index, int expectedIndent)
    {
        while (index < lines.Length)
        {
            var line = PrepareLine(lines[index]);
            if (line.IsEmpty)
            {
                index++;
                continue;
            }

            if (line.Indent < expectedIndent)
                break;

            index++;
        }
    }

    private static int ParseYamlPositiveInt(string value, string key)
    {
        if (int.TryParse(Unquote(value), out var parsedValue) && parsedValue > 0)
            return parsedValue;

        throw new FormatException($"La valeur YAML '{key}' doit être un entier positif.");
    }

    private static bool ParseYamlBoolean(string value, string key)
    {
        if (bool.TryParse(Unquote(value), out var parsedValue))
            return parsedValue;

        throw new FormatException($"La valeur YAML '{key}' doit être un booléen true/false.");
    }

    private static bool TryParseHostPort(string value, out TcpTargetConfig target)
    {
        target = new TcpTargetConfig();
        var lastColon = value.LastIndexOf(':');
        if (lastColon <= 0)
            return false;

        var host = value[..lastColon].Trim();
        var portRaw = value[(lastColon + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(host) || !int.TryParse(portRaw, out var port) || port <= 0)
            return false;

        target = new TcpTargetConfig { Host = host, Port = port };
        return true;
    }

    private static bool TryParseScalar(string content, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var separatorIndex = content.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == content.Length - 1)
            return false;

        key = content[..separatorIndex].Trim();
        value = content[(separatorIndex + 1)..].Trim();
        return true;
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2)
        {
            if ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\''))
                return trimmed[1..^1];
        }

        return trimmed;
    }

    private static ParsedLine PrepareLine(string rawLine)
    {
        var withoutComment = StripComment(rawLine);
        var trimmedEnd = withoutComment.TrimEnd();
        if (string.IsNullOrWhiteSpace(trimmedEnd))
            return new ParsedLine(0, string.Empty, true);

        var indent = 0;
        while (indent < trimmedEnd.Length && char.IsWhiteSpace(trimmedEnd[indent]))
            indent++;

        return new ParsedLine(indent, trimmedEnd[indent..], false);
    }

    private static string StripComment(string line)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var index = 0; index < line.Length; index++)
        {
            switch (line[index])
            {
                case '\'' when !inDoubleQuote:
                    inSingleQuote = !inSingleQuote;
                    break;
                case '"' when !inSingleQuote:
                    inDoubleQuote = !inDoubleQuote;
                    break;
                case '#' when !inSingleQuote && !inDoubleQuote:
                    return line[..index];
            }
        }

        return line;
    }

    private readonly record struct ParsedLine(int Indent, string Content, bool IsEmpty);
}

sealed class YamlAppConfig
{
    public string? AppVersion { get; set; }
    public string? PushoverToken { get; set; }
    public string? PushoverUser { get; set; }
    public string? StartupSound { get; set; }
    public string? ShutdownSound { get; set; }
    public int? SnoozeDays { get; set; }
    public string? ScheduleCron { get; set; }
    public int? ScheduleIntervalSeconds { get; set; }
    public bool? DashboardEnabled { get; set; }
    public bool? DashboardAuthEnabled { get; set; }
    public string? DashboardAuthUsername { get; set; }
    public string? DashboardAuthPasswordHash { get; set; }
    public string? DashboardAuthPasswordHashFile { get; set; }
    public int? DashboardSessionHours { get; set; }
    public int? DashboardRefreshSeconds { get; set; }
    public List<string>? PingTargets { get; set; }
    public List<TcpTargetConfig>? TcpTargets { get; set; }
    public List<HttpTargetConfig>? HttpTargets { get; set; }
    public List<DnsTargetConfig>? DnsTargets { get; set; }
}
