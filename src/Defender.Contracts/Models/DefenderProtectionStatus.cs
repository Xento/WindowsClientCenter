namespace WindowsClientCenter.Defender.Contracts.Models;

public sealed record DefenderProtectionStatus(
    bool? AntivirusEnabled,
    bool? RealtimeProtectionEnabled,
    bool? BehaviorMonitorEnabled,
    bool? IoavProtectionEnabled,
    bool? OnAccessProtectionEnabled,
    bool? NisEnabled,
    bool? TamperProtectionEnabled,
    string RunningMode);
