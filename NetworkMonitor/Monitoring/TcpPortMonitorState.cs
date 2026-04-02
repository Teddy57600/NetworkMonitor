using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

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
                await PushoverClient.SendAsync("🔴 DOWN", $"Port TCP {_port} ({_host}) KO", 1, _logger, ct);

                // ouvre circuit pendant 1 min
                _lastCheckAllowed = DateTime.UtcNow.AddMinutes(1);
                StateStore.SetMonitor($"{_host}:{_port}", new MonitorSnapshot { IsDown = true, DownSince = _downSince });
            }
            else if (_isDown && _downSince.HasValue &&
                     (DateTime.UtcNow - _downSince.Value).TotalMinutes > 5)
            {
                _logger.LogError("🚨 STILL DOWN : {Host}:{Port} toujours KO depuis {Minutes:F0} min", _host, _port, (DateTime.UtcNow - _downSince.Value).TotalMinutes);
                await PushoverClient.SendAsync("🚨 STILL DOWN", $"Port TCP {_port} ({_host}) toujours KO", 2, _logger, ct);

                _downSince = DateTime.UtcNow;
            }
        }
        else
        {
            if (_isDown)
            {
                _logger.LogInformation("🟢 RECOVERY : {Host}:{Port} de nouveau accessible", _host, _port);
                await PushoverClient.SendAsync("🟢 RECOVERY", $"Port TCP {_port} ({_host}) OK", 0, _logger, ct);
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
}
