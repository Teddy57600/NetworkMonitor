using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

class DnsMonitorState
{
    private readonly DnsTargetConfig _target;
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
    private string? _lastResolvedHostName;

    public DnsMonitorState(DnsTargetConfig target, ILogger logger)
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
            _logger.LogDebug("Circuit breaker ouvert pour {Host}, prochain essai à {Time:HH:mm:ss}", _target.Host, _lastCheckAllowed);
            return;
        }

        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var success = await CheckDnsWithRetry(ct);
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
                StateStore.StartIncident(MonitorKey, "DNS", DisplayName, _downSince.Value);

                _logger.LogWarning("🔴 DOWN : vérification DNS impossible pour {DisplayName} après {Count} tentatives", DisplayName, _failCount * 3);
                await PushoverClient.SendAsync("🔴 DOWN", $"DNS {DisplayName} KO", 1, MonitorKey, _logger, ct);

                _lastCheckAllowed = DateTime.UtcNow.AddMinutes(1);
                StateStore.SetMonitor(MonitorKey, new MonitorSnapshot { IsDown = true, DownSince = _downSince });
            }
            else if (_isDown && _downSince.HasValue && (DateTime.UtcNow - (_lastEscalationAt ?? _downSince.Value)).TotalMinutes > 5)
            {
                _logger.LogError("🚨 STILL DOWN : DNS {DisplayName} toujours KO depuis {Minutes:F0} min", DisplayName, (DateTime.UtcNow - _downSince.Value).TotalMinutes);
                await PushoverClient.SendAsync("🚨 STILL DOWN", $"DNS {DisplayName} toujours KO", 2, MonitorKey, _logger, ct);
                _lastEscalationAt = DateTime.UtcNow;
            }
        }
        else
        {
            _lastSuccessAt = DateTime.UtcNow;
            if (_isDown)
            {
                StateStore.ResolveIncident(MonitorKey, _lastSuccessAt.Value);
                _logger.LogInformation("🟢 RECOVERY : DNS {DisplayName} de nouveau valide", DisplayName);
                await PushoverClient.SendAsync("🟢 RECOVERY", $"DNS {DisplayName} OK", 0, MonitorKey, _logger, ct);
            }

            _logger.LogInformation("DNS {DisplayName} est UP", DisplayName);
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
            Type = "DNS",
            DisplayName = DisplayName,
            HostName = GetHostNameForDisplay(),
            Source = AppConfigProvider.GetDnsTargetSource(!string.IsNullOrWhiteSpace(_target.ReverseLookupIp) ? _target.ReverseLookupIp! : _target.Host),
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

    private string MonitorKey => !string.IsNullOrWhiteSpace(_target.ReverseLookupIp)
        ? $"DNS:PTR:{_target.ReverseLookupIp}"
        : $"DNS:A:{_target.Host}";

    private string DisplayName => !string.IsNullOrWhiteSpace(_target.ReverseLookupIp)
        ? $"PTR {_target.ReverseLookupIp}"
        : _target.Host;

    private string? GetHostNameForDisplay()
    {
        if (string.IsNullOrWhiteSpace(_target.ReverseLookupIp))
            return null;

        return !string.IsNullOrWhiteSpace(_target.ExpectedHost)
            ? _target.ExpectedHost
            : _lastResolvedHostName;
    }

    private async Task<bool> CheckDnsWithRetry(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_target.ReverseLookupIp))
                {
                    if (!IPAddress.TryParse(_target.ReverseLookupIp, out var ipAddress))
                    {
                        _logger.LogDebug("DNS PTR {Ip} — tentative {Attempt}/3 : IP invalide", _target.ReverseLookupIp, attempt);
                    }
                    else
                    {
                        var entry = await Dns.GetHostEntryAsync(ipAddress);
                        _lastResolvedHostName = string.IsNullOrWhiteSpace(entry.HostName)
                            ? null
                            : entry.HostName.TrimEnd('.');

                        if (string.IsNullOrWhiteSpace(entry.HostName))
                        {
                            _logger.LogDebug("DNS PTR {Ip} — tentative {Attempt}/3 : aucun hostname retourné", _target.ReverseLookupIp, attempt);
                        }
                        else if (!string.IsNullOrWhiteSpace(_target.ExpectedHost) && !string.Equals(entry.HostName.TrimEnd('.'), _target.ExpectedHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogDebug("DNS PTR {Ip} — tentative {Attempt}/3 : hostname attendu {ExpectedHost} absent (retour {ActualHost})", _target.ReverseLookupIp, attempt, _target.ExpectedHost, entry.HostName);
                        }
                        else
                        {
                            return true;
                        }
                    }
                }
                else
                {
                    var addresses = await Dns.GetHostAddressesAsync(_target.Host, ct);
                    if (addresses.Length == 0)
                    {
                        _logger.LogDebug("DNS {Host} — tentative {Attempt}/3 : aucune adresse retournée", _target.Host, attempt);
                    }
                    else if (!string.IsNullOrWhiteSpace(_target.ExpectedAddress) && !addresses.Any(address => string.Equals(address.ToString(), _target.ExpectedAddress, StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogDebug("DNS {Host} — tentative {Attempt}/3 : adresse attendue {ExpectedAddress} absente", _target.Host, attempt, _target.ExpectedAddress);
                    }
                    else
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DNS {DisplayName} — tentative {Attempt}/3 : exception", DisplayName, attempt);
            }

            await Task.Delay(1000, ct);
        }

        return false;
    }
}
