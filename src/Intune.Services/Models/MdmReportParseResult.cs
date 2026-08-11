namespace WindowsClientCenter.Intune.Services.Models;

public sealed record MdmReportParseResult(
    string ReportDirectory,
    string XmlPath,
    string HtmlPath,
    int XmlNodeCount,
    int HtmlLineCount);
