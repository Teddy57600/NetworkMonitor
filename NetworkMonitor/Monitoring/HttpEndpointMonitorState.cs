using System.Text.Json;
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
    private bool _isWarning;
    private DateTime? _downSince;
    private DateTime _lastCheckAllowed = DateTime.UtcNow;
    private DateTime? _lastCheckAt;
    private DateTime? _lastSuccessAt;
    private DateTime? _lastFailureAt;
    private double? _lastDurationMs;
    private int? _lastStatusCode;
    private string? _lastFailureReason;
    private string? _lastHeaderValue;
    private string? _lastJsonValue;

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
        var result = await CheckHttpWithRetry(ct);

        _lastCheckAt = startedAt;
        _lastDurationMs = result.DurationMs;
        _lastStatusCode = result.StatusCode;
        _lastFailureReason = result.FailureReason;
        _lastHeaderValue = result.HeaderValue;
        _lastJsonValue = result.JsonValue;
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

            if (_isWarning)
                _logger.LogWarning("🟠 WARNING : HTTP {Url} a répondu en {Duration:F0} ms (seuil {Threshold} ms)", _target.Url, _lastDurationMs, _target.MaxResponseTimeMs);
            else
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
            Status = _isDown ? "DOWN" : _isWarning ? "WARNING" : "UP",
            IsDown = _isDown,
            IsWarning = _isWarning,
            FailCount = _failCount,
            LastCheckAt = _lastCheckAt,
            LastSuccessAt = _lastSuccessAt,
            LastFailureAt = _lastFailureAt,
            DownSince = _downSince,
            CircuitOpenUntil = circuitOpenUntil == DateTime.MinValue ? null : circuitOpenUntil,
            SnoozeUntil = snoozeUntil == DateTime.MinValue ? null : snoozeUntil,
            LastDurationMs = _lastDurationMs,
            HttpStatusCode = _lastStatusCode,
            FailureReason = _lastFailureReason,
            HeaderValue = _lastHeaderValue,
            JsonValue = _lastJsonValue
        };
    }

    private string MonitorKey => $"HTTP:{_target.Url}";

    private async Task<HttpCheckResult> CheckHttpWithRetry(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var response = await Client.GetAsync(_target.Url, ct);
                stopwatch.Stop();
                var statusCode = (int)response.StatusCode;
                string? content = null;

                if (_target.ExpectedStatusCode.HasValue && statusCode != _target.ExpectedStatusCode.Value)
                {
                    _logger.LogDebug("HTTP {Url} — tentative {Attempt}/3 : code {StatusCode} inattendu", _target.Url, attempt, statusCode);
                    return Fail(stopwatch.Elapsed.TotalMilliseconds, statusCode, $"Code HTTP inattendu : {statusCode}");
                }

                if (!string.IsNullOrWhiteSpace(_target.ContainsText) || !string.IsNullOrWhiteSpace(_target.JsonPath))
                    content = await response.Content.ReadAsStringAsync(ct);

                if (!string.IsNullOrWhiteSpace(_target.ContainsText)
                    && (content is null || !content.Contains(_target.ContainsText, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogDebug("HTTP {Url} — tentative {Attempt}/3 : texte attendu introuvable", _target.Url, attempt);
                    return Fail(stopwatch.Elapsed.TotalMilliseconds, statusCode, "Texte attendu introuvable");
                }

                string? headerValue = null;
                if (!string.IsNullOrWhiteSpace(_target.ExpectedHeaderName))
                {
                    headerValue = GetHeaderValue(response, _target.ExpectedHeaderName);
                    if (string.IsNullOrWhiteSpace(headerValue))
                    {
                        _logger.LogDebug("HTTP {Url} — tentative {Attempt}/3 : header {HeaderName} absent", _target.Url, attempt, _target.ExpectedHeaderName);
                        return Fail(stopwatch.Elapsed.TotalMilliseconds, statusCode, $"Header attendu absent : {_target.ExpectedHeaderName}");
                    }

                    if (!string.IsNullOrWhiteSpace(_target.ExpectedHeaderContains)
                        && !headerValue.Contains(_target.ExpectedHeaderContains, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("HTTP {Url} — tentative {Attempt}/3 : header {HeaderName} sans la valeur attendue", _target.Url, attempt, _target.ExpectedHeaderName);
                        return Fail(stopwatch.Elapsed.TotalMilliseconds, statusCode, $"Valeur attendue absente du header {_target.ExpectedHeaderName}", headerValue: headerValue);
                    }
                }

                string? jsonValue = null;
                if (!string.IsNullOrWhiteSpace(_target.JsonPath))
                {
                    if (string.IsNullOrWhiteSpace(content))
                        return Fail(stopwatch.Elapsed.TotalMilliseconds, statusCode, "Réponse JSON vide");

                    if (!TryReadJsonValue(content, _target.JsonPath, out jsonValue))
                    {
                        _logger.LogDebug("HTTP {Url} — tentative {Attempt}/3 : chemin JSON {JsonPath} introuvable", _target.Url, attempt, _target.JsonPath);
                        return Fail(stopwatch.Elapsed.TotalMilliseconds, statusCode, $"Chemin JSON introuvable : {_target.JsonPath}");
                    }

                    if (!string.IsNullOrWhiteSpace(_target.ExpectedJsonValue)
                        && !string.Equals(jsonValue, _target.ExpectedJsonValue, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("HTTP {Url} — tentative {Attempt}/3 : valeur JSON {JsonPath} inattendue ({JsonValue})", _target.Url, attempt, _target.JsonPath, jsonValue);
                        return Fail(stopwatch.Elapsed.TotalMilliseconds, statusCode, $"Valeur JSON inattendue pour {_target.JsonPath}", headerValue: headerValue, jsonValue: jsonValue);
                    }
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("HTTP {Url} — tentative {Attempt}/3 : HTTP {StatusCode}", _target.Url, attempt, statusCode);
                    return Fail(stopwatch.Elapsed.TotalMilliseconds, statusCode, $"HTTP {statusCode}", headerValue: headerValue, jsonValue: jsonValue);
                }

                var isWarning = _target.MaxResponseTimeMs is int maxResponseTimeMs
                    && stopwatch.Elapsed.TotalMilliseconds > maxResponseTimeMs;

                return new HttpCheckResult(true, isWarning, stopwatch.Elapsed.TotalMilliseconds, statusCode, null, headerValue, jsonValue);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogDebug(ex, "HTTP {Url} — tentative {Attempt}/3 : exception", _target.Url, attempt);
                if (attempt == 3)
                    return Fail(stopwatch.Elapsed.TotalMilliseconds, null, ex.Message);
            }

            await Task.Delay(1000, ct);
        }

        return Fail(null, null, "Échec HTTP inattendu");

        static HttpCheckResult Fail(double? durationMs, int? statusCode, string failureReason, string? headerValue = null, string? jsonValue = null)
            => new(false, false, durationMs, statusCode, failureReason, headerValue, jsonValue);
    }

    private static string? GetHeaderValue(HttpResponseMessage response, string headerName)
    {
        if (response.Headers.TryGetValues(headerName, out var headerValues))
            return string.Join(", ", headerValues);

        if (response.Content.Headers.TryGetValues(headerName, out var contentHeaderValues))
            return string.Join(", ", contentHeaderValues);

        return null;
    }

    private static bool TryReadJsonValue(string content, string path, out string? value)
    {
        value = null;

        try
        {
            using var document = JsonDocument.Parse(content);
            JsonElement current = document.RootElement;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                    return false;
            }

            value = current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Number => current.ToString(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Null => null,
                _ => current.ToString()
            };

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private readonly record struct HttpCheckResult(bool Success, bool IsWarning, double? DurationMs, int? StatusCode, string? FailureReason, string? HeaderValue, string? JsonValue);
}
