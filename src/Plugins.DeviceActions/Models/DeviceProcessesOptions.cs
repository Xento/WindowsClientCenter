namespace WindowsClientCenter.Plugins.DeviceActions.Models;

public sealed class DeviceProcessesOptions
{
    public ProcessViewMode DefaultViewMode { get; init; } = ProcessViewMode.List;
    public IReadOnlyList<int> RefreshIntervalsSeconds { get; init; } = [0, 5, 10, 30, 60];
    public int DefaultRefreshIntervalSeconds { get; init; }

    public static DeviceProcessesOptions FromSettings(IReadOnlyDictionary<string, string> settings)
    {
        var configuredIntervals = ParseIntervals(GetString(settings, "refreshIntervals", string.Empty));
        var intervals = configuredIntervals.Count == 0 ? [0, 5, 10, 30, 60] : configuredIntervals;
        if (!intervals.Contains(0))
        {
            intervals.Insert(0, 0);
        }

        intervals = intervals
            .Where(static interval => interval >= 0)
            .Distinct()
            .OrderBy(static interval => interval)
            .ToList();

        var defaultRefreshIntervalSeconds = GetInt(settings, "defaultRefreshIntervalSeconds", 0);
        if (!intervals.Contains(defaultRefreshIntervalSeconds))
        {
            defaultRefreshIntervalSeconds = 0;
        }

        return new DeviceProcessesOptions
        {
            DefaultViewMode = ParseViewMode(GetString(settings, "defaultViewMode", "list")),
            RefreshIntervalsSeconds = intervals,
            DefaultRefreshIntervalSeconds = defaultRefreshIntervalSeconds
        };
    }

    private static string GetString(IReadOnlyDictionary<string, string> settings, string key, string defaultValue)
    {
        return settings.TryGetValue(key, out var value) ? value : defaultValue;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> settings, string key, int defaultValue)
    {
        return settings.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static ProcessViewMode ParseViewMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "tree" => ProcessViewMode.Tree,
            _ => ProcessViewMode.List
        };
    }

    private static List<int> ParseIntervals(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static token => int.TryParse(token, out var parsed) ? parsed : -1)
            .Where(static parsed => parsed >= 0)
            .ToList();
    }
}

public enum ProcessViewMode
{
    List,
    Tree
}
