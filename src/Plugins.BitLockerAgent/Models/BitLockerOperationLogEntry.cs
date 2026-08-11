namespace WindowsClientCenter.Plugins.BitLockerAgent.Models;

public sealed record BitLockerOperationLogEntry(
    DateTimeOffset TimestampUtc,
    string Level,
    string Target,
    string Message,
    string DetailsText);
