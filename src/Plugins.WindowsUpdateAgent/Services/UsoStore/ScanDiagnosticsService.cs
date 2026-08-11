using System.Globalization;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Services.UsoStore;

public sealed class ScanDiagnosticsService
{
    private readonly TimestampParser _timestampParser;

    public ScanDiagnosticsService(TimestampParser timestampParser)
    {
        _timestampParser = timestampParser;
    }

    public IReadOnlyList<ProviderScanStatus> Build(IReadOnlyList<UsoProviderPropertyRecord> providerProperties)
    {
        return providerProperties
            .GroupBy(record => record.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(BuildProviderStatus)
            .OrderBy(status => status.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string BuildHealthSummary(IReadOnlyList<ProviderScanStatus> statuses)
    {
        if (statuses.Count == 0)
        {
            return "No provider scan data found.";
        }

        var attention = statuses.Where(status => status.AttentionRequired).ToArray();
        if (attention.Length == 0)
        {
            return $"All {statuses.Count.ToString(CultureInfo.InvariantCulture)} providers look healthy.";
        }

        var topIssues = attention
            .Take(3)
            .Select(status => $"{status.ProviderId}: {status.LastScanStatus}")
            .ToArray();
        return $"{attention.Length.ToString(CultureInfo.InvariantCulture)} provider(s) require attention. {string.Join("; ", topIssues)}";
    }

    private ProviderScanStatus BuildProviderStatus(IGrouping<string, UsoProviderPropertyRecord> group)
    {
        var byVariable = group.ToDictionary(record => record.Variable, record => record, StringComparer.OrdinalIgnoreCase);
        var scanAttempt = GetParsedDateTime(byVariable, "ScanAttemptTime");
        var scanTime = GetParsedDateTime(byVariable, "ScanTime");
        var scanErrorTime = GetParsedDateTime(byVariable, "ScanErrorTime");
        var scanSummaryTime = GetParsedDateTime(byVariable, "ScanSummaryTime");
        var scanError = GetParsedInteger(byVariable, "ScanError");
        var scanErrorInteractive = GetParsedBoolean(byVariable, "ScanErrorInteractive");
        var failures = GetParsedInteger(byVariable, "ScanFailuresSinceLastSuccess");

        var explanations = new List<string>();
        var lastScanStatus = "Unknown";
        var attentionRequired = false;

        if (scanTime.HasValue && (scanError ?? 0) == 0)
        {
            lastScanStatus = "Likely success";
            explanations.Add("Successful scan timestamp is present and raw error code is zero.");
        }
        else if (!scanTime.HasValue && scanAttempt.HasValue && (scanError ?? 0) != 0)
        {
            lastScanStatus = "Likely failed";
            attentionRequired = true;
            explanations.Add("Recent scan attempt exists, but no successful scan timestamp was recorded and the raw error code is non-zero.");
        }
        else if (!scanAttempt.HasValue && !scanTime.HasValue)
        {
            lastScanStatus = "No recent scan activity";
            explanations.Add("No scan attempt or success timestamp was found for this provider.");
        }
        else
        {
            lastScanStatus = "Partial telemetry only";
            explanations.Add("The provider has incomplete scan fields, so the status is heuristic.");
        }

        if ((failures ?? 0) >= 3)
        {
            attentionRequired = true;
            explanations.Add($"ScanFailuresSinceLastSuccess={failures.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)} indicates a recurring issue.");
        }

        if (string.Equals(group.Key, "GraphProvider", StringComparison.OrdinalIgnoreCase) && (scanError ?? 0) != 0)
        {
            attentionRequired = true;
            explanations.Add("GraphProvider currently reports a persistent non-zero raw scan error.");
        }

        return new ProviderScanStatus
        {
            ProviderId = group.Key,
            ScanAttemptTimeRaw = GetRawValue(byVariable, "ScanAttemptTime"),
            ScanAttemptTimeLocal = scanAttempt,
            ScanAttemptTimeDisplay = _timestampParser.FormatDateTime(scanAttempt),
            ScanTimeRaw = GetRawValue(byVariable, "ScanTime"),
            ScanTimeLocal = scanTime,
            ScanTimeDisplay = _timestampParser.FormatDateTime(scanTime),
            ScanErrorRaw = GetRawValue(byVariable, "ScanError"),
            ScanError = scanError,
            ScanErrorInteractiveRaw = GetRawValue(byVariable, "ScanErrorInteractive"),
            ScanErrorInteractive = scanErrorInteractive,
            ScanErrorTimeRaw = GetRawValue(byVariable, "ScanErrorTime"),
            ScanErrorTimeLocal = scanErrorTime,
            ScanErrorTimeDisplay = _timestampParser.FormatDateTime(scanErrorTime),
            ScanFailuresSinceLastSuccessRaw = GetRawValue(byVariable, "ScanFailuresSinceLastSuccess"),
            ScanFailuresSinceLastSuccess = failures,
            ScanSummaryTimeRaw = GetRawValue(byVariable, "ScanSummaryTime"),
            ScanSummaryTimeLocal = scanSummaryTime,
            ScanSummaryTimeDisplay = _timestampParser.FormatDateTime(scanSummaryTime),
            ScanTags = GetRawValue(byVariable, "ScanTags"),
            ScanCache = GetRawValue(byVariable, "ScanCache"),
            LastScanStatus = lastScanStatus,
            AttentionRequired = attentionRequired,
            HeuristicExplanation = string.Join(" ", explanations),
            SearchText = string.Join(" ", group.Key, lastScanStatus, string.Join(' ', explanations), GetRawValue(byVariable, "ScanTags"), GetRawValue(byVariable, "ScanCache"))
        };
    }

    private long? GetParsedInteger(IReadOnlyDictionary<string, UsoProviderPropertyRecord> byVariable, string key)
    {
        return byVariable.TryGetValue(key, out var record)
            ? _timestampParser.ParseInteger(record.Value)
            : null;
    }

    private bool? GetParsedBoolean(IReadOnlyDictionary<string, UsoProviderPropertyRecord> byVariable, string key)
    {
        return byVariable.TryGetValue(key, out var record)
            ? _timestampParser.ParseBoolean(record.Value)
            : null;
    }

    private DateTime? GetParsedDateTime(IReadOnlyDictionary<string, UsoProviderPropertyRecord> byVariable, string key)
    {
        return byVariable.TryGetValue(key, out var record)
            ? _timestampParser.ParseFlexibleDateTime(record.Value, record.Type)
            : null;
    }

    private static string GetRawValue(IReadOnlyDictionary<string, UsoProviderPropertyRecord> byVariable, string key)
    {
        return byVariable.TryGetValue(key, out var record)
            ? record.Value
            : string.Empty;
    }
}
