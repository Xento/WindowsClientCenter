namespace WindowsClientCenter.Intune.Services.Models;

public sealed record PortAuthenticationEventEntry(
    DateTimeOffset? TimeCreated,
    string LogName,
    int Id,
    string Level,
    string StatusLevel,
    string Summary,
    string RecommendedAction,
    string Message);
