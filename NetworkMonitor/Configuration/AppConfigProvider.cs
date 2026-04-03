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
    public IReadOnlyList<string> PingTargets { get; init; } = [];
    public IReadOnlyList<TcpTargetConfig> TcpTargets { get; init; } = [];
}

sealed class TcpTargetConfig
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
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

        try
        {
            _changeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
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
            PingTargets = MergePingTargets(environmentPingTargets, yamlPingTargets),
            TcpTargets = MergeTcpTargets(environmentTcpTargets, yamlTcpTargets)
        };
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

    private static int ParsePositiveInt(string? rawValue, int defaultValue)
    {
        return int.TryParse(rawValue, out var value) && value > 0 ? value : defaultValue;
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
    public List<string>? PingTargets { get; set; }
    public List<TcpTargetConfig>? TcpTargets { get; set; }
}
