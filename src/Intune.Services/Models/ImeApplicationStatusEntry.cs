using System.Linq;
using WindowsClientCenter.Shared.Diagnostics;

namespace WindowsClientCenter.Intune.Services.Models;

public sealed record ImeApplicationStatusEntry(
    string AppId,
    string AppName,
    string Intent,
    string TargetInstallContext,
    string InstallStatus,
    DateTimeOffset? LastUpdated,
    string ResultCode,
    string SourceFile,
    string LastMessage,
    bool IsInstalledForAnyIdentity,
    IReadOnlyList<ImeApplicationIdentityStatusEntry> IdentityStatuses)
{
    public string InstalledForAnyLabel => IsInstalledForAnyIdentity ? "Yes" : "No";
    public string ResultCodeDescription => ErrorCodeResolver.ResolveDescription(ResultCode);

    public string InstallContextSummary
    {
        get
        {
            if (IdentityStatuses.Count > 0)
            {
                var hasSystem = IdentityStatuses.Any(identity => string.Equals(identity.Scope, "System", StringComparison.OrdinalIgnoreCase));
                var hasUser = IdentityStatuses.Any(identity => string.Equals(identity.Scope, "User", StringComparison.OrdinalIgnoreCase));
                if (hasSystem && hasUser)
                {
                    return "Mixed";
                }

                if (hasSystem)
                {
                    return "System";
                }

                if (hasUser)
                {
                    return "User";
                }
            }

            return "Unknown";
        }
    }

    public string ApplicabilitySummary
    {
        get
        {
            if (IdentityStatuses.Count == 0)
            {
                return "Unknown";
            }

            if (IdentityStatuses.Any(identity => string.Equals(identity.ApplicabilityStatus, "ApplicabilityError", StringComparison.OrdinalIgnoreCase)))
            {
                return "ApplicabilityError";
            }

            var distinct = IdentityStatuses
                .Select(identity => identity.ApplicabilityStatus)
                .Where(status => !string.IsNullOrWhiteSpace(status))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (distinct.Length == 0)
            {
                return "Unknown";
            }

            if (distinct.All(status => string.Equals(status, "Applicable", StringComparison.OrdinalIgnoreCase)))
            {
                return "Applicable";
            }

            var nonApplicable = distinct
                .Where(IsNotApplicableStatus)
                .ToArray();
            if (nonApplicable.Length > 0)
            {
                if (nonApplicable.Length == 1 && !distinct.Any(status => string.Equals(status, "Applicable", StringComparison.OrdinalIgnoreCase)))
                {
                    return nonApplicable[0];
                }

                return distinct.Any(status => string.Equals(status, "Applicable", StringComparison.OrdinalIgnoreCase))
                    ? "PartiallyApplicable"
                    : "NotApplicable";
            }

            if (distinct.Any(status => string.Equals(status, "Applicable", StringComparison.OrdinalIgnoreCase)))
            {
                return "Applicable";
            }

            return "Unknown";
        }
    }

    public string DependencySummary
    {
        get
        {
            if (IdentityStatuses.Count == 0)
            {
                return "Unknown";
            }

            if (IdentityStatuses.Any(identity => string.Equals(identity.DependencyStatus, "Blocked", StringComparison.OrdinalIgnoreCase)))
            {
                return "Blocked";
            }

            if (IdentityStatuses.All(identity => string.Equals(identity.DependencyStatus, "SatisfiedOrNotRequired", StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(identity.DependencyStatus, "Satisfied", StringComparison.OrdinalIgnoreCase)))
            {
                return "SatisfiedOrNotRequired";
            }

            if (IdentityStatuses.Any(identity => string.Equals(identity.DependencyStatus, "Satisfied", StringComparison.OrdinalIgnoreCase)))
            {
                return "Satisfied";
            }

            return "Unknown";
        }
    }

    private static bool IsNotApplicableStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        if (string.Equals(status, "Applicable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Unknown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "ApplicabilityError", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return status.Contains("NotApplicable", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("RequirementsNotMet", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("Unsupported", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("ProcessorArchitecture", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("Minimum", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("RuleNotMet", StringComparison.OrdinalIgnoreCase);
    }
}
