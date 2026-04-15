using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

class TlsMonitorState
{
    private readonly TlsTargetConfig _target;
    private readonly ILogger _logger;
    private DateTime? _lastEscalationAt;
    private int _failCount;
    private bool _isDown;
    private bool _isWarning;
    private DateTime? _downSince;
    private DateTime _lastCheckAllowed = DateTime.UtcNow;
    private DateTime? _lastCheckAt;
    private DateTime? _lastSuccessAt;
    private DateTime? _lastFailureAt;
    private double? _lastDurationMs;
    private DateTimeOffset? _lastCertificateNotAfter;
    private string? _lastCertificateSubject;
    private string? _lastCertificateIssuer;
    private int? _lastDaysRemaining;

    public TlsMonitorState(TlsTargetConfig target, ILogger logger)
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
            _logger.LogDebug("Circuit breaker ouvert pour TLS {Target}, prochain essai à {Time:HH:mm:ss}", DisplayName, _lastCheckAllowed);
            return;
        }

        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var result = await CheckTlsWithRetry(ct);
        stopwatch.Stop();

        _lastCheckAt = startedAt;
        _lastDurationMs = stopwatch.Elapsed.TotalMilliseconds;
        _isWarning = result.IsWarning;

        if (!result.Success)
        {
            _lastFailureAt = DateTime.UtcNow;
            _failCount++;

            if (!_isDown)
            {
                _isDown = true;
                _downSince = DateTime.UtcNow;
                _lastEscalationAt = _downSince;
                StateStore.StartIncident(MonitorKey, "TLS", DisplayName, _downSince.Value);

                _logger.LogWarning("🔴 DOWN : vérification TLS impossible pour {Target} après {Count} tentatives", DisplayName, _failCount * 3);
                await PushoverClient.SendAsync("🔴 DOWN", $"TLS {DisplayName} KO", 1, MonitorKey, _logger, ct);

                _lastCheckAllowed = DateTime.UtcNow.AddMinutes(1);
                StateStore.SetMonitor(MonitorKey, new MonitorSnapshot { IsDown = true, DownSince = _downSince });
            }
            else if (_isDown && _downSince.HasValue && (DateTime.UtcNow - (_lastEscalationAt ?? _downSince.Value)).TotalMinutes > 5)
            {
                _logger.LogError("🚨 STILL DOWN : TLS {Target} toujours KO depuis {Minutes:F0} min", DisplayName, (DateTime.UtcNow - _downSince.Value).TotalMinutes);
                await PushoverClient.SendAsync("🚨 STILL DOWN", $"TLS {DisplayName} toujours KO", 2, MonitorKey, _logger, ct);
                _lastEscalationAt = DateTime.UtcNow;
            }

            return;
        }

        _lastSuccessAt = DateTime.UtcNow;
        if (_isDown)
        {
            StateStore.ResolveIncident(MonitorKey, _lastSuccessAt.Value);
            _logger.LogInformation("🟢 RECOVERY : TLS {Target} de nouveau valide", DisplayName);
            await PushoverClient.SendAsync("🟢 RECOVERY", $"TLS {DisplayName} OK", 0, MonitorKey, _logger, ct);
        }

        if (_isWarning)
        {
            _logger.LogWarning("🟠 WARNING : certificat TLS {Target} expire dans {DaysRemaining} jour(s)", DisplayName, _lastDaysRemaining);
        }
        else
        {
            _logger.LogInformation("TLS {Target} est UP", DisplayName);
        }

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
            Type = "TLS",
            DisplayName = DisplayName,
            HostName = _target.ExpectedHost,
            Source = AppConfigProvider.GetTlsTargetSource(_target.Host, _target.Port),
            Status = _isDown ? "DOWN" : _isWarning ? "WARNING" : "UP",
            IsDown = _isDown,
            FailCount = _failCount,
            LastCheckAt = _lastCheckAt,
            LastSuccessAt = _lastSuccessAt,
            LastFailureAt = _lastFailureAt,
            DownSince = _downSince,
            CircuitOpenUntil = circuitOpenUntil == DateTime.MinValue ? null : circuitOpenUntil,
            SnoozeUntil = snoozeUntil == DateTime.MinValue ? null : snoozeUntil,
            LastDurationMs = _lastDurationMs,
            CertificateNotAfter = _lastCertificateNotAfter,
            CertificateSubject = _lastCertificateSubject,
            CertificateIssuer = _lastCertificateIssuer,
            DaysRemaining = _lastDaysRemaining,
            IsWarning = _isWarning
        };
    }

    private string MonitorKey => $"TLS:{_target.Host}:{_target.Port}";

    private string DisplayName => $"{_target.Host}:{_target.Port}";

    private async Task<TlsCheckResult> CheckTlsWithRetry(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(_target.Host, _target.Port, ct);
                using var sslStream = new SslStream(tcpClient.GetStream(), false, (_, certificate, _, sslPolicyErrors) =>
                {
                    if (certificate is null)
                        return false;

                    if (sslPolicyErrors == SslPolicyErrors.None)
                        return true;

                    return sslPolicyErrors == SslPolicyErrors.RemoteCertificateNameMismatch && !string.IsNullOrWhiteSpace(_target.ExpectedHost);
                });

                var authTargetHost = string.IsNullOrWhiteSpace(_target.ExpectedHost) ? _target.Host : _target.ExpectedHost;
                await sslStream.AuthenticateAsClientAsync(authTargetHost, null, System.Security.Authentication.SslProtocols.None, true);
                var certificate = new X509Certificate2(sslStream.RemoteCertificate ?? throw new InvalidOperationException("Aucun certificat TLS retourné."));

                _lastCertificateSubject = certificate.Subject;
                _lastCertificateIssuer = certificate.Issuer;
                _lastCertificateNotAfter = certificate.NotAfter;
                _lastDaysRemaining = (int)Math.Floor((certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays);

                if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                {
                    _logger.LogDebug("TLS {Target} — tentative {Attempt}/3 : certificat expiré au {NotAfter:u}", DisplayName, attempt, certificate.NotAfter);
                }
                else
                {
                    var warningDays = _target.WarningDays ?? 30;
                    var isWarning = _lastDaysRemaining <= warningDays;
                    return new TlsCheckResult(true, isWarning);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TLS {Target} — tentative {Attempt}/3 : exception", DisplayName, attempt);
            }

            await Task.Delay(1000, ct);
        }

        return new TlsCheckResult(false, false);
    }

    private readonly record struct TlsCheckResult(bool Success, bool IsWarning);
}
