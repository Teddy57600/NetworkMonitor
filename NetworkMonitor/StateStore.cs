using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkMonitor;

static class StateStore
{
    public static readonly string DataDir =
        Environment.GetEnvironmentVariable("DATA_DIR") ?? ".";

    private static readonly string FilePath =
        Path.Combine(DataDir, "state.json");

    private static readonly object _lock = new();
    private static AppState _state = Load();

    public static DateTime SnoozeUntil
    {
        get { lock (_lock) return _state.SnoozeUntil; }
    }

    public static void SetSnooze(DateTime until)
    {
        lock (_lock)
        {
            _state.SnoozeUntil = until;
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

    private static AppState Load()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
            }
        }
        catch { }
        return new AppState();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }
}

sealed class AppState
{
    [JsonPropertyName("snoozeUntil")]
    public DateTime SnoozeUntil { get; set; } = DateTime.MinValue;

    [JsonPropertyName("monitors")]
    public Dictionary<string, MonitorSnapshot> Monitors { get; set; } = new();
}

sealed class MonitorSnapshot
{
    [JsonPropertyName("isDown")]
    public bool IsDown { get; set; }

    [JsonPropertyName("downSince")]
    public DateTime? DownSince { get; set; }
}
