using WindowsClientCenter.Shared.Diagnostics;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models;

public sealed record ReportingEventsLogEntry(
    string EventInstanceId,
    string Timestamp,
    string TimestampDisplay,
    long TimestampSortKey,
    string NamespaceId,
    string EventId,
    string AgentEvent,
    string SourceId,
    string UpdateId,
    string Revision,
    string Win32Hresult,
    string AppName,
    string Result,
    string Area,
    string Operation,
    string Message,
    string CorrelationToken,
    string RawLine)
{
    public string MessageToolTip => BuildMessageToolTip(Message);
    public bool HasMessageToolTip => !string.IsNullOrWhiteSpace(MessageToolTip);

    private static string BuildMessageToolTip(string message)
    {
        var codes = ErrorCodeResolver.FindInText(message)
            .Select(result => result.Resolution)
            .DistinctBy(result => result.NormalizedCode)
            .ToArray();

        return codes.Length == 0
            ? string.Empty
            : string.Join(Environment.NewLine, codes.Select(result => ErrorCodeResolver.FormatForDisplay(result.NormalizedCode)));
    }
}
