using System.Net.NetworkInformation;
using System.Net.Http;
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
                .AddProvider(new FileLoggerProvider("logs")));

        var logger = loggerFactory.CreateLogger<Program>();
        var monitors = ips.ToDictionary(
            ip => ip,
            ip => new MonitorState(ip, loggerFactory.CreateLogger<MonitorState>()));
        var schedule = BuildSchedule();

        logger.LogInformation("NetworkMonitor démarré — {Schedule}", schedule.Description);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        while (!cts.Token.IsCancellationRequested)
        {
            foreach (var monitor in monitors.Values)
                await monitor.Check();

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

    public string Description => $"CRON \"{_expression}\"";

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
    }

    public async Task Check()
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
                await Send("🔴 DOWN", $"IP {_ip} KO", 1);

                // ouvre circuit pendant 1 min
                _lastCheckAllowed = DateTime.UtcNow.AddMinutes(1);
            }
            else if (_isDown && _downSince.HasValue &&
                     (DateTime.UtcNow - _downSince.Value).TotalMinutes > 5)
            {
                _logger.LogError("🚨 STILL DOWN : IP {Ip} toujours KO depuis {Minutes:F0} min", _ip, (DateTime.UtcNow - _downSince.Value).TotalMinutes);
                await Send("🚨 STILL DOWN", $"IP {_ip} toujours KO", 2);

                _downSince = DateTime.UtcNow;
            }
        }
        else
        {
            if (_isDown)
            {
                _logger.LogInformation("🟢 RECOVERY : IP {Ip} de nouveau joignable", _ip);
                await Send("🟢 RECOVERY", $"IP {_ip} OK", 0);
            }

            _logger.LogInformation("IP {Ip} est UP", _ip);
            _failCount = 0;
            _isDown = false;
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

    private async Task Send(string title, string message, int priority)
    {
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
                new FormUrlEncodedContent(data));

            _logger.LogDebug("Notification Pushover envoyée : {Title} (HTTP {StatusCode})", title, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec de l'envoi de la notification Pushover : {Title}", title);
        }
    }
}