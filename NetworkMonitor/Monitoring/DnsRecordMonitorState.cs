using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

class DnsRecordMonitorState
{
    private readonly DnsRecordTargetConfig _target;
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
    private string? _lastObservedValue;
    private string? _lastFailureReason;

    public DnsRecordMonitorState(DnsRecordTargetConfig target, ILogger logger)
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
            _logger.LogDebug("Circuit breaker ouvert pour DNS record {DisplayName}, prochain essai à {Time:HH:mm:ss}", DisplayName, _lastCheckAllowed);
            return;
        }

        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var result = await CheckDnsRecordWithRetry(ct);
        stopwatch.Stop();

        _lastCheckAt = startedAt;
        _lastDurationMs = stopwatch.Elapsed.TotalMilliseconds;
        _lastObservedValue = result.ObservedValue;
        _lastFailureReason = result.FailureReason;

        if (!result.Success)
        {
            _lastFailureAt = DateTime.UtcNow;
            _failCount++;

            if (!_isDown)
            {
                _isDown = true;
                _downSince = DateTime.UtcNow;
                _lastEscalationAt = _downSince;
                StateStore.StartIncident(MonitorKey, "DNS Record", DisplayName, _downSince.Value);

                _logger.LogWarning("🔴 DOWN : vérification DNS record impossible pour {DisplayName} après {Count} tentatives", DisplayName, _failCount * 3);
                await PushoverClient.SendAsync("🔴 DOWN", $"DNS Record {DisplayName} KO", 1, MonitorKey, _logger, ct);

                _lastCheckAllowed = DateTime.UtcNow.AddMinutes(1);
                StateStore.SetMonitor(MonitorKey, new MonitorSnapshot { IsDown = true, DownSince = _downSince });
            }
            else if (_isDown && _downSince.HasValue && (DateTime.UtcNow - (_lastEscalationAt ?? _downSince.Value)).TotalMinutes > 5)
            {
                _logger.LogError("🚨 STILL DOWN : DNS record {DisplayName} toujours KO depuis {Minutes:F0} min", DisplayName, (DateTime.UtcNow - _downSince.Value).TotalMinutes);
                await PushoverClient.SendAsync("🚨 STILL DOWN", $"DNS Record {DisplayName} toujours KO", 2, MonitorKey, _logger, ct);
                _lastEscalationAt = DateTime.UtcNow;
            }

            return;
        }

        _lastSuccessAt = DateTime.UtcNow;
        if (_isDown)
        {
            StateStore.ResolveIncident(MonitorKey, _lastSuccessAt.Value);
            _logger.LogInformation("🟢 RECOVERY : DNS record {DisplayName} de nouveau valide", DisplayName);
            await PushoverClient.SendAsync("🟢 RECOVERY", $"DNS Record {DisplayName} OK", 0, MonitorKey, _logger, ct);
        }

        _logger.LogInformation("DNS record {DisplayName} est UP", DisplayName);
        _failCount = 0;
        _isDown = false;
        _downSince = null;
        _lastEscalationAt = null;
        StateStore.SetMonitor(MonitorKey, new MonitorSnapshot { IsDown = false });
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
            Type = "DNS Record",
            DisplayName = DisplayName,
            HostName = _target.Host,
            RecordType = _target.RecordType,
            Source = AppConfigProvider.GetDnsRecordTargetSource(_target.Host, _target.RecordType, _target.ExpectedValue, _target.ContainsText),
            Status = _isDown ? "DOWN" : "UP",
            IsDown = _isDown,
            FailCount = _failCount,
            LastCheckAt = _lastCheckAt,
            LastSuccessAt = _lastSuccessAt,
            LastFailureAt = _lastFailureAt,
            DownSince = _downSince,
            CircuitOpenUntil = circuitOpenUntil == DateTime.MinValue ? null : circuitOpenUntil,
            SnoozeUntil = snoozeUntil == DateTime.MinValue ? null : snoozeUntil,
            LastDurationMs = _lastDurationMs,
            FailureReason = _lastFailureReason,
            HeaderValue = _lastObservedValue,
            JsonValue = _target.ExpectedValue
        };
    }

    private string MonitorKey => $"DNSREC:{_target.RecordType}:{_target.Host}:{_target.ExpectedValue}:{_target.ContainsText}";

    private string DisplayName => $"{_target.RecordType} {_target.Host}";

    private async Task<DnsRecordCheckResult> CheckDnsRecordWithRetry(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var values = await QueryDnsRecordAsync(ct);
                if (values.Count == 0)
                {
                    _logger.LogDebug("DNS record {DisplayName} — tentative {Attempt}/3 : aucune valeur retournée", DisplayName, attempt);
                    return new DnsRecordCheckResult(false, null, "Aucune valeur retournée");
                }

                var observedValue = string.Join(" | ", values);
                if (!string.IsNullOrWhiteSpace(_target.ExpectedValue)
                    && !values.Any(value => string.Equals(value, _target.ExpectedValue, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogDebug("DNS record {DisplayName} — tentative {Attempt}/3 : valeur attendue {ExpectedValue} absente", DisplayName, attempt, _target.ExpectedValue);
                    return new DnsRecordCheckResult(false, observedValue, $"Valeur attendue absente : {_target.ExpectedValue}");
                }

                if (!string.IsNullOrWhiteSpace(_target.ContainsText)
                    && !values.Any(value => value.Contains(_target.ContainsText, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogDebug("DNS record {DisplayName} — tentative {Attempt}/3 : texte attendu {ContainsText} absent", DisplayName, attempt, _target.ContainsText);
                    return new DnsRecordCheckResult(false, observedValue, $"Texte attendu absent : {_target.ContainsText}");
                }

                return new DnsRecordCheckResult(true, observedValue, null);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DNS record {DisplayName} — tentative {Attempt}/3 : exception", DisplayName, attempt);
                if (attempt == 3)
                    return new DnsRecordCheckResult(false, null, ex.Message);
            }

            await Task.Delay(1000, ct);
        }

        return new DnsRecordCheckResult(false, null, "Échec DNS record inattendu");
    }

    private async Task<IReadOnlyList<string>> QueryDnsRecordAsync(CancellationToken ct)
    {
        return await DnsQueryClient.QueryAsync(_target.Host, _target.RecordType, ct);
    }

    private readonly record struct DnsRecordCheckResult(bool Success, string? ObservedValue, string? FailureReason);
}
