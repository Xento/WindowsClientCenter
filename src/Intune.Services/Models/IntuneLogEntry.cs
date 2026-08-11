namespace WindowsClientCenter.Intune.Services.Models;

public sealed record IntuneLogEntry(
    string LogName,
    DateTimeOffset? TimeCreated,
    int Id,
    string Level,
    string Provider,
    string Message);
