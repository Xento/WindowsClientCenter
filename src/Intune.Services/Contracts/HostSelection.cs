namespace WindowsClientCenter.Intune.Services.Contracts;

public readonly record struct HostSelection(string Host, long Version, CancellationToken CancellationToken);
