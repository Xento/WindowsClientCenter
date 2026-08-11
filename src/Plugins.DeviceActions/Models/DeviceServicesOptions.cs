namespace WindowsClientCenter.Plugins.DeviceActions.Models;

public sealed class DeviceServicesOptions
{
    public IReadOnlyList<ServiceFilterCategory> Categories { get; init; } =
    [
        new("All services", true, []),
        new(
            "MECM / Intune related",
            false,
            ["CcmExec", "ccmsetup", "CmRcService", "smstsmgr", "IntuneManagementExtension", "dmwappushservice", "BITS", "DoSvc"])
    ];

    public string DefaultCategoryName => Categories.Count == 0 ? "All services" : Categories[0].DisplayName;

    public static DeviceServicesOptions FromSettings(IReadOnlyDictionary<string, string> settings)
    {
        var categories = new List<ServiceFilterCategory>();
        for (var index = 0; ; index++)
        {
            var prefix = $"filters:{index}";
            if (!HasKey(settings, prefix))
            {
                break;
            }

            var displayName = GetString(settings, $"{prefix}:displayName", string.Empty);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            var includeAllServices = GetBool(settings, $"{prefix}:includeAllServices", false);
            var configuredServices = ParseServiceNames(GetString(settings, $"{prefix}:serviceNames", string.Empty));
            categories.Add(new ServiceFilterCategory(displayName.Trim(), includeAllServices, configuredServices));
        }

        if (categories.Count == 0)
        {
            return new DeviceServicesOptions();
        }

        if (!categories.Any(static category => category.IncludeAllServices))
        {
            categories.Insert(0, new ServiceFilterCategory("All services", true, []));
        }

        return new DeviceServicesOptions
        {
            Categories = categories
        };
    }

    private static bool HasKey(IReadOnlyDictionary<string, string> settings, string prefix)
    {
        return settings.Keys.Any(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetString(IReadOnlyDictionary<string, string> settings, string key, string defaultValue)
    {
        return settings.TryGetValue(key, out var value)
            ? value
            : defaultValue;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> settings, string key, bool defaultValue)
    {
        return settings.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static IReadOnlyList<string> ParseServiceNames(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public sealed record ServiceFilterCategory(string DisplayName, bool IncludeAllServices, IReadOnlyList<string> ServiceNames)
    {
        public HashSet<string> ServiceNameSet { get; } = new(ServiceNames, StringComparer.OrdinalIgnoreCase);
    }
}
