using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Plugins.MecmAgent.Models;

public sealed record MecmApplicationRow(MecmApplicationEntry Entry)
{
    private HashSet<string>? _allowedActions;
    private string? _icon;

    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string Icon => _icon ??= ResolveIcon(Entry);
    public string Version => Entry.SoftwareVersion;
    public string Revision => Entry.Revision;
    public string Description => Entry.Description;
    public string InstallState => Entry.InstallState;
    public string StatusDisplay => string.IsNullOrWhiteSpace(Entry.EvaluationStateText) ? Entry.ResolvedState : Entry.EvaluationStateText;
    public string ErrorCodeDisplay => Entry.ErrorCode.HasValue ? $"0x{Entry.ErrorCode.Value:X8}" : string.Empty;
    public string ErrorMessage => Entry.ErrorCodeText;
    public bool UserUiExperience => Entry.UserUiExperience;
    public bool IsPreflightOnly => Entry.IsPreflightOnly;
    public string TargetDisplay => Entry.IsMachineTarget ? "Machine" : "User";
    public string TargetIconGlyph => Entry.IsMachineTarget ? "\uE7F8" : "\uE77B";
    public string InstallTimeDisplay => Entry.LastInstallTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
    public string IconGlyph => Entry.HasIcon ? "\uE7B8" : "\uE8FD";
    public HashSet<string> AllowedActionSet => _allowedActions ??= new HashSet<string>(Entry.AllowedActions, StringComparer.OrdinalIgnoreCase);

    private static string ResolveIcon(MecmApplicationEntry entry)
    {
        var iconProperty = typeof(MecmApplicationEntry).GetProperty(nameof(Icon));
        if (iconProperty?.PropertyType != typeof(string))
        {
            return string.Empty;
        }

        return iconProperty.GetValue(entry) as string ?? string.Empty;
    }
}

public sealed record MecmPendingUpdateRow(MecmPendingUpdateEntry Entry)
{
    public string UpdateId => Entry.UpdateId;
    public string Name => Entry.Name;
    public string Publisher => Entry.Publisher;
    public string Description => Entry.Description;
    public string ArticleId => Entry.ArticleId;
    public string BulletinId => Entry.BulletinId;
    public string EvaluationStateText => Entry.EvaluationStateText;
    public string PercentCompleteDisplay => Entry.PercentComplete.HasValue ? $"{Entry.PercentComplete.Value} %" : string.Empty;
    public string ErrorCodeDisplay => Entry.ErrorCode.HasValue ? $"0x{Entry.ErrorCode.Value:X8}" : string.Empty;
    public string ErrorMessage => Entry.ErrorCodeText;
    public string DeadlineDisplay => Entry.DeadlineUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
}

public sealed record MecmAllUpdateRow(MecmAllUpdateEntry Entry)
{
    public string UniqueId => Entry.UniqueId;
    public string Title => Entry.Title;
    public string Article => Entry.Article;
    public string Bulletin => Entry.Bulletin;
    public string Language => Entry.Language;
    public int? RevisionNumber => Entry.RevisionNumber;
    public string ScanTimeDisplay => Entry.ScanTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
    public int? SourceVersion => Entry.SourceVersion;
    public string Status => Entry.Status;
    public string ProductId => Entry.ProductId;
}

public sealed record MecmPackageRow(MecmPackageEntry Entry)
{
    public string AdvertisementId => Entry.AdvertisementId;
    public string PackageId => Entry.PackageId;
    public string PackageName => Entry.PackageName;
    public string ProgramId => Entry.ProgramId;
    public string ProgramName => Entry.ProgramName;
    public string Manufacturer => Entry.Manufacturer;
    public string Version => Entry.Version;
    public bool IsMandatory => Entry.IsMandatory;
    public string RepeatRunBehavior => Entry.RepeatRunBehavior;
    public string LastRunStatus => Entry.LastRunStatus;
    public string LastExitCodeDisplay => Entry.LastExitCode.HasValue ? $"0x{Entry.LastExitCode.Value:X8}" : string.Empty;
    public string LastRunTimeDisplay => Entry.LastRunTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
    public string AvailableFromDisplay => Entry.AvailableFromUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
    public string ExpiresDisplay => Entry.ExpiresUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
    public bool RequiresUserInput => Entry.RequiresUserInput;
    public string Comment => Entry.Comment;
}

public sealed record MecmBaselineRow(MecmBaselineEntry Entry)
{
    public string Name => Entry.Name;
    public string DisplayName => Entry.DisplayName;
    public string Version => Entry.Version;
    public bool IsMachineTarget => Entry.IsMachineTarget;
    public bool IsCompliant => Entry.IsCompliant;
    public string ComplianceStateDisplay => Entry.IsCompliant ? "Compliant" : "Non-compliant";
    public string LastComplianceStatusDisplay => Entry.LastComplianceStatus?.ToString() ?? string.Empty;
    public string StatusDisplay => Entry.Status?.ToString() ?? string.Empty;
    public string LastEvalTimeDisplay => Entry.LastEvalTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
    public string ComplianceDetailsSummary => Entry.ComplianceDetailsSummary;
    public string TargetDisplay => Entry.IsMachineTarget ? "Machine" : "User";
}

public sealed record MecmBaselineConfigItemRow(MecmBaselineConfigItem Entry)
{
    public string LogicalName => Entry.LogicalName;
    public string Name => Entry.Name;
    public string Description => Entry.Description;
    public string Version => Entry.Version;
    public string Type => Entry.Type;
    public bool Compliant => Entry.Compliant;
    public bool Detected => Entry.Detected;
    public bool Applicable => Entry.Applicable;
    public string ConstraintViolation => Entry.ConstraintViolation;
}

public sealed record MecmOverviewActivityRow(MecmOverviewActivityEntry Entry)
{
    public string Name => Entry.Name;
    public string StatusText => Entry.StatusText;
    public string StatusLevel => Entry.StatusLevel;
    public string StartedDisplay => Entry.StartedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
    public string ReportedDisplay => Entry.ReportedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
    public string Detail => Entry.Detail;
}

public sealed record MecmCoManagementWorkloadRow(MecmCoManagementWorkloadEntry Entry)
{
    public string Name => Entry.Name;
    public string Authority => Entry.Authority;
    public string AuthorityDisplay => string.IsNullOrWhiteSpace(Entry.Authority) ? "Unknown" : Entry.Authority;
    public string TileBackground => GetTileBackground(AuthorityDisplay);
    public string TileBorderBrush => GetTileBorderBrush(AuthorityDisplay);
    public string BadgeBackground => GetBadgeBackground(AuthorityDisplay);
    public string BadgeForeground => GetBadgeForeground(AuthorityDisplay);

    private static string GetTileBackground(string authority)
    {
        return authority switch
        {
            "Intune" => "#EAF6EE",
            "ConfigMgr" => "#EEF4FB",
            _ => "#F7F8FA"
        };
    }

    private static string GetTileBorderBrush(string authority)
    {
        return authority switch
        {
            "Intune" => "#B9DEC2",
            "ConfigMgr" => "#BFD2E8",
            _ => "#D7DCE3"
        };
    }

    private static string GetBadgeBackground(string authority)
    {
        return authority switch
        {
            "Intune" => "#1F7A3F",
            "ConfigMgr" => "#225C9B",
            _ => "#6B7280"
        };
    }

    private static string GetBadgeForeground(string authority) => "#FFFFFF";
}

public sealed record MecmClientComponentRow(MecmClientComponentEntry Entry)
{
    public string DisplayName => Entry.DisplayName;
    public string Name => Entry.Name;
    public string Version => Entry.Version;
    public string EnabledDisplay => Entry.IsEnabled.HasValue ? (Entry.IsEnabled.Value ? "Yes" : "No") : "Unknown";
    public string StatusLevel => Entry.StatusLevel;
    public string Detail => Entry.Detail;
}

public sealed record MecmClientServiceRow(MecmClientServiceEntry Entry)
{
    public string Name => Entry.Name;
    public string DisplayName => Entry.DisplayName;
    public string Status => Entry.Status;
    public string StartMode => Entry.StartMode;
    public string StatusLevel => Entry.StatusLevel;
    public string Detail => Entry.Detail;
}

public sealed record MecmHealthCheckRow(MecmHealthCheckEntry Entry)
{
    public string Name => Entry.Name;
    public string StatusText => Entry.StatusText;
    public string StatusLevel => Entry.StatusLevel;
    public string Detail => Entry.Detail;
}

public enum MecmSection
{
    Overview,
    Applications,
    PendingUpdates,
    AllUpdates,
    Packages,
    DcmBaselines
}
