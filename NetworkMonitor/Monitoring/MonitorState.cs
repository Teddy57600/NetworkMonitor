using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

class MonitorState
{
    private readonly string _ip;
    private readonly ILogger _logger;
    private int _failCount = 0;
    private bool _isDown = false;
    private DateTime? _downSince = null;
    private DateTime _lastCheckAllowed = DateTime.UtcNow;
    private DateTime? _lastCheckAt;
    private DateTime? _lastSuccessAt;
    private DateTime? _lastFailureAt;
    private double? _lastDurationMs;

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

        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        bool success = await PingWithRetry();
        stopwatch.Stop();

        _lastCheckAt = startedAt;
        _lastDurationMs = stopwatch.Elapsed.TotalMilliseconds;

        if (!success)
        {
            _lastFailureAt = DateTime.UtcNow;
            _failCount++;

            if (_failCount >= 3 && !_isDown)
            {
                _isDown = true;
                _downSince = DateTime.UtcNow;

                _logger.LogWarning("🔴 DOWN : IP {Ip} injoignable après {Count} échecs consécutifs", _ip, _failCount);
                await PushoverClient.SendAsync("🔴 DOWN", $"IP {_ip} KO", 1, _ip, _logger, ct);

                // ouvre circuit pendant 1 min
                _lastCheckAllowed = DateTime.UtcNow.AddMinutes(1);
                StateStore.SetMonitor(_ip, new MonitorSnapshot { IsDown = true, DownSince = _downSince });
            }
            else if (_isDown && _downSince.HasValue &&
                     (DateTime.UtcNow - _downSince.Value).TotalMinutes > 5)
            {
                _logger.LogError("🚨 STILL DOWN : IP {Ip} toujours KO depuis {Minutes:F0} min", _ip, (DateTime.UtcNow - _downSince.Value).TotalMinutes);
                await PushoverClient.SendAsync("🚨 STILL DOWN", $"IP {_ip} toujours KO", 2, _ip, _logger, ct);

                _downSince = DateTime.UtcNow;
            }
        }
        else
        {
            _lastSuccessAt = DateTime.UtcNow;
            if (_isDown)
            {
                _logger.LogInformation("🟢 RECOVERY : IP {Ip} de nouveau joignable", _ip);
                await PushoverClient.SendAsync("🟢 RECOVERY", $"IP {_ip} OK", 0, _ip, _logger, ct);
            }

            _logger.LogInformation("IP {Ip} est UP", _ip);
            _failCount = 0;
            _isDown = false;
            StateStore.SetMonitor(_ip, new MonitorSnapshot { IsDown = false });
        }
    }

    public DashboardMonitorSnapshot GetDashboardSnapshot()
    {
        var snoozeUntil = PushoverSnooze.GetSnoozeUntil(_ip);
        if (snoozeUntil <= DateTime.UtcNow)
            snoozeUntil = DateTime.MinValue;

        var circuitOpenUntil = _lastCheckAllowed > DateTime.UtcNow
            ? _lastCheckAllowed
            : DateTime.MinValue;

        return new DashboardMonitorSnapshot
        {
            Key = _ip,
            Type = "Ping",
            DisplayName = _ip,
            Status = _isDown ? "DOWN" : "UP",
            IsDown = _isDown,
            FailCount = _failCount,
            LastCheckAt = _lastCheckAt,
            LastSuccessAt = _lastSuccessAt,
            LastFailureAt = _lastFailureAt,
            DownSince = _downSince,
            CircuitOpenUntil = circuitOpenUntil == DateTime.MinValue ? null : circuitOpenUntil,
            SnoozeUntil = snoozeUntil == DateTime.MinValue ? null : snoozeUntil,
            LastDurationMs = _lastDurationMs
        };
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
}
