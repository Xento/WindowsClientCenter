namespace WindowsClientCenter.Intune.Services.Models;

public sealed record MecmApplicationSnapshot(
    string Host,
    IReadOnlyList<MecmApplicationEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed partial record MecmApplicationEntry(
    string Id,
    string Name,
    string FullName,
    string Description,
    string Icon,
    string SoftwareVersion,
    string Revision,
    bool UserUiExperience,
    bool IsPreflightOnly,
    bool IsMachineTarget,
    IReadOnlyList<string> AllowedActions,
    string InstallState,
    string ApplicabilityState,
    string ResolvedState,
    int? EvaluationState,
    string EvaluationStateText,
    uint? ErrorCode,
    string ErrorCodeText,
    DateTimeOffset? LastEvalTimeUtc,
    DateTimeOffset? LastInstallTimeUtc,
    bool HasInstallCommand,
    bool HasUninstallCommand,
    bool HasIcon);

public sealed partial record MecmApplicationEntry
{
    public MecmApplicationEntry(
        string id,
        string name,
        string fullName,
        string description,
        string icon,
        string softwareVersion,
        string revision,
        bool userUiExperience,
        bool isPreflightOnly,
        bool isMachineTarget,
        IReadOnlyList<string> allowedActions,
        string installState,
        string applicabilityState,
        string resolvedState,
        int? evaluationState,
        string evaluationStateText,
        uint? errorCode,
        string errorCodeText,
        DateTimeOffset? lastEvalTimeUtc,
        DateTimeOffset? lastInstallTimeUtc,
        bool hasIcon)
        : this(
            id,
            name,
            fullName,
            description,
            icon,
            softwareVersion,
            revision,
            userUiExperience,
            isPreflightOnly,
            isMachineTarget,
            allowedActions,
            installState,
            applicabilityState,
            resolvedState,
            evaluationState,
            evaluationStateText,
            errorCode,
            errorCodeText,
            lastEvalTimeUtc,
            lastInstallTimeUtc,
            false,
            false,
            hasIcon)
    {
    }

    public MecmApplicationEntry(
        string id,
        string name,
        string fullName,
        string description,
        string softwareVersion,
        string revision,
        bool userUiExperience,
        bool isPreflightOnly,
        bool isMachineTarget,
        IReadOnlyList<string> allowedActions,
        string installState,
        string applicabilityState,
        string resolvedState,
        int? evaluationState,
        string evaluationStateText,
        uint? errorCode,
        string errorCodeText,
        DateTimeOffset? lastEvalTimeUtc,
        DateTimeOffset? lastInstallTimeUtc,
        bool hasIcon)
        : this(
            id,
            name,
            fullName,
            description,
            string.Empty,
            softwareVersion,
            revision,
            userUiExperience,
            isPreflightOnly,
            isMachineTarget,
            allowedActions,
            installState,
            applicabilityState,
            resolvedState,
            evaluationState,
            evaluationStateText,
            errorCode,
            errorCodeText,
            lastEvalTimeUtc,
            lastInstallTimeUtc,
            false,
            false,
            hasIcon)
    {
    }
}

public enum MecmOverviewAction
{
    RequestMachinePolicy,
    EvaluateMachinePolicy,
    TriggerHeartbeatDiscovery,
    TriggerHardwareInventory,
    TriggerSoftwareInventory,
    RunCcmeval,
    RestartCcmExec,
    ResetPolicySoft,
    ResetPolicyHard,
    RepairClient
}

public sealed record MecmOverviewSnapshot(
    string Host,
    string ClientVersion,
    string AssignedSite,
    string ManagementPoint,
    string RebootPendingText,
    string CoManagementStateText,
    IReadOnlyList<MecmOverviewActivityEntry> Activities,
    IReadOnlyList<MecmCoManagementWorkloadEntry> Workloads,
    IReadOnlyList<MecmClientComponentEntry> Components,
    IReadOnlyList<MecmClientServiceEntry> Services,
    IReadOnlyList<MecmHealthCheckEntry> HealthChecks,
    IReadOnlyList<string> Warnings);

public sealed record MecmOverviewActivityEntry(
    string Name,
    string StatusText,
    string StatusLevel,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? ReportedUtc,
    string Detail);

public sealed record MecmCoManagementWorkloadEntry(
    string Name,
    string Authority,
    string StatusLevel,
    string Detail);

public sealed record MecmClientComponentEntry(
    string DisplayName,
    string Name,
    string Version,
    bool? IsEnabled,
    string StatusLevel,
    string Detail);

public sealed record MecmClientServiceEntry(
    string Name,
    string DisplayName,
    string Status,
    string StartMode,
    string StatusLevel,
    string Detail);

public sealed record MecmHealthCheckEntry(
    string Name,
    string StatusText,
    string StatusLevel,
    string Detail);

public enum MecmApplicationAction
{
    Install,
    Repair,
    Uninstall
}

public enum MecmApplicationEvaluationMode
{
    UserPolicy,
    MachinePolicy,
    GlobalEvaluation
}

public sealed record MecmPendingUpdatesSnapshot(
    string Host,
    IReadOnlyList<MecmPendingUpdateEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed record MecmPendingUpdateEntry(
    string UpdateId,
    string Name,
    string Publisher,
    string Description,
    string ArticleId,
    string BulletinId,
    int? EvaluationState,
    string EvaluationStateText,
    int? PercentComplete,
    uint? ErrorCode,
    string ErrorCodeText,
    DateTimeOffset? DeadlineUtc);

public sealed record MecmAllUpdatesSnapshot(
    string Host,
    IReadOnlyList<MecmAllUpdateEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed record MecmAllUpdateEntry(
    string UniqueId,
    string Title,
    string Article,
    string Bulletin,
    string Language,
    int? RevisionNumber,
    DateTimeOffset? ScanTimeUtc,
    int? SourceVersion,
    string Status,
    string ProductId);

public enum MecmUpdateInstallMode
{
    Selected,
    AllMandatory,
    AllApproved
}

public sealed record MecmUpdateInstallRequest(
    MecmUpdateInstallMode Mode,
    IReadOnlyList<string> SelectedUpdateIds);

public sealed record MecmPackagesSnapshot(
    string Host,
    IReadOnlyList<MecmPackageEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed record MecmPackageEntry(
    string AdvertisementId,
    string PackageId,
    string PackageName,
    string ProgramId,
    string ProgramName,
    string Manufacturer,
    string Version,
    bool IsMandatory,
    string RepeatRunBehavior,
    string LastRunStatus,
    uint? LastExitCode,
    DateTimeOffset? LastRunTimeUtc,
    DateTimeOffset? AvailableFromUtc,
    DateTimeOffset? ExpiresUtc,
    bool RequiresUserInput,
    string Comment);

public sealed record MecmBaselinesSnapshot(
    string Host,
    IReadOnlyList<MecmBaselineEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed record MecmBaselineEntry(
    string Name,
    string DisplayName,
    string Version,
    bool IsMachineTarget,
    bool IsCompliant,
    int? LastComplianceStatus,
    int? Status,
    DateTimeOffset? LastEvalTimeUtc,
    string ComplianceDetailsSummary);

public sealed record MecmBaselineDetails(
    string Name,
    string DisplayName,
    string Version,
    bool IsMachineTarget,
    IReadOnlyList<MecmBaselineConfigItem> ConfigItems,
    IReadOnlyList<string> Warnings);

public sealed record MecmBaselineConfigItem(
    string LogicalName,
    string Name,
    string Description,
    string Version,
    string Type,
    bool Compliant,
    bool Detected,
    bool Applicable,
    string ConstraintViolation);
