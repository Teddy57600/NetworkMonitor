using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkMonitor;

static class StateStore
{
    private const int MaxIncidentHistory = 100;

    public static readonly string DataDir =
        Environment.GetEnvironmentVariable("DATA_DIR") ?? ".";

    private static readonly string FilePath =
        Path.Combine(DataDir, "state.json");

    private static readonly object _lock = new();
    private static AppState _state = Load();

    public static DateTime GetSnoozeUntil(string key)
    {
        lock (_lock)
            return _state.Snooze.TryGetValue(key, out var dt) ? dt : DateTime.MinValue;
    }

    public static void SetSnooze(string key, DateTime until)
    {
        lock (_lock)
        {
            _state.Snooze[key] = until;
            Save();
        }
    }

    public static void ClearSnooze(string key)
    {
        lock (_lock)
        {
            if (_state.Snooze.Remove(key))
                Save();
        }
    }

    public static MonitorSnapshot? GetMonitor(string key)
    {
        lock (_lock)
            return _state.Monitors.TryGetValue(key, out var s) ? s : null;
    }

    public static void SetMonitor(string key, MonitorSnapshot snapshot)
    {
        lock (_lock)
        {
            _state.Monitors[key] = snapshot;
            Save();
        }
    }

    public static void StartIncident(string key, string type, string displayName, DateTime startedAt)
    {
        lock (_lock)
        {
            var existingOpenIncident = _state.Incidents
                .LastOrDefault(incident => string.Equals(incident.Key, key, StringComparison.OrdinalIgnoreCase) && incident.ResolvedAt is null);

            if (existingOpenIncident is not null)
                return;

            _state.Incidents.Add(new IncidentRecord
            {
                Id = Guid.NewGuid().ToString("n"),
                Key = key,
                Type = type,
                DisplayName = displayName,
                StartedAt = startedAt
            });

            if (_state.Incidents.Count > MaxIncidentHistory)
                _state.Incidents.RemoveRange(0, _state.Incidents.Count - MaxIncidentHistory);

            Save();
        }
    }

    public static void ResolveIncident(string key, DateTime resolvedAt)
    {
        lock (_lock)
        {
            var incident = _state.Incidents
                .LastOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase) && item.ResolvedAt is null);

            if (incident is null)
                return;

            incident.ResolvedAt = resolvedAt;
            Save();
        }
    }

    public static IReadOnlyList<IncidentRecord> GetRecentIncidents(int maxCount)
    {
        lock (_lock)
        {
            return _state.Incidents
                .OrderByDescending(incident => incident.StartedAt)
                .Take(maxCount)
                .Select(incident => new IncidentRecord
                {
                    Id = incident.Id,
                    Key = incident.Key,
                    Type = incident.Type,
                    DisplayName = incident.DisplayName,
                    StartedAt = incident.StartedAt,
                    ResolvedAt = incident.ResolvedAt
                })
                .ToArray();
        }
    }

    private static AppState Load()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize(json, AppStateJsonContext.Default.AppState) ?? new AppState();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StateStore] Échec du chargement de {FilePath} : {ex.Message}");
        }
        return new AppState();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(_state, AppStateJsonContext.Default.AppState);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StateStore] Échec de la sauvegarde dans {FilePath} : {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppState))]
internal partial class AppStateJsonContext : JsonSerializerContext { }

sealed class AppState
{
    [JsonPropertyName("snooze")]
    public Dictionary<string, DateTime> Snooze { get; set; } = new();

    [JsonPropertyName("monitors")]
    public Dictionary<string, MonitorSnapshot> Monitors { get; set; } = new();

    [JsonPropertyName("incidents")]
    public List<IncidentRecord> Incidents { get; set; } = [];
}

sealed class MonitorSnapshot
{
    [JsonPropertyName("isDown")]
    public bool IsDown { get; set; }

    [JsonPropertyName("downSince")]
    public DateTime? DownSince { get; set; }
}

sealed class IncidentRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; }

    [JsonPropertyName("resolvedAt")]
    public DateTime? ResolvedAt { get; set; }
}
