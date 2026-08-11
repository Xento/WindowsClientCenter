namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models;

public sealed record WindowsUpdateProviderEntry(
    string Name,
    string ServiceId,
    bool IsDefault,
    bool IsRegisteredWithAutomaticUpdates,
    bool OffersWindowsUpdates,
    bool IsManaged);
