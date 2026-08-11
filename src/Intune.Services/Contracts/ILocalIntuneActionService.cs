using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface ILocalIntuneActionService
{
    ValueTask<LocalIntuneActionResult> MdmSyncNowAsync(string host, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<MdmSyncStatusEntry>> GetMdmSyncStatusAsync(string host, int maxEvents, CancellationToken cancellationToken);
    ValueTask<string> GetImeLogTimelineFingerprintAsync(string host, string logDirectory, string filePattern, CancellationToken cancellationToken);
    ValueTask<ImeLogTimelineSnapshot> GetImeLogTimelineSnapshotAsync(string host, string logDirectory, string filePattern, int maxLines, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ImeLogTimelineEntry>> GetImeLogTimelineAsync(string host, string logDirectory, string filePattern, int maxLines, CancellationToken cancellationToken);
    ValueTask<ImeLogAnalysisResult> GetImeLogAnalysisAsync(string host, string logDirectory, string filePattern, int maxLines, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ImeApplicationStatusEntry>> GetImeApplicationStatusesAsync(string host, string logDirectory, int maxLines, CancellationToken cancellationToken);
    ValueTask<MdmReportParseResult> GenerateMdmDiagnosticsReportAsync(string host, string outputDirectory, CancellationToken cancellationToken);
    ValueTask<MdmReportParseResult> ParseMdmDiagnosticsReportAsync(string host, string reportDirectory, CancellationToken cancellationToken);
    ValueTask<IntunePolicyResultReport> GenerateIntunePolicyResultAsync(string host, string outputDirectory, CancellationToken cancellationToken);
    ValueTask<IntunePolicyResultReport> ParseIntunePolicyResultAsync(string host, string reportDirectory, string outputDirectory, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> ImeSyncAppsAsync(string host, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> ImeSyncComplianceAsync(string host, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> ParseImeAppWorkloadPoliciesAsync(string host, string logDirectory, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> RunImeHealthEvaluationAsync(string host, string taskNameContains, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> RestartImeServiceAsync(string host, CancellationToken cancellationToken);
    ValueTask<bool> GetImeTestModeEnabledAsync(string host, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> SetImeTestModeEnabledAsync(string host, bool enabled, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> RetryWin32AppAsync(string host, Win32RetryRequest request, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> RetryAllFailedWin32AppsAsync(string host, Win32RetryAllRequest request, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> RestartPortAuthenticationServicesAsync(string host, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> RestartPortAuthenticationAdapterAsync(string host, string interfaceName, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> SetPortAuthenticationTracingAsync(string host, PortAuthenticationTracingMode mode, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> SetPortAuthenticationAutoconfigAsync(string host, string interfaceName, bool enabled, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> ReapplyPortAuthenticationProfileAsync(string host, string profileName, string? interfaceName, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> ExportSupportEventLogsAsync(string host, string outputDirectory, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> CreateDiagnosticsBundleAsync(string host, string bundleRoot, string zipPath, CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> RunAutopilotDiagnosticsCommunityAsync(
        string host,
        bool allSessions,
        bool showPolicies,
        string moduleVersion,
        int maxOutputLines,
        CancellationToken cancellationToken);
    ValueTask<LocalIntuneActionResult> RunImeQuickStatusAsync(string host, int maxOutputLines, CancellationToken cancellationToken);
}
