namespace WindowsClientCenter.Intune.Services.Models;

public sealed record MdmEventAnalysisEntry(
    string LogName,
    DateTimeOffset? TimeCreated,
    long? RecordId,
    int Id,
    string Level,
    string Provider,
    MdmEventSeverity Severity,
    bool IsFailure,
    string Summary,
    string ResultCode,
    string ResolvedError,
    string PolicyName,
    string Area,
    string CspUri,
    string EnrollmentId,
    string RecommendedAction,
    string Message)
{
    public string Description =>
        string.IsNullOrWhiteSpace(Message)
            ? Summary
            : Message.ReplaceLineEndings(" ").Trim();
}
