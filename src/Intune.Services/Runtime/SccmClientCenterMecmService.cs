using System.Diagnostics;
using System.Globalization;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using Microsoft.Extensions.Logging;
using sccmclictr.automation;
using sccmclictr.automation.functions;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class SccmClientCenterMecmService(IPowerShellExecutor executor, ILogger<SccmClientCenterMecmService>? logger = null) : IMecmClientService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MecmOverviewClient _overviewClient = new(executor);
    private readonly ILogger<SccmClientCenterMecmService>? _logger = logger;
    private SCCMAgent? _agent;
    private string _connectedHost = string.Empty;

    public void Dispose()
    {
        DisposeAgent();
        _gate.Dispose();
    }

    public ValueTask<MecmOverviewSnapshot> GetOverviewAsync(string host, CancellationToken cancellationToken)
    {
        return _overviewClient.GetOverviewAsync(host, cancellationToken);
    }

    public ValueTask<DeviceActionResult> ExecuteOverviewActionAsync(string host, MecmOverviewAction action, CancellationToken cancellationToken)
    {
        return _overviewClient.ExecuteActionAsync(host, action, cancellationToken);
    }

    public async ValueTask<MecmApplicationSnapshot> GetApplicationsAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return new MecmApplicationSnapshot(string.Empty, [], ["No host was provided."]);
        }

        var normalizedHost = host.Trim();
        try
        {
            var entries = await ExecuteWithClientAsync(
                normalizedHost,
                client => client.SoftwareDistribution.Applications
                    .Select(MapApplication)
                    .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.SoftwareVersion, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.Revision, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                cancellationToken);

            return new MecmApplicationSnapshot(normalizedHost, entries, []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading MECM applications failed for '{Host}'.", normalizedHost);
            return new MecmApplicationSnapshot(normalizedHost, [], [ex.Message]);
        }
    }

    public async ValueTask<DeviceActionResult> ExecuteApplicationActionAsync(string host, string applicationId, string revision, bool isMachineTarget, MecmApplicationAction action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        if (string.IsNullOrWhiteSpace(applicationId))
        {
            return DeviceActionResult.Fail("No application id was provided.", "no_application_id");
        }

        var normalizedHost = host.Trim();
        var normalizedApplicationId = applicationId.Trim();

        try
        {
            var completed = await ExecuteWithClientAsync(
                normalizedHost,
                client =>
                {
                    var application = client.SoftwareDistribution.Applications.FirstOrDefault(app =>
                        string.Equals(app.Id, normalizedApplicationId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(app.Revision ?? string.Empty, revision?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                        (app.IsMachineTarget ?? false) == isMachineTarget);

                    if (application is null)
                    {
                        throw new InvalidOperationException($"MECM application '{normalizedApplicationId}' was not found.");
                    }

                    switch (action)
                    {
                        case MecmApplicationAction.Install:
                            application.Install();
                            break;
                        case MecmApplicationAction.Repair:
                            application.Repair();
                            break;
                        case MecmApplicationAction.Uninstall:
                            application.Uninstall();
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported MECM application action.");
                    }

                    return true;
                },
                cancellationToken);

            return completed
                ? DeviceActionResult.Ok($"{action} queued for '{normalizedApplicationId}' on '{normalizedHost}'.")
                : DeviceActionResult.Fail($"{action} failed for '{normalizedApplicationId}' on '{normalizedHost}'.", "mecm_application_action_failed");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Executing MECM application action {Action} failed for '{Host}'.", action, normalizedHost);
            return DeviceActionResult.Fail($"{action} failed for '{normalizedApplicationId}' on '{normalizedHost}': {ex.Message}", "mecm_application_action_failed");
        }
    }

    public async ValueTask<DeviceActionResult> TriggerApplicationEvaluationAsync(string host, MecmApplicationEvaluationMode mode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        var normalizedHost = host.Trim();
        try
        {
            var success = await ExecuteWithClientAsync(
                normalizedHost,
                client => mode switch
                {
                    MecmApplicationEvaluationMode.UserPolicy => client.AgentActions.AppManUserPolicyAction(),
                    MecmApplicationEvaluationMode.MachinePolicy => client.AgentActions.AppManPolicyAction(),
                    MecmApplicationEvaluationMode.GlobalEvaluation => client.AgentActions.AppManGlobalEvaluation(),
                    _ => false
                },
                cancellationToken);

            return success
                ? DeviceActionResult.Ok($"MECM application evaluation '{mode}' requested on '{normalizedHost}'.")
                : DeviceActionResult.Fail($"MECM application evaluation '{mode}' failed on '{normalizedHost}'.", "mecm_application_evaluation_failed");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Triggering MECM application evaluation {Mode} failed for '{Host}'.", mode, normalizedHost);
            return DeviceActionResult.Fail($"MECM application evaluation '{mode}' failed on '{normalizedHost}': {ex.Message}", "mecm_application_evaluation_failed");
        }
    }

    public async ValueTask<MecmPendingUpdatesSnapshot> GetPendingUpdatesAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return new MecmPendingUpdatesSnapshot(string.Empty, [], ["No host was provided."]);
        }

        var normalizedHost = host.Trim();
        try
        {
            var entries = await ExecuteWithClientAsync(
                normalizedHost,
                client =>
                {
                    var updateLookup = client.SoftwareUpdates.SoftwareUpdate
                        .GroupBy(static update => update.UpdateID, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

                    return client.SoftwareUpdates.TargetUpdates
                        .Select(target =>
                        {
                            updateLookup.TryGetValue(target.UpdateId ?? string.Empty, out var update);
                            return new MecmPendingUpdateEntry(
                                target.UpdateId ?? string.Empty,
                                update?.Name ?? update?.FullName ?? target.UpdateId ?? string.Empty,
                                update?.Publisher ?? string.Empty,
                                update?.Description ?? string.Empty,
                                update?.ArticleID ?? string.Empty,
                                update?.BulletinID ?? string.Empty,
                                ConvertInt(update?.EvaluationState ?? target.UpdateState),
                                update?.EvaluationStateText ?? FormatNumericState("UpdateState", target.UpdateState),
                                ConvertInt(target.PercentComplete),
                                update?.ErrorCode,
                                update?.ErrorCodeText ?? string.Empty,
                                ToUtcOffset(target.Deadline ?? update?.Deadline));
                        })
                        .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(static item => item.ArticleId, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                },
                cancellationToken);

            return new MecmPendingUpdatesSnapshot(normalizedHost, entries, []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading pending MECM updates failed for '{Host}'.", normalizedHost);
            return new MecmPendingUpdatesSnapshot(normalizedHost, [], [ex.Message]);
        }
    }

    public async ValueTask<MecmAllUpdatesSnapshot> GetAllUpdatesAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return new MecmAllUpdatesSnapshot(string.Empty, [], ["No host was provided."]);
        }

        var normalizedHost = host.Trim();
        try
        {
            var entries = await ExecuteWithClientAsync(
                normalizedHost,
                client => client.SoftwareUpdates.UpdateStatus
                    .Select(static update => new MecmAllUpdateEntry(
                        update.UniqueId ?? string.Empty,
                        update.Title ?? string.Empty,
                        update.Article ?? string.Empty,
                        update.Bulletin ?? string.Empty,
                        update.Language ?? string.Empty,
                        ConvertInt(update.RevisionNumber),
                        ToUtcOffset(update.ScanTime),
                        ConvertInt(update.SourceVersion),
                        update.Status ?? string.Empty,
                        update.ProductID ?? string.Empty))
                    .OrderBy(static item => item.Title, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.Article, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.RevisionNumber)
                    .ToArray(),
                cancellationToken);

            return new MecmAllUpdatesSnapshot(normalizedHost, entries, []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading MECM update catalog failed for '{Host}'.", normalizedHost);
            return new MecmAllUpdatesSnapshot(normalizedHost, [], [ex.Message]);
        }
    }

    public async ValueTask<DeviceActionResult> InstallUpdatesAsync(string host, MecmUpdateInstallRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        if (request.Mode == MecmUpdateInstallMode.Selected && request.SelectedUpdateIds.Count == 0)
        {
            return DeviceActionResult.Fail("No MECM updates were selected.", "no_updates_selected");
        }

        var normalizedHost = host.Trim();
        try
        {
            await ExecuteWithClientAsync(
                normalizedHost,
                client =>
                {
                    switch (request.Mode)
                    {
                        case MecmUpdateInstallMode.AllMandatory:
                            client.SoftwareUpdates.InstallAllRequiredUpdates();
                            break;
                        case MecmUpdateInstallMode.AllApproved:
                            client.SoftwareUpdates.InstallAllApprovedUpdates();
                            break;
                        case MecmUpdateInstallMode.Selected:
                        {
                            var selectedIds = new HashSet<string>(request.SelectedUpdateIds, StringComparer.OrdinalIgnoreCase);
                            var updates = client.SoftwareUpdates.SoftwareUpdate
                                .Where(update => selectedIds.Contains(update.UpdateID ?? string.Empty))
                                .ToList();

                            if (updates.Count == 0)
                            {
                                throw new InvalidOperationException("No selected MECM updates were found.");
                            }

                            client.SoftwareUpdates.InstallUpdates(updates);
                            break;
                        }
                        default:
                            throw new ArgumentOutOfRangeException(nameof(request.Mode), request.Mode, "Unsupported MECM update install mode.");
                    }

                    return true;
                },
                cancellationToken);

            return DeviceActionResult.Ok($"MECM update installation requested on '{normalizedHost}'.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Installing MECM updates failed for '{Host}'.", normalizedHost);
            return DeviceActionResult.Fail($"MECM update installation failed on '{normalizedHost}': {ex.Message}", "mecm_update_install_failed");
        }
    }

    public async ValueTask<MecmPackagesSnapshot> GetPackagesAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return new MecmPackagesSnapshot(string.Empty, [], ["No host was provided."]);
        }

        var normalizedHost = host.Trim();
        try
        {
            var entries = await ExecuteWithClientAsync(
                normalizedHost,
                client => client.SoftwareDistribution.Advertisements
                    .Select(static package => new MecmPackageEntry(
                        package.ADV_AdvertisementID ?? string.Empty,
                        package.PKG_PackageID ?? string.Empty,
                        package.PKG_Name ?? string.Empty,
                        package.PRG_ProgramID ?? string.Empty,
                        package.PRG_ProgramName ?? string.Empty,
                        package.PKG_Manufacturer ?? string.Empty,
                        package.PKG_version ?? string.Empty,
                        package.ADV_MandatoryAssignments ?? false,
                        package.ADV_RepeatRunBehavior ?? string.Empty,
                        package.ADV_MandatoryAssignments == true ? "Mandatory" : "Available",
                        null,
                        null,
                        ToUtcOffset(package.ADV_ActiveTime),
                        ToUtcOffset(package.ADV_ExpirationTime),
                        package.PRG_PRF_UserInputRequired ?? false,
                        package.PRG_Comment ?? string.Empty))
                    .OrderBy(static item => item.PackageName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.ProgramName, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                cancellationToken);

            return new MecmPackagesSnapshot(normalizedHost, entries, []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading MECM packages failed for '{Host}'.", normalizedHost);
            return new MecmPackagesSnapshot(normalizedHost, [], [ex.Message]);
        }
    }

    public async ValueTask<DeviceActionResult> ExecutePackageAsync(string host, string advertisementId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        if (string.IsNullOrWhiteSpace(advertisementId))
        {
            return DeviceActionResult.Fail("No advertisement id was provided.", "no_advertisement_id");
        }

        var normalizedHost = host.Trim();
        var normalizedAdvertisementId = advertisementId.Trim();

        try
        {
            await ExecuteWithClientAsync(
                normalizedHost,
                client =>
                {
                    var advertisement = client.SoftwareDistribution.Advertisements.FirstOrDefault(item =>
                        string.Equals(item.ADV_AdvertisementID, normalizedAdvertisementId, StringComparison.OrdinalIgnoreCase));

                    if (advertisement is null)
                    {
                        throw new InvalidOperationException($"MECM package advertisement '{normalizedAdvertisementId}' was not found.");
                    }

                    advertisement.TriggerSchedule(true);
                    return true;
                },
                cancellationToken);

            return DeviceActionResult.Ok($"MECM package '{normalizedAdvertisementId}' queued on '{normalizedHost}'.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Executing MECM package '{AdvertisementId}' failed for '{Host}'.", normalizedAdvertisementId, normalizedHost);
            return DeviceActionResult.Fail($"MECM package '{normalizedAdvertisementId}' failed on '{normalizedHost}': {ex.Message}", "mecm_package_action_failed");
        }
    }

    public async ValueTask<MecmBaselinesSnapshot> GetBaselinesAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return new MecmBaselinesSnapshot(string.Empty, [], ["No host was provided."]);
        }

        var normalizedHost = host.Trim();
        try
        {
            var entries = await ExecuteWithClientAsync(
                normalizedHost,
                client => client.DCM.DCMBaselines
                    .Select(static baseline => new MecmBaselineEntry(
                        baseline.Name ?? string.Empty,
                        baseline.DisplayName ?? baseline.Name ?? string.Empty,
                        baseline.Version ?? string.Empty,
                        baseline.IsMachineTarget ?? true,
                        baseline.isCompliant,
                        ConvertInt(baseline.LastComplianceStatus),
                        ConvertInt(baseline.Status),
                        ToUtcOffset(baseline.LastEvalTime),
                        SummarizeComplianceDetails(baseline.ComplianceDetails)))
                    .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static item => item.Version, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                cancellationToken);

            return new MecmBaselinesSnapshot(normalizedHost, entries, []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading MECM baselines failed for '{Host}'.", normalizedHost);
            return new MecmBaselinesSnapshot(normalizedHost, [], [ex.Message]);
        }
    }

    public async ValueTask<MecmBaselineDetails> GetBaselineDetailsAsync(string host, string baselineName, string version, bool isMachineTarget, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return new MecmBaselineDetails(baselineName ?? string.Empty, baselineName ?? string.Empty, version ?? string.Empty, isMachineTarget, [], ["No host was provided."]);
        }

        var normalizedHost = host.Trim();
        try
        {
            var result = await ExecuteWithClientAsync(
                normalizedHost,
                client =>
                {
                    var baseline = FindBaseline(client, baselineName, version, isMachineTarget);
                    if (baseline is null)
                    {
                        return new MecmBaselineDetails(
                            baselineName ?? string.Empty,
                            baselineName ?? string.Empty,
                            version ?? string.Empty,
                            isMachineTarget,
                            [],
                            [$"MECM baseline '{baselineName}' was not found."]);
                    }

                    var configItems = baseline.ConfigItems()
                        .Select(static item => new MecmBaselineConfigItem(
                            item.LogicalName ?? string.Empty,
                            item.CIName ?? string.Empty,
                            item.CIDescription ?? string.Empty,
                            item.Version ?? string.Empty,
                            item.Type ?? string.Empty,
                            item.Compliant,
                            item.Detected,
                            item.Applicable,
                            item.ConstraintViolation ?? string.Empty))
                        .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    return new MecmBaselineDetails(
                        baseline.Name ?? string.Empty,
                        baseline.DisplayName ?? baseline.Name ?? string.Empty,
                        baseline.Version ?? string.Empty,
                        baseline.IsMachineTarget ?? true,
                        configItems,
                        []);
                },
                cancellationToken);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Loading MECM baseline details failed for '{Host}'.", normalizedHost);
            return new MecmBaselineDetails(
                baselineName ?? string.Empty,
                baselineName ?? string.Empty,
                version ?? string.Empty,
                isMachineTarget,
                [],
                [ex.Message]);
        }
    }

    public async ValueTask<DeviceActionResult> TriggerBaselineEvaluationAsync(string host, string baselineName, string version, bool isMachineTarget, bool enforce, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        if (string.IsNullOrWhiteSpace(baselineName))
        {
            return DeviceActionResult.Fail("No baseline was provided.", "no_baseline");
        }

        var normalizedHost = host.Trim();
        try
        {
            var result = await ExecuteWithClientAsync(
                normalizedHost,
                client =>
                {
                    var baseline = FindBaseline(client, baselineName, version, isMachineTarget);
                    if (baseline is null)
                    {
                        throw new InvalidOperationException($"MECM baseline '{baselineName}' was not found.");
                    }

                    var returnCode = baseline.TriggerEvaluation(enforce, out var jobId);
                    return (ReturnCode: returnCode, JobId: jobId ?? string.Empty);
                },
                cancellationToken);

            return result.ReturnCode == 0
                ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(result.JobId)
                    ? $"MECM baseline evaluation requested on '{normalizedHost}'."
                    : $"MECM baseline evaluation requested on '{normalizedHost}'. JobId={result.JobId}.")
                : DeviceActionResult.Fail(
                    $"MECM baseline evaluation failed on '{normalizedHost}' with return code 0x{result.ReturnCode:X8}.",
                    "mecm_baseline_evaluation_failed");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Triggering MECM baseline evaluation failed for '{Host}'.", normalizedHost);
            return DeviceActionResult.Fail($"MECM baseline evaluation failed on '{normalizedHost}': {ex.Message}", "mecm_baseline_evaluation_failed");
        }
    }

    private async Task<TResult> ExecuteWithClientAsync<TResult>(string host, Func<ccm, TResult> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureConnected(host);
            return await Task.Run(() => operation(_agent!.Client), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureConnected(string host)
    {
        if (_agent is not null &&
            string.Equals(_connectedHost, host, StringComparison.OrdinalIgnoreCase) &&
            _agent.isConnected)
        {
            return;
        }

        DisposeAgent();

        Trace.AutoFlush = false;
        _agent = new SCCMAgent(host);
        _connectedHost = host;
    }

    private void DisposeAgent()
    {
        if (_agent is null)
        {
            _connectedHost = string.Empty;
            return;
        }

        try
        {
            if (_agent.isConnected)
            {
                _agent.disconnect();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Disposing SCCM agent connection failed.");
        }
        finally
        {
            try
            {
                _agent.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Disposing SCCM agent resources failed.");
            }

            _agent = null;
            _connectedHost = string.Empty;
        }
    }

    private static MecmApplicationEntry MapApplication(softwaredistribution.CCM_Application application)
    {
        var allowedActions = application.AllowedActions ?? [];
        return new MecmApplicationEntry(
            application.Id ?? string.Empty,
            application.Name ?? string.Empty,
            application.FullName ?? string.Empty,
            application.Description ?? string.Empty,
            application.Icon ?? string.Empty,
            application.SoftwareVersion ?? string.Empty,
            application.Revision ?? string.Empty,
            application.UserUIExperience ?? false,
            application.IsPreflightOnly ?? false,
            application.IsMachineTarget ?? true,
            allowedActions,
            application.InstallState ?? string.Empty,
            application.ApplicabilityState ?? string.Empty,
            application.ResolvedState ?? string.Empty,
            ConvertInt(application.EvaluationState),
            application.EvaluationStateText ?? string.Empty,
            application.ErrorCode,
            application.ErrorCodeText ?? string.Empty,
            ToUtcOffset(application.LastEvalTime),
            ToUtcOffset(application.LastInstallTime),
            allowedActions.Contains("Install", StringComparer.OrdinalIgnoreCase),
            allowedActions.Contains("Uninstall", StringComparer.OrdinalIgnoreCase),
            !string.IsNullOrWhiteSpace(application.Icon));
    }

    private static dcm.SMS_DesiredConfiguration? FindBaseline(ccm client, string baselineName, string version, bool isMachineTarget)
    {
        var normalizedName = baselineName?.Trim() ?? string.Empty;
        var normalizedVersion = version?.Trim() ?? string.Empty;

        return client.DCM.DCMBaselines.FirstOrDefault(item =>
            string.Equals(item.Name ?? string.Empty, normalizedName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Version ?? string.Empty, normalizedVersion, StringComparison.OrdinalIgnoreCase) &&
            (item.IsMachineTarget ?? true) == isMachineTarget);
    }

    private static DateTimeOffset? ToUtcOffset(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        var normalized = value.Value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Local)
            : value.Value;

        return new DateTimeOffset(normalized).ToUniversalTime();
    }

    private static int? ConvertInt(uint? value) => value.HasValue ? checked((int)value.Value) : null;

    private static string FormatNumericState(string label, uint? value)
    {
        return value.HasValue ? $"{label} {value.Value.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
    }

    private static string SummarizeComplianceDetails(string complianceDetails)
    {
        if (string.IsNullOrWhiteSpace(complianceDetails))
        {
            return string.Empty;
        }

        return complianceDetails.Length <= 120
            ? complianceDetails
            : complianceDetails[..120] + "...";
    }
}
