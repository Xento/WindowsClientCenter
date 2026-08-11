namespace WindowsClientCenter.Intune.Services.Models;

public sealed record IntunePolicyResultReport(
    string Host,
    DateTimeOffset GeneratedAtUtc,
    string ReportDirectory,
    string XmlPath,
    string HtmlPath,
    string Source,
    IntunePolicyResultSummary Summary,
    IReadOnlyList<IntunePolicyResultEntry> Entries,
    string ExportHtmlPath,
    string ExportJsonPath,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Timings);
