using System.Net.NetworkInformation;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Cronos;
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

    static IReadOnlyList<(string Host, int Port)> ParseTcpTargets()
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

    static ISchedule BuildSchedule()
    {
        var cronExpr = Environment.GetEnvironmentVariable("SCHEDULE_CRON");
        if (!string.IsNullOrWhiteSpace(cronExpr))
            return new CronSchedule(cronExpr);

        var intervalStr = Environment.GetEnvironmentVariable("SCHEDULE_INTERVAL_SECONDS");
        int seconds = int.TryParse(intervalStr, out var s) && s > 0 ? s : 10;
        return new IntervalSchedule(seconds);
    }
}

interface ISchedule
{
    string Description { get; }
    Task WaitForNextAsync(CancellationToken ct);
}

class IntervalSchedule(int seconds) : ISchedule
{
    public string Description => $"intervalle toutes les {seconds}s";

    public Task WaitForNextAsync(CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(seconds), ct);
}

class CronSchedule : ISchedule
{
    private readonly CronExpression _cron;
    private readonly string _expression;

    public CronSchedule(string expression)
    {
        _expression = expression;
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _cron = parts.Length == 6
            ? CronExpression.Parse(expression, CronFormat.IncludeSeconds)
            : CronExpression.Parse(expression);
    }

    public string Description => CronDescription.ToFrench(_expression);

    public async Task WaitForNextAsync(CancellationToken ct)
    {
        var next = _cron.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        if (next is null) return;

        var delay = next.Value - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);
    }
}

class MonitorState
{
    private readonly string _ip;
    private readonly ILogger _logger;
    private int _failCount = 0;
    private bool _isDown = false;
    private DateTime? _downSince = null;
    private DateTime _lastCheckAllowed = DateTime.UtcNow;

    public MonitorState(string ip, ILogger logger)
    {
        _ip = ip;
        _logger = logger;
        var snapshot = StateStore.GetMonitor(ip);
        if (snapshot?.IsDown == true)
        {
            _isDown = true;
            _downSince = snapshot.DownSince;
        }
    }

    public async Task Check(CancellationToken ct = default)
    {
        // Circuit breaker OPEN
        if (DateTime.UtcNow < _lastCheckAllowed)
        {
            _logger.LogDebug("Circuit breaker ouvert pour {Ip}, prochain essai à {Time:HH:mm:ss}", _ip, _lastCheckAllowed);
            return;
        }

        bool success = await PingWithRetry();

        if (!success)
        {
            _failCount++;

            if (_failCount >= 3 && !_isDown)
            {
                _isDown = true;
                _downSince = DateTime.UtcNow;

                _logger.LogWarning("🔴 DOWN : IP {Ip} injoignable après {Count} échecs consécutifs", _ip, _failCount);
                await Send("🔴 DOWN", $"IP {_ip} KO", 1, ct);

                // ouvre circuit pendant 1 min
                _lastCheckAllowed = DateTime.UtcNow.AddMinutes(1);
                StateStore.SetMonitor(_ip, new MonitorSnapshot { IsDown = true, DownSince = _downSince });
            }
            else if (_isDown && _downSince.HasValue &&
                     (DateTime.UtcNow - _downSince.Value).TotalMinutes > 5)
            {
                _logger.LogError("🚨 STILL DOWN : IP {Ip} toujours KO depuis {Minutes:F0} min", _ip, (DateTime.UtcNow - _downSince.Value).TotalMinutes);
                await Send("🚨 STILL DOWN", $"IP {_ip} toujours KO", 2, ct);

                _downSince = DateTime.UtcNow;
            }
        }
        else
        {
            if (_isDown)
            {
                _logger.LogInformation("🟢 RECOVERY : IP {Ip} de nouveau joignable", _ip);
                await Send("🟢 RECOVERY", $"IP {_ip} OK", 0, ct);
            }

            _logger.LogInformation("IP {Ip} est UP", _ip);
            _failCount = 0;
            _isDown = false;
            StateStore.SetMonitor(_ip, new MonitorSnapshot { IsDown = false });
        }
    }

    private async Task<bool> PingWithRetry()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var ping = new Ping();
                var reply = await ping.SendPingAsync(_ip, 3000);

                if (reply.Status == IPStatus.Success)
                    return true;

                _logger.LogDebug("Ping {Ip} — tentative {Attempt}/3 : {Status}", _ip, i + 1, reply.Status);
            }
            catch (Exception ex)
            {
                            _logger.LogDebug(ex, "Ping {Ip} — tentative {Attempt}/3 : exception", _ip, i + 1);
                            }

                            await Task.Delay(1000);
                        }

                        return false;
                    }

                    private async Task Send(string title, string message, int priority, CancellationToken ct = default)
                    {
                        if (PushoverSnooze.IsSnoozed)
                        {
                            _logger.LogDebug("🔕 Notification ignorée (snooze actif jusqu'au {Until:dd/MM/yyyy HH:mm} UTC) : {Title}", PushoverSnooze.SnoozeUntil, title);
                            return;
                        }

                        try
                        {
                            using var client = new HttpClient();

                            var data = new Dictionary<string, string>
                            {
                                ["token"] = Environment.GetEnvironmentVariable("PUSHOVER_TOKEN")!,
                                ["user"] = Environment.GetEnvironmentVariable("PUSHOVER_USER")!,
                                ["title"] = title,
                                ["message"] = message,
                                ["priority"] = priority.ToString()
                            };

                            if (priority == 2)
                            {
                                data["retry"] = "30";
                                data["expire"] = "300";
                            }

                            var response = await client.PostAsync("https://api.pushover.net/1/messages.json",
                                new FormUrlEncodedContent(data), ct);

                            _logger.LogDebug("Notification Pushover envoyée : {Title} (HTTP {StatusCode})", title, (int)response.StatusCode);

                            if (priority == 2 && response.IsSuccessStatusCode)
                            {
                                var json = await response.Content.ReadAsStringAsync(ct);
                                var doc = JsonDocument.Parse(json);
                                if (doc.RootElement.TryGetProperty("receipt", out var receiptProp))
                                {
                                    var receipt = receiptProp.GetString();
                                    if (!string.IsNullOrEmpty(receipt))
                                        PushoverSnooze.StartWatching(receipt, _logger, ct);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Échec de l'envoi de la notification Pushover : {Title}", title);
                        }
                    }
                }

class TcpPortMonitorState
{
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger _logger;
    private int _failCount = 0;
    private bool _isDown = false;
    private DateTime? _downSince = null;
    private DateTime _lastCheckAllowed = DateTime.UtcNow;

    public TcpPortMonitorState(string host, int port, ILogger logger)
    {
        _host = host;
        _port = port;
        _logger = logger;
        var snapshot = StateStore.GetMonitor($"{host}:{port}");
        if (snapshot?.IsDown == true)
        {
            _isDown = true;
            _downSince = snapshot.DownSince;
        }
    }

    public async Task Check(CancellationToken ct = default)
    {
        // Circuit breaker OPEN
        if (DateTime.UtcNow < _lastCheckAllowed)
        {
            _logger.LogDebug("Circuit breaker ouvert pour {Host}:{Port}, prochain essai à {Time:HH:mm:ss}", _host, _port, _lastCheckAllowed);
            return;
        }

        bool success = await TcpCheckWithRetry();

        if (!success)
        {
            _failCount++;

            if (!_isDown)
            {
                _isDown = true;
                _downSince = DateTime.UtcNow;

                _logger.LogWarning("🔴 DOWN : {Host}:{Port} inaccessible après {Count} tentatives", _host, _port, _failCount * 3);
                await Send("🔴 DOWN", $"Port TCP {_port} ({_host}) KO", 1, ct);

                // ouvre circuit pendant 1 min
                _lastCheckAllowed = DateTime.UtcNow.AddMinutes(1);
                StateStore.SetMonitor($"{_host}:{_port}", new MonitorSnapshot { IsDown = true, DownSince = _downSince });
            }
            else if (_isDown && _downSince.HasValue &&
                     (DateTime.UtcNow - _downSince.Value).TotalMinutes > 5)
            {
                _logger.LogError("🚨 STILL DOWN : {Host}:{Port} toujours KO depuis {Minutes:F0} min", _host, _port, (DateTime.UtcNow - _downSince.Value).TotalMinutes);
                await Send("🚨 STILL DOWN", $"Port TCP {_port} ({_host}) toujours KO", 2, ct);

                _downSince = DateTime.UtcNow;
            }
        }
        else
        {
            if (_isDown)
            {
                _logger.LogInformation("🟢 RECOVERY : {Host}:{Port} de nouveau accessible", _host, _port);
                await Send("🟢 RECOVERY", $"Port TCP {_port} ({_host}) OK", 0, ct);
            }

            _logger.LogInformation("TCP {Host}:{Port} est UP", _host, _port);
            _failCount = 0;
            _isDown = false;
            StateStore.SetMonitor($"{_host}:{_port}", new MonitorSnapshot { IsDown = false });
        }
    }

    private async Task<bool> TcpCheckWithRetry()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var client = new TcpClient();
                await client.ConnectAsync(_host, _port, cts.Token);
                return true;
            }
            catch (Exception ex)
            {
                            _logger.LogDebug("TCP {Host}:{Port} — tentative {Attempt}/3 : exception", _host, _port, i + 1);
                            }

                            await Task.Delay(1000);
                        }

                        return false;
                    }

                    private async Task Send(string title, string message, int priority, CancellationToken ct = default)
                    {
                        if (PushoverSnooze.IsSnoozed)
                        {
                            _logger.LogDebug("🔕 Notification ignorée (snooze actif jusqu'au {Until:dd/MM/yyyy HH:mm} UTC) : {Title}", PushoverSnooze.SnoozeUntil, title);
                            return;
                        }

                        try
                        {
                            using var client = new HttpClient();

                            var data = new Dictionary<string, string>
                            {
                                ["token"] = Environment.GetEnvironmentVariable("PUSHOVER_TOKEN")!,
                                ["user"] = Environment.GetEnvironmentVariable("PUSHOVER_USER")!,
                                ["title"] = title,
                                ["message"] = message,
                                ["priority"] = priority.ToString()
                            };

                            if (priority == 2)
                            {
                                data["retry"] = "30";
                                data["expire"] = "300";
                            }

                            var response = await client.PostAsync("https://api.pushover.net/1/messages.json",
                                new FormUrlEncodedContent(data), ct);

                            _logger.LogDebug("Notification Pushover envoyée : {Title} (HTTP {StatusCode})", title, (int)response.StatusCode);

                            if (priority == 2 && response.IsSuccessStatusCode)
                            {
                                var json = await response.Content.ReadAsStringAsync(ct);
                                var doc = JsonDocument.Parse(json);
                                if (doc.RootElement.TryGetProperty("receipt", out var receiptProp))
                                {
                                    var receipt = receiptProp.GetString();
                                    if (!string.IsNullOrEmpty(receipt))
                                        PushoverSnooze.StartWatching(receipt, _logger, ct);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Échec de l'envoi de la notification Pushover : {Title}", title);
                        }
                    }
                }

                static class PushoverSnooze
                {
                    private static DateTime _snoozeUntil = StateStore.SnoozeUntil;

                    public static DateTime SnoozeUntil => _snoozeUntil;
                    public static bool IsSnoozed => DateTime.UtcNow < _snoozeUntil;

                    private static int SnoozeDays =>
                        int.TryParse(Environment.GetEnvironmentVariable("SNOOZE_DAYS"), out var d) && d > 0 ? d : 1;

                    public static void StartWatching(string receipt, ILogger logger, CancellationToken ct)
                    {
                        _ = Task.Run(() => WatchAsync(receipt, logger, ct), CancellationToken.None);
                    }

                    private static async Task WatchAsync(string receipt, ILogger logger, CancellationToken ct)
                    {
                        var token = Environment.GetEnvironmentVariable("PUSHOVER_TOKEN")!;
                        var url = $"https://api.pushover.net/1/receipts/{receipt}.json?token={token}";
                        var deadline = DateTime.UtcNow.AddSeconds(300);

                        using var client = new HttpClient();

                        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
                        {
                            try
                            {
                                await Task.Delay(TimeSpan.FromSeconds(30), ct);

                                var json = await client.GetStringAsync(url, ct);
                                var doc = JsonDocument.Parse(json);

                                if (doc.RootElement.TryGetProperty("acknowledged", out var ackProp) && ackProp.GetInt32() == 1)
                                {
                                    _snoozeUntil = DateTime.UtcNow.AddDays(SnoozeDays);
                                    StateStore.SetSnooze(_snoozeUntil);
                                    logger.LogInformation("🔕 Notification acquittée — notifications suspendues jusqu'au {Until:dd/MM/yyyy HH:mm} UTC", _snoozeUntil);
                                    return;
                                }
                            }
                            catch (OperationCanceledException) { return; }
                            catch (Exception ex)
                            {
                                logger.LogDebug(ex, "Impossible de vérifier le reçu Pushover {Receipt}", receipt);
                            }
                        }
                    }
                }