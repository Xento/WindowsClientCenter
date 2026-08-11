using System.Text.RegularExpressions;
using WindowsClientCenter.Shared.Diagnostics;

namespace WindowsClientCenter.Intune.Services.Models;

public sealed record ImeApplicationIdentityStatusEntry(
    string IdentityId,
    string Scope,
    string InstallStatus,
    DateTimeOffset? LastUpdated,
    string ResultCode,
    string Source,
    string Details)
{
    private static readonly Regex ApplicabilityCodeRegex = new(@"applicabilitycode2?=(?<code>-?\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ApplicabilityStateRegex = new(@"applicabilitystate=(?<code>-?\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string ApplicabilityStatus => ResolveApplicabilityStatus(Details, InstallStatus);
    public string DependencyStatus => ResolveDependencyStatus(Details, InstallStatus, ResultCode);
    public string ResultCodeDescription => ErrorCodeResolver.ResolveDescription(ResultCode);

    private static string ResolveApplicabilityStatus(string details, string installStatus)
    {
        if (string.Equals(installStatus, "Installed", StringComparison.OrdinalIgnoreCase))
        {
            return "Applicable";
        }

        var normalized = details?.ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Unknown";
        }

        if (normalized.Contains("notapplicable", StringComparison.Ordinal) ||
            normalized.Contains("not applicable", StringComparison.Ordinal))
        {
            return "NotApplicable";
        }

        if (normalized.Contains("applicabilityerroroccurred=true", StringComparison.Ordinal))
        {
            return "ApplicabilityError";
        }

        foreach (Match match in ApplicabilityCodeRegex.Matches(normalized))
        {
            if (int.TryParse(match.Groups["code"].Value, out var code))
            {
                return MapApplicabilityCode(code);
            }
        }

        foreach (Match match in ApplicabilityStateRegex.Matches(normalized))
        {
            if (int.TryParse(match.Groups["code"].Value, out var code))
            {
                return code == 0 ? "Applicable" : "RequirementsNotMet";
            }
        }

        if (normalized.Contains("applicabilitystate=0", StringComparison.Ordinal) ||
            normalized.Contains("applicability=0", StringComparison.Ordinal) ||
            normalized.Contains("applicable=true", StringComparison.Ordinal))
        {
            return "Applicable";
        }

        if (normalized.Contains("applicabilitystate=1", StringComparison.Ordinal) ||
            normalized.Contains("applicabilitystate=2", StringComparison.Ordinal) ||
            normalized.Contains("applicable=false", StringComparison.Ordinal))
        {
            return "NotApplicable";
        }

        return "Unknown";
    }

    private static string MapApplicabilityCode(int code) =>
        code switch
        {
            0 => "Applicable",
            1 => "RequirementsNotMet",
            1000 => "ProcessorArchitectureNotApplicable",
            1001 => "MinimumDiskSpaceNotMet",
            1002 => "MinimumOSVersionNotMet",
            1003 => "MinimumPhysicalMemoryNotMet",
            1004 => "MinimumLogicalProcessorCountNotMet",
            1005 => "MinimumCPUSpeedNotMet",
            1006 => "FileSystemRequirementRuleNotMet",
            1007 => "RegistryRequirementRuleNotMet",
            1008 => "ScriptRequirementRuleNotMet",
            1011 => "AppUnsupportedDueToUnknownReason",
            _ => code > 0 ? $"NotApplicable({code})" : "Unknown"
        };

    private static string ResolveDependencyStatus(string details, string installStatus, string resultCode)
    {
        if (string.Equals(installStatus, "Installed", StringComparison.OrdinalIgnoreCase))
        {
            return "SatisfiedOrNotRequired";
        }

        var normalized = details?.ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Unknown";
        }

        var mentionsDependency = normalized.Contains("dependenc", StringComparison.Ordinal) ||
                                 normalized.Contains("prerequis", StringComparison.Ordinal) ||
                                 normalized.Contains("required app", StringComparison.Ordinal);
        if (!mentionsDependency)
        {
            return "Unknown";
        }

        if (normalized.Contains("missing", StringComparison.Ordinal) ||
            normalized.Contains("not found", StringComparison.Ordinal) ||
            normalized.Contains("failed", StringComparison.Ordinal) ||
            normalized.Contains("cannot", StringComparison.Ordinal) ||
            normalized.Contains("unable", StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(resultCode) && !string.Equals(resultCode, "0x00000000", StringComparison.OrdinalIgnoreCase)))
        {
            return "Blocked";
        }

        if (normalized.Contains("installed", StringComparison.Ordinal) ||
            normalized.Contains("satisfied", StringComparison.Ordinal) ||
            normalized.Contains("success", StringComparison.Ordinal))
        {
            return "Satisfied";
        }

        return "Unknown";
    }
}
