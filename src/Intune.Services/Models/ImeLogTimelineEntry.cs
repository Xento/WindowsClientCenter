using WindowsClientCenter.Shared.Diagnostics;

namespace WindowsClientCenter.Intune.Services.Models;

public sealed record ImeLogTimelineEntry(
    DateTimeOffset? TimeCreated,
    string Severity,
    string Component,
    string Message,
    string SourceFile,
    int LineNumber,
    string RawLine,
    bool IsPolicyPayload,
    string PolicyJson,
    string Flow = "",
    string Phase = "",
    string Effect = "",
    string CorrelationSummary = "",
    string EntityType = "",
    string EntityId = "",
    string PolicyId = "",
    string SessionId = "",
    string UserId = "",
    string ResultCode = "")
{
    public string FlowDisplay => string.IsNullOrWhiteSpace(Flow) ? "Informational" : Flow;
    public string PhaseDisplay => string.IsNullOrWhiteSpace(Phase) ? "-" : Phase;
    public string EffectDisplay => string.IsNullOrWhiteSpace(Effect) ? "-" : Effect;
    public string CorrelationDisplay => string.IsNullOrWhiteSpace(CorrelationSummary) ? "-" : CorrelationSummary;
    public string EntityDisplay => string.IsNullOrWhiteSpace(EntityType) || string.IsNullOrWhiteSpace(EntityId) ? "-" : $"{EntityType} {EntityId}";
    public string ResultCodeDisplay => string.IsNullOrWhiteSpace(ResultCode) ? "-" : ResultCode;
    public string ResultCodeDescription => ErrorCodeResolver.ResolveDescription(ResultCode);
    public string SourceLocationDisplay => string.IsNullOrWhiteSpace(SourceFile) ? "-" : $"{SourceFile}:{LineNumber}";
    public string TimelineContextDisplay => $"{FlowDisplay} | {PhaseDisplay} | {EffectDisplay} | {EntityDisplay} | {ResultCodeDisplay} | {CorrelationDisplay}";
    public bool IsRelatedHighlight { get; set; }
}
