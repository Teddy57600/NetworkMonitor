using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

class HttpEndpointMonitorState
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private readonly HttpTargetConfig _target;
    private readonly ILogger _logger;
    private DateTime? _lastEscalationAt;
    private int _failCount;
    private bool _isDown;
    private DateTime? _downSince;
    private DateTime _lastCheckAllowed = DateTime.UtcNow;
    private DateTime? _lastCheckAt;
    private DateTime? _lastSuccessAt;
    private DateTime? _lastFailureAt;
    private double? _lastDurationMs;

    public HttpEndpointMonitorState(HttpTargetConfig target, ILogger logger)
    {
        _target = target;
        _logger = logger;

        var snapshot = StateStore.GetMonitor(MonitorKey);
        if (snapshot?.IsDown == true)
        {
            _isDown = true;
            _downSince = snapshot.DownSince;
            _lastEscalationAt = snapshot.DownSince;
        }
    }

    public async Task Check(CancellationToken ct = default)
    {
        if (DateTime.UtcNow < _lastCheckAllowed)
        {
            _logger.LogDebug("Circuit breaker ouvert pour {Url}, prochain essai à {Time:HH:mm:ss}", _target.Url, _lastCheckAllowed);
            return;
        }

        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var success = await CheckHttpWithRetry(ct);
        stopwatch.Stop();

        _lastCheckAt = startedAt;
        _lastDurationMs = stopwatch.Elapsed.TotalMilliseconds;

        if (!success)
        {
            _lastFailureAt = DateTime.UtcNow;
            _failCount++;

            if (!_isDown)
            {
                _isDown = true;
                _downSince = DateTime.UtcNow;
                _lastEscalationAt = _downSince;
                StateStore.StartIncident(MonitorKey, "HTTP", _target.Url, _downSince.Value);

                _logger.LogWarning("🔴 DOWN : endpoint HTTP {Url} indisponible après {Count} tentatives", _target.Url, _failCount * 3);
                await PushoverClient.SendAsync("🔴 DOWN", $"HTTP {_target.Url} KO", 1, MonitorKey, _logger, ct);

                _lastCheckAllowed = DateTime.UtcNow.AddMinutes(1);
                StateStore.SetMonitor(MonitorKey, new MonitorSnapshot { IsDown = true, DownSince = _downSince });
            }
            else if (_isDown && _downSince.HasValue && (DateTime.UtcNow - (_lastEscalationAt ?? _downSince.Value)).TotalMinutes > 5)
            {
                _logger.LogError("🚨 STILL DOWN : endpoint HTTP {Url} toujours KO depuis {Minutes:F0} min", _target.Url, (DateTime.UtcNow - _downSince.Value).TotalMinutes);
                await PushoverClient.SendAsync("🚨 STILL DOWN", $"HTTP {_target.Url} toujours KO", 2, MonitorKey, _logger, ct);
                _lastEscalationAt = DateTime.UtcNow;
            }
        }
        else
        {
            _lastSuccessAt = DateTime.UtcNow;
            if (_isDown)
            {
                StateStore.ResolveIncident(MonitorKey, _lastSuccessAt.Value);
                _logger.LogInformation("🟢 RECOVERY : endpoint HTTP {Url} de nouveau disponible", _target.Url);
                await PushoverClient.SendAsync("🟢 RECOVERY", $"HTTP {_target.Url} OK", 0, MonitorKey, _logger, ct);
            }

            _logger.LogInformation("HTTP {Url} est UP", _target.Url);
            _failCount = 0;
            _isDown = false;
            _downSince = null;
            _lastEscalationAt = null;
            StateStore.SetMonitor(MonitorKey, new MonitorSnapshot { IsDown = false });
        }
    }

    public DashboardMonitorSnapshot GetDashboardSnapshot()
    {
        var snoozeUntil = PushoverSnooze.GetSnoozeUntil(MonitorKey);
        if (snoozeUntil <= DateTime.UtcNow)
            snoozeUntil = DateTime.MinValue;

        var circuitOpenUntil = _lastCheckAllowed > DateTime.UtcNow ? _lastCheckAllowed : DateTime.MinValue;

        return new DashboardMonitorSnapshot
        {
            Key = MonitorKey,
            Type = "HTTP",
            DisplayName = _target.Url,
            Source = AppConfigProvider.GetHttpTargetSource(_target.Url),
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

    private string MonitorKey => $"HTTP:{_target.Url}";

    private async Task<bool> CheckHttpWithRetry(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var response = await Client.GetAsync(_target.Url, ct);
                if (_target.ExpectedStatusCode.HasValue && (int)response.StatusCode != _target.ExpectedStatusCode.Value)
                {
                    _logger.LogDebug("HTTP {Url} — tentative {Attempt}/3 : code {StatusCode} inattendu", _target.Url, attempt, (int)response.StatusCode);
                }
                else if (!string.IsNullOrWhiteSpace(_target.ContainsText))
                {
                    var content = await response.Content.ReadAsStringAsync(ct);
                    if (!content.Contains(_target.ContainsText, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("HTTP {Url} — tentative {Attempt}/3 : texte attendu introuvable", _target.Url, attempt);
                    }
                    else
                    {
                        return true;
                    }
                }
                else if (response.IsSuccessStatusCode)
                {
                    return true;
                }
                else
                {
                    _logger.LogDebug("HTTP {Url} — tentative {Attempt}/3 : HTTP {StatusCode}", _target.Url, attempt, (int)response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "HTTP {Url} — tentative {Attempt}/3 : exception", _target.Url, attempt);
            }

            await Task.Delay(1000, ct);
        }

        return false;
    }
}
