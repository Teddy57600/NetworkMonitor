using System.Text.Json.Serialization;

namespace NetworkMonitor;

sealed class DashboardSnapshot
{
    public DateTimeOffset GeneratedAt { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public string Version { get; init; } = string.Empty;
    public string Schedule { get; init; } = string.Empty;
    public int DefaultSnoozeDays { get; init; }
    public string ConfigPath { get; init; } = string.Empty;
    public int ConfigVersion { get; init; }
    public string TimeZone { get; init; } = string.Empty;
    public int RefreshIntervalSeconds { get; init; }
    public DashboardSummary Summary { get; init; } = new();
    public IReadOnlyList<DashboardMonitorSnapshot> PingMonitors { get; init; } = [];
    public IReadOnlyList<DashboardMonitorSnapshot> TcpMonitors { get; init; } = [];
    public IReadOnlyList<DashboardMonitorSnapshot> HttpMonitors { get; init; } = [];
    public IReadOnlyList<DashboardMonitorSnapshot> DnsMonitors { get; init; } = [];
    public IReadOnlyList<DashboardIncidentSnapshot> RecentIncidents { get; init; } = [];
}

sealed class DashboardActionResponse
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

sealed class DashboardConfigDocument
{
    public bool Success { get; init; }
    public string Path { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}

sealed class DashboardSummary
{
    public int Total { get; init; }
    public int Up { get; init; }
    public int Down { get; init; }
    public int Snoozed { get; init; }
}

sealed class DashboardMonitorSnapshot
{
    public string Key { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? HostName { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool IsDown { get; init; }
    public int FailCount { get; init; }
    public DateTime? LastCheckAt { get; init; }
    public DateTime? LastSuccessAt { get; init; }
    public DateTime? LastFailureAt { get; init; }
    public DateTime? DownSince { get; init; }
    public DateTime? CircuitOpenUntil { get; init; }
    public DateTime? SnoozeUntil { get; init; }
    public double? LastDurationMs { get; init; }
}

sealed class DashboardIncidentSnapshot
{
    public string Id { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public bool IsOpen { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DashboardSnapshot))]
[JsonSerializable(typeof(DashboardSummary))]
[JsonSerializable(typeof(DashboardActionResponse))]
[JsonSerializable(typeof(DashboardConfigDocument))]
[JsonSerializable(typeof(DashboardMonitorSnapshot[]))]
[JsonSerializable(typeof(DashboardIncidentSnapshot[]))]
internal partial class DashboardJsonContext : JsonSerializerContext
{
}
