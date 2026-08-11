using System.IO;
using System.Text.Json;

namespace WindowsClientCenter.Host.Runtime;

public sealed record HostUserSettings(
    IReadOnlyList<string>? RecentHosts = null,
    IReadOnlyList<NavigationNodeState>? NavigationStates = null)
{
    public static HostUserSettings Empty { get; } = new([], []);
}

public sealed record NavigationNodeState(string NodePath, bool IsExpanded);

public interface IHostUserSettingsStore
{
    Task<HostUserSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(HostUserSettings settings, CancellationToken cancellationToken);
}

public sealed class JsonHostUserSettingsStore : IHostUserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsFile;

    public JsonHostUserSettingsStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var settingsDir = Path.Combine(appData, "WindowsClientCenter");
        Directory.CreateDirectory(settingsDir);

        _settingsFile = Path.Combine(settingsDir, "user-settings.json");
    }

    public async Task<HostUserSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsFile))
        {
            return HostUserSettings.Empty;
        }

        await using var stream = File.OpenRead(_settingsFile);
        var settings = await JsonSerializer.DeserializeAsync<HostUserSettings>(stream, JsonOptions, cancellationToken);
        return settings ?? HostUserSettings.Empty;
    }

    public async Task SaveAsync(HostUserSettings settings, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_settingsFile);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }
}
