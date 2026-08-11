using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Globalization;
using System.Net;
using System.Diagnostics;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Shared.Diagnostics;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed partial class LocalIntuneDiagnosticsService(IPowerShellExecutor executor, HttpClient httpClient, IntuneRuntimeOptions options) : ILocalIntuneDiagnosticsService
{
    private const string DefaultMdmAdminLogName = "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin";
    private static readonly Regex HexCodeRegex = new(@"0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);
    private static readonly Regex GuidRegex = new(@"[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}", RegexOptions.Compiled);
    private static readonly Regex AreaRegex = new(@"Area\s*[:=]\s*([^\r\n,]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PolicyRegex = new(@"Policy\s*[:=]\s*([^\r\n,]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CspUriRegex = new(@"(?:\./)?(?:Device/|User/)?Vendor/MSFT/[^\s,;]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DsregFieldRegex = new(@"^\s*([^\r\n:=][^\r\n:=]*?)\s*[:=]\s*([^\r\n]*)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex LegacyJsonDateRegex = new(@"^/Date\((?<ms>-?\d+)(?<offset>[+-]\d{4})?\)/$", RegexOptions.Compiled);
    private static readonly Regex ReleaseRowRegex = new(@"<tr>\s*<td>.*?</td>\s*<td>.*?</td>\s*<td>(?<date>.*?)</td>\s*<td>(?<build>\d+\.\d+)</td>\s*<td>.*?KB(?<kb>\d+).*?</td>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private const string Windows11ReleaseHealthUrl = "https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information";
    private const string Windows10ReleaseHealthUrl = "https://learn.microsoft.com/en-us/windows/release-health/release-information";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _vpnAdapterDescriptionMatch = options.VpnAdapterDescriptionMatch?.Trim() ?? string.Empty;

    private readonly string _vpnProviderName = options.VpnProviderName?.Trim() ?? string.Empty;

    public async ValueTask<LocalIntuneSnapshot> GetSnapshotAsync(string host, CancellationToken cancellationToken)
    {
        var result = await GetSnapshotDiagnosticsAsync(host, cancellationToken);
        return result.Snapshot;
    }

    public async ValueTask<LocalIntuneSnapshotDiagnosticsResult> GetSnapshotDiagnosticsAsync(string host, CancellationToken cancellationToken)
    {
        var timings = new List<string>();
        var totalTimer = Stopwatch.StartNew();

        var executionTimer = Stopwatch.StartNew();
        var execution = await executor.ExecuteForHostAsync(host, BuildSnapshotScript(), cancellationToken);
        timings.Add($"PowerShell snapshot script completed in {executionTimer.ElapsedMilliseconds} ms.");

        var parseTimer = Stopwatch.StartNew();
        var snapshot = ParseSnapshot(host, execution, out var scriptTimings);
        timings.AddRange(scriptTimings);
        timings.Add($"Snapshot payload parsing completed in {parseTimer.ElapsedMilliseconds} ms.");

        var patchTimer = Stopwatch.StartNew();
        snapshot = await EnrichPatchStatusAsync(snapshot, cancellationToken, timings);
        timings.Add($"Patch status enrichment completed in {patchTimer.ElapsedMilliseconds} ms.");

        timings.Add($"Local diagnostics total completed in {totalTimer.ElapsedMilliseconds} ms.");
        return new LocalIntuneSnapshotDiagnosticsResult(snapshot, timings);
    }

    public async ValueTask<LocalIntuneSnapshot> GetOverviewCoreSnapshotAsync(string host, CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteForHostAsync(host, BuildOverviewCoreSnapshotScript(), cancellationToken);
        var snapshot = ParseSnapshot(host, execution, out _);
        return await EnrichPatchStatusAsync(snapshot, cancellationToken);
    }

    public async ValueTask<PlatformSecuritySnapshot?> GetPlatformSecuritySnapshotAsync(string host, CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteForHostAsync(host, BuildPlatformSecuritySnapshotScript(), cancellationToken);
        return ParsePlatformSecurityOnlySnapshot(execution);
    }

    public async ValueTask<SystemRuntimeSnapshot?> GetSystemRuntimeSnapshotAsync(string host, CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteForHostAsync(host, BuildSystemRuntimeSnapshotScript(), cancellationToken);
        return ParseSystemRuntimeOnlySnapshot(execution);
    }

    public async ValueTask<NetworkConnectivitySnapshot?> GetNetworkConnectivitySnapshotAsync(string host, CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteForHostAsync(host, BuildNetworkConnectivitySnapshotScript(), cancellationToken);
        var snapshot = ParseNetworkConnectivityOnlySnapshot(execution);
        try
        {
            var portAuthentication = await GetPortAuthenticationSnapshotAsync(host, cancellationToken);
            if (snapshot is not null && portAuthentication is not null)
            {
                snapshot = snapshot with
                {
                    PortAuthenticationStatusText = portAuthentication.OverallStatusText,
                    PortAuthenticationDetailText = portAuthentication.OverallDetailText
                };
            }
        }
        catch
        {
        }

        return snapshot;
    }

    public async ValueTask<DeliveryOptimizationSnapshot?> GetDeliveryOptimizationSnapshotAsync(string host, CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteForHostAsync(host, BuildDeliveryOptimizationSnapshotScript(), cancellationToken);
        return ParseDeliveryOptimizationOnlySnapshot(execution);
    }

    public async ValueTask<IReadOnlyList<IntuneLogEntry>> GetLogEntriesAsync(string host, string logName, int maxEntries, CancellationToken cancellationToken)
    {
        var normalizedLog = NormalizeLogName(logName);
        var clampedMaxEntries = Math.Clamp(maxEntries, 1, 200);
        var execution = await executor.ExecuteForHostAsync(host, BuildEventLogScript(normalizedLog, clampedMaxEntries), cancellationToken);
        if (execution.ExitCode != 0)
        {
            return
            [
                new IntuneLogEntry(normalizedLog, DateTimeOffset.UtcNow, 0, "Error", "WindowsClientCenter", NormalizeError(execution))
            ];
        }

        try
        {
            if (!TryParsePowerShellJsonDocument(execution.StdOut, out var document, out _, out var parseError))
            {
                return
                [
                    new IntuneLogEntry(normalizedLog, DateTimeOffset.UtcNow, 0, "Error", "WindowsClientCenter", $"Failed to parse event log payload: {parseError}")
                ];
            }

            using (document)
            {
                var entries = document.RootElement.Deserialize<List<EventLogPayload>>(JsonOptions) ?? [];
                return entries.Select(entry => new IntuneLogEntry(
                    normalizedLog,
                    ParseTimestamp(entry.TimeCreated),
                    entry.Id,
                    entry.Level ?? string.Empty,
                    entry.Provider ?? string.Empty,
                    entry.Message ?? string.Empty)).ToArray();
            }
        }
        catch (JsonException ex)
        {
            return
            [
                new IntuneLogEntry(normalizedLog, DateTimeOffset.UtcNow, 0, "Error", "WindowsClientCenter", $"Failed to parse event log payload: {ex.Message}")
            ];
        }
    }

    public async ValueTask<IReadOnlyList<MdmEventAnalysisEntry>> GetMdmAdminEventsAsync(string host, int maxEntries, CancellationToken cancellationToken)
    {
        var clampedMaxEntries = Math.Clamp(maxEntries, 20, 400);
        var execution = await executor.ExecuteForHostAsync(host, BuildDetailedEventLogScript(DefaultMdmAdminLogName, clampedMaxEntries), cancellationToken);
        if (execution.ExitCode != 0)
        {
            return
            [
                BuildSyntheticFailure(DefaultMdmAdminLogName, NormalizeError(execution))
            ];
        }

        try
        {
            if (!TryParsePowerShellJsonDocument(execution.StdOut, out var document, out _, out var parseError))
            {
                return
                [
                    BuildSyntheticFailure(DefaultMdmAdminLogName, $"Failed to parse MDM event payload: {parseError}")
                ];
            }

            using (document)
            {
                var entries = document.RootElement.Deserialize<List<DetailedEventLogPayload>>(JsonOptions) ?? [];
                return entries
                    .Select(entry => AnalyzeMdmEvent(entry, DefaultMdmAdminLogName))
                    .OrderByDescending(entry => entry.TimeCreated ?? DateTimeOffset.MinValue)
                    .ToArray();
            }
        }
        catch (JsonException ex)
        {
            return
            [
                BuildSyntheticFailure(DefaultMdmAdminLogName, $"Failed to parse MDM event payload: {ex.Message}")
            ];
        }
    }

    public async ValueTask<string> ExportSnapshotAsync(string host, string outputDirectory, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(host, cancellationToken);
        var deliveryOptimization = await GetDeliveryOptimizationSnapshotAsync(host, cancellationToken);
        if (deliveryOptimization is not null)
        {
            snapshot = snapshot with { DeliveryOptimization = deliveryOptimization };
        }

        Directory.CreateDirectory(outputDirectory);
        var safeHost = SanitizeFileName(snapshot.Host);
        var path = Path.Combine(outputDirectory, $"intune-snapshot-{safeHost}-{DateTimeOffset.Now:yyyyMMddHHmmss}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken);
        return path;
    }

    public async ValueTask<string> ExportMdmDiagnosticsAsync(string host, string outputDirectory, CancellationToken cancellationToken)
    {
        if (!LocalPowerShellExecutor.IsLocalHost(host))
        {
            throw new InvalidOperationException("MDM diagnostics export is only supported for the local host in v1.");
        }

        Directory.CreateDirectory(outputDirectory);
        var safeOutput = outputDirectory.Replace("'", "''", StringComparison.Ordinal);
        var execution = await executor.ExecuteForHostAsync(host, BuildMdmDiagnosticsExportScript(safeOutput), cancellationToken);
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(NormalizeError(execution));
        }

        var exportedPath = execution.StdOut.Trim();
        if (string.IsNullOrWhiteSpace(exportedPath))
        {
            throw new InvalidOperationException("mdmdiagnosticstool.exe did not return an export path.");
        }

        return exportedPath;
    }

    private static LocalIntuneSnapshot ParseSnapshot(string host, PowershellExecutionResult execution, out IReadOnlyList<string> diagnosticsTimings)
    {
        diagnosticsTimings = [];
        if (execution.ExitCode != 0)
        {
            return new LocalIntuneSnapshot(
                host,
                Environment.MachineName,
                DateTimeOffset.UtcNow,
                LocalPowerShellExecutor.IsLocalHost(host),
                "Unknown",
                "Failed to collect diagnostics.",
                string.Empty,
                [],
                [],
                [],
                [],
                [],
                [NormalizeError(execution)],
                "Unknown",
                "Unknown",
                "Unknown",
                "Unknown",
                "Unknown",
                "Patch status unavailable.",
                "Unknown",
                null,
                null,
                null,
                null,
                "Unknown",
                "Unknown",
                "Unknown",
                "Unknown");
        }

        try
        {
            if (!TryParsePowerShellJsonDocument(execution.StdOut, out var document, out var parseWarning, out var parseError))
            {
                throw new InvalidOperationException(parseError);
            }

            using var _ = document;
            var payload = document.RootElement.Deserialize<SnapshotPayload>(JsonOptions)
                          ?? throw new InvalidOperationException("Diagnostics payload was empty.");
            diagnosticsTimings = payload.DiagnosticsTimings ?? [];
            var evaluatedDsregHighlights = EvaluateDsregHighlights(payload.DsregStatusText, payload.DsregHighlights);
            var doSnapshot = ParseDeliveryOptimizationSnapshot(payload.DeliveryOptimization);
            var platformSecurity = ParsePlatformSecuritySnapshot(payload.PlatformSecurity);
            var systemRuntime = ParseSystemRuntimeSnapshot(payload.SystemRuntime);
            var networkConnectivity = ParseNetworkConnectivitySnapshot(payload.NetworkConnectivity);
            var manufacturerText = string.IsNullOrWhiteSpace(payload.ManufacturerText) ? "Unknown" : payload.ManufacturerText.Trim();
            var modelText = string.IsNullOrWhiteSpace(payload.ModelText) ? "Unknown" : payload.ModelText.Trim();
            var serialNumberText = string.IsNullOrWhiteSpace(payload.SerialNumberText) ? "Unknown" : payload.SerialNumberText.Trim();
            var adJoinPathText = string.IsNullOrWhiteSpace(payload.AdJoinPathText) ? "Unknown" : payload.AdJoinPathText.Trim();
            var updateRingText = string.IsNullOrWhiteSpace(payload.UpdateRingText) ? "Unknown" : payload.UpdateRingText.Trim();
            var notes = payload.Notes ?? [];
            if (!string.IsNullOrWhiteSpace(parseWarning))
            {
                notes = [.. notes, parseWarning];
            }

            return new LocalIntuneSnapshot(
                host,
                payload.MachineName ?? host,
                ParseCapturedAtUtc(payload.CapturedAtUtc) ?? DateTimeOffset.UtcNow,
                LocalPowerShellExecutor.IsLocalHost(host),
                payload.LastSyncText ?? "Unknown",
                payload.RegistrationSummary ?? "Unknown",
                payload.DsregStatusText ?? string.Empty,
                evaluatedDsregHighlights,
                (payload.EnrollmentArtifacts ?? []).Select(ToArtifact).ToArray(),
                payload.EnterpriseMgmtTasks ?? [],
                payload.CertificateSummaries ?? [],
                (payload.ServiceValues ?? []).Select(item => new NameValueItem(item.Name ?? string.Empty, item.Value ?? string.Empty)).ToArray(),
                notes,
                payload.MdmLastSyncText ?? payload.LastSyncText ?? "Unknown",
                payload.ImeLastSyncText ?? "Unknown",
                payload.WindowsVersionText ?? "Unknown",
                payload.WindowsBuildText ?? "Unknown",
                payload.FreeDiskSpaceText ?? "Unknown",
                "Patch status unavailable.",
                "Unknown",
                doSnapshot,
                platformSecurity,
                systemRuntime,
                networkConnectivity,
                manufacturerText,
                modelText,
                serialNumberText,
                adJoinPathText,
                updateRingText);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new LocalIntuneSnapshot(
                host,
                Environment.MachineName,
                DateTimeOffset.UtcNow,
                LocalPowerShellExecutor.IsLocalHost(host),
                "Unknown",
                "Failed to parse diagnostics payload.",
                execution.StdOut,
                [],
                [],
                [],
                [],
                [],
                [$"Diagnostics parsing failed: {ex.Message}"],
                "Unknown",
                "Unknown",
                "Unknown",
                "Unknown",
                "Unknown",
                "Patch status unavailable.",
                "Unknown",
                null,
                null,
                null,
                null,
                "Unknown",
                "Unknown",
                "Unknown",
                "Unknown");
        }
    }

    private static DeliveryOptimizationSnapshot ParseDeliveryOptimizationOnlySnapshot(PowershellExecutionResult execution)
    {
        if (execution.ExitCode != 0)
        {
            return BuildUnavailableDeliveryOptimizationSnapshot(NormalizeError(execution));
        }

        try
        {
            if (!TryParsePowerShellJsonDocument(execution.StdOut, out var document, out var parseWarning, out var parseError))
            {
                throw new InvalidOperationException(parseError);
            }

            using var _ = document;
            var payload = document.RootElement.Deserialize<DeliveryOptimizationOnlyPayload>(JsonOptions)
                          ?? throw new InvalidOperationException("Delivery Optimization payload was empty.");
            var snapshot = ParseDeliveryOptimizationSnapshot(payload.DeliveryOptimization);
            if (snapshot is null)
            {
                return BuildUnavailableDeliveryOptimizationSnapshot("Delivery Optimization data is not available on this device.");
            }

            if (!string.IsNullOrWhiteSpace(parseWarning))
            {
                snapshot = snapshot with { Notes = [.. snapshot.Notes, parseWarning] };
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return BuildUnavailableDeliveryOptimizationSnapshot($"Delivery Optimization parsing failed: {ex.Message}");
        }
    }

    private static PlatformSecuritySnapshot ParsePlatformSecurityOnlySnapshot(PowershellExecutionResult execution)
    {
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(NormalizeError(execution));
        }

        try
        {
            if (!TryParsePowerShellJsonDocument(execution.StdOut, out var document, out _, out var parseError))
            {
                throw new InvalidOperationException(parseError);
            }

            using var parsedDocument = document;
            var payload = document.RootElement.Deserialize<PlatformSecurityOnlyPayload>(JsonOptions)
                          ?? throw new InvalidOperationException("Platform security payload was empty.");
            return ParsePlatformSecuritySnapshot(payload.PlatformSecurity)
                   ?? new PlatformSecuritySnapshot("Unknown", "BitLocker status is not available.", "Unknown", "Unknown", "TPM status is not available.", "Unknown", "Unknown", "Unknown", "Unknown");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Platform security parsing failed: {ex.Message}", ex);
        }
    }

    private static SystemRuntimeSnapshot ParseSystemRuntimeOnlySnapshot(PowershellExecutionResult execution)
    {
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(NormalizeError(execution));
        }

        try
        {
            if (!TryParsePowerShellJsonDocument(execution.StdOut, out var document, out _, out var parseError))
            {
                throw new InvalidOperationException(parseError);
            }

            using var parsedDocument = document;
            var payload = document.RootElement.Deserialize<SystemRuntimeOnlyPayload>(JsonOptions)
                          ?? throw new InvalidOperationException("System runtime payload was empty.");
        return ParseSystemRuntimeSnapshot(payload.SystemRuntime)
                   ?? new SystemRuntimeSnapshot(
                       "Unknown",
                       "Unknown",
                       "Unknown",
                       "Unknown",
                       "Pending reboot state is not available.",
                       "Unknown",
                       "Windows Update scheduled restart state is not available.",
                       "MECM scheduled restart state is not available.",
                       "Unknown",
                       "Session lock state is not available.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"System runtime parsing failed: {ex.Message}", ex);
        }
    }

    private static NetworkConnectivitySnapshot ParseNetworkConnectivityOnlySnapshot(PowershellExecutionResult execution)
    {
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(NormalizeError(execution));
        }

        try
        {
            if (!TryParsePowerShellJsonDocument(execution.StdOut, out var document, out _, out var parseError))
            {
                throw new InvalidOperationException(parseError);
            }

            using var parsedDocument = document;
            var payload = document.RootElement.Deserialize<NetworkConnectivityOnlyPayload>(JsonOptions)
                          ?? throw new InvalidOperationException("Network connectivity payload was empty.");
            return ParseNetworkConnectivitySnapshot(payload.NetworkConnectivity)
                   ?? new NetworkConnectivitySnapshot("Unknown", "Unknown", "Not connected", "Not detected", "-", false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Network connectivity parsing failed: {ex.Message}", ex);
        }
    }

    private static DeliveryOptimizationSnapshot BuildUnavailableDeliveryOptimizationSnapshot(string note)
    {
        var notes = string.IsNullOrWhiteSpace(note)
            ? Array.Empty<string>()
            : new[] { note.Trim() };

        return new DeliveryOptimizationSnapshot(
            false,
            DateTimeOffset.UtcNow,
            [],
            [],
            notes,
            false,
            null,
            null,
            [],
            [],
            [],
            [],
            []);
    }

    private static EnrollmentArtifact ToArtifact(ArtifactPayload payload) =>
        new(
            payload.ArtifactType ?? string.Empty,
            payload.ArtifactPath ?? string.Empty,
            payload.Description ?? string.Empty,
            payload.EnrollmentId,
            payload.IsRemovable);

    private static DeliveryOptimizationSnapshot? ParseDeliveryOptimizationSnapshot(DeliveryOptimizationPayload? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var capturedAt = ParseCapturedAtUtc(payload.CapturedAtUtc) ?? DateTimeOffset.UtcNow;
        var sourceStats = (payload.SourceStats ?? [])
            .Select(item => new DeliveryOptimizationSourceStat(
                NormalizeDeliveryOptimizationSource(item.Source),
                Math.Max(0L, item.Bytes),
                Math.Max(0, item.TransferCount)))
            .Where(item => item.Bytes > 0 || item.TransferCount > 0)
            .ToArray();

        var transfers = (payload.Transfers ?? [])
            .Select(item =>
            {
                var timestamp = ParseCapturedAtUtc(item.TimestampUtc) ?? capturedAt;
                return new DeliveryOptimizationTransferEntry(
                    timestamp,
                    NormalizeDeliveryOptimizationSource(item.Source),
                    Math.Max(0L, item.Bytes),
                    string.IsNullOrWhiteSpace(item.Description) ? "-" : item.Description!.Trim());
            })
            .Where(item => item.Bytes > 0)
            .OrderByDescending(item => item.TimestampUtc)
            .ToArray();

        var dataStartUtc = ParseCapturedAtUtc(payload.DataStartUtc);
        var dataEndUtc = ParseCapturedAtUtc(payload.DataEndUtc);
        if (dataEndUtc is null && transfers.Length > 0)
        {
            dataEndUtc = transfers.Max(item => item.TimestampUtc);
        }

        if (dataStartUtc is null && transfers.Length > 0)
        {
            dataStartUtc = transfers.Min(item => item.TimestampUtc);
        }

        var currentMetrics = (payload.CurrentMetrics ?? [])
            .Select(item => new NameValueItem(item.Name ?? string.Empty, item.Value ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Value))
            .ToArray();

        var monthlyMetrics = (payload.MonthlyMetrics ?? [])
            .Select(item => new NameValueItem(item.Name ?? string.Empty, item.Value ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Value))
            .ToArray();

        var configuration = (payload.Configuration ?? [])
            .Select(item => new NameValueItem(item.Name ?? string.Empty, item.Value ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Value))
            .ToArray();

        var peerStatuses = (payload.PeerStatuses ?? [])
            .Select(item => new DeliveryOptimizationPeerStatus(
                string.IsNullOrWhiteSpace(item.Content) ? "-" : item.Content!.Trim(),
                string.IsNullOrWhiteSpace(item.Status) ? "-" : item.Status!.Trim(),
                Math.Max(0, item.CandidateCount),
                Math.Max(0, item.ConnectedPeerCount),
                Math.Max(0L, item.BytesFromPeers),
                Math.Max(0L, item.BytesFromHttp),
                string.IsNullOrWhiteSpace(item.Details) ? "-" : item.Details!.Trim()))
            .ToArray();

        var activeJobs = (payload.ActiveJobs ?? [])
            .Select(item => new DeliveryOptimizationJobStatus(
                string.IsNullOrWhiteSpace(item.Content) ? "-" : item.Content!.Trim(),
                string.IsNullOrWhiteSpace(item.Status) ? "-" : item.Status!.Trim(),
                Math.Max(0L, item.FileSizeBytes),
                Math.Max(0L, item.DownloadedBytes),
                Math.Max(0L, item.DownloadRateBytesPerSecond),
                string.IsNullOrWhiteSpace(item.Details) ? "-" : item.Details!.Trim()))
            .ToArray();

        return new DeliveryOptimizationSnapshot(
            payload.IsAvailable,
            capturedAt,
            sourceStats,
            transfers,
            payload.Notes ?? [],
            payload.SupportsTimeRangeFiltering,
            dataStartUtc,
            dataEndUtc,
            currentMetrics,
            monthlyMetrics,
            configuration,
            peerStatuses,
            activeJobs);
    }

    private static PlatformSecuritySnapshot? ParsePlatformSecuritySnapshot(PlatformSecurityPayload? payload)
    {
        if (payload is null)
        {
            return null;
        }

        return new PlatformSecuritySnapshot(
            payload.BitLockerStatusText ?? "Unknown",
            payload.BitLockerDetailText ?? "BitLocker status is not available.",
            payload.TpmStatusText ?? "Unknown",
            payload.TpmVersionText ?? "Unknown",
            payload.TpmDetailText ?? "TPM status is not available.",
            payload.SecureBootStatusText ?? "Unknown",
            payload.CredentialGuardStatusText ?? "Unknown",
            payload.VbsStatusText ?? "Unknown",
            payload.MemoryIntegrityStatusText ?? "Unknown");
    }

    private static SystemRuntimeSnapshot? ParseSystemRuntimeSnapshot(SystemRuntimePayload? payload)
    {
        if (payload is null)
        {
            return null;
        }

        return new SystemRuntimeSnapshot(
            payload.UptimeText ?? "Unknown",
            payload.LastBootText ?? "Unknown",
            payload.InstallDateText ?? "Unknown",
            payload.PendingRebootStatusText ?? "Unknown",
            payload.PendingRebootDetailText ?? "Pending reboot state is not available.",
            payload.WindowsUpdateScheduledRestartStatusText ?? "Unknown",
            payload.WindowsUpdateScheduledRestartTimeText ?? "Windows Update scheduled restart state is not available.",
            payload.MecmScheduledRestartTimeText ?? "MECM scheduled restart state is not available.",
            payload.SessionLockStatusText ?? "Unknown",
            payload.SessionLockedSinceText ?? "Session lock state is not available.");
    }

    private static NetworkConnectivitySnapshot? ParseNetworkConnectivitySnapshot(NetworkConnectivityPayload? payload)
    {
        if (payload is null)
        {
            return null;
        }

        return new NetworkConnectivitySnapshot(
            payload.PrimaryConnectionText ?? "Unknown",
            payload.PrimaryAdapterText ?? "Unknown",
            payload.WiFiSsidText ?? "Not connected",
            payload.VpnStatusText ?? "Unknown",
            payload.VpnProviderText ?? "-",
            payload.IsCheckpointVpnDetected,
            payload.PortAuthenticationStatusText ?? "Unknown",
            payload.PortAuthenticationDetailText ?? "Port authentication status is not available.");
    }

    private static string NormalizeDeliveryOptimizationSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "Unknown";
        }

        return source.Trim() switch
        {
            "Http" => "HTTP/CDN",
            "CacheServer" => "Cache Server",
            "PeerLan" => "Peer (LAN)",
            "PeerGroup" => "Peer (Group)",
            "PeerInternet" => "Peer (Internet)",
            "Peer" => "Peer (Unclassified)",
            _ => source.Trim()
        };
    }

    private static string NormalizeLogName(string logName)
    {
        return string.IsNullOrWhiteSpace(logName)
            ? DefaultMdmAdminLogName
            : logName.Trim();
    }

    private static bool TryParsePowerShellJsonDocument(
        string rawOutput,
        out JsonDocument document,
        out string warning,
        out string error)
    {
        warning = string.Empty;
        error = "PowerShell output was empty.";
        document = null!;

        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return false;
        }

        var trimmed = rawOutput.Trim();
        if (TryParseJsonDocument(trimmed, out document))
        {
            error = string.Empty;
            return true;
        }

        var startIndex = -1;
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] == '{' || trimmed[i] == '[')
            {
                startIndex = i;
                break;
            }
        }

        if (startIndex < 0 ||
            !TryExtractBalancedJsonBlock(trimmed, startIndex, out var jsonText, out var prefixLength, out var suffixLength) ||
            !TryParseJsonDocument(jsonText, out document))
        {
            error = "No valid JSON payload found in PowerShell output.";
            return false;
        }

        error = string.Empty;
        if (prefixLength > 0 || suffixLength > 0)
        {
            warning = $"Diagnostics output contained additional console text and was normalized (prefix chars: {prefixLength}, suffix chars: {suffixLength}).";
        }

        return true;

        static bool TryParseJsonDocument(string candidate, out JsonDocument parsed)
        {
            try
            {
                parsed = JsonDocument.Parse(candidate);
                return true;
            }
            catch (JsonException)
            {
                parsed = null!;
                return false;
            }
        }
    }

    private static bool TryExtractBalancedJsonBlock(
        string text,
        int startIndex,
        out string json,
        out int prefixLength,
        out int suffixLength)
    {
        json = string.Empty;
        prefixLength = 0;
        suffixLength = 0;

        if (string.IsNullOrWhiteSpace(text) || startIndex < 0 || startIndex >= text.Length)
        {
            return false;
        }

        var open = text[startIndex];
        var close = open == '{' ? '}' : open == '[' ? ']' : '\0';
        if (close == '\0')
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = startIndex; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == open)
            {
                depth++;
                continue;
            }

            if (ch != close)
            {
                continue;
            }

            depth--;
            if (depth != 0)
            {
                continue;
            }

            json = text[startIndex..(i + 1)];
            prefixLength = startIndex;
            suffixLength = text.Length - i - 1;
            return true;
        }

        return false;
    }

    private static string NormalizeError(PowershellExecutionResult execution)
    {
        var raw = string.IsNullOrWhiteSpace(execution.StdErr) ? execution.StdOut : execution.StdErr;
        return string.IsNullOrWhiteSpace(raw)
            ? $"PowerShell execution failed with exit code {execution.ExitCode}."
            : raw.Trim();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private static IReadOnlyList<string> EvaluateDsregHighlights(string? dsregStatusText, IReadOnlyList<string>? fallbackHighlights)
    {
        if (string.IsNullOrWhiteSpace(dsregStatusText))
        {
            return fallbackHighlights ?? [];
        }

        var fields = ParseDsregFields(dsregStatusText);
        if (fields.Count == 0)
        {
            return fallbackHighlights ?? [];
        }

        var highlights = new List<string>();
        AddExpectedBooleanHighlight(highlights, fields, "AzureAdJoined", "YES", "Device is Microsoft Entra joined.", "Device is not Microsoft Entra joined.", false);
        AddExpectedBooleanHighlight(highlights, fields, "AzureAdPrt", "YES", "Primary Refresh Token is available.", "Primary Refresh Token is missing in the current session.", true);
        AddExpectedBooleanHighlight(highlights, fields, "TpmProtected", "YES", "Device registration keys are TPM-protected.", "Device registration keys are not TPM-protected.", true);

        var deviceAuthStatus = GetDsregField(fields, "DeviceAuthStatus");
        if (!string.IsNullOrWhiteSpace(deviceAuthStatus))
        {
            var isSuccess = deviceAuthStatus.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase);
            var severity = isSuccess ? "OK" : "Error";
            var guidance = isSuccess ? "Device authentication is healthy." : "Device authentication with Entra ID is failing or incomplete.";
            highlights.Add($"[{severity}] DeviceAuthStatus: {deviceAuthStatus} - {guidance}");
        }

        AddInformationalBooleanHighlight(highlights, fields, "DomainJoined", "Device is joined to on-premises Active Directory.");
        AddInformationalBooleanHighlight(highlights, fields, "WorkplaceJoined", "Workplace join is present.");

        var mdmUrl = GetDsregField(fields, "MdmUrl");
        if (string.IsNullOrWhiteSpace(mdmUrl) || mdmUrl == "-")
        {
            highlights.Add("[Info] MdmUrl: not reported. This can be tenant/scope dependent and is not always a hard failure.");
        }

        foreach (var fieldName in new[] { "ClientErrorCode", "ServerErrorCode", "AttemptStatus", "HttpError" })
        {
            var value = GetDsregField(fields, fieldName);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (Match codeMatch in HexCodeRegex.Matches(value))
            {
                var code = codeMatch.Value.ToUpperInvariant();
                var description = ErrorCodeResolver.ResolveDescription(code);
                if (string.IsNullOrWhiteSpace(description))
                {
                    description = "Unmapped dsregcmd error code.";
                }
                var severity = code.Equals("0x80090031", StringComparison.OrdinalIgnoreCase) ? "Warning" : "Error";
                highlights.Add($"[{severity}] {fieldName}: {code} - {description}");
            }
        }

        var aggregatedErrors = string.Join(" ",
            GetDsregField(fields, "ServerMessage"),
            GetDsregField(fields, "ServerErrorDescription"),
            GetDsregField(fields, "ClientErrorCode"),
            GetDsregField(fields, "ServerErrorCode"));

        if (!string.IsNullOrWhiteSpace(aggregatedErrors))
        {
            if (aggregatedErrors.Contains("AADSTS50126", StringComparison.OrdinalIgnoreCase))
            {
                highlights.Add("[Error] Authentication failed with AADSTS50126 (invalid credentials or identity mismatch).");
            }

            if (aggregatedErrors.Contains("AADSTS90002", StringComparison.OrdinalIgnoreCase) ||
                aggregatedErrors.Contains("tenant uuid not found", StringComparison.OrdinalIgnoreCase))
            {
                highlights.Add("[Error] Tenant identifier could not be resolved (AADSTS90002 / tenant lookup failure).");
            }
        }

        return highlights.Count > 0 ? highlights : fallbackHighlights ?? [];
    }

    private static Dictionary<string, string> ParseDsregFields(string dsregStatusText)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in DsregFieldRegex.Matches(dsregStatusText))
        {
            if (!match.Success)
            {
                continue;
            }

            var normalizedKey = NormalizeDsregKey(match.Groups[1].Value);
            var value = match.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(normalizedKey) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            fields[normalizedKey] = value;
        }

        return fields;
    }

    private static string NormalizeDsregKey(string key)
    {
        return string.Concat(key.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    private static string GetDsregField(IReadOnlyDictionary<string, string> fields, string key)
    {
        return fields.TryGetValue(NormalizeDsregKey(key), out var value) ? value : string.Empty;
    }

    private static bool? ParseDsregBooleanValue(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var span = rawValue.AsSpan().Trim();
        var tokenBuffer = new char[32];
        var tokenLength = 0;
        foreach (var ch in span)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (tokenLength < tokenBuffer.Length)
                {
                    tokenBuffer[tokenLength++] = char.ToUpperInvariant(ch);
                }

                continue;
            }

            if (tokenLength > 0)
            {
                break;
            }
        }

        if (tokenLength == 0)
        {
            return null;
        }

        var token = new string(tokenBuffer, 0, tokenLength);
        return token switch
        {
            "YES" or "Y" or "TRUE" or "1" or "JA" or "WAHR" or "AKTIV" => true,
            "NO" or "N" or "FALSE" or "0" or "NEIN" or "FALSCH" or "INAKTIV" => false,
            _ => null
        };
    }

    private static void AddExpectedBooleanHighlight(List<string> highlights, IReadOnlyDictionary<string, string> fields, string key, string expectedValue, string successGuidance, string failureGuidance, bool warningOnMismatch)
    {
        var value = GetDsregField(fields, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var parsedValue = ParseDsregBooleanValue(value);
        var parsedExpected = ParseDsregBooleanValue(expectedValue);
        var matchesExpected = parsedValue.HasValue && parsedExpected.HasValue
            ? parsedValue.Value == parsedExpected.Value
            : value.Equals(expectedValue, StringComparison.OrdinalIgnoreCase);
        var severity = matchesExpected ? "OK" : warningOnMismatch ? "Warning" : "Error";
        highlights.Add($"[{severity}] {key}: {value} - {(matchesExpected ? successGuidance : failureGuidance)}");
    }

    private static void AddInformationalBooleanHighlight(List<string> highlights, IReadOnlyDictionary<string, string> fields, string key, string guidanceWhenYes)
    {
        var value = GetDsregField(fields, key);
        var isYes = ParseDsregBooleanValue(value);
        if (isYes != true)
        {
            return;
        }

        highlights.Add($"[Info] {key}: {value} - {guidanceWhenYes}");
    }

    private string BuildSnapshotScript()
    {
        var escapedVpnAdapterDescriptionMatch = _vpnAdapterDescriptionMatch.Replace("'", "''", StringComparison.Ordinal);
        var escapedVpnProviderName = _vpnProviderName.Replace("'", "''", StringComparison.Ordinal);
        return """
        $script:diagTimings = New-Object System.Collections.Generic.List[string]
        $script:diagScriptStartedUtc = [DateTime]::UtcNow

        function Start-DiagTimer {
          return [DateTime]::UtcNow
        }

        function Add-DiagTiming([string]$name, $startedAt) {
          if ([string]::IsNullOrWhiteSpace($name) -or $null -eq $startedAt) { return }
          try {
            $elapsedMs = [int][Math]::Round(([DateTime]::UtcNow - [DateTime]$startedAt).TotalMilliseconds)
            $script:diagTimings.Add($name + ' completed in ' + $elapsedMs + ' ms.') | Out-Null
          } catch {
          }
        }

        function Get-GuidLikeChildren($path) {
          if (-not (Test-Path -LiteralPath $path)) { return @() }
          Get-ChildItem -LiteralPath $path -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^[0-9A-Fa-f-]{36}$' }
        }

        function Get-FirstPropertyValue($obj, [string[]]$names) {
          if ($null -eq $obj -or $null -eq $names) { return $null }
          foreach ($name in $names) {
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            $prop = $obj.PSObject.Properties[$name]
            if ($null -eq $prop -or $null -eq $prop.Value) { continue }
            if ($prop.Value -is [string] -and [string]::IsNullOrWhiteSpace($prop.Value)) { continue }
            return $prop.Value
          }
          return $null
        }

        function Convert-ToInt64($value) {
          if ($null -eq $value) { return 0L }
          if ($value -is [long]) { return [long]$value }
          if ($value -is [int]) { return [long]$value }
          if ($value -is [double]) { return [long][Math]::Round($value) }
          if ($value -is [decimal]) { return [long][Math]::Round([double]$value) }
          $text = [string]$value
          if ([string]::IsNullOrWhiteSpace($text)) { return 0L }
          $numeric = 0L
          if ([long]::TryParse($text, [ref]$numeric)) { return $numeric }
          $compact = ($text -replace '[^0-9\.\-]', '')
          $doubleValue = 0.0
          if ([double]::TryParse($compact, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$doubleValue)) {
            return [long][Math]::Round($doubleValue)
          }
          return 0L
        }

        function Test-ScalarValue($value) {
          if ($null -eq $value) { return $false }
          return $value -is [string] -or
                 $value -is [ValueType] -or
                 $value -is [DateTime] -or
                 $value -is [DateTimeOffset] -or
                 $value -is [Guid]
        }

        function Convert-ToDisplayString($value) {
          if ($null -eq $value) { return '' }
          if ($value -is [DateTime]) { return ([DateTime]$value).ToUniversalTime().ToString('o') }
          if ($value -is [DateTimeOffset]) { return ([DateTimeOffset]$value).ToUniversalTime().ToString('o') }
          if ($value -is [bool]) { return $(if ($value) { 'True' } else { 'False' }) }
          if ($value -is [string]) { return $value.Trim() }
          if ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string])) {
            $items = @($value | ForEach-Object { Convert-ToDisplayString $_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            return ($items -join ', ')
          }
          return [string]$value
        }

        function Convert-ToUtcDisplay($value) {
          if ($null -eq $value) { return 'Unknown' }
          try {
            if ($value -is [DateTimeOffset]) {
              $utc = ([DateTimeOffset]$value).ToUniversalTime()
              if ($utc.Year -gt 2000) { return $utc.ToString('u') }
            }
            elseif ($value -is [DateTime]) {
              $utc = ([DateTime]$value).ToUniversalTime()
              if ($utc.Year -gt 2000) { return $utc.ToString('u') }
            }
            else {
              $parsed = [DateTimeOffset]::MinValue
              if ([DateTimeOffset]::TryParse([string]$value, [ref]$parsed) -and $parsed.Year -gt 2000) {
                return $parsed.ToUniversalTime().ToString('u')
              }
            }
          } catch {
          }

          $text = Convert-ToDisplayString $value
          return $(if ([string]::IsNullOrWhiteSpace($text)) { 'Unknown' } else { $text })
        }

        function Format-Uptime($timeSpan) {
          if ($null -eq $timeSpan) { return 'Unknown' }
          try {
            $span = [TimeSpan]$timeSpan
            if ($span.TotalSeconds -lt 0) { return 'Unknown' }
            $parts = New-Object System.Collections.Generic.List[string]
            if ($span.Days -gt 0) { $parts.Add($span.Days.ToString() + 'd') | Out-Null }
            if ($span.Hours -gt 0 -or $parts.Count -gt 0) { $parts.Add($span.Hours.ToString('00') + 'h') | Out-Null }
            if ($span.Minutes -gt 0 -or $parts.Count -gt 0) { $parts.Add($span.Minutes.ToString('00') + 'm') | Out-Null }
            if ($parts.Count -eq 0) { $parts.Add([Math]::Max(0, [int][Math]::Floor($span.TotalSeconds)).ToString() + 's') | Out-Null }
            return ($parts -join ' ')
          } catch {
            return 'Unknown'
          }
        }

        function Format-BoolState($value, [string]$trueText, [string]$falseText, [string]$unknownText) {
          if ($null -eq $value) { return $unknownText }
          if ($value -is [bool]) { return $(if ($value) { $trueText } else { $falseText }) }
          $text = [string]$value
          if ([string]::IsNullOrWhiteSpace($text)) { return $unknownText }
          if ($text -match '^(?i:true|yes|1|enabled|on)$') { return $trueText }
          if ($text -match '^(?i:false|no|0|disabled|off)$') { return $falseText }
          return $unknownText
        }

        function Add-DoNameValue([System.Collections.Generic.List[object]]$target, [string]$name, $value) {
          if ($null -eq $target -or [string]::IsNullOrWhiteSpace($name)) { return }
          $text = Convert-ToDisplayString $value
          if ([string]::IsNullOrWhiteSpace($text)) { return }
          $target.Add([ordered]@{
            Name = [string]$name
            Value = $text
          }) | Out-Null
        }

        function Add-DoScalarProperties([System.Collections.Generic.List[object]]$target, $obj, [string[]]$priorityNames) {
          if ($null -eq $target -or $null -eq $obj) { return }
          $seen = @{}
          foreach ($name in @($priorityNames)) {
            if ([string]::IsNullOrWhiteSpace($name) -or $seen.ContainsKey($name)) { continue }
            $prop = $obj.PSObject.Properties[$name]
            if ($null -eq $prop -or -not (Test-ScalarValue $prop.Value)) { continue }
            Add-DoNameValue $target $name $prop.Value
            $seen[$name] = $true
          }

          foreach ($prop in ($obj.PSObject.Properties | Sort-Object Name)) {
            if ($null -eq $prop -or [string]::IsNullOrWhiteSpace($prop.Name) -or $seen.ContainsKey($prop.Name)) { continue }
            if (-not (Test-ScalarValue $prop.Value)) { continue }
            Add-DoNameValue $target $prop.Name $prop.Value
            $seen[$prop.Name] = $true
          }
        }

        function Normalize-DoSource([string]$source) {
          if ([string]::IsNullOrWhiteSpace($source)) { return 'Unknown' }
          $normalized = $source.Trim().ToLowerInvariant()
          if ($normalized -match 'cache|mcc') { return 'CacheServer' }
          if ($normalized -match 'lan') { return 'PeerLan' }
          if ($normalized -match 'group') { return 'PeerGroup' }
          if ($normalized -match 'internet') { return 'PeerInternet' }
          if ($normalized -match 'peer|p2p') { return 'Peer' }
          if ($normalized -match 'http|cdn|wan') { return 'Http' }
          return $source.Trim()
        }

        $doTransfers = New-Object System.Collections.Generic.List[object]
        $doSourceTotals = @{}
        $doNotes = New-Object System.Collections.Generic.List[string]
        $doCurrentMetrics = New-Object System.Collections.Generic.List[object]
        $doMonthlyMetrics = New-Object System.Collections.Generic.List[object]
        $doConfiguration = New-Object System.Collections.Generic.List[object]
        $doPeerStatuses = New-Object System.Collections.Generic.List[object]
        $doActiveJobs = New-Object System.Collections.Generic.List[object]
        $doSupportsTimeRange = $false
        $doDataStartUtc = $null
        $doDataEndUtc = $null
        $updateRingText = 'Unknown'

        try {
          $autopatchBroker = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\WindowsAutopatch\ClientBroker' -ErrorAction SilentlyContinue
          if ($null -ne $autopatchBroker) {
            $ringCandidate = Convert-ToDisplayString (Get-FirstPropertyValue $autopatchBroker @('Ring'))
            if (-not [string]::IsNullOrWhiteSpace($ringCandidate)) {
              $updateRingText = $ringCandidate
            }
          }
        } catch {
          $doNotes.Add('Failed to read Windows Autopatch ring: ' + $_.Exception.Message) | Out-Null
        }

        function Add-DoTotal([string]$source, [long]$bytes) {
          if ($bytes -le 0) { return }
          $normalized = Normalize-DoSource $source
          if (-not $script:doSourceTotals.ContainsKey($normalized)) {
            $script:doSourceTotals[$normalized] = [long]0
          }

          if ([long]$script:doSourceTotals[$normalized] -lt [long]$bytes) {
            $script:doSourceTotals[$normalized] = [long]$bytes
          }
        }

        function Add-DoTransfer([string]$source, [long]$bytes, $timestamp, [string]$description) {
          if ($bytes -le 0) { return }
          $normalized = Normalize-DoSource $source
          if (-not $script:doSourceTotals.ContainsKey($normalized)) {
            $script:doSourceTotals[$normalized] = [long]0
          }
          $script:doSourceTotals[$normalized] = [long]$script:doSourceTotals[$normalized] + [long]$bytes

          $timeText = (Get-Date).ToUniversalTime().ToString('o')
          if ($null -ne $timestamp -and $timestamp -is [DateTime] -and $timestamp.Year -gt 2000) {
            $utc = ([DateTime]$timestamp).ToUniversalTime()
            $timeText = $utc.ToString('o')
            if ($null -eq $script:doDataStartUtc -or $utc -lt $script:doDataStartUtc) { $script:doDataStartUtc = $utc }
            if ($null -eq $script:doDataEndUtc -or $utc -gt $script:doDataEndUtc) { $script:doDataEndUtc = $utc }
            $script:doSupportsTimeRange = $true
          }

          if ([string]::IsNullOrWhiteSpace($description)) { $description = '-' }
          $script:doTransfers.Add([ordered]@{
            TimestampUtc = $timeText
            Source = $normalized
            Bytes = [long]$bytes
            Description = [string]$description
          }) | Out-Null
        }

        function Add-DoActiveJob($item) {
          if ($null -eq $item) { return }

          $content = Convert-ToDisplayString (Get-FirstPropertyValue $item @('FileName', 'ContentId', 'DownloadUrl', 'SourceUrl', 'FileId'))
          if ([string]::IsNullOrWhiteSpace($content)) { $content = '-' }

          $statusText = Convert-ToDisplayString (Get-FirstPropertyValue $item @('Status', 'DownloadState', 'State', 'JobState'))
          if ([string]::IsNullOrWhiteSpace($statusText)) { $statusText = '-' }

          $fileSizeBytes = Convert-ToInt64 (Get-FirstPropertyValue $item @('FileSize', 'TotalBytesToDownload', 'TotalBytes', 'BytesTotal'))
          $downloadedBytes = Convert-ToInt64 (Get-FirstPropertyValue $item @('BytesDownloaded', 'TotalBytesDownloaded', 'DownloadedBytes', 'BytesTransferred'))
          $bytesFromPeers = Convert-ToInt64 (Get-FirstPropertyValue $item @('BytesFromPeers', 'PeerBytes', 'BytesDownloadedFromPeers'))
          $bytesFromHttp = Convert-ToInt64 (Get-FirstPropertyValue $item @('BytesFromHttp', 'HttpBytes', 'BytesDownloadedFromHttp', 'BytesFromCDN'))
          if ($downloadedBytes -le 0) {
            $downloadedBytes = [long]$bytesFromPeers + [long]$bytesFromHttp
          }

          $downloadRateBytesPerSecond = Convert-ToInt64 (Get-FirstPropertyValue $item @('BytesPerSecond', 'DownloadRateBytesPerSecond', 'DownloadRate', 'BytesPerSec'))
          $detailParts = @()
          foreach ($detailName in 'PeerType', 'CacheHost', 'DownloadMode') {
            $detailValue = Convert-ToDisplayString (Get-FirstPropertyValue $item @($detailName))
            if (-not [string]::IsNullOrWhiteSpace($detailValue)) {
              $detailParts += ($detailName + '=' + $detailValue)
            }
          }

          $script:doActiveJobs.Add([ordered]@{
            Content = $content
            Status = $statusText
            FileSizeBytes = [long]$fileSizeBytes
            DownloadedBytes = [long]$downloadedBytes
            DownloadRateBytesPerSecond = [long]$downloadRateBytesPerSecond
            Details = if ($detailParts.Count -gt 0) { $detailParts -join '; ' } else { '-' }
          }) | Out-Null
        }

        function Resolve-DoTimestamp($obj) {
          $raw = Get-FirstPropertyValue $obj @('Timestamp', 'TimeCreated', 'StartTime', 'Date', 'ModifiedTime')
          if ($null -eq $raw) { return $null }
          if ($raw -is [DateTime]) { return [DateTime]$raw }
          $text = [string]$raw
          if ([string]::IsNullOrWhiteSpace($text)) { return $null }
          $parsed = [DateTime]::MinValue
          if ([DateTime]::TryParse($text, [ref]$parsed)) { return $parsed }
          return $null
        }

        function Add-DoBytesFromObject($obj, [DateTime]$timestamp, [string]$description) {
          if ($null -eq $obj) { return }

          $httpBytes = Convert-ToInt64 (Get-FirstPropertyValue $obj @('BytesFromHttp', 'HttpBytes', 'BytesDownloadedFromHttp', 'BytesFromCDN'))
          $cacheBytes = Convert-ToInt64 (Get-FirstPropertyValue $obj @('BytesFromCacheServer', 'BytesFromCacheHost', 'CacheHostBytes', 'CacheServerBytes'))
          $lanPeerBytes = Convert-ToInt64 (Get-FirstPropertyValue $obj @('BytesFromLanPeers', 'LanPeerBytes'))
          $groupPeerBytes = Convert-ToInt64 (Get-FirstPropertyValue $obj @('BytesFromGroupPeers', 'GroupPeerBytes'))
          $internetPeerBytes = Convert-ToInt64 (Get-FirstPropertyValue $obj @('BytesFromInternetPeers', 'InternetPeerBytes'))
          $peerBytes = Convert-ToInt64 (Get-FirstPropertyValue $obj @('BytesFromPeers', 'PeerBytes', 'BytesDownloadedFromPeers'))

          if ($httpBytes -gt 0) { Add-DoTransfer 'Http' $httpBytes $timestamp $description }
          if ($cacheBytes -gt 0) { Add-DoTransfer 'CacheServer' $cacheBytes $timestamp $description }
          if ($lanPeerBytes -gt 0) { Add-DoTransfer 'PeerLan' $lanPeerBytes $timestamp $description }
          if ($groupPeerBytes -gt 0) { Add-DoTransfer 'PeerGroup' $groupPeerBytes $timestamp $description }
          if ($internetPeerBytes -gt 0) { Add-DoTransfer 'PeerInternet' $internetPeerBytes $timestamp $description }

          if ($peerBytes -gt 0 -and $lanPeerBytes -le 0 -and $groupPeerBytes -le 0 -and $internetPeerBytes -le 0) {
            Add-DoTransfer 'Peer' $peerBytes $timestamp $description
          }
        }

        function Add-DoBytesFromMessage([string]$message, [DateTime]$timestamp, [string]$fallbackDescription) {
          if ([string]::IsNullOrWhiteSpace($message)) { return }
          $source = 'Unknown'
          if ($message -match '(?i)cache server|mcc') { $source = 'CacheServer' }
          elseif ($message -match '(?i)peer|p2p') { $source = 'Peer' }
          elseif ($message -match '(?i)http|cdn|internet') { $source = 'Http' }

          $bytes = 0L
          if ($message -match '(?i)(?<value>\d+)\s*(?<unit>bytes|byte|kb|mb|gb)') {
            $base = Convert-ToInt64 $matches['value']
            $unit = $matches['unit'].ToLowerInvariant()
            switch ($unit) {
              'gb' { $bytes = $base * 1GB }
              'mb' { $bytes = $base * 1MB }
              'kb' { $bytes = $base * 1KB }
              default { $bytes = $base }
            }
          }
          elseif ($message -match '(?i)bytes\s*[:=]\s*(?<value>\d+)') {
            $bytes = Convert-ToInt64 $matches['value']
          }

          if ($bytes -gt 0) {
            Add-DoTransfer $source $bytes $timestamp $fallbackDescription
          }
        }

        function Collect-DeliveryOptimizationData {
          $hasStatusCommand = $null -ne (Get-Command -Name 'Get-DeliveryOptimizationStatus' -ErrorAction SilentlyContinue)
          $hasPerfSnapCommand = $null -ne (Get-Command -Name 'Get-DeliveryOptimizationPerfSnap' -ErrorAction SilentlyContinue)
          $hasPerfSnapMonthCommand = $null -ne (Get-Command -Name 'Get-DeliveryOptimizationPerfSnapThisMonth' -ErrorAction SilentlyContinue)
          $hasLogCommand = $null -ne (Get-Command -Name 'Get-DeliveryOptimizationLog' -ErrorAction SilentlyContinue)
          $hasConfigCommand = $null -ne (Get-Command -Name 'Get-DOConfig' -ErrorAction SilentlyContinue)
          $hasPeerInfoStatus = $false
          if ($hasStatusCommand) {
            try {
              $statusCommand = Get-Command -Name 'Get-DeliveryOptimizationStatus' -ErrorAction SilentlyContinue
              $hasPeerInfoStatus = $null -ne $statusCommand -and $statusCommand.Parameters.ContainsKey('PeerInfo')
            } catch {
              $hasPeerInfoStatus = $false
            }
          }
          $hasOperationalLog = $false
          try {
            $hasOperationalLog = $null -ne (Get-WinEvent -ListLog 'Microsoft-Windows-DeliveryOptimization/Operational' -ErrorAction SilentlyContinue)
          } catch {
            $hasOperationalLog = $false
          }

          if (-not $hasStatusCommand -and -not $hasPerfSnapCommand -and -not $hasPerfSnapMonthCommand -and -not $hasLogCommand -and -not $hasConfigCommand -and -not $hasOperationalLog) {
            $script:doNotes.Add('Delivery Optimization commandlets, configuration, or operational log are not available on this device.') | Out-Null
            return $false
          }

          if ($hasStatusCommand) {
            try {
              $statusItems = @(Get-DeliveryOptimizationStatus -ErrorAction Stop -WarningAction SilentlyContinue)
              foreach ($item in $statusItems) {
                $description = [string](Get-FirstPropertyValue $item @('FileId', 'FileName', 'ContentId', 'SourceUrl', 'DownloadUrl'))
                $timestamp = Resolve-DoTimestamp $item
                Add-DoBytesFromObject $item $timestamp $description
                Add-DoActiveJob $item
              }
            } catch {
              $script:doNotes.Add('Get-DeliveryOptimizationStatus failed: ' + $_.Exception.Message) | Out-Null
            }
          }

          if ($hasPeerInfoStatus) {
            try {
              $peerItems = @(Get-DeliveryOptimizationStatus -PeerInfo -ErrorAction Stop -WarningAction SilentlyContinue | Select-Object -First 100)
              foreach ($item in $peerItems) {
                $content = Convert-ToDisplayString (Get-FirstPropertyValue $item @('FileId', 'FileName', 'ContentId', 'DownloadUrl', 'SourceUrl'))
                if ([string]::IsNullOrWhiteSpace($content)) { $content = '-' }
                $statusText = Convert-ToDisplayString (Get-FirstPropertyValue $item @('PeerStatus', 'Status', 'State', 'DownloadState'))
                if ([string]::IsNullOrWhiteSpace($statusText)) { $statusText = '-' }
                $candidateCount = [int](Convert-ToInt64 (Get-FirstPropertyValue $item @('PeerCount', 'NumPeers', 'TotalPeers', 'PeerCandidateCount')))
                $connectedCount = [int](Convert-ToInt64 (Get-FirstPropertyValue $item @('ConnectedPeerCount', 'ConnectedPeers', 'ConnectedPeerConnections')))
                $bytesFromPeers = Convert-ToInt64 (Get-FirstPropertyValue $item @('BytesFromPeers', 'PeerBytes', 'BytesDownloadedFromPeers'))
                $bytesFromHttp = Convert-ToInt64 (Get-FirstPropertyValue $item @('BytesFromHttp', 'HttpBytes', 'BytesDownloadedFromHttp', 'BytesFromCDN'))
                $detailParts = @()
                foreach ($detailName in 'PeerType', 'CacheHost', 'DownloadUrl', 'SourceUrl') {
                  $detailValue = Convert-ToDisplayString (Get-FirstPropertyValue $item @($detailName))
                  if (-not [string]::IsNullOrWhiteSpace($detailValue)) {
                    $detailParts += ($detailName + '=' + $detailValue)
                  }
                }

                $script:doPeerStatuses.Add([ordered]@{
                  Content = $content
                  Status = $statusText
                  CandidateCount = $candidateCount
                  ConnectedPeerCount = $connectedCount
                  BytesFromPeers = [long]$bytesFromPeers
                  BytesFromHttp = [long]$bytesFromHttp
                  Details = if ($detailParts.Count -gt 0) { $detailParts -join '; ' } else { '-' }
                }) | Out-Null
              }
            } catch {
              $script:doNotes.Add('Get-DeliveryOptimizationStatus -PeerInfo failed: ' + $_.Exception.Message) | Out-Null
            }
          }

          if ($hasLogCommand) {
            try {
              $logItems = @(Get-DeliveryOptimizationLog -ErrorAction Stop -WarningAction SilentlyContinue | Select-Object -First 400)
              foreach ($item in $logItems) {
                $description = [string](Get-FirstPropertyValue $item @('FileId', 'FileName', 'ContentId', 'SourceUrl', 'Message'))
                $timestamp = Resolve-DoTimestamp $item
                Add-DoBytesFromObject $item $timestamp $description
                if ([string]::IsNullOrWhiteSpace($description)) { $description = 'DeliveryOptimizationLog' }
                Add-DoBytesFromMessage ([string](Get-FirstPropertyValue $item @('Message', 'Description', 'Details'))) $timestamp $description
              }
            } catch {
              $script:doNotes.Add('Get-DeliveryOptimizationLog failed: ' + $_.Exception.Message) | Out-Null
            }
          }

          if ($hasOperationalLog) {
            try {
              $evtItems = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-DeliveryOptimization/Operational'; StartTime = (Get-Date).AddDays(-30) } -MaxEvents 400 -ErrorAction Stop)
              foreach ($evt in $evtItems) {
                $description = 'Event ' + [string]$evt.Id
                Add-DoBytesFromMessage ([string]$evt.Message) $evt.TimeCreated $description
              }
            } catch {
              $script:doNotes.Add('Delivery Optimization operational log query failed: ' + $_.Exception.Message) | Out-Null
            }
          }

          if ($hasPerfSnapCommand) {
            try {
              $perfItems = @(Get-DeliveryOptimizationPerfSnap -ErrorAction Stop -WarningAction SilentlyContinue)
              $perf = $perfItems | Select-Object -First 1
              if ($null -ne $perf) {
                Add-DoScalarProperties $script:doCurrentMetrics $perf @(
                  'DownloadMode', 'DODownloadMode', 'NumberOfPeers', 'PeerCount',
                  'HttpBytes', 'BytesFromHttp', 'BytesDownloadedFromHttp',
                  'CacheHostBytes', 'BytesFromCacheServer', 'BytesFromCacheHost',
                  'LanPeerBytes', 'GroupPeerBytes', 'InternetPeerBytes',
                  'PeerBytes', 'BytesFromPeers')
                $perfHttp = Convert-ToInt64 (Get-FirstPropertyValue $perf @('HttpBytes', 'BytesFromHttp', 'BytesDownloadedFromHttp'))
                $perfCache = Convert-ToInt64 (Get-FirstPropertyValue $perf @('CacheHostBytes', 'BytesFromCacheServer', 'BytesFromCacheHost'))
                $perfLan = Convert-ToInt64 (Get-FirstPropertyValue $perf @('LanPeerBytes', 'BytesFromLanPeers'))
                $perfGroup = Convert-ToInt64 (Get-FirstPropertyValue $perf @('GroupPeerBytes', 'BytesFromGroupPeers'))
                $perfInternet = Convert-ToInt64 (Get-FirstPropertyValue $perf @('InternetPeerBytes', 'BytesFromInternetPeers'))
                $perfPeer = Convert-ToInt64 (Get-FirstPropertyValue $perf @('PeerBytes', 'BytesFromPeers'))

                if ($perfHttp -gt 0) { Add-DoTotal 'Http' $perfHttp }
                if ($perfCache -gt 0) { Add-DoTotal 'CacheServer' $perfCache }
                if ($perfLan -gt 0) { Add-DoTotal 'PeerLan' $perfLan }
                if ($perfGroup -gt 0) { Add-DoTotal 'PeerGroup' $perfGroup }
                if ($perfInternet -gt 0) { Add-DoTotal 'PeerInternet' $perfInternet }
                if ($perfPeer -gt 0 -and $perfLan -le 0 -and $perfGroup -le 0 -and $perfInternet -le 0) {
                  Add-DoTotal 'Peer' $perfPeer
                }
              }
            } catch {
              $script:doNotes.Add('Get-DeliveryOptimizationPerfSnap failed: ' + $_.Exception.Message) | Out-Null
            }
          }

          if ($hasPerfSnapMonthCommand) {
            try {
              $perfMonthItems = @(Get-DeliveryOptimizationPerfSnapThisMonth -ErrorAction Stop -WarningAction SilentlyContinue)
              $perfMonth = $perfMonthItems | Select-Object -First 1
              if ($null -ne $perfMonth) {
                Add-DoScalarProperties $script:doMonthlyMetrics $perfMonth @(
                  'DownloadMode', 'DODownloadMode', 'NumberOfPeers', 'PeerCount',
                  'HttpBytes', 'BytesFromHttp', 'BytesDownloadedFromHttp',
                  'CacheHostBytes', 'BytesFromCacheServer', 'BytesFromCacheHost',
                  'LanPeerBytes', 'GroupPeerBytes', 'InternetPeerBytes',
                  'PeerBytes', 'BytesFromPeers')
              }
            } catch {
              $script:doNotes.Add('Get-DeliveryOptimizationPerfSnapThisMonth failed: ' + $_.Exception.Message) | Out-Null
            }
          }

          if ($hasConfigCommand) {
            try {
              $configItems = @(Get-DOConfig -ErrorAction Stop -WarningAction SilentlyContinue)
              $config = $configItems | Select-Object -First 1
              if ($null -ne $config) {
                Add-DoScalarProperties $script:doConfiguration $config @(
                  'DODownloadMode', 'DOGroupID', 'DOGroupId', 'DOMCCServer', 'DOCacheHost',
                  'DOMaxCacheSize', 'DOMaxCacheAge', 'DOMinDiskSizeAllowedToPeer', 'DOMinRAMAllowedToPeer',
                  'DOMinFileSizeToCache', 'DOAbsoluteMaxCacheSize', 'DOVpnKeywords', 'DOAllowVPNPeerCaching')
              }
            } catch {
              $script:doNotes.Add('Get-DOConfig failed: ' + $_.Exception.Message) | Out-Null
            }
          }

          return $true
        }

        $dsregTimer = Start-DiagTimer
        $dsreg = (cmd /c 'dsregcmd /status') | Out-String
        Add-DiagTiming 'dsregcmd status collection' $dsregTimer
        $enrollmentArtifacts = New-Object System.Collections.Generic.List[object]
        $serviceValues = New-Object System.Collections.Generic.List[object]
        $notes = New-Object System.Collections.Generic.List[string]
        $enrollmentRoot = 'HKLM:\SOFTWARE\Microsoft\Enrollments'
        $enrollmentScanTimer = Start-DiagTimer
        foreach ($key in Get-GuidLikeChildren $enrollmentRoot) {
          $props = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
          $description = @($props.ProviderID, $props.UPN, $props.DiscoveryServiceFullURL, $props.MdmServerUrl) | Where-Object { $_ } | Select-Object -First 1
          $enrollmentArtifacts.Add([ordered]@{ ArtifactType='Registry'; ArtifactPath=$key.Name; Description=([string]$description); EnrollmentId=$key.PSChildName; IsRemovable=$true })
          foreach ($name in 'ProviderID','UPN','DiscoveryServiceFullURL','MdmServerUrl','EnrollmentType','TenantID') {
            if ($null -ne $props.$name -and -not [string]::IsNullOrWhiteSpace([string]$props.$name)) {
              $serviceValues.Add([ordered]@{ Name=$name; Value=[string]$props.$name }) | Out-Null
            }
          }
        }
        foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Enrollments\Status','HKLM:\SOFTWARE\Microsoft\EnterpriseResourceManager\Tracked','HKLM:\SOFTWARE\Microsoft\Provisioning\OMADM\Accounts','HKLM:\SOFTWARE\Microsoft\Provisioning\OMADM\Logger','HKLM:\SOFTWARE\Microsoft\Provisioning\OMADM\Sessions') {
          foreach ($key in Get-GuidLikeChildren $root) {
            $enrollmentArtifacts.Add([ordered]@{ ArtifactType='Registry'; ArtifactPath=$key.Name; Description=$root; EnrollmentId=$key.PSChildName; IsRemovable=$true }) | Out-Null
          }
        }
        Add-DiagTiming 'Enrollment registry scan' $enrollmentScanTimer
        $taskScanTimer = Start-DiagTimer
        $tasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskPath -like '\Microsoft\Windows\EnterpriseMgmt\*' } | ForEach-Object { $_.TaskPath + $_.TaskName })
        Add-DiagTiming 'EnterpriseMgmt scheduled task scan' $taskScanTimer
        $lastSyncText = 'Unknown'
        $mdmLastSyncText = 'Unknown'
        $mdmLastSyncUtc = $null
        $mdmLogName = 'Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin'
        $mdmSyncEvent = $null
        $mdmSyncTimer = Start-DiagTimer
        try {
          $candidateMdmSyncEvents = @(Get-WinEvent -FilterHashtable @{ LogName = $mdmLogName; Id = 208, 209 } -MaxEvents 64 -ErrorAction Stop)
          $mdmSyncEvent = $candidateMdmSyncEvents | Where-Object {
            $_.Id -eq 209 -or ($_.Id -eq 208 -and $_.Message -match '(?i)sync|session|oma-dm')
          } | Select-Object -First 1
        } catch { $notes.Add('Failed to read MDM sync event: ' + $_.Exception.Message) | Out-Null }
        if ($null -ne $mdmSyncEvent -and $mdmSyncEvent.TimeCreated -and $mdmSyncEvent.TimeCreated.Year -gt 2000) {
          $mdmLastSyncUtc = $mdmSyncEvent.TimeCreated.ToUniversalTime()
        }
        $pushLaunchTasks = @()
        try {
          $pushLaunchTasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -eq 'PushLaunch' -and $_.TaskPath -like '\Microsoft\Windows\EnterpriseMgmt\*' })
        } catch { $notes.Add('Failed to enumerate MDM sync tasks: ' + $_.Exception.Message) | Out-Null }
        foreach ($pushLaunchTask in $pushLaunchTasks) {
          try {
            $pushLaunchInfo = Get-ScheduledTaskInfo -TaskName $pushLaunchTask.TaskName -TaskPath $pushLaunchTask.TaskPath -ErrorAction Stop
            if ($pushLaunchInfo.LastRunTime -and $pushLaunchInfo.LastRunTime.Year -gt 2000) {
              $pushLaunchSyncUtc = ([DateTime]$pushLaunchInfo.LastRunTime).ToUniversalTime()
              if ($null -eq $mdmLastSyncUtc -or $pushLaunchSyncUtc -gt $mdmLastSyncUtc) {
                $mdmLastSyncUtc = $pushLaunchSyncUtc
              }
            }
          } catch { $notes.Add('Failed to read MDM sync task info: ' + $_.Exception.Message) | Out-Null }
        }
        Add-DiagTiming 'MDM sync evidence lookup' $mdmSyncTimer
        if ($null -ne $mdmLastSyncUtc) {
          $mdmLastSyncText = $mdmLastSyncUtc.ToString('u')
          $lastSyncText = $mdmLastSyncText
        }
        $imeLastSyncText = 'Unknown'
        $imeLogPath = 'C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\IntuneManagementExtension.log'
        if (Test-Path -LiteralPath $imeLogPath) {
          try {
            $imeFile = Get-Item -LiteralPath $imeLogPath -ErrorAction Stop
            if ($imeFile.LastWriteTimeUtc -and $imeFile.LastWriteTimeUtc.Year -gt 2000) { $imeLastSyncText = $imeFile.LastWriteTimeUtc.ToString('u') }
          } catch { $notes.Add($_.Exception.Message) | Out-Null }
        }
        $systemDrive = [System.Environment]::GetEnvironmentVariable('SystemDrive')
        if ([string]::IsNullOrWhiteSpace($systemDrive)) { $systemDrive = 'C:' }
        $osRegistryTimer = Start-DiagTimer
        $osProps = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
        Add-DiagTiming 'OS version registry read' $osRegistryTimer
        $buildMajor = 0
        if ($osProps.CurrentBuild) { [void][int]::TryParse([string]$osProps.CurrentBuild, [ref]$buildMajor) }
        $windowsFamily = if ($buildMajor -ge 22000) { 'Windows 11' } elseif ($buildMajor -gt 0) { 'Windows 10' } else { 'Windows' }
        $editionText = if ([string]::IsNullOrWhiteSpace([string]$osProps.EditionID)) { '' } else { [string]$osProps.EditionID }
        $windowsVersionText = if ([string]::IsNullOrWhiteSpace($editionText)) { $windowsFamily } else { $windowsFamily + ' ' + $editionText }
        if ($windowsFamily -eq 'Windows' -and -not [string]::IsNullOrWhiteSpace([string]$osProps.ProductName)) { $windowsVersionText = [string]$osProps.ProductName }
        if ([string]::IsNullOrWhiteSpace([string]$windowsVersionText)) { $windowsVersionText = 'Unknown' }
        $ubr = if ($null -ne $osProps.UBR) { [int]$osProps.UBR } else { 0 }
        $windowsBuildText = 'Unknown'
        if ($buildMajor -gt 0) { $windowsBuildText = '10.0.' + $buildMajor + '.' + $ubr }
        $manufacturerText = 'Unknown'
        $modelText = 'Unknown'
        $serialNumberText = 'Unknown'
        $identityTimer = Start-DiagTimer
        try {
          $computerSystem = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
          if ($null -ne $computerSystem) {
            $manufacturerCandidate = Convert-ToDisplayString (Get-FirstPropertyValue $computerSystem @('Manufacturer'))
            $modelCandidate = Convert-ToDisplayString (Get-FirstPropertyValue $computerSystem @('Model'))
            if (-not [string]::IsNullOrWhiteSpace($manufacturerCandidate)) { $manufacturerText = $manufacturerCandidate }
            if (-not [string]::IsNullOrWhiteSpace($modelCandidate)) { $modelText = $modelCandidate }
          }

          $bios = Get-CimInstance Win32_BIOS -ErrorAction SilentlyContinue
          if ($null -ne $bios) {
            $serialCandidate = Convert-ToDisplayString (Get-FirstPropertyValue $bios @('SerialNumber'))
            if (-not [string]::IsNullOrWhiteSpace($serialCandidate)) { $serialNumberText = $serialCandidate }
          }
        } catch { $notes.Add('Failed to read device identity information: ' + $_.Exception.Message) | Out-Null }
        Add-DiagTiming 'Device identity CIM' $identityTimer
        $freeDiskSpaceText = 'Unknown'
        $diskTimer = Start-DiagTimer
        try {
          $disk = Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='" + $systemDrive + "'") -ErrorAction SilentlyContinue
          if ($null -ne $disk -and $disk.FreeSpace -ge 0) {
            $freeDiskSpaceText = ('{0:N1} GB free on {1}' -f ($disk.FreeSpace / 1GB), $systemDrive)
          }
        } catch { $notes.Add($_.Exception.Message) | Out-Null }
        Add-DiagTiming 'System drive free space CIM' $diskTimer
        $lastBootText = 'Unknown'
        $installDateText = 'Unknown'
        $uptimeText = 'Unknown'
        $osRuntimeTimer = Start-DiagTimer
        try {
          $osInfo = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
          if ($null -ne $osInfo) {
            if ($null -ne $osInfo.LastBootUpTime) {
              try {
                $bootTime = [Management.ManagementDateTimeConverter]::ToDateTime($osInfo.LastBootUpTime.ToString())
                $lastBootText = ([DateTimeOffset]$bootTime).ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss zzz')
              } catch {
                $lastBootText = Convert-ToUtcDisplay $osInfo.LastBootUpTime
              }
              try {
                $bootTime = [DateTime]$osInfo.LastBootUpTime
                if ($bootTime.Year -gt 2000) {
                  $uptimeText = Format-Uptime ((Get-Date) - $bootTime)
                }
              } catch {
              }
            }

            if ($null -ne $osInfo.InstallDate) {
              $installDateText = Convert-ToUtcDisplay $osInfo.InstallDate
            }
          }
        } catch { $notes.Add('Failed to read operating system runtime information: ' + $_.Exception.Message) | Out-Null }
        Add-DiagTiming 'Operating system runtime CIM' $osRuntimeTimer
        $pendingRebootReasons = New-Object System.Collections.Generic.List[string]
        $pendingRebootTimer = Start-DiagTimer
        try {
          if (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') {
            $pendingRebootReasons.Add('Component Based Servicing requested a restart.') | Out-Null
          }
          if (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') {
            $pendingRebootReasons.Add('Windows Update requested a restart.') | Out-Null
          }
          $sessionManager = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -ErrorAction SilentlyContinue
          if ($null -ne $sessionManager.PendingFileRenameOperations) {
            $pendingRebootReasons.Add('Pending file rename operations were detected.') | Out-Null
          }
        } catch { $notes.Add('Failed to read pending reboot state: ' + $_.Exception.Message) | Out-Null }
        Add-DiagTiming 'Pending reboot detection' $pendingRebootTimer
        $pendingRebootStatusText = if ($pendingRebootReasons.Count -gt 0) { 'Restart required' } else { 'No pending restart' }
        $pendingRebootDetailText = if ($pendingRebootReasons.Count -gt 0) { $pendingRebootReasons -join ' | ' } else { 'No pending reboot indicators were found.' }
        $windowsUpdateScheduledRestartStatusText = 'Unknown'
        $windowsUpdateScheduledRestartTimeText = 'Windows Update scheduled restart state is not available.'
        $mecmScheduledRestartTimeText = 'MECM scheduled restart state is not available.'
        $scheduledRestartTimer = Start-DiagTimer
        try {
          $scheduledRestartTask = Get-ScheduledTask -TaskPath '\Microsoft\Windows\UpdateOrchestrator\' -TaskName 'Reboot' -ErrorAction SilentlyContinue
          $scheduledRestartInfo = if ($null -ne $scheduledRestartTask) { Get-ScheduledTaskInfo -InputObject $scheduledRestartTask -ErrorAction SilentlyContinue } else { $null }
          $scheduledRestartTime = if ($null -ne $scheduledRestartInfo) { $scheduledRestartInfo.NextRunTime } else { $null }
          if ($scheduledRestartTime -is [DateTime] -and $scheduledRestartTime.Year -gt 2000) {
            $windowsUpdateScheduledRestartStatusText = 'Scheduled'
            $windowsUpdateScheduledRestartTimeText = ([DateTimeOffset]$scheduledRestartTime).ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss zzz')
          } else {
            $windowsUpdateScheduledRestartStatusText = 'Not scheduled'
            $windowsUpdateScheduledRestartTimeText = 'No Windows Update restart is scheduled.'
          }
        } catch {
          $notes.Add('Failed to read Windows Update scheduled restart state: ' + $_.Exception.Message) | Out-Null
        }
        try {
          $mecmRebootResult = Invoke-CimMethod -Namespace 'root\ccm\ClientSDK' -ClassName 'CCM_ClientUtilities' -MethodName 'DetermineIfRebootPending' -ErrorAction SilentlyContinue
          if ($null -ne $mecmRebootResult) {
            $mecmDeadline = $null
            foreach ($propertyName in @('RebootDeadline','Deadline','OverrideRebootWindowTime')) {
              if ($mecmRebootResult.PSObject.Properties.Name -contains $propertyName) {
                $candidate = $mecmRebootResult.$propertyName
                if ($candidate -is [DateTime] -and $candidate.Year -gt 2000) {
                  $mecmDeadline = [DateTimeOffset]$candidate
                  break
                }
                if (-not [string]::IsNullOrWhiteSpace([string]$candidate)) {
                  try {
                    $parsedCandidate = [Management.ManagementDateTimeConverter]::ToDateTime([string]$candidate)
                    if ($parsedCandidate.Year -gt 2000) {
                      $mecmDeadline = [DateTimeOffset]$parsedCandidate
                      break
                    }
                  } catch {}
                }
              }
            }
            if ($null -ne $mecmDeadline) {
              $mecmScheduledRestartTimeText = $mecmDeadline.ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss zzz')
            } else {
              $mecmScheduledRestartTimeText = 'No MECM restart deadline is scheduled.'
            }
          } else {
            $mecmScheduledRestartTimeText = 'No MECM restart deadline is scheduled.'
          }
        } catch {
          $notes.Add('Failed to read MECM scheduled restart state: ' + $_.Exception.Message) | Out-Null
        }
        Add-DiagTiming 'Windows Update scheduled restart detection' $scheduledRestartTimer
        $sessionLockStatusText = 'Unknown'
        $sessionLockedSinceText = 'Session lock state is not available.'
        $sessionLockTimer = Start-DiagTimer
        try {
          $signedInUser = $null
          $computerSystemForLock = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
          if ($null -ne $computerSystemForLock -and -not [string]::IsNullOrWhiteSpace([string]$computerSystemForLock.UserName)) {
            $signedInUser = [string]$computerSystemForLock.UserName
          }

          if ([string]::IsNullOrWhiteSpace($signedInUser)) {
            $explorerProcesses = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" -ErrorAction SilentlyContinue)
            foreach ($explorerProcess in @($explorerProcesses)) {
              try {
                $owner = Invoke-CimMethod -InputObject $explorerProcess -MethodName GetOwner -ErrorAction SilentlyContinue
                $ownerUser = [string]$owner.User
                if ([string]::IsNullOrWhiteSpace($ownerUser)) { continue }
                $ownerDomain = [string]$owner.Domain
                $signedInUser = if ([string]::IsNullOrWhiteSpace($ownerDomain)) { $ownerUser } else { $ownerDomain + '\' + $ownerUser }
                break
              } catch {
              }
            }
          }

          if ([string]::IsNullOrWhiteSpace($signedInUser)) {
            $sessionLockStatusText = 'No signed-in user'
            $sessionLockedSinceText = 'No signed-in user detected.'
          } else {
            $lockCandidates = New-Object System.Collections.Generic.List[DateTimeOffset]
            $logonUiProcesses = @(Get-CimInstance Win32_Process -Filter "Name='LogonUI.exe'" -ErrorAction SilentlyContinue)
            foreach ($logonUiProcess in @($logonUiProcesses)) {
              $creationRaw = $logonUiProcess.CreationDate
              if ($null -eq $creationRaw) { continue }
              try {
                $creationDate = [Management.ManagementDateTimeConverter]::ToDateTime($creationRaw.ToString())
                if ($creationDate.Year -gt 2000) { $lockCandidates.Add([DateTimeOffset]$creationDate) | Out-Null }
              } catch {
                try {
                  $creationDateOffset = [DateTimeOffset]$creationRaw
                  if ($creationDateOffset.Year -gt 2000) { $lockCandidates.Add($creationDateOffset) | Out-Null }
                } catch {
                }
              }
            }

            if ($lockCandidates.Count -gt 0) {
              $lockedSince = $lockCandidates | Sort-Object | Select-Object -First 1
              $sessionLockStatusText = 'Locked'
              $sessionLockedSinceText = $lockedSince.ToString('yyyy-MM-dd HH:mm:ss zzz')
            } else {
              $sessionLockStatusText = 'Unlocked'
              $sessionLockedSinceText = 'A signed-in user was detected but the session is not currently locked.'
            }
          }
        } catch {
          $notes.Add('Failed to read session lock state: ' + $_.Exception.Message) | Out-Null
        }
        Add-DiagTiming 'Session lock detection' $sessionLockTimer
        $bitLockerStatusText = 'Unknown'
        $bitLockerDetailText = 'BitLocker status is not available.'
        $bitLockerTimer = Start-DiagTimer
        try {
          if ($null -ne (Get-Command -Name 'Get-BitLockerVolume' -ErrorAction SilentlyContinue)) {
            $bitLockerVolume = @(Get-BitLockerVolume -MountPoint $systemDrive -ErrorAction Stop) | Select-Object -First 1
            if ($null -ne $bitLockerVolume) {
              $protectionStatus = Convert-ToDisplayString (Get-FirstPropertyValue $bitLockerVolume @('ProtectionStatus'))
              $volumeStatus = Convert-ToDisplayString (Get-FirstPropertyValue $bitLockerVolume @('VolumeStatus'))
              $encryptionPercent = Convert-ToDisplayString (Get-FirstPropertyValue $bitLockerVolume @('EncryptionPercentage'))
              $encryptionMethod = Convert-ToDisplayString (Get-FirstPropertyValue $bitLockerVolume @('EncryptionMethod'))
              $keyProtectorTypes = @($bitLockerVolume.KeyProtector | ForEach-Object { Convert-ToDisplayString (Get-FirstPropertyValue $_ @('KeyProtectorType')) } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
              $encryptionPercentValue = Convert-ToInt64 $encryptionPercent
              $hasProtectors = $keyProtectorTypes.Count -gt 0
              $looksEncrypted = $encryptionPercentValue -gt 0 -or $volumeStatus -match '(?i)encrypt'
              if ($protectionStatus -match '^(?i:1|on)$') {
                $bitLockerStatusText = 'Protected'
              }
              elseif ($protectionStatus -match '^(?i:0|off)$' -and ($hasProtectors -or $looksEncrypted)) {
                $bitLockerStatusText = 'Protection suspended'
              }
              elseif ($protectionStatus -match '^(?i:0|off)$') {
                $bitLockerStatusText = 'Not protected'
              }
              elseif (-not [string]::IsNullOrWhiteSpace($protectionStatus)) {
                $bitLockerStatusText = $protectionStatus
              }

              $bitLockerParts = New-Object System.Collections.Generic.List[string]
              $bitLockerParts.Add($systemDrive) | Out-Null
              if (-not [string]::IsNullOrWhiteSpace($volumeStatus)) { $bitLockerParts.Add($volumeStatus) | Out-Null }
              if (-not [string]::IsNullOrWhiteSpace($encryptionPercent)) { $bitLockerParts.Add($encryptionPercent + '% encrypted') | Out-Null }
              if (-not [string]::IsNullOrWhiteSpace($encryptionMethod)) { $bitLockerParts.Add($encryptionMethod) | Out-Null }
              if ($keyProtectorTypes.Count -gt 0) { $bitLockerParts.Add('Protectors: ' + ($keyProtectorTypes -join ', ')) | Out-Null }
              if ($bitLockerParts.Count -gt 0) {
                $bitLockerDetailText = $bitLockerParts -join ' | '
              }
            }
          }
        } catch { $notes.Add('Failed to read BitLocker state: ' + $_.Exception.Message) | Out-Null }
        Add-DiagTiming 'BitLocker probe' $bitLockerTimer
        $tpmStatusText = 'Unknown'
        $tpmVersionText = 'Unknown'
        $tpmDetailText = 'TPM status is not available.'
        $tpmTimer = Start-DiagTimer
        try {
          if ($null -ne (Get-Command -Name 'Get-Tpm' -ErrorAction SilentlyContinue)) {
            $tpm = Get-Tpm -ErrorAction Stop
            if ($null -ne $tpm) {
              if (-not $tpm.TpmPresent) {
                $tpmStatusText = 'Not present'
              }
              elseif ($tpm.TpmReady -and $tpm.TpmEnabled -and $tpm.TpmActivated) {
                $tpmStatusText = 'Ready'
              }
              elseif ($tpm.TpmPresent) {
                $tpmStatusText = 'Present with issues'
              }
              else {
                $tpmStatusText = 'Not ready'
              }

              $tpmParts = New-Object System.Collections.Generic.List[string]
              $tpmParts.Add('Present: ' + (Format-BoolState $tpm.TpmPresent 'Yes' 'No' 'Unknown')) | Out-Null
              $tpmParts.Add('Ready: ' + (Format-BoolState $tpm.TpmReady 'Yes' 'No' 'Unknown')) | Out-Null
              $tpmParts.Add('Enabled: ' + (Format-BoolState $tpm.TpmEnabled 'Yes' 'No' 'Unknown')) | Out-Null
              $tpmParts.Add('Activated: ' + (Format-BoolState $tpm.TpmActivated 'Yes' 'No' 'Unknown')) | Out-Null
              $manufacturerId = Convert-ToDisplayString (Get-FirstPropertyValue $tpm @('ManufacturerIdTxt', 'ManufacturerId'))
              $specVersion = Convert-ToDisplayString (Get-FirstPropertyValue $tpm @('SpecVersion'))
              if ([string]::IsNullOrWhiteSpace($specVersion)) {
                try {
                  $tpmCim = Get-CimInstance -Namespace 'root\cimv2\Security\MicrosoftTpm' -ClassName Win32_Tpm -ErrorAction SilentlyContinue
                  if ($null -ne $tpmCim) {
                    $specVersion = Convert-ToDisplayString (Get-FirstPropertyValue $tpmCim @('SpecVersion'))
                  }
                } catch {
                }
              }
              if (-not [string]::IsNullOrWhiteSpace($specVersion)) { $tpmVersionText = $specVersion }
              if (-not [string]::IsNullOrWhiteSpace($manufacturerId)) { $tpmParts.Add('Manufacturer: ' + $manufacturerId) | Out-Null }
              if (-not [string]::IsNullOrWhiteSpace($specVersion)) { $tpmParts.Add('Spec: ' + $specVersion) | Out-Null }
              $tpmDetailText = $tpmParts -join ' | '
            }
          }
        } catch { $notes.Add('Failed to read TPM state: ' + $_.Exception.Message) | Out-Null }
        Add-DiagTiming 'TPM probe' $tpmTimer
        $secureBootStatusText = 'Unknown'
        $secureBootTimer = Start-DiagTimer
        try {
          $secureBootEnabled = Confirm-SecureBootUEFI -ErrorAction Stop
          $secureBootStatusText = if ($secureBootEnabled) { 'Enabled' } else { 'Disabled' }
        } catch {
          if ($_.Exception.Message -match '(?i)not supported') {
            $secureBootStatusText = 'Not supported'
          }
          else {
            $notes.Add('Failed to read Secure Boot state: ' + $_.Exception.Message) | Out-Null
          }
        }
        Add-DiagTiming 'Secure Boot probe' $secureBootTimer
        $credentialGuardStatusText = 'Unknown'
        $vbsStatusText = 'Unknown'
        $memoryIntegrityStatusText = 'Unknown'
        $deviceGuardTimer = Start-DiagTimer
        try {
          $deviceGuard = @(Get-CimInstance -Namespace 'root\Microsoft\Windows\DeviceGuard' -ClassName 'Win32_DeviceGuard' -ErrorAction Stop) | Select-Object -First 1
          if ($null -ne $deviceGuard) {
            $configuredServices = @($deviceGuard.SecurityServicesConfigured | ForEach-Object { [int]$_ })
            $runningServices = @($deviceGuard.SecurityServicesRunning | ForEach-Object { [int]$_ })
            $credentialGuardStatusText = if ($runningServices -contains 1) { 'Running' } elseif ($configuredServices -contains 1) { 'Configured' } else { 'Not enabled' }
            $memoryIntegrityStatusText = if ($runningServices -contains 2) { 'Running' } elseif ($configuredServices -contains 2) { 'Configured' } else { 'Not enabled' }
            switch ([int]$deviceGuard.VirtualizationBasedSecurityStatus) {
              2 { $vbsStatusText = 'Running' }
              1 { $vbsStatusText = 'Enabled' }
              0 { $vbsStatusText = 'Not enabled' }
              default { $vbsStatusText = Convert-ToDisplayString $deviceGuard.VirtualizationBasedSecurityStatus }
            }
          }
        } catch { $notes.Add('Failed to read Device Guard state: ' + $_.Exception.Message) | Out-Null }
        Add-DiagTiming 'Device Guard probe' $deviceGuardTimer
        $primaryConnectionText = 'Unknown'
        $primaryAdapterText = 'Unknown'
        $wiFiSsidText = 'Not connected'
        $vpnStatusText = 'Not detected'
        $vpnProviderText = '-'
        $isCheckpointVpnDetected = $false
        $checkpointAdapterDescription = '{{VPN_ADAPTER_MATCH}}'
        $allAdapters = @()
        $activeAdapters = @()
        $activePhysicalAdapters = @()
        $networkTimer = Start-DiagTimer
        try {
          $allAdapters = @(Get-NetAdapter -ErrorAction Stop)
          $activeAdapters = @($allAdapters | Where-Object { $_.Status -eq 'Up' })
          $activePhysicalAdapters = @($activeAdapters | Where-Object {
            $fingerprint = @(
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('Name')),
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceAlias')),
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceDescription')),
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('NdisPhysicalMedium'))
            ) -join ' '
            -not [string]::IsNullOrWhiteSpace($fingerprint) -and
            $fingerprint -notmatch '(?i)virtual|vpn|loopback|bluetooth|hyper-v|vmware|vethernet|tunnel|wan miniport|ras'
          })
        } catch { $notes.Add('Failed to read network adapters: ' + $_.Exception.Message) | Out-Null }
        $primaryAdapter = @($activePhysicalAdapters | Where-Object {
          $fingerprint = @(
            Convert-ToDisplayString (Get-FirstPropertyValue $_ @('Name')),
            Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceAlias')),
            Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceDescription')),
            Convert-ToDisplayString (Get-FirstPropertyValue $_ @('NdisPhysicalMedium'))
          ) -join ' '
          $fingerprint -match '(?i)wi-?fi|wlan|wireless|802\.11'
        } | Select-Object -First 1)
        if ($primaryAdapter.Count -gt 0) {
          $primaryConnectionText = 'Wi-Fi'
          $wiFiSsidText = 'Connected'
        }
        if (($null -eq $primaryAdapter -or $primaryAdapter.Count -eq 0) -and $activePhysicalAdapters.Count -gt 0) {
          $primaryAdapter = @($activePhysicalAdapters | Where-Object {
            $fingerprint = @(
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('Name')),
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceAlias')),
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceDescription')),
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('NdisPhysicalMedium'))
            ) -join ' '
            $fingerprint -match '(?i)ethernet|lan|gigabit'
          } | Select-Object -First 1)
          if ($primaryAdapter.Count -gt 0) {
            $primaryConnectionText = 'LAN'
          }
        }
        if (($null -eq $primaryAdapter -or $primaryAdapter.Count -eq 0) -and $activePhysicalAdapters.Count -gt 0) {
          $primaryAdapter = @($activePhysicalAdapters | Select-Object -First 1)
          if ($primaryAdapter.Count -gt 0) {
            $primaryConnectionText = 'Connected'
          }
        }
        if ($primaryAdapter.Count -gt 0) {
          $adapter = $primaryAdapter[0]
          $primaryAdapterText = Convert-ToDisplayString (Get-FirstPropertyValue $adapter @('InterfaceDescription', 'Name', 'InterfaceAlias'))
        }
        foreach ($adapter in $allAdapters) {
          $adapterDescription = Convert-ToDisplayString (Get-FirstPropertyValue $adapter @('InterfaceDescription', 'Name', 'InterfaceAlias'))
          if ([string]::IsNullOrWhiteSpace($adapterDescription)) { continue }
          if (-not [string]::IsNullOrWhiteSpace($checkpointAdapterDescription) -and $adapterDescription -like ('*' + $checkpointAdapterDescription + '*')) {
            $isCheckpointVpnDetected = $true
            if ($adapter.Status -eq 'Up') {
              $vpnStatusText = 'Connected'
            }
          }
        }
        if ($isCheckpointVpnDetected -and $vpnStatusText -eq 'Not detected') {
          $vpnStatusText = 'Adapter detected'
        }
        if ($isCheckpointVpnDetected -and -not [string]::IsNullOrWhiteSpace('{{VPN_PROVIDER_NAME}}')) {
          $vpnProviderText = '{{VPN_PROVIDER_NAME}}'
        }
        Add-DiagTiming 'Network and VPN probe' $networkTimer
        $certificateTimer = Start-DiagTimer
        $certificates = @(Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.Issuer -match 'Intune|MS-Organization-Access|Microsoft Workplace Join' -or $_.Subject -match 'MS-Organization-Access' } | ForEach-Object { $_.Subject + ' | ' + $_.Issuer + ' | ' + $_.Thumbprint })
        Add-DiagTiming 'Certificate scan' $certificateTimer
        $highlights = New-Object System.Collections.Generic.List[string]
        foreach ($line in ($dsreg -split [Environment]::NewLine)) {
          if ($line -match 'AzureAdJoined|DomainJoined|DeviceId|TenantId|MdmUrl|WorkplaceJoined') { $highlights.Add($line.Trim()) | Out-Null }
        }
        $registrationSummary = ($highlights | Select-Object -First 5) -join '; '
        if ([string]::IsNullOrWhiteSpace($registrationSummary)) { $registrationSummary = 'No dsreg summary detected.' }
        $adJoinPathText = 'Unknown'
        try {
          if ($dsreg -match '(?im)^\s*DomainJoined\s*[:=]\s*YES\s*$') {
            $computerName = [string]$env:COMPUTERNAME
            $rootDse = [ADSI]'LDAP://RootDse'
            $defaultNamingContext = [string]$rootDse.defaultNamingContext
            if (-not [string]::IsNullOrWhiteSpace($defaultNamingContext)) {
              $searchRoot = [ADSI]('LDAP://' + $defaultNamingContext)
              $searcher = New-Object System.DirectoryServices.DirectorySearcher($searchRoot)
              $searcher.Filter = '(&(objectCategory=computer)(sAMAccountName=' + $computerName + '$))'
              $searcher.SearchScope = [System.DirectoryServices.SearchScope]::Subtree
              $searcher.PageSize = 1
              $searchResult = $searcher.FindOne()
              if ($null -ne $searchResult) {
                $distinguishedName = [string]$searchResult.Properties['distinguishedname'][0]
                if (-not [string]::IsNullOrWhiteSpace($distinguishedName)) {
                  $commaIndex = $distinguishedName.IndexOf(',')
                  $adJoinPathText = if ($commaIndex -gt 0 -and $commaIndex -lt ($distinguishedName.Length - 1)) { $distinguishedName.Substring($commaIndex + 1) } else { $distinguishedName }
                }
              }
            }
          }
        } catch { $notes.Add('Failed to read AD computer location: ' + $_.Exception.Message) | Out-Null }

        $deliveryOptimization = $null
        $platformSecurity = [ordered]@{
          BitLockerStatusText = $bitLockerStatusText
          BitLockerDetailText = $bitLockerDetailText
          TpmStatusText = $tpmStatusText
          TpmVersionText = $tpmVersionText
          TpmDetailText = $tpmDetailText
          SecureBootStatusText = $secureBootStatusText
          CredentialGuardStatusText = $credentialGuardStatusText
          VbsStatusText = $vbsStatusText
          MemoryIntegrityStatusText = $memoryIntegrityStatusText
        }
        $systemRuntime = [ordered]@{
          UptimeText = $uptimeText
          LastBootText = $lastBootText
          InstallDateText = $installDateText
          PendingRebootStatusText = $pendingRebootStatusText
          PendingRebootDetailText = $pendingRebootDetailText
          WindowsUpdateScheduledRestartStatusText = $windowsUpdateScheduledRestartStatusText
          WindowsUpdateScheduledRestartTimeText = $windowsUpdateScheduledRestartTimeText
          MecmScheduledRestartTimeText = $mecmScheduledRestartTimeText
          SessionLockStatusText = $sessionLockStatusText
          SessionLockedSinceText = $sessionLockedSinceText
        }
        $networkConnectivity = [ordered]@{
          PrimaryConnectionText = $primaryConnectionText
          PrimaryAdapterText = $primaryAdapterText
          WiFiSsidText = $wiFiSsidText
          VpnStatusText = $vpnStatusText
          VpnProviderText = $vpnProviderText
          IsCheckpointVpnDetected = $isCheckpointVpnDetected
        }

        $script:diagTimings.Add('Local diagnostics script total completed in ' + [int][Math]::Round(([DateTime]::UtcNow - $script:diagScriptStartedUtc).TotalMilliseconds) + ' ms.') | Out-Null
        $result = [ordered]@{
          MachineName=$env:COMPUTERNAME
          CapturedAtUtc=(Get-Date).ToUniversalTime().ToString('o')
          LastSyncText=$lastSyncText
          MdmLastSyncText=$mdmLastSyncText
          ImeLastSyncText=$imeLastSyncText
          WindowsVersionText=[string]$windowsVersionText
          WindowsBuildText=[string]$windowsBuildText
          FreeDiskSpaceText=[string]$freeDiskSpaceText
          ManufacturerText=[string]$manufacturerText
          ModelText=[string]$modelText
          SerialNumberText=[string]$serialNumberText
          AdJoinPathText=[string]$adJoinPathText
          UpdateRingText=[string]$updateRingText
          RegistrationSummary=$registrationSummary
          DsregStatusText=$dsreg.TrimEnd()
          DsregHighlights=$highlights
          EnrollmentArtifacts=$enrollmentArtifacts
          EnterpriseMgmtTasks=$tasks
          CertificateSummaries=$certificates
          ServiceValues=$serviceValues
          Notes=$notes
          DiagnosticsTimings=$script:diagTimings
          DeliveryOptimization=$deliveryOptimization
          PlatformSecurity=$platformSecurity
          SystemRuntime=$systemRuntime
          NetworkConnectivity=$networkConnectivity
        }
        $result | ConvertTo-Json -Depth 10 -Compress
        """
            .Replace("{{VPN_ADAPTER_MATCH}}", escapedVpnAdapterDescriptionMatch, StringComparison.Ordinal)
            .Replace("{{VPN_PROVIDER_NAME}}", escapedVpnProviderName, StringComparison.Ordinal);
    }

    private static string BuildOverviewCoreSnapshotScript() =>
        """
        function Get-GuidLikeChildren($path) {
          if (-not (Test-Path -LiteralPath $path)) { return @() }
          Get-ChildItem -LiteralPath $path -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^[0-9A-Fa-f-]{36}$' }
        }

        function Get-FirstPropertyValue($obj, [string[]]$names) {
          if ($null -eq $obj -or $null -eq $names) { return $null }
          foreach ($name in $names) {
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            $prop = $obj.PSObject.Properties[$name]
            if ($null -eq $prop -or $null -eq $prop.Value) { continue }
            if ($prop.Value -is [string] -and [string]::IsNullOrWhiteSpace($prop.Value)) { continue }
            return $prop.Value
          }
          return $null
        }

        function Convert-ToDisplayString($value) {
          if ($null -eq $value) { return '' }
          if ($value -is [DateTime]) { return ([DateTime]$value).ToUniversalTime().ToString('o') }
          if ($value -is [DateTimeOffset]) { return ([DateTimeOffset]$value).ToUniversalTime().ToString('o') }
          if ($value -is [string]) { return $value.Trim() }
          if ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string])) {
            $items = @($value | ForEach-Object { Convert-ToDisplayString $_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            return ($items -join ', ')
          }
          return [string]$value
        }

        $dsreg = (cmd /c 'dsregcmd /status') | Out-String
        $enrollmentArtifacts = New-Object System.Collections.Generic.List[object]
        $serviceValues = New-Object System.Collections.Generic.List[object]
        $notes = New-Object System.Collections.Generic.List[string]
        $updateRingText = 'Unknown'
        $enrollmentRoot = 'HKLM:\SOFTWARE\Microsoft\Enrollments'
        foreach ($key in Get-GuidLikeChildren $enrollmentRoot) {
          $props = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
          $description = @($props.ProviderID, $props.UPN, $props.DiscoveryServiceFullURL, $props.MdmServerUrl) | Where-Object { $_ } | Select-Object -First 1
          $enrollmentArtifacts.Add([ordered]@{ ArtifactType='Registry'; ArtifactPath=$key.Name; Description=([string]$description); EnrollmentId=$key.PSChildName; IsRemovable=$true }) | Out-Null
          foreach ($name in 'ProviderID','UPN','DiscoveryServiceFullURL','MdmServerUrl','EnrollmentType','TenantID') {
            if ($null -ne $props.$name -and -not [string]::IsNullOrWhiteSpace([string]$props.$name)) {
              $serviceValues.Add([ordered]@{ Name=$name; Value=[string]$props.$name }) | Out-Null
            }
          }
        }

        try {
          $autopatchBroker = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\WindowsAutopatch\ClientBroker' -ErrorAction SilentlyContinue
          if ($null -ne $autopatchBroker) {
            $ringCandidate = Convert-ToDisplayString (Get-FirstPropertyValue $autopatchBroker @('Ring'))
            if (-not [string]::IsNullOrWhiteSpace($ringCandidate)) {
              $updateRingText = $ringCandidate
            }
          }
        } catch { $notes.Add('Failed to read Windows Autopatch ring: ' + $_.Exception.Message) | Out-Null }

        $lastSyncText = 'Unknown'
        $mdmLastSyncText = 'Unknown'
        $mdmLastSyncUtc = $null
        $mdmLogName = 'Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin'
        try {
          $candidateMdmSyncEvents = @(Get-WinEvent -FilterHashtable @{ LogName = $mdmLogName; Id = 208, 209 } -MaxEvents 64 -ErrorAction Stop)
          $mdmSyncEvent = $candidateMdmSyncEvents | Where-Object {
            $_.Id -eq 209 -or ($_.Id -eq 208 -and $_.Message -match '(?i)sync|session|oma-dm')
          } | Select-Object -First 1
          if ($null -ne $mdmSyncEvent -and $mdmSyncEvent.TimeCreated -and $mdmSyncEvent.TimeCreated.Year -gt 2000) {
            $mdmLastSyncUtc = $mdmSyncEvent.TimeCreated.ToUniversalTime()
          }
        } catch { $notes.Add('Failed to read MDM sync event: ' + $_.Exception.Message) | Out-Null }
        if ($null -ne $mdmLastSyncUtc) {
          $mdmLastSyncText = $mdmLastSyncUtc.ToString('u')
          $lastSyncText = $mdmLastSyncText
        }

        $imeLastSyncText = 'Unknown'
        $imeLogPath = 'C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\IntuneManagementExtension.log'
        if (Test-Path -LiteralPath $imeLogPath) {
          try {
            $imeFile = Get-Item -LiteralPath $imeLogPath -ErrorAction Stop
            if ($imeFile.LastWriteTimeUtc -and $imeFile.LastWriteTimeUtc.Year -gt 2000) { $imeLastSyncText = $imeFile.LastWriteTimeUtc.ToString('u') }
          } catch { $notes.Add($_.Exception.Message) | Out-Null }
        }

        $systemDrive = [System.Environment]::GetEnvironmentVariable('SystemDrive')
        if ([string]::IsNullOrWhiteSpace($systemDrive)) { $systemDrive = 'C:' }
        $osProps = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
        $buildMajor = 0
        if ($osProps.CurrentBuild) { [void][int]::TryParse([string]$osProps.CurrentBuild, [ref]$buildMajor) }
        $windowsFamily = if ($buildMajor -ge 22000) { 'Windows 11' } elseif ($buildMajor -gt 0) { 'Windows 10' } else { 'Windows' }
        $editionText = if ([string]::IsNullOrWhiteSpace([string]$osProps.EditionID)) { '' } else { [string]$osProps.EditionID }
        $windowsVersionText = if ([string]::IsNullOrWhiteSpace($editionText)) { $windowsFamily } else { $windowsFamily + ' ' + $editionText }
        if ($windowsFamily -eq 'Windows' -and -not [string]::IsNullOrWhiteSpace([string]$osProps.ProductName)) { $windowsVersionText = [string]$osProps.ProductName }
        if ([string]::IsNullOrWhiteSpace([string]$windowsVersionText)) { $windowsVersionText = 'Unknown' }
        $ubr = if ($null -ne $osProps.UBR) { [int]$osProps.UBR } else { 0 }
        $windowsBuildText = 'Unknown'
        if ($buildMajor -gt 0) { $windowsBuildText = '10.0.' + $buildMajor + '.' + $ubr }

        $manufacturerText = 'Unknown'
        $modelText = 'Unknown'
        $serialNumberText = 'Unknown'
        try {
          $computerSystem = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
          if ($null -ne $computerSystem) {
            $manufacturerCandidate = Convert-ToDisplayString (Get-FirstPropertyValue $computerSystem @('Manufacturer'))
            $modelCandidate = Convert-ToDisplayString (Get-FirstPropertyValue $computerSystem @('Model'))
            if (-not [string]::IsNullOrWhiteSpace($manufacturerCandidate)) { $manufacturerText = $manufacturerCandidate }
            if (-not [string]::IsNullOrWhiteSpace($modelCandidate)) { $modelText = $modelCandidate }
          }

          $bios = Get-CimInstance Win32_BIOS -ErrorAction SilentlyContinue
          if ($null -ne $bios) {
            $serialCandidate = Convert-ToDisplayString (Get-FirstPropertyValue $bios @('SerialNumber'))
            if (-not [string]::IsNullOrWhiteSpace($serialCandidate)) { $serialNumberText = $serialCandidate }
          }
        } catch { $notes.Add('Failed to read device identity information: ' + $_.Exception.Message) | Out-Null }

        $freeDiskSpaceText = 'Unknown'
        try {
          $disk = Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='" + $systemDrive + "'") -ErrorAction SilentlyContinue
          if ($null -ne $disk -and $disk.FreeSpace -ge 0) {
            $freeDiskSpaceText = ('{0:N1} GB free on {1}' -f ($disk.FreeSpace / 1GB), $systemDrive)
          }
        } catch { $notes.Add($_.Exception.Message) | Out-Null }

        $highlights = New-Object System.Collections.Generic.List[string]
        foreach ($line in ($dsreg -split [Environment]::NewLine)) {
          if ($line -match 'AzureAdJoined|DomainJoined|DeviceId|TenantId|MdmUrl|WorkplaceJoined') { $highlights.Add($line.Trim()) | Out-Null }
        }
        $registrationSummary = ($highlights | Select-Object -First 5) -join '; '
        if ([string]::IsNullOrWhiteSpace($registrationSummary)) { $registrationSummary = 'No dsreg summary detected.' }

        $result = [ordered]@{
          MachineName=$env:COMPUTERNAME
          CapturedAtUtc=(Get-Date).ToUniversalTime().ToString('o')
          LastSyncText=$lastSyncText
          MdmLastSyncText=$mdmLastSyncText
          ImeLastSyncText=$imeLastSyncText
          WindowsVersionText=[string]$windowsVersionText
          WindowsBuildText=[string]$windowsBuildText
          FreeDiskSpaceText=[string]$freeDiskSpaceText
          ManufacturerText=[string]$manufacturerText
          ModelText=[string]$modelText
          SerialNumberText=[string]$serialNumberText
          UpdateRingText=[string]$updateRingText
          RegistrationSummary=$registrationSummary
          DsregStatusText=$dsreg.TrimEnd()
          DsregHighlights=$highlights
          EnrollmentArtifacts=$enrollmentArtifacts
          EnterpriseMgmtTasks=@()
          CertificateSummaries=@()
          ServiceValues=$serviceValues
          Notes=$notes
        }
        $result | ConvertTo-Json -Depth 10 -Compress
        """;

    private static string BuildPlatformSecuritySnapshotScript() =>
        """
        function Get-FirstPropertyValue($obj, [string[]]$names) {
          if ($null -eq $obj -or $null -eq $names) { return $null }
          foreach ($name in $names) {
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            $prop = $obj.PSObject.Properties[$name]
            if ($null -eq $prop -or $null -eq $prop.Value) { continue }
            if ($prop.Value -is [string] -and [string]::IsNullOrWhiteSpace($prop.Value)) { continue }
            return $prop.Value
          }
          return $null
        }
        function Convert-ToInt64($value) {
          if ($null -eq $value) { return 0L }
          if ($value -is [long]) { return [long]$value }
          if ($value -is [int]) { return [long]$value }
          if ($value -is [double]) { return [long][Math]::Round($value) }
          $text = [string]$value
          if ([string]::IsNullOrWhiteSpace($text)) { return 0L }
          $numeric = 0L
          if ([long]::TryParse($text, [ref]$numeric)) { return $numeric }
          return 0L
        }
        function Convert-ToDisplayString($value) {
          if ($null -eq $value) { return '' }
          if ($value -is [DateTime]) { return ([DateTime]$value).ToUniversalTime().ToString('o') }
          if ($value -is [DateTimeOffset]) { return ([DateTimeOffset]$value).ToUniversalTime().ToString('o') }
          if ($value -is [string]) { return $value.Trim() }
          if ($value -is [bool]) { return $(if ($value) { 'True' } else { 'False' }) }
          return [string]$value
        }
        function Format-BoolState($value, [string]$trueText, [string]$falseText, [string]$unknownText) {
          if ($null -eq $value) { return $unknownText }
          if ($value -is [bool]) { return $(if ($value) { $trueText } else { $falseText }) }
          $text = [string]$value
          if ([string]::IsNullOrWhiteSpace($text)) { return $unknownText }
          if ($text -match '^(?i:true|yes|1|enabled|on)$') { return $trueText }
          if ($text -match '^(?i:false|no|0|disabled|off)$') { return $falseText }
          return $unknownText
        }

        $systemDrive = [System.Environment]::GetEnvironmentVariable('SystemDrive')
        if ([string]::IsNullOrWhiteSpace($systemDrive)) { $systemDrive = 'C:' }
        $bitLockerStatusText = 'Unknown'
        $bitLockerDetailText = 'BitLocker status is not available.'
        try {
          if ($null -ne (Get-Command -Name 'Get-BitLockerVolume' -ErrorAction SilentlyContinue)) {
            $bitLockerVolume = @(Get-BitLockerVolume -MountPoint $systemDrive -ErrorAction Stop) | Select-Object -First 1
            if ($null -ne $bitLockerVolume) {
              $protectionStatus = Convert-ToDisplayString (Get-FirstPropertyValue $bitLockerVolume @('ProtectionStatus'))
              $volumeStatus = Convert-ToDisplayString (Get-FirstPropertyValue $bitLockerVolume @('VolumeStatus'))
              $encryptionPercent = Convert-ToDisplayString (Get-FirstPropertyValue $bitLockerVolume @('EncryptionPercentage'))
              $encryptionMethod = Convert-ToDisplayString (Get-FirstPropertyValue $bitLockerVolume @('EncryptionMethod'))
              $keyProtectorTypes = @($bitLockerVolume.KeyProtector | ForEach-Object { Convert-ToDisplayString (Get-FirstPropertyValue $_ @('KeyProtectorType')) } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
              $encryptionPercentValue = Convert-ToInt64 $encryptionPercent
              $hasProtectors = $keyProtectorTypes.Count -gt 0
              $looksEncrypted = $encryptionPercentValue -gt 0 -or $volumeStatus -match '(?i)encrypt'
              if ($protectionStatus -match '^(?i:1|on)$') {
                $bitLockerStatusText = 'Protected'
              }
              elseif ($protectionStatus -match '^(?i:0|off)$' -and ($hasProtectors -or $looksEncrypted)) {
                $bitLockerStatusText = 'Protection suspended'
              }
              elseif ($protectionStatus -match '^(?i:0|off)$') {
                $bitLockerStatusText = 'Not protected'
              }
              elseif (-not [string]::IsNullOrWhiteSpace($protectionStatus)) {
                $bitLockerStatusText = $protectionStatus
              }

              $bitLockerParts = New-Object System.Collections.Generic.List[string]
              $bitLockerParts.Add($systemDrive) | Out-Null
              if (-not [string]::IsNullOrWhiteSpace($volumeStatus)) { $bitLockerParts.Add($volumeStatus) | Out-Null }
              if (-not [string]::IsNullOrWhiteSpace($encryptionPercent)) { $bitLockerParts.Add($encryptionPercent + '% encrypted') | Out-Null }
              if (-not [string]::IsNullOrWhiteSpace($encryptionMethod)) { $bitLockerParts.Add($encryptionMethod) | Out-Null }
              if ($keyProtectorTypes.Count -gt 0) { $bitLockerParts.Add('Protectors: ' + ($keyProtectorTypes -join ', ')) | Out-Null }
              if ($bitLockerParts.Count -gt 0) { $bitLockerDetailText = $bitLockerParts -join ' | ' }
            }
          }
        } catch {}

        $tpmStatusText = 'Unknown'
        $tpmVersionText = 'Unknown'
        $tpmDetailText = 'TPM status is not available.'
        try {
          if ($null -ne (Get-Command -Name 'Get-Tpm' -ErrorAction SilentlyContinue)) {
            $tpm = Get-Tpm -ErrorAction Stop
            if ($null -ne $tpm) {
              if (-not $tpm.TpmPresent) { $tpmStatusText = 'Not present' }
              elseif ($tpm.TpmReady -and $tpm.TpmEnabled -and $tpm.TpmActivated) { $tpmStatusText = 'Ready' }
              elseif ($tpm.TpmPresent) { $tpmStatusText = 'Present with issues' }
              else { $tpmStatusText = 'Not ready' }
              $tpmParts = New-Object System.Collections.Generic.List[string]
              $tpmParts.Add('Present: ' + (Format-BoolState $tpm.TpmPresent 'Yes' 'No' 'Unknown')) | Out-Null
              $tpmParts.Add('Ready: ' + (Format-BoolState $tpm.TpmReady 'Yes' 'No' 'Unknown')) | Out-Null
              $tpmParts.Add('Enabled: ' + (Format-BoolState $tpm.TpmEnabled 'Yes' 'No' 'Unknown')) | Out-Null
              $tpmParts.Add('Activated: ' + (Format-BoolState $tpm.TpmActivated 'Yes' 'No' 'Unknown')) | Out-Null
              $manufacturerId = Convert-ToDisplayString (Get-FirstPropertyValue $tpm @('ManufacturerIdTxt', 'ManufacturerId'))
              $specVersion = Convert-ToDisplayString (Get-FirstPropertyValue $tpm @('SpecVersion'))
              if ([string]::IsNullOrWhiteSpace($specVersion)) {
                try {
                  $tpmCim = Get-CimInstance -Namespace 'root\cimv2\Security\MicrosoftTpm' -ClassName Win32_Tpm -ErrorAction SilentlyContinue
                  if ($null -ne $tpmCim) {
                    $specVersion = Convert-ToDisplayString (Get-FirstPropertyValue $tpmCim @('SpecVersion'))
                  }
                } catch {}
              }
              $tpmVersionText = if (-not [string]::IsNullOrWhiteSpace($specVersion)) { $specVersion } else { 'Unknown' }
              if (-not [string]::IsNullOrWhiteSpace($manufacturerId)) { $tpmParts.Add('Manufacturer: ' + $manufacturerId) | Out-Null }
              if (-not [string]::IsNullOrWhiteSpace($specVersion)) { $tpmParts.Add('Spec: ' + $specVersion) | Out-Null }
              $tpmDetailText = $tpmParts -join ' | '
            }
          }
        } catch {}

        $secureBootStatusText = 'Unknown'
        try {
          $secureBootEnabled = Confirm-SecureBootUEFI -ErrorAction Stop
          $secureBootStatusText = if ($secureBootEnabled) { 'Enabled' } else { 'Disabled' }
        } catch {
          if ($_.Exception.Message -match '(?i)not supported') { $secureBootStatusText = 'Not supported' }
        }

        $credentialGuardStatusText = 'Unknown'
        $vbsStatusText = 'Unknown'
        $memoryIntegrityStatusText = 'Unknown'
        try {
          $deviceGuard = @(Get-CimInstance -Namespace 'root\Microsoft\Windows\DeviceGuard' -ClassName 'Win32_DeviceGuard' -ErrorAction Stop) | Select-Object -First 1
          if ($null -ne $deviceGuard) {
            $configuredServices = @($deviceGuard.SecurityServicesConfigured | ForEach-Object { [int]$_ })
            $runningServices = @($deviceGuard.SecurityServicesRunning | ForEach-Object { [int]$_ })
            $credentialGuardStatusText = if ($runningServices -contains 1) { 'Running' } elseif ($configuredServices -contains 1) { 'Configured' } else { 'Not enabled' }
            $memoryIntegrityStatusText = if ($runningServices -contains 2) { 'Running' } elseif ($configuredServices -contains 2) { 'Configured' } else { 'Not enabled' }
            switch ([int]$deviceGuard.VirtualizationBasedSecurityStatus) {
              2 { $vbsStatusText = 'Running' }
              1 { $vbsStatusText = 'Enabled' }
              0 { $vbsStatusText = 'Not enabled' }
              default { $vbsStatusText = Convert-ToDisplayString $deviceGuard.VirtualizationBasedSecurityStatus }
            }
          }
        } catch {}

        [ordered]@{
          PlatformSecurity = [ordered]@{
            BitLockerStatusText = $bitLockerStatusText
            BitLockerDetailText = $bitLockerDetailText
            TpmStatusText = $tpmStatusText
            TpmVersionText = $tpmVersionText
            TpmDetailText = $tpmDetailText
            SecureBootStatusText = $secureBootStatusText
            CredentialGuardStatusText = $credentialGuardStatusText
            VbsStatusText = $vbsStatusText
            MemoryIntegrityStatusText = $memoryIntegrityStatusText
          }
        } | ConvertTo-Json -Depth 6 -Compress
        """;

    private static string BuildSystemRuntimeSnapshotScript() =>
        """
        function Convert-ToUtcDisplay($value) {
          if ($null -eq $value) { return 'Unknown' }
          try {
            if ($value -is [DateTimeOffset]) {
              $utc = ([DateTimeOffset]$value).ToUniversalTime()
              if ($utc.Year -gt 2000) { return $utc.ToString('u') }
            }
            elseif ($value -is [DateTime]) {
              $utc = ([DateTime]$value).ToUniversalTime()
              if ($utc.Year -gt 2000) { return $utc.ToString('u') }
            }
          } catch {}
          return 'Unknown'
        }
        function Format-Uptime($timeSpan) {
          if ($null -eq $timeSpan) { return 'Unknown' }
          try {
            $span = [TimeSpan]$timeSpan
            if ($span.TotalSeconds -lt 0) { return 'Unknown' }
            $parts = New-Object System.Collections.Generic.List[string]
            if ($span.Days -gt 0) { $parts.Add($span.Days.ToString() + 'd') | Out-Null }
            if ($span.Hours -gt 0 -or $parts.Count -gt 0) { $parts.Add($span.Hours.ToString('00') + 'h') | Out-Null }
            if ($span.Minutes -gt 0 -or $parts.Count -gt 0) { $parts.Add($span.Minutes.ToString('00') + 'm') | Out-Null }
            if ($parts.Count -eq 0) { $parts.Add([Math]::Max(0, [int][Math]::Floor($span.TotalSeconds)).ToString() + 's') | Out-Null }
            return ($parts -join ' ')
          } catch { return 'Unknown' }
        }

        $lastBootText = 'Unknown'
        $installDateText = 'Unknown'
        $uptimeText = 'Unknown'
        try {
          $osInfo = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
          if ($null -ne $osInfo) {
            if ($osInfo.LastBootUpTime) {
              try {
                $bootTime = [Management.ManagementDateTimeConverter]::ToDateTime($osInfo.LastBootUpTime.ToString())
                $lastBootText = ([DateTimeOffset]$bootTime).ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss zzz')
              } catch {
                $lastBootText = Convert-ToUtcDisplay $osInfo.LastBootUpTime
              }
              try { $uptimeText = Format-Uptime ((Get-Date) - ([Management.ManagementDateTimeConverter]::ToDateTime($osInfo.LastBootUpTime.ToString()))) } catch {}
            }
            if ($osInfo.InstallDate) { $installDateText = Convert-ToUtcDisplay $osInfo.InstallDate }
          }
        } catch {}

        $pendingRebootReasons = New-Object System.Collections.Generic.List[string]
        if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') { $pendingRebootReasons.Add('Component Based Servicing requires a restart.') | Out-Null }
        if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') { $pendingRebootReasons.Add('Windows Update requested a restart.') | Out-Null }
        try {
          $sessionManager = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -ErrorAction SilentlyContinue
          if ($null -ne $sessionManager -and $null -ne $sessionManager.PendingFileRenameOperations) {
            $pendingRebootReasons.Add('Pending file rename operations were detected.') | Out-Null
          }
        } catch {}
        $pendingRebootStatusText = if ($pendingRebootReasons.Count -gt 0) { 'Restart required' } else { 'No pending restart' }
        $pendingRebootDetailText = if ($pendingRebootReasons.Count -gt 0) { $pendingRebootReasons -join ' | ' } else { 'No pending reboot indicators were found.' }
        $windowsUpdateScheduledRestartStatusText = 'Unknown'
        $windowsUpdateScheduledRestartTimeText = 'Windows Update scheduled restart state is not available.'
        $mecmScheduledRestartTimeText = 'MECM scheduled restart state is not available.'
        try {
          $scheduledRestartTask = Get-ScheduledTask -TaskPath '\Microsoft\Windows\UpdateOrchestrator\' -TaskName 'Reboot' -ErrorAction SilentlyContinue
          $scheduledRestartInfo = if ($null -ne $scheduledRestartTask) { Get-ScheduledTaskInfo -InputObject $scheduledRestartTask -ErrorAction SilentlyContinue } else { $null }
          $scheduledRestartTime = if ($null -ne $scheduledRestartInfo) { $scheduledRestartInfo.NextRunTime } else { $null }
          if ($scheduledRestartTime -is [DateTime] -and $scheduledRestartTime.Year -gt 2000) {
            $windowsUpdateScheduledRestartStatusText = 'Scheduled'
            $windowsUpdateScheduledRestartTimeText = ([DateTimeOffset]$scheduledRestartTime).ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss zzz')
          } else {
            $windowsUpdateScheduledRestartStatusText = 'Not scheduled'
            $windowsUpdateScheduledRestartTimeText = 'No Windows Update restart is scheduled.'
          }
        } catch {}
        try {
          $mecmRebootResult = Invoke-CimMethod -Namespace 'root\ccm\ClientSDK' -ClassName 'CCM_ClientUtilities' -MethodName 'DetermineIfRebootPending' -ErrorAction SilentlyContinue
          if ($null -ne $mecmRebootResult) {
            $mecmDeadline = $null
            foreach ($propertyName in @('RebootDeadline','Deadline','OverrideRebootWindowTime')) {
              if ($mecmRebootResult.PSObject.Properties.Name -contains $propertyName) {
                $candidate = $mecmRebootResult.$propertyName
                if ($candidate -is [DateTime] -and $candidate.Year -gt 2000) {
                  $mecmDeadline = [DateTimeOffset]$candidate
                  break
                }
                if (-not [string]::IsNullOrWhiteSpace([string]$candidate)) {
                  try {
                    $parsedCandidate = [Management.ManagementDateTimeConverter]::ToDateTime([string]$candidate)
                    if ($parsedCandidate.Year -gt 2000) {
                      $mecmDeadline = [DateTimeOffset]$parsedCandidate
                      break
                    }
                  } catch {}
                }
              }
            }
            if ($null -ne $mecmDeadline) {
              $mecmScheduledRestartTimeText = $mecmDeadline.ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss zzz')
            } else {
              $mecmScheduledRestartTimeText = 'No MECM restart deadline is scheduled.'
            }
          } else {
            $mecmScheduledRestartTimeText = 'No MECM restart deadline is scheduled.'
          }
        } catch {}
        $sessionLockStatusText = 'Unknown'
        $sessionLockedSinceText = 'Session lock state is not available.'
        try {
          $signedInUser = $null
          $computerSystemForLock = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
          if ($null -ne $computerSystemForLock -and -not [string]::IsNullOrWhiteSpace([string]$computerSystemForLock.UserName)) {
            $signedInUser = [string]$computerSystemForLock.UserName
          }

          if ([string]::IsNullOrWhiteSpace($signedInUser)) {
            $explorerProcesses = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" -ErrorAction SilentlyContinue)
            foreach ($explorerProcess in @($explorerProcesses)) {
              try {
                $owner = Invoke-CimMethod -InputObject $explorerProcess -MethodName GetOwner -ErrorAction SilentlyContinue
                $ownerUser = [string]$owner.User
                if ([string]::IsNullOrWhiteSpace($ownerUser)) { continue }
                $ownerDomain = [string]$owner.Domain
                $signedInUser = if ([string]::IsNullOrWhiteSpace($ownerDomain)) { $ownerUser } else { $ownerDomain + '\' + $ownerUser }
                break
              } catch {
              }
            }
          }

          if ([string]::IsNullOrWhiteSpace($signedInUser)) {
            $sessionLockStatusText = 'No signed-in user'
            $sessionLockedSinceText = 'No signed-in user detected.'
          } else {
            $lockCandidates = New-Object System.Collections.Generic.List[DateTimeOffset]
            $logonUiProcesses = @(Get-CimInstance Win32_Process -Filter "Name='LogonUI.exe'" -ErrorAction SilentlyContinue)
            foreach ($logonUiProcess in @($logonUiProcesses)) {
              $creationRaw = $logonUiProcess.CreationDate
              if ($null -eq $creationRaw) { continue }
              try {
                $creationDate = [Management.ManagementDateTimeConverter]::ToDateTime($creationRaw.ToString())
                if ($creationDate.Year -gt 2000) { $lockCandidates.Add([DateTimeOffset]$creationDate) | Out-Null }
              } catch {
                try {
                  $creationDateOffset = [DateTimeOffset]$creationRaw
                  if ($creationDateOffset.Year -gt 2000) { $lockCandidates.Add($creationDateOffset) | Out-Null }
                } catch {
                }
              }
            }

            if ($lockCandidates.Count -gt 0) {
              $lockedSince = $lockCandidates | Sort-Object | Select-Object -First 1
              $sessionLockStatusText = 'Locked'
              $sessionLockedSinceText = $lockedSince.ToString('yyyy-MM-dd HH:mm:ss zzz')
            } else {
              $sessionLockStatusText = 'Unlocked'
              $sessionLockedSinceText = 'A signed-in user was detected but the session is not currently locked.'
            }
          }
        } catch {}

        [ordered]@{
          SystemRuntime = [ordered]@{
            UptimeText = $uptimeText
            LastBootText = $lastBootText
            InstallDateText = $installDateText
            PendingRebootStatusText = $pendingRebootStatusText
            PendingRebootDetailText = $pendingRebootDetailText
            WindowsUpdateScheduledRestartStatusText = $windowsUpdateScheduledRestartStatusText
            WindowsUpdateScheduledRestartTimeText = $windowsUpdateScheduledRestartTimeText
            MecmScheduledRestartTimeText = $mecmScheduledRestartTimeText
            SessionLockStatusText = $sessionLockStatusText
            SessionLockedSinceText = $sessionLockedSinceText
        }
        } | ConvertTo-Json -Depth 5 -Compress
        """;

    private string BuildNetworkConnectivitySnapshotScript()
    {
        var escapedVpnAdapterDescriptionMatch = _vpnAdapterDescriptionMatch.Replace("'", "''", StringComparison.Ordinal);
        var escapedVpnProviderName = _vpnProviderName.Replace("'", "''", StringComparison.Ordinal);
        return """
        function Get-FirstPropertyValue($obj, [string[]]$names) {
          if ($null -eq $obj -or $null -eq $names) { return $null }
          foreach ($name in $names) {
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            $prop = $obj.PSObject.Properties[$name]
            if ($null -eq $prop -or $null -eq $prop.Value) { continue }
            if ($prop.Value -is [string] -and [string]::IsNullOrWhiteSpace($prop.Value)) { continue }
            return $prop.Value
          }
          return $null
        }
        function Convert-ToDisplayString($value) {
          if ($null -eq $value) { return '' }
          if ($value -is [string]) { return $value.Trim() }
          return [string]$value
        }
        function Get-ClientAuthCertificateSummary {
          $now = Get-Date
          $allCerts = @()
          foreach ($storePath in @('Cert:\LocalMachine\My', 'Cert:\CurrentUser\My')) {
            try { $allCerts += @(Get-ChildItem -Path $storePath -ErrorAction Stop) } catch {}
          }
          $clientAuthCerts = @($allCerts | Where-Object {
            $cert = $_
            @($cert.EnhancedKeyUsageList | Where-Object {
              $_.ObjectId -eq '1.3.6.1.5.5.7.3.2' -or $_.Value -eq '1.3.6.1.5.5.7.3.2'
            }).Count -gt 0
          })
          $validClientAuthCerts = @($clientAuthCerts | Where-Object {
            $_.NotBefore -le $now -and $_.NotAfter -gt $now -and $_.HasPrivateKey
          })
          $best = @($validClientAuthCerts | Sort-Object NotAfter -Descending | Select-Object -First 1)
          $detail = if ($best.Count -gt 0) {
            'Valid client authentication certificate: ' + $best[0].Subject + ' (expires ' + $best[0].NotAfter.ToString('yyyy-MM-dd') + ').'
          } elseif ($clientAuthCerts.Count -gt 0) {
            'Client authentication certificates found, but none are currently valid with a private key.'
          } else {
            'No client authentication certificate was found in LocalMachine\My or CurrentUser\My.'
          }

          [ordered]@{
            HasValidCertificate = ($validClientAuthCerts.Count -gt 0)
            Detail = $detail
          }
        }
        function Get-Dot3PolicySummary {
          $profilesOutput = ''
          $profileNames = @()
          try {
            $profilesOutput = (netsh lan show profiles 2>&1 | Out-String).Trim()
            $profileNames = @($profilesOutput -split "`r?`n" | ForEach-Object {
              $line = $_.Trim()
              if ($line -match ':\s*(?<name>.+)$' -and $line -notmatch '(?i)profiles\s+on\s+interface') {
                $Matches['name'].Trim()
              }
            } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
          } catch {
            return [ordered]@{
              HasPolicy = $false
              HasValidXml = $false
              Detail = 'dot3svc policy could not be queried: ' + $_.Exception.Message
            }
          }

          if ($profileNames.Count -eq 0) {
            $detail = if ([string]::IsNullOrWhiteSpace($profilesOutput)) { 'No wired 802.1X profiles were returned by netsh lan show profiles.' } else { 'No wired 802.1X profiles were found. netsh output: ' + $profilesOutput }
            return [ordered]@{
              HasPolicy = $false
              HasValidXml = $false
              Detail = $detail
            }
          }

          $tempFolder = Join-Path ([System.IO.Path]::GetTempPath()) ('ICC-dot3svc-' + [Guid]::NewGuid().ToString('N'))
          $xmlValid = $false
          try {
            New-Item -ItemType Directory -Path $tempFolder -Force | Out-Null
            netsh lan export profile folder="$tempFolder" | Out-Null
            foreach ($xmlFile in @(Get-ChildItem -Path $tempFolder -Filter '*.xml' -ErrorAction SilentlyContinue)) {
              try {
                [xml](Get-Content -Path $xmlFile.FullName -Raw -ErrorAction Stop) | Out-Null
                $xmlValid = $true
              } catch {}
            }
          } catch {
          } finally {
            try { Remove-Item -Path $tempFolder -Recurse -Force -ErrorAction SilentlyContinue } catch {}
          }

          [ordered]@{
            HasPolicy = $true
            HasValidXml = $xmlValid
            Detail = 'dot3svc profiles: ' + ($profileNames -join ', ') + '. XML export valid: ' + $xmlValid + '.'
          }
        }
        function Get-PortAuthenticationSummary([string]$vpnStatusText) {
          if ($vpnStatusText -eq 'Connected') {
            return [ordered]@{
              Status = 'Skipped'
              Detail = 'Port authentication check skipped because VPN is connected.'
            }
          }

          $details = [System.Collections.Generic.List[string]]::new()
          $dot3svcRunning = $false
          try {
            $service = Get-Service -Name dot3svc -ErrorAction Stop
            $dot3svcRunning = ($service.Status -eq 'Running')
            $details.Add('dot3svc service: ' + $service.Status + '.')
          } catch {
            $details.Add('dot3svc service could not be queried: ' + $_.Exception.Message)
          }

          $authState = ''
          try {
            $interfacesOutput = (netsh lan show interfaces 2>&1 | Out-String).Trim()
            $authStateMatches = @([regex]::Matches($interfacesOutput, '(?im)^\s*(?:Authentication\s+state|Authentication)\s*:\s*(?<state>.+?)\s*$'))
            if ($authStateMatches.Count -gt 0) {
              $authState = ($authStateMatches | ForEach-Object { $_.Groups['state'].Value.Trim() } | Select-Object -Unique) -join ', '
            }
            if ([string]::IsNullOrWhiteSpace($authState)) {
              $details.Add('802.1X authentication state was not reported by netsh lan show interfaces.')
            } else {
              $details.Add('802.1X authentication state: ' + $authState + '.')
            }
          } catch {
            $details.Add('802.1X authentication state could not be queried: ' + $_.Exception.Message)
          }

          $certificate = Get-ClientAuthCertificateSummary
          $policy = Get-Dot3PolicySummary
          $details.Add($certificate.Detail)
          $details.Add($policy.Detail)

          $isAuthenticated = -not [string]::IsNullOrWhiteSpace($authState) -and $authState -match '(?i)\bauthenticated\b' -and $authState -notmatch '(?i)\bnot\s+authenticated\b'
          $isReady = $dot3svcRunning -and $certificate.HasValidCertificate -and $policy.HasPolicy -and $policy.HasValidXml
          $status = if ($isAuthenticated) { 'Authenticated' } elseif ($isReady) { 'Not authenticated' } else { 'Not ready' }

          [ordered]@{
            Status = $status
            Detail = ($details -join ' ')
          }
        }

        $primaryConnectionText = 'Unknown'
        $primaryAdapterText = 'Unknown'
        $wiFiSsidText = 'Not connected'
        $vpnStatusText = 'Not detected'
        $vpnProviderText = '-'
        $isCheckpointVpnDetected = $false
        $portAuthentication = [ordered]@{
          Status = 'Unknown'
          Detail = 'Port authentication status is not available.'
        }
        $checkpointAdapterDescription = '{{VPN_ADAPTER_MATCH}}'
        $allAdapters = @()
        $activeAdapters = @()
        $activePhysicalAdapters = @()
        try {
          $allAdapters = @(Get-NetAdapter -ErrorAction Stop)
          $activeAdapters = @($allAdapters | Where-Object { $_.Status -eq 'Up' })
          $activePhysicalAdapters = @($activeAdapters | Where-Object {
            $fingerprint = @(
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('Name')),
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceAlias')),
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceDescription')),
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('NdisPhysicalMedium'))
            ) -join ' '
            -not [string]::IsNullOrWhiteSpace($fingerprint) -and
            $fingerprint -notmatch '(?i)virtual|vpn|loopback|bluetooth|hyper-v|vmware|vethernet|tunnel|wan miniport|ras'
          })
        } catch {}

        $primaryAdapter = @($activePhysicalAdapters | Where-Object {
          $fingerprint = @(
            Convert-ToDisplayString (Get-FirstPropertyValue $_ @('Name')),
            Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceAlias')),
            Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceDescription')),
            Convert-ToDisplayString (Get-FirstPropertyValue $_ @('NdisPhysicalMedium'))
          ) -join ' '
          $fingerprint -match '(?i)wi-?fi|wlan|wireless|802\.11'
        } | Select-Object -First 1)
        if ($primaryAdapter.Count -gt 0) {
          $primaryConnectionText = 'Wi-Fi'
          $wiFiSsidText = 'Connected'
        }
        if (($null -eq $primaryAdapter -or $primaryAdapter.Count -eq 0) -and $activePhysicalAdapters.Count -gt 0) {
          $primaryAdapter = @($activePhysicalAdapters | Where-Object {
            $fingerprint = @(
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('Name')),
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceAlias')),
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('InterfaceDescription')),
              Convert-ToDisplayString (Get-FirstPropertyValue $_ @('NdisPhysicalMedium'))
            ) -join ' '
            $fingerprint -match '(?i)ethernet|lan|gigabit'
          } | Select-Object -First 1)
          if ($primaryAdapter.Count -gt 0) { $primaryConnectionText = 'LAN' }
        }
        if (($null -eq $primaryAdapter -or $primaryAdapter.Count -eq 0) -and $activePhysicalAdapters.Count -gt 0) {
          $primaryAdapter = @($activePhysicalAdapters | Select-Object -First 1)
          if ($primaryAdapter.Count -gt 0) { $primaryConnectionText = 'Connected' }
        }
        if ($primaryAdapter.Count -gt 0) {
          $primaryAdapterText = Convert-ToDisplayString (Get-FirstPropertyValue $primaryAdapter[0] @('InterfaceDescription', 'Name', 'InterfaceAlias'))
        }

        foreach ($adapter in $allAdapters) {
          $adapterDescription = Convert-ToDisplayString (Get-FirstPropertyValue $adapter @('InterfaceDescription', 'Name', 'InterfaceAlias'))
          if ([string]::IsNullOrWhiteSpace($adapterDescription)) { continue }
          if (-not [string]::IsNullOrWhiteSpace($checkpointAdapterDescription) -and $adapterDescription -like ('*' + $checkpointAdapterDescription + '*')) {
            $isCheckpointVpnDetected = $true
            if ($adapter.Status -eq 'Up') { $vpnStatusText = 'Connected' }
          }
        }
        if ($isCheckpointVpnDetected -and $vpnStatusText -eq 'Not detected') { $vpnStatusText = 'Adapter detected' }
        if ($isCheckpointVpnDetected -and -not [string]::IsNullOrWhiteSpace('{{VPN_PROVIDER_NAME}}')) { $vpnProviderText = '{{VPN_PROVIDER_NAME}}' }
        $portAuthentication = Get-PortAuthenticationSummary $vpnStatusText

        [ordered]@{
          NetworkConnectivity = [ordered]@{
            PrimaryConnectionText = $primaryConnectionText
            PrimaryAdapterText = $primaryAdapterText
            WiFiSsidText = $wiFiSsidText
            VpnStatusText = $vpnStatusText
            VpnProviderText = $vpnProviderText
            IsCheckpointVpnDetected = $isCheckpointVpnDetected
            PortAuthenticationStatusText = $portAuthentication.Status
            PortAuthenticationDetailText = $portAuthentication.Detail
          }
        } | ConvertTo-Json -Depth 5 -Compress
        """
            .Replace("{{VPN_ADAPTER_MATCH}}", escapedVpnAdapterDescriptionMatch, StringComparison.Ordinal)
            .Replace("{{VPN_PROVIDER_NAME}}", escapedVpnProviderName, StringComparison.Ordinal);
    }

    private static string BuildDeliveryOptimizationSnapshotScript() =>
        """
        function Get-FirstPropertyValue($obj, [string[]]$names) {
          if ($null -eq $obj -or $null -eq $names) { return $null }
          foreach ($name in $names) {
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            $prop = $obj.PSObject.Properties[$name]
            if ($null -eq $prop -or $null -eq $prop.Value) { continue }
            if ($prop.Value -is [string] -and [string]::IsNullOrWhiteSpace($prop.Value)) { continue }
            return $prop.Value
          }
          return $null
        }

        function Convert-ToInt64($value) {
          if ($null -eq $value) { return 0L }
          if ($value -is [long]) { return [long]$value }
          if ($value -is [int]) { return [long]$value }
          if ($value -is [double]) { return [long][Math]::Round($value) }
          if ($value -is [decimal]) { return [long][Math]::Round([double]$value) }
          $text = [string]$value
          if ([string]::IsNullOrWhiteSpace($text)) { return 0L }
          $numeric = 0L
          if ([long]::TryParse($text, [ref]$numeric)) { return $numeric }
          $compact = ($text -replace '[^0-9\.\-]', '')
          $doubleValue = 0.0
          if ([double]::TryParse($compact, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$doubleValue)) {
            return [long][Math]::Round($doubleValue)
          }
          return 0L
        }

        function Test-ScalarValue($value) {
          if ($null -eq $value) { return $false }
          return $value -is [string] -or
                 $value -is [ValueType] -or
                 $value -is [DateTime] -or
                 $value -is [DateTimeOffset] -or
                 $value -is [Guid]
        }

        function Convert-ToDisplayString($value) {
          if ($null -eq $value) { return '' }
          if ($value -is [DateTime]) { return ([DateTime]$value).ToUniversalTime().ToString('o') }
          if ($value -is [DateTimeOffset]) { return ([DateTimeOffset]$value).ToUniversalTime().ToString('o') }
          if ($value -is [bool]) { return $(if ($value) { 'True' } else { 'False' }) }
          if ($value -is [string]) { return $value.Trim() }
          if ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string])) {
            $items = @($value | ForEach-Object { Convert-ToDisplayString $_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            return ($items -join ', ')
          }
          return [string]$value
        }

        function Add-DoNameValue([System.Collections.Generic.List[object]]$target, [string]$name, $value) {
          if ($null -eq $target -or [string]::IsNullOrWhiteSpace($name)) { return }
          $text = Convert-ToDisplayString $value
          if ([string]::IsNullOrWhiteSpace($text)) { return }
          $target.Add([ordered]@{
            Name = [string]$name
            Value = $text
          }) | Out-Null
        }

        function Add-DoScalarProperties([System.Collections.Generic.List[object]]$target, $obj, [string[]]$priorityNames) {
          if ($null -eq $target -or $null -eq $obj) { return }
          $seen = @{}
          foreach ($name in @($priorityNames)) {
            if ([string]::IsNullOrWhiteSpace($name) -or $seen.ContainsKey($name)) { continue }
            $prop = $obj.PSObject.Properties[$name]
            if ($null -eq $prop -or -not (Test-ScalarValue $prop.Value)) { continue }
            Add-DoNameValue $target $name $prop.Value
            $seen[$name] = $true
          }

          foreach ($prop in ($obj.PSObject.Properties | Sort-Object Name)) {
            if ($null -eq $prop -or [string]::IsNullOrWhiteSpace($prop.Name) -or $seen.ContainsKey($prop.Name)) { continue }
            if (-not (Test-ScalarValue $prop.Value)) { continue }
            Add-DoNameValue $target $prop.Name $prop.Value
            $seen[$prop.Name] = $true
          }
        }

        function Normalize-DoSource([string]$source) {
          if ([string]::IsNullOrWhiteSpace($source)) { return 'Unknown' }
          $normalized = $source.Trim().ToLowerInvariant()
          if ($normalized -match 'cache|mcc') { return 'CacheServer' }
          if ($normalized -match 'lan') { return 'PeerLan' }
          if ($normalized -match 'group') { return 'PeerGroup' }
          if ($normalized -match 'internet') { return 'PeerInternet' }
          if ($normalized -match 'peer|p2p') { return 'Peer' }
          if ($normalized -match 'http|cdn|wan') { return 'Http' }
          return $source.Trim()
        }

        $doTransfers = New-Object System.Collections.Generic.List[object]
        $doSourceTotals = @{}
        $doNotes = New-Object System.Collections.Generic.List[string]
        $doCurrentMetrics = New-Object System.Collections.Generic.List[object]
        $doMonthlyMetrics = New-Object System.Collections.Generic.List[object]
        $doConfiguration = New-Object System.Collections.Generic.List[object]
        $doPeerStatuses = New-Object System.Collections.Generic.List[object]
        $doSupportsTimeRange = $false
        $doDataStartUtc = $null
        $doDataEndUtc = $null

        function Add-DoTotal([string]$source, [long]$bytes) {
          if ($bytes -le 0) { return }
          $normalized = Normalize-DoSource $source
          if (-not $script:doSourceTotals.ContainsKey($normalized)) {
            $script:doSourceTotals[$normalized] = [long]0
          }

          if ([long]$script:doSourceTotals[$normalized] -lt [long]$bytes) {
            $script:doSourceTotals[$normalized] = [long]$bytes
          }
        }

        function Add-DoTransfer([string]$source, [long]$bytes, $timestamp, [string]$description) {
          if ($bytes -le 0) { return }
          $normalized = Normalize-DoSource $source
          if (-not $script:doSourceTotals.ContainsKey($normalized)) {
            $script:doSourceTotals[$normalized] = [long]0
          }
          $script:doSourceTotals[$normalized] = [long]$script:doSourceTotals[$normalized] + [long]$bytes

          $timeText = (Get-Date).ToUniversalTime().ToString('o')
          if ($null -ne $timestamp -and $timestamp -is [DateTime] -and $timestamp.Year -gt 2000) {
            $utc = ([DateTime]$timestamp).ToUniversalTime()
            $timeText = $utc.ToString('o')
            if ($null -eq $script:doDataStartUtc -or $utc -lt $script:doDataStartUtc) { $script:doDataStartUtc = $utc }
            if ($null -eq $script:doDataEndUtc -or $utc -gt $script:doDataEndUtc) { $script:doDataEndUtc = $utc }
            $script:doSupportsTimeRange = $true
          }

          if ([string]::IsNullOrWhiteSpace($description)) { $description = '-' }
          $script:doTransfers.Add([ordered]@{
            TimestampUtc = $timeText
            Source = $normalized
            Bytes = [long]$bytes
            Description = [string]$description
          }) | Out-Null
        }

        function Resolve-DoTimestamp($obj) {
          $raw = Get-FirstPropertyValue $obj @('Timestamp', 'TimeCreated', 'StartTime', 'Date', 'ModifiedTime')
          if ($null -eq $raw) { return $null }
          if ($raw -is [DateTime]) { return [DateTime]$raw }
          $text = [string]$raw
          if ([string]::IsNullOrWhiteSpace($text)) { return $null }
          $parsed = [DateTime]::MinValue
          if ([DateTime]::TryParse($text, [ref]$parsed)) { return $parsed }
          return $null
        }

        function Add-DoBytesFromObject($obj, [DateTime]$timestamp, [string]$description) {
          if ($null -eq $obj) { return }

          $httpBytes = Convert-ToInt64 (Get-FirstPropertyValue $obj @('BytesFromHttp', 'HttpBytes', 'BytesDownloadedFromHttp', 'BytesFromCDN'))
          $cacheBytes = Convert-ToInt64 (Get-FirstPropertyValue $obj @('BytesFromCacheServer', 'BytesFromCacheHost', 'CacheHostBytes', 'CacheServerBytes'))
          $lanPeerBytes = Convert-ToInt64 (Get-FirstPropertyValue $obj @('BytesFromLanPeers', 'LanPeerBytes'))
          $groupPeerBytes = Convert-ToInt64 (Get-FirstPropertyValue $obj @('BytesFromGroupPeers', 'GroupPeerBytes'))
          $internetPeerBytes = Convert-ToInt64 (Get-FirstPropertyValue $obj @('BytesFromInternetPeers', 'InternetPeerBytes'))
          $peerBytes = Convert-ToInt64 (Get-FirstPropertyValue $obj @('BytesFromPeers', 'PeerBytes', 'BytesDownloadedFromPeers'))

          if ($httpBytes -gt 0) { Add-DoTransfer 'Http' $httpBytes $timestamp $description }
          if ($cacheBytes -gt 0) { Add-DoTransfer 'CacheServer' $cacheBytes $timestamp $description }
          if ($lanPeerBytes -gt 0) { Add-DoTransfer 'PeerLan' $lanPeerBytes $timestamp $description }
          if ($groupPeerBytes -gt 0) { Add-DoTransfer 'PeerGroup' $groupPeerBytes $timestamp $description }
          if ($internetPeerBytes -gt 0) { Add-DoTransfer 'PeerInternet' $internetPeerBytes $timestamp $description }

          if ($peerBytes -gt 0 -and $lanPeerBytes -le 0 -and $groupPeerBytes -le 0 -and $internetPeerBytes -le 0) {
            Add-DoTransfer 'Peer' $peerBytes $timestamp $description
          }
        }

        function Add-DoBytesFromMessage([string]$message, [DateTime]$timestamp, [string]$fallbackDescription) {
          if ([string]::IsNullOrWhiteSpace($message)) { return }
          $source = 'Unknown'
          if ($message -match '(?i)cache server|mcc') { $source = 'CacheServer' }
          elseif ($message -match '(?i)peer|p2p') { $source = 'Peer' }
          elseif ($message -match '(?i)http|cdn|internet') { $source = 'Http' }

          $bytes = 0L
          if ($message -match '(?i)(?<value>\d+)\s*(?<unit>bytes|byte|kb|mb|gb)') {
            $base = Convert-ToInt64 $matches['value']
            $unit = $matches['unit'].ToLowerInvariant()
            switch ($unit) {
              'gb' { $bytes = $base * 1GB }
              'mb' { $bytes = $base * 1MB }
              'kb' { $bytes = $base * 1KB }
              default { $bytes = $base }
            }
          }
          elseif ($message -match '(?i)bytes\s*[:=]\s*(?<value>\d+)') {
            $bytes = Convert-ToInt64 $matches['value']
          }

          if ($bytes -gt 0) {
            Add-DoTransfer $source $bytes $timestamp $fallbackDescription
          }
        }

        function Collect-DeliveryOptimizationData {
          $hasStatusCommand = $null -ne (Get-Command -Name 'Get-DeliveryOptimizationStatus' -ErrorAction SilentlyContinue)
          $hasPerfSnapCommand = $null -ne (Get-Command -Name 'Get-DeliveryOptimizationPerfSnap' -ErrorAction SilentlyContinue)
          $hasPerfSnapMonthCommand = $null -ne (Get-Command -Name 'Get-DeliveryOptimizationPerfSnapThisMonth' -ErrorAction SilentlyContinue)
          $hasLogCommand = $null -ne (Get-Command -Name 'Get-DeliveryOptimizationLog' -ErrorAction SilentlyContinue)
          $hasConfigCommand = $null -ne (Get-Command -Name 'Get-DOConfig' -ErrorAction SilentlyContinue)
          $hasPeerInfoStatus = $false
          if ($hasStatusCommand) {
            try {
              $statusCommand = Get-Command -Name 'Get-DeliveryOptimizationStatus' -ErrorAction SilentlyContinue
              $hasPeerInfoStatus = $null -ne $statusCommand -and $statusCommand.Parameters.ContainsKey('PeerInfo')
            } catch {
              $hasPeerInfoStatus = $false
            }
          }
          $hasOperationalLog = $false
          try {
            $hasOperationalLog = $null -ne (Get-WinEvent -ListLog 'Microsoft-Windows-DeliveryOptimization/Operational' -ErrorAction SilentlyContinue)
          } catch {
            $hasOperationalLog = $false
          }

          if (-not $hasStatusCommand -and -not $hasPerfSnapCommand -and -not $hasPerfSnapMonthCommand -and -not $hasLogCommand -and -not $hasConfigCommand -and -not $hasOperationalLog) {
            $script:doNotes.Add('Delivery Optimization commandlets, configuration, or operational log are not available on this device.') | Out-Null
            return $false
          }

          if ($hasStatusCommand) {
            try {
              $statusItems = @(Get-DeliveryOptimizationStatus -ErrorAction Stop -WarningAction SilentlyContinue)
              foreach ($item in $statusItems) {
                $description = [string](Get-FirstPropertyValue $item @('FileId', 'FileName', 'ContentId', 'SourceUrl', 'DownloadUrl'))
                $timestamp = Resolve-DoTimestamp $item
                Add-DoBytesFromObject $item $timestamp $description
              }
            } catch {
              $script:doNotes.Add('Get-DeliveryOptimizationStatus failed: ' + $_.Exception.Message) | Out-Null
            }
          }

          if ($hasPeerInfoStatus) {
            try {
              $peerItems = @(Get-DeliveryOptimizationStatus -PeerInfo -ErrorAction Stop -WarningAction SilentlyContinue | Select-Object -First 100)
              foreach ($item in $peerItems) {
                $content = Convert-ToDisplayString (Get-FirstPropertyValue $item @('FileId', 'FileName', 'ContentId', 'DownloadUrl', 'SourceUrl'))
                if ([string]::IsNullOrWhiteSpace($content)) { $content = '-' }
                $statusText = Convert-ToDisplayString (Get-FirstPropertyValue $item @('PeerStatus', 'Status', 'State', 'DownloadState'))
                if ([string]::IsNullOrWhiteSpace($statusText)) { $statusText = '-' }
                $candidateCount = [int](Convert-ToInt64 (Get-FirstPropertyValue $item @('PeerCount', 'NumPeers', 'TotalPeers', 'PeerCandidateCount')))
                $connectedCount = [int](Convert-ToInt64 (Get-FirstPropertyValue $item @('ConnectedPeerCount', 'ConnectedPeers', 'ConnectedPeerConnections')))
                $bytesFromPeers = Convert-ToInt64 (Get-FirstPropertyValue $item @('BytesFromPeers', 'PeerBytes', 'BytesDownloadedFromPeers'))
                $bytesFromHttp = Convert-ToInt64 (Get-FirstPropertyValue $item @('BytesFromHttp', 'HttpBytes', 'BytesDownloadedFromHttp', 'BytesFromCDN'))
                $detailParts = @()
                foreach ($detailName in 'PeerType', 'CacheHost', 'DownloadUrl', 'SourceUrl') {
                  $detailValue = Convert-ToDisplayString (Get-FirstPropertyValue $item @($detailName))
                  if (-not [string]::IsNullOrWhiteSpace($detailValue)) {
                    $detailParts += ($detailName + '=' + $detailValue)
                  }
                }

                $script:doPeerStatuses.Add([ordered]@{
                  Content = $content
                  Status = $statusText
                  CandidateCount = $candidateCount
                  ConnectedPeerCount = $connectedCount
                  BytesFromPeers = [long]$bytesFromPeers
                  BytesFromHttp = [long]$bytesFromHttp
                  Details = if ($detailParts.Count -gt 0) { $detailParts -join '; ' } else { '-' }
                }) | Out-Null
              }
            } catch {
              $script:doNotes.Add('Get-DeliveryOptimizationStatus -PeerInfo failed: ' + $_.Exception.Message) | Out-Null
            }
          }

          if ($hasLogCommand) {
            try {
              $logItems = @(Get-DeliveryOptimizationLog -ErrorAction Stop -WarningAction SilentlyContinue | Select-Object -First 400)
              foreach ($item in $logItems) {
                $description = [string](Get-FirstPropertyValue $item @('FileId', 'FileName', 'ContentId', 'SourceUrl', 'Message'))
                $timestamp = Resolve-DoTimestamp $item
                Add-DoBytesFromObject $item $timestamp $description
                if ([string]::IsNullOrWhiteSpace($description)) { $description = 'DeliveryOptimizationLog' }
                Add-DoBytesFromMessage ([string](Get-FirstPropertyValue $item @('Message', 'Description', 'Details'))) $timestamp $description
              }
            } catch {
              $script:doNotes.Add('Get-DeliveryOptimizationLog failed: ' + $_.Exception.Message) | Out-Null
            }
          }

          if ($hasOperationalLog) {
            try {
              $evtItems = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-DeliveryOptimization/Operational'; StartTime = (Get-Date).AddDays(-30) } -MaxEvents 400 -ErrorAction Stop)
              foreach ($evt in $evtItems) {
                $description = 'Event ' + [string]$evt.Id
                Add-DoBytesFromMessage ([string]$evt.Message) $evt.TimeCreated $description
              }
            } catch {
              $script:doNotes.Add('Delivery Optimization operational log query failed: ' + $_.Exception.Message) | Out-Null
            }
          }

          if ($hasPerfSnapCommand) {
            try {
              $perfItems = @(Get-DeliveryOptimizationPerfSnap -ErrorAction Stop -WarningAction SilentlyContinue)
              $perf = $perfItems | Select-Object -First 1
              if ($null -ne $perf) {
                Add-DoScalarProperties $script:doCurrentMetrics $perf @(
                  'DownloadMode', 'DODownloadMode', 'NumberOfPeers', 'PeerCount',
                  'HttpBytes', 'BytesFromHttp', 'BytesDownloadedFromHttp',
                  'CacheHostBytes', 'BytesFromCacheServer', 'BytesFromCacheHost',
                  'LanPeerBytes', 'GroupPeerBytes', 'InternetPeerBytes',
                  'PeerBytes', 'BytesFromPeers')
                $perfHttp = Convert-ToInt64 (Get-FirstPropertyValue $perf @('HttpBytes', 'BytesFromHttp', 'BytesDownloadedFromHttp'))
                $perfCache = Convert-ToInt64 (Get-FirstPropertyValue $perf @('CacheHostBytes', 'BytesFromCacheServer', 'BytesFromCacheHost'))
                $perfLan = Convert-ToInt64 (Get-FirstPropertyValue $perf @('LanPeerBytes', 'BytesFromLanPeers'))
                $perfGroup = Convert-ToInt64 (Get-FirstPropertyValue $perf @('GroupPeerBytes', 'BytesFromGroupPeers'))
                $perfInternet = Convert-ToInt64 (Get-FirstPropertyValue $perf @('InternetPeerBytes', 'BytesFromInternetPeers'))
                $perfPeer = Convert-ToInt64 (Get-FirstPropertyValue $perf @('PeerBytes', 'BytesFromPeers'))

                if ($perfHttp -gt 0) { Add-DoTotal 'Http' $perfHttp }
                if ($perfCache -gt 0) { Add-DoTotal 'CacheServer' $perfCache }
                if ($perfLan -gt 0) { Add-DoTotal 'PeerLan' $perfLan }
                if ($perfGroup -gt 0) { Add-DoTotal 'PeerGroup' $perfGroup }
                if ($perfInternet -gt 0) { Add-DoTotal 'PeerInternet' $perfInternet }
                if ($perfPeer -gt 0 -and $perfLan -le 0 -and $perfGroup -le 0 -and $perfInternet -le 0) {
                  Add-DoTotal 'Peer' $perfPeer
                }
              }
            } catch {
              $script:doNotes.Add('Get-DeliveryOptimizationPerfSnap failed: ' + $_.Exception.Message) | Out-Null
            }
          }

          if ($hasPerfSnapMonthCommand) {
            try {
              $perfMonthItems = @(Get-DeliveryOptimizationPerfSnapThisMonth -ErrorAction Stop -WarningAction SilentlyContinue)
              $perfMonth = $perfMonthItems | Select-Object -First 1
              if ($null -ne $perfMonth) {
                Add-DoScalarProperties $script:doMonthlyMetrics $perfMonth @(
                  'DownloadMode', 'DODownloadMode', 'NumberOfPeers', 'PeerCount',
                  'HttpBytes', 'BytesFromHttp', 'BytesDownloadedFromHttp',
                  'CacheHostBytes', 'BytesFromCacheServer', 'BytesFromCacheHost',
                  'LanPeerBytes', 'GroupPeerBytes', 'InternetPeerBytes',
                  'PeerBytes', 'BytesFromPeers')
              }
            } catch {
              $script:doNotes.Add('Get-DeliveryOptimizationPerfSnapThisMonth failed: ' + $_.Exception.Message) | Out-Null
            }
          }

          if ($hasConfigCommand) {
            try {
              $configItems = @(Get-DOConfig -ErrorAction Stop -WarningAction SilentlyContinue)
              $config = $configItems | Select-Object -First 1
              if ($null -ne $config) {
                Add-DoScalarProperties $script:doConfiguration $config @(
                  'DODownloadMode', 'DOGroupID', 'DOGroupId', 'DOMCCServer', 'DOCacheHost',
                  'DOMaxCacheSize', 'DOMaxCacheAge', 'DOMinDiskSizeAllowedToPeer', 'DOMinRAMAllowedToPeer',
                  'DOMinFileSizeToCache', 'DOAbsoluteMaxCacheSize', 'DOVpnKeywords', 'DOAllowVPNPeerCaching')
              }
            } catch {
              $script:doNotes.Add('Get-DOConfig failed: ' + $_.Exception.Message) | Out-Null
            }
          }

          return $true
        }

        $doAvailable = Collect-DeliveryOptimizationData
        $doSourceStats = New-Object System.Collections.Generic.List[object]
        foreach ($entry in ($doSourceTotals.GetEnumerator() | Sort-Object Value -Descending)) {
          $transferCount = @($doTransfers | Where-Object { $_.Source -eq $entry.Key }).Count
          $doSourceStats.Add([ordered]@{
            Source = [string]$entry.Key
            Bytes = [long]$entry.Value
            TransferCount = [int]$transferCount
          }) | Out-Null
        }

        $result = [ordered]@{
          DeliveryOptimization = [ordered]@{
            IsAvailable = $doAvailable
            CapturedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
            SupportsTimeRangeFiltering = $doSupportsTimeRange
            DataStartUtc = if ($null -ne $doDataStartUtc) { $doDataStartUtc.ToString('o') } else { $null }
            DataEndUtc = if ($null -ne $doDataEndUtc) { $doDataEndUtc.ToString('o') } else { $null }
            SourceStats = $doSourceStats
            Transfers = $doTransfers
            Notes = $doNotes
            CurrentMetrics = $doCurrentMetrics
            MonthlyMetrics = $doMonthlyMetrics
            Configuration = $doConfiguration
            PeerStatuses = $doPeerStatuses
            ActiveJobs = $doActiveJobs
          }
        }
        $result | ConvertTo-Json -Depth 10 -Compress
        """;

    private static string BuildEventLogScript(string logName, int maxEntries)
    {
        var escapedLog = logName.Replace("'", "''", StringComparison.Ordinal);
        return
            "$logName='" + escapedLog + "';" +
            "$entries = @(Get-WinEvent -LogName $logName -MaxEvents " + maxEntries + " -ErrorAction Stop | " +
            "  Select-Object @{Name='TimeCreated';Expression={ if ($_.TimeCreated) { $_.TimeCreated.ToString('o') } else { $null } }}, @{Name='Id';Expression={$_.Id}}, @{Name='Level';Expression={$_.LevelDisplayName}}, @{Name='LevelValue';Expression={$_.Level}}, @{Name='Provider';Expression={$_.ProviderName}}, @{Name='Message';Expression={$_.Message}});" +
            "$entries | ConvertTo-Json -Depth 5 -Compress;";
    }

    private static string BuildDetailedEventLogScript(string logName, int maxEntries)
    {
        var escapedLog = logName.Replace("'", "''", StringComparison.Ordinal);
        return
            "$logName='" + escapedLog + "';" +
            "$entries = @(Get-WinEvent -LogName $logName -MaxEvents " + maxEntries + " -ErrorAction Stop | " +
            "  Select-Object " +
            "    @{Name='TimeCreated';Expression={ if ($_.TimeCreated) { $_.TimeCreated.ToString('o') } else { $null } }}," +
            "    @{Name='RecordId';Expression={$_.RecordId}}," +
            "    @{Name='Id';Expression={$_.Id}}," +
            "    @{Name='Level';Expression={$_.LevelDisplayName}}," +
            "    @{Name='LevelValue';Expression={$_.Level}}," +
            "    @{Name='Provider';Expression={$_.ProviderName}}," +
            "    @{Name='Message';Expression={$_.Message}}," +
            "    @{Name='Xml';Expression={$_.ToXml()}});" +
            "$entries | ConvertTo-Json -Depth 6 -Compress;";
    }

    private static string BuildMdmDiagnosticsExportScript(string outputDirectory)
    {
        return
            "$outDir='" + outputDirectory + "';" +
            "$tool=Join-Path $env:SystemRoot 'System32\\mdmdiagnosticstool.exe';" +
            "if (-not (Test-Path -LiteralPath $tool)) { throw 'mdmdiagnosticstool.exe was not found.' };" +
            "$name='mdm-diagnostics-' + $env:COMPUTERNAME + '-' + (Get-Date -Format 'yyyyMMddHHmmss');" +
            "$target=Join-Path $outDir $name;" +
            "New-Item -ItemType Directory -Path $target -Force | Out-Null;" +
            "& $tool -area 'DeviceEnrollment;DeviceProvisioning;Autopilot' -cab $target | Out-Null;" +
            "$target;";
    }

    private sealed class SnapshotPayload
    {
        public string? MachineName { get; init; }
        public string? CapturedAtUtc { get; init; }
        public string? LastSyncText { get; init; }
        public string? MdmLastSyncText { get; init; }
        public string? ImeLastSyncText { get; init; }
        public string? WindowsVersionText { get; init; }
        public string? WindowsBuildText { get; init; }
        public string? FreeDiskSpaceText { get; init; }
        public string? ManufacturerText { get; init; }
        public string? ModelText { get; init; }
        public string? SerialNumberText { get; init; }
        public string? AdJoinPathText { get; init; }
        public string? UpdateRingText { get; init; }
        public string? RegistrationSummary { get; init; }
        public string? DsregStatusText { get; init; }
        public List<string>? DsregHighlights { get; init; }
        public List<ArtifactPayload>? EnrollmentArtifacts { get; init; }
        public List<string>? EnterpriseMgmtTasks { get; init; }
        public List<string>? CertificateSummaries { get; init; }
        public List<NameValuePayload>? ServiceValues { get; init; }
        public List<string>? Notes { get; init; }
        public List<string>? DiagnosticsTimings { get; init; }
        public DeliveryOptimizationPayload? DeliveryOptimization { get; init; }
        public PlatformSecurityPayload? PlatformSecurity { get; init; }
        public SystemRuntimePayload? SystemRuntime { get; init; }
        public NetworkConnectivityPayload? NetworkConnectivity { get; init; }
    }

    private sealed class DeliveryOptimizationOnlyPayload
    {
        public DeliveryOptimizationPayload? DeliveryOptimization { get; init; }
    }

    private sealed class PlatformSecurityOnlyPayload
    {
        public PlatformSecurityPayload? PlatformSecurity { get; init; }
    }

    private sealed class SystemRuntimeOnlyPayload
    {
        public SystemRuntimePayload? SystemRuntime { get; init; }
    }

    private sealed class NetworkConnectivityOnlyPayload
    {
        public NetworkConnectivityPayload? NetworkConnectivity { get; init; }
    }

    private sealed class PlatformSecurityPayload
    {
        public string? BitLockerStatusText { get; init; }
        public string? BitLockerDetailText { get; init; }
        public string? TpmStatusText { get; init; }
        public string? TpmVersionText { get; init; }
        public string? TpmDetailText { get; init; }
        public string? SecureBootStatusText { get; init; }
        public string? CredentialGuardStatusText { get; init; }
        public string? VbsStatusText { get; init; }
        public string? MemoryIntegrityStatusText { get; init; }
    }

    private sealed class SystemRuntimePayload
    {
        public string? UptimeText { get; init; }
        public string? LastBootText { get; init; }
        public string? InstallDateText { get; init; }
        public string? PendingRebootStatusText { get; init; }
        public string? PendingRebootDetailText { get; init; }
        public string? WindowsUpdateScheduledRestartStatusText { get; init; }
        public string? WindowsUpdateScheduledRestartTimeText { get; init; }
        public string? MecmScheduledRestartTimeText { get; init; }
        public string? SessionLockStatusText { get; init; }
        public string? SessionLockedSinceText { get; init; }
    }

    private sealed class NetworkConnectivityPayload
    {
        public string? PrimaryConnectionText { get; init; }
        public string? PrimaryAdapterText { get; init; }
        public string? WiFiSsidText { get; init; }
        public string? VpnStatusText { get; init; }
        public string? VpnProviderText { get; init; }
        public bool IsCheckpointVpnDetected { get; init; }
        public string? PortAuthenticationStatusText { get; init; }
        public string? PortAuthenticationDetailText { get; init; }
    }

    private sealed class DeliveryOptimizationPayload
    {
        public bool IsAvailable { get; init; }
        public string? CapturedAtUtc { get; init; }
        public List<DeliveryOptimizationSourceStatPayload>? SourceStats { get; init; }
        public List<DeliveryOptimizationTransferPayload>? Transfers { get; init; }
        public List<string>? Notes { get; init; }
        public bool SupportsTimeRangeFiltering { get; init; }
        public string? DataStartUtc { get; init; }
        public string? DataEndUtc { get; init; }
        public List<NameValuePayload>? CurrentMetrics { get; init; }
        public List<NameValuePayload>? MonthlyMetrics { get; init; }
        public List<NameValuePayload>? Configuration { get; init; }
        public List<DeliveryOptimizationPeerStatusPayload>? PeerStatuses { get; init; }
        public List<DeliveryOptimizationJobStatusPayload>? ActiveJobs { get; init; }
    }

    private sealed class DeliveryOptimizationSourceStatPayload
    {
        public string? Source { get; init; }
        public long Bytes { get; init; }
        public int TransferCount { get; init; }
    }

    private sealed class DeliveryOptimizationTransferPayload
    {
        public string? TimestampUtc { get; init; }
        public string? Source { get; init; }
        public long Bytes { get; init; }
        public string? Description { get; init; }
    }

    private sealed class DeliveryOptimizationPeerStatusPayload
    {
        public string? Content { get; init; }
        public string? Status { get; init; }
        public int CandidateCount { get; init; }
        public int ConnectedPeerCount { get; init; }
        public long BytesFromPeers { get; init; }
        public long BytesFromHttp { get; init; }
        public string? Details { get; init; }
    }

    private sealed class DeliveryOptimizationJobStatusPayload
    {
        public string? Content { get; init; }
        public string? Status { get; init; }
        public long FileSizeBytes { get; init; }
        public long DownloadedBytes { get; init; }
        public long DownloadRateBytesPerSecond { get; init; }
        public string? Details { get; init; }
    }

    private sealed class ArtifactPayload
    {
        public string? ArtifactType { get; init; }
        public string? ArtifactPath { get; init; }
        public string? Description { get; init; }
        public string? EnrollmentId { get; init; }
        public bool IsRemovable { get; init; }
    }

    private sealed class NameValuePayload
    {
        public string? Name { get; init; }
        public string? Value { get; init; }
    }

    private class EventLogPayload
    {
        public string? TimeCreated { get; init; }
        public int Id { get; init; }
        public string? Level { get; init; }
        public int? LevelValue { get; init; }
        public string? Provider { get; init; }
        public string? Message { get; init; }
    }

    private sealed class DetailedEventLogPayload : EventLogPayload
    {
        public long? RecordId { get; init; }
        public string? Xml { get; init; }
    }

    private static MdmEventAnalysisEntry AnalyzeMdmEvent(DetailedEventLogPayload payload, string logName)
    {
        var message = payload.Message?.Trim() ?? string.Empty;
        var eventData = ExtractEventData(payload.Xml);

        var resultCode = NormalizeResultCode(
            FirstNonEmpty(
                FindField(eventData, "Result"),
                FindField(eventData, "HRESULT"),
                FindField(eventData, "HexInt1"),
                FindRegex(HexCodeRegex, message)));

        var policyName = FirstNonEmpty(
            FindField(eventData, "Policy"),
            FindField(eventData, "PolicyName"),
            FindField(eventData, "Message1"),
            FindRegex(PolicyRegex, message, captureGroup: 1));

        var area = FirstNonEmpty(
            FindField(eventData, "Area"),
            FindField(eventData, "AreaName"),
            FindField(eventData, "Message2"),
            FindRegex(AreaRegex, message, captureGroup: 1));

        var cspUri = FirstNonEmpty(
            FindField(eventData, "CspUri"),
            FindField(eventData, "PolicyPath"),
            FindField(eventData, "PolicyCspPath"),
            FindField(eventData, "Message5"),
            FindRegex(CspUriRegex, message));

        if (string.IsNullOrWhiteSpace(cspUri))
        {
            cspUri = FindRegex(CspUriRegex, payload.Xml ?? string.Empty);
        }

        var enrollmentId = FirstNonEmpty(
            FindField(eventData, "EnrollmentId"),
            FindField(eventData, "EnrollmentID"),
            FindRegex(GuidRegex, message));

        if (string.IsNullOrWhiteSpace(enrollmentId))
        {
            enrollmentId = FindRegex(GuidRegex, payload.Xml ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(policyName))
        {
            policyName = InferPolicyName(eventData, cspUri);
        }

        if (string.IsNullOrWhiteSpace(area))
        {
            area = InferArea(eventData, cspUri);
        }

        if (string.IsNullOrWhiteSpace(resultCode))
        {
            resultCode = InferResultCode(eventData, payload.Xml);
        }

        var levelValue = payload.LevelValue ?? InferLevelValue(payload.Xml);
        var isFailure = IsFailure(payload.Level, levelValue, message, resultCode);
        var severity = ResolveSeverity(payload.Level, levelValue, isFailure, resultCode, message);
        var resolvedError = ResolveErrorDescription(resultCode, message);
        var summary = BuildSummary(payload.Id, message, isFailure, resultCode, policyName, area);
        var recommendedAction = BuildRecommendedAction(resultCode, message, area, cspUri, isFailure);

        return new MdmEventAnalysisEntry(
            logName,
            ParseTimestamp(payload.TimeCreated),
            payload.RecordId,
            payload.Id,
            payload.Level ?? string.Empty,
            payload.Provider ?? string.Empty,
            severity,
            isFailure,
            summary,
            resultCode,
            resolvedError,
            policyName,
            area,
            cspUri,
            enrollmentId,
            recommendedAction,
            message);
    }

    private static MdmEventAnalysisEntry BuildSyntheticFailure(string logName, string message)
    {
        return new MdmEventAnalysisEntry(
            logName,
            DateTimeOffset.UtcNow,
            null,
            0,
            "Error",
            "WindowsClientCenter",
            MdmEventSeverity.Critical,
            true,
            "Failed to load MDM admin events.",
            string.Empty,
            message,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "Verify the local Event Log channel exists and the current process can access it.",
            message);
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
        {
            return dto;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
        {
            return new DateTimeOffset(dt);
        }

        return null;
    }

    private static Dictionary<string, string> ExtractEventData(string? xml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return result;
        }

        try
        {
            var document = XDocument.Parse(xml);
            var dataElements = document
                .Descendants()
                .Where(element => element.Name.LocalName.Equals("Data", StringComparison.OrdinalIgnoreCase));

            var index = 0;
            foreach (var data in dataElements)
            {
                var name = data.Attribute("Name")?.Value;
                var value = data.Value?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    result[name] = value;
                }
                else
                {
                    result[$"Data{index}"] = value;
                    index++;
                }
            }
        }
        catch
        {
            // Best effort: raw message analysis still works without XML parsing.
        }

        return result;
    }

    private static string FindField(IReadOnlyDictionary<string, string> values, string fieldName)
    {
        return values.TryGetValue(fieldName, out var value) ? value : string.Empty;
    }

    private static string FindRegex(Regex regex, string input, int captureGroup = 0)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var match = regex.Match(input);
        if (!match.Success)
        {
            return string.Empty;
        }

        return match.Groups.Count > captureGroup
            ? match.Groups[captureGroup].Value.Trim()
            : match.Value.Trim();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string NormalizeResultCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value.ToUpperInvariant()
            : value;
    }

    private static string InferPolicyName(IReadOnlyDictionary<string, string> eventData, string cspUri)
    {
        var message1 = FindField(eventData, "Message1");
        if (!string.IsNullOrWhiteSpace(message1) &&
            !GuidRegex.IsMatch(message1) &&
            !message1.Contains('/', StringComparison.Ordinal))
        {
            return message1.Trim();
        }

        foreach (var value in eventData.Values)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Contains('/', StringComparison.Ordinal) ||
                GuidRegex.IsMatch(value) ||
                HexCodeRegex.IsMatch(value))
            {
                continue;
            }

            if (value.Length <= 96)
            {
                return value.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(cspUri))
        {
            var segments = cspUri.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length > 0)
            {
                return segments[^1];
            }
        }

        return string.Empty;
    }

    private static string InferArea(IReadOnlyDictionary<string, string> eventData, string cspUri)
    {
        var message2 = FindField(eventData, "Message2");
        if (!string.IsNullOrWhiteSpace(message2) &&
            !message2.Contains('\\', StringComparison.Ordinal) &&
            !message2.Contains('/', StringComparison.Ordinal) &&
            !GuidRegex.IsMatch(message2))
        {
            return message2.Trim();
        }

        foreach (var value in eventData.Values)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Contains('/', StringComparison.Ordinal) ||
                GuidRegex.IsMatch(value) ||
                HexCodeRegex.IsMatch(value))
            {
                continue;
            }

            if (value.Length <= 64 &&
                !value.Equals(InferPolicyName(eventData, cspUri), StringComparison.OrdinalIgnoreCase))
            {
                return value.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(cspUri))
        {
            var normalized = cspUri.Replace("./", string.Empty, StringComparison.Ordinal);
            var marker = "/Policy/Config/";
            var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var remainder = normalized[(index + marker.Length)..];
                var segments = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (segments.Length >= 2)
                {
                    return segments[0];
                }
            }
        }

        return string.Empty;
    }

    private static string InferResultCode(IReadOnlyDictionary<string, string> eventData, string? xml)
    {
        foreach (var value in eventData.Values)
        {
            var normalized = NormalizeResultCode(value);
            if (!string.IsNullOrWhiteSpace(normalized) &&
                (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || uint.TryParse(normalized, out _)))
            {
                return normalized;
            }
        }

        return NormalizeResultCode(FindRegex(HexCodeRegex, xml ?? string.Empty));
    }

    private static int? InferLevelValue(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var document = XDocument.Parse(xml);
            var levelElement = document
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals("Level", StringComparison.OrdinalIgnoreCase));

            return int.TryParse(levelElement?.Value, out var levelValue) ? levelValue : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsFailure(string? level, int? levelValue, string message, string resultCode)
    {
        if (!string.IsNullOrWhiteSpace(resultCode) &&
            !resultCode.Equals("0x00000000", StringComparison.OrdinalIgnoreCase) &&
            !resultCode.Equals("0", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (levelValue is 1 or 2)
        {
            return true;
        }

        if (level?.Equals("Error", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        var normalizedMessage = message.ToLowerInvariant();
        return normalizedMessage.Contains("failed", StringComparison.Ordinal) ||
               normalizedMessage.Contains("error", StringComparison.Ordinal) ||
               normalizedMessage.Contains("rejected", StringComparison.Ordinal);
    }

    private static MdmEventSeverity ResolveSeverity(string? level, int? levelValue, bool isFailure, string resultCode, string message)
    {
        if (levelValue == 1)
        {
            return MdmEventSeverity.Critical;
        }

        if (levelValue == 2)
        {
            return IsCriticalFailure(resultCode, message)
                ? MdmEventSeverity.Critical
                : MdmEventSeverity.Error;
        }

        if (levelValue == 3)
        {
            return MdmEventSeverity.Warning;
        }

        if (levelValue is 0 or 4 or 5)
        {
            return MdmEventSeverity.Information;
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            var normalizedLevel = level.ToLowerInvariant();
            if (normalizedLevel.Contains("critical", StringComparison.Ordinal) ||
                normalizedLevel.Contains("krit", StringComparison.Ordinal))
            {
                return MdmEventSeverity.Critical;
            }

            if (normalizedLevel.Contains("error", StringComparison.Ordinal) ||
                normalizedLevel.Contains("fehl", StringComparison.Ordinal))
            {
                return MdmEventSeverity.Error;
            }

            if (normalizedLevel.Contains("warn", StringComparison.Ordinal))
            {
                return MdmEventSeverity.Warning;
            }

            if (normalizedLevel.Contains("info", StringComparison.Ordinal))
            {
                return MdmEventSeverity.Information;
            }
        }

        if (!string.IsNullOrWhiteSpace(resultCode) &&
            !resultCode.Equals("0x00000000", StringComparison.OrdinalIgnoreCase) &&
            !resultCode.Equals("0", StringComparison.OrdinalIgnoreCase))
        {
            return IsCriticalFailure(resultCode, message)
                ? MdmEventSeverity.Critical
                : MdmEventSeverity.Error;
        }

        if (message.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("warnung", StringComparison.OrdinalIgnoreCase))
        {
            return MdmEventSeverity.Warning;
        }

        return MdmEventSeverity.Information;
    }

    private static bool IsCriticalFailure(string resultCode, string message)
    {
        if (resultCode.Equals("0x8018002B", StringComparison.OrdinalIgnoreCase) ||
            resultCode.Equals("0x80070005", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return message.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("fatal", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("critical", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveErrorDescription(string resultCode, string message)
    {
        if (string.IsNullOrWhiteSpace(resultCode))
        {
            return message.Contains("failed", StringComparison.OrdinalIgnoreCase)
                ? "The event reports a policy processing failure but did not include a distinct result code."
                : "No result code was present in the event.";
        }

        return ErrorCodeResolver.ResolveDescription(resultCode);
    }

    private static string BuildSummary(int eventId, string message, bool isFailure, string resultCode, string policyName, string area)
    {
        if (!isFailure)
        {
            return !string.IsNullOrWhiteSpace(policyName)
                ? $"Policy '{policyName}' processed successfully."
                : $"MDM event {eventId} completed successfully.";
        }

        if (!string.IsNullOrWhiteSpace(policyName))
        {
            return $"Policy '{policyName}' failed to apply.";
        }

        if (!string.IsNullOrWhiteSpace(area))
        {
            return $"Policy processing failed in area '{area}'.";
        }

        if (!string.IsNullOrWhiteSpace(resultCode))
        {
            return $"MDM policy processing failed with {resultCode}.";
        }

        if (message.Contains("policy", StringComparison.OrdinalIgnoreCase))
        {
            return "MDM policy processing failed.";
        }

        return $"MDM event {eventId} indicates a processing failure.";
    }

    private static string BuildRecommendedAction(string resultCode, string message, string area, string cspUri, bool isFailure)
    {
        if (!isFailure)
        {
            return "No action needed.";
        }

        return resultCode.ToUpperInvariant() switch
        {
            "0X80070002" => "Verify that the referenced file, path, or resource exists on the client and that the policy value points to a valid location.",
            "0X80070003" => "Verify that the target path exists and that the policy is targeting the correct filesystem or registry location.",
            "0X80070005" => "Verify device context, required privileges, and whether the setting must apply in device scope instead of user scope.",
            "0X8007000D" => "Verify the policy value type and data format. Invalid OMA-URI or malformed values commonly trigger this result.",
            "0X80070032" => "The target setting is not supported on this Windows edition or build. Verify CSP support on the client.",
            "0X80070057" => "Review the policy parameter/value pair. This usually indicates an invalid setting value or malformed CSP input.",
            "0X8018002B" => "Check for stale enrollment artifacts and consider running Re-enroll Preview before retrying policy application.",
            _ when message.Contains("licens", StringComparison.OrdinalIgnoreCase) =>
                "Verify Windows edition, Intune licensing, and whether the setting requires Enterprise or Education SKU support.",
            _ when message.Contains("rejected", StringComparison.OrdinalIgnoreCase) =>
                "Open the raw event details and verify the policy area, CSP URI, and current client state before retrying sync.",
            _ => BuildContextAwareRecommendation(area, cspUri)
        };
    }

    private static string BuildContextAwareRecommendation(string area, string cspUri)
    {
        if (!string.IsNullOrWhiteSpace(cspUri))
        {
            return $"Review the CSP path '{cspUri}' on the client and compare the configured value with what the local policy engine supports.";
        }

        if (!string.IsNullOrWhiteSpace(area))
        {
            return $"Review recent MDM events for area '{area}' and compare the setting with the client's local state before retrying sync.";
        }

        return "Open the raw MDM event details, identify the failing CSP or policy area, and compare it with the current local client state.";
    }

    private async ValueTask<LocalIntuneSnapshot> EnrichPatchStatusAsync(
        LocalIntuneSnapshot snapshot,
        CancellationToken cancellationToken,
        List<string>? timings = null)
    {
        if (!TryParseBuild(snapshot.WindowsBuildText, out var deviceBuild))
        {
            return snapshot with
            {
                PatchStatusLevel = "Unknown",
                PatchStatusText = "Patch status could not be determined because the OS build is missing."
            };
        }

        var sourceUrl = deviceBuild.Major >= 22000 ? Windows11ReleaseHealthUrl : Windows10ReleaseHealthUrl;
        string html;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
            var downloadTimer = Stopwatch.StartNew();
            html = await httpClient.GetStringAsync(sourceUrl, timeoutCts.Token);
            timings?.Add($"Patch status release history download completed in {downloadTimer.ElapsedMilliseconds} ms.");
        }
        catch
        {
            return snapshot with
            {
                PatchStatusLevel = "Unknown",
                PatchStatusText = "Patch status could not be loaded from Microsoft Update History."
            };
        }

        var parseTimer = Stopwatch.StartNew();
        var sectionRows = ParseReleaseRowsForBaseBuild(html, deviceBuild.Major);
        timings?.Add($"Patch status release history parsing completed in {parseTimer.ElapsedMilliseconds} ms.");
        if (sectionRows.Count == 0)
        {
            return snapshot with
            {
                PatchStatusLevel = "Unknown",
                PatchStatusText = "Patch status is unavailable because no matching build series was found in Microsoft Update History."
            };
        }

        var latest = sectionRows[0];
        var deviceRow = sectionRows.FirstOrDefault(item => item.Build.Major == deviceBuild.Major && item.Build.Revision == deviceBuild.Revision);
        var hasDeviceRow = deviceRow.Build.Major > 0;
        var latestMonth = $"{latest.ReleaseDate:yyyy-MM}";
        var evaluationTimer = Stopwatch.StartNew();

        if (hasDeviceRow)
        {
            var monthsBehind = ((latest.ReleaseDate.Year - deviceRow.ReleaseDate.Year) * 12) + latest.ReleaseDate.Month - deviceRow.ReleaseDate.Month;
            var level = monthsBehind switch
            {
                <= 0 => "Green",
                1 => "Yellow",
                _ => "Red"
            };

            var text = monthsBehind <= 0
                ? $"Current patch level ({latestMonth}, KB{latest.KbArticle}, build {latest.Build.Major}.{latest.Build.Revision})."
                : $"Patch level is {deviceRow.ReleaseDate:yyyy-MM} (KB{deviceRow.KbArticle}, build {deviceRow.Build.Major}.{deviceRow.Build.Revision}); latest is {latestMonth}.";

            var result = snapshot with
            {
                PatchStatusLevel = level,
                PatchStatusText = text
            };
            timings?.Add($"Patch status evaluation completed in {evaluationTimer.ElapsedMilliseconds} ms.");
            return result;
        }

        if (deviceBuild.Revision >= latest.Build.Revision)
        {
            var result = snapshot with
            {
                PatchStatusLevel = "Green",
                PatchStatusText = $"Build {snapshot.WindowsBuildText} is at least on the latest known patch level ({latestMonth}, KB{latest.KbArticle})."
            };
            timings?.Add($"Patch status evaluation completed in {evaluationTimer.ElapsedMilliseconds} ms.");
            return result;
        }

        var fallbackResult = snapshot with
        {
            PatchStatusLevel = "Red",
            PatchStatusText = $"Build {snapshot.WindowsBuildText} is older than the current patch level ({latestMonth}, KB{latest.KbArticle}, build {latest.Build.Major}.{latest.Build.Revision})."
        };
        timings?.Add($"Patch status evaluation completed in {evaluationTimer.ElapsedMilliseconds} ms.");
        return fallbackResult;
    }

    private static List<ReleaseHistoryRow> ParseReleaseRowsForBaseBuild(string html, int baseBuild)
    {
        var decoded = WebUtility.HtmlDecode(html);
        var sectionMarker = $"(OS build {baseBuild})";
        var markerIndex = decoded.IndexOf(sectionMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return [];
        }

        var sectionStart = decoded.LastIndexOf("<strong>", markerIndex, StringComparison.OrdinalIgnoreCase);
        if (sectionStart < 0)
        {
            sectionStart = markerIndex;
        }

        var sectionEnd = decoded.IndexOf("<details><summary><strong>Version", markerIndex + sectionMarker.Length, StringComparison.OrdinalIgnoreCase);
        if (sectionEnd < 0)
        {
            sectionEnd = decoded.Length;
        }

        var sectionHtml = decoded[sectionStart..sectionEnd];
        var rows = new List<ReleaseHistoryRow>();
        foreach (Match rowMatch in ReleaseRowRegex.Matches(sectionHtml))
        {
            if (!rowMatch.Success)
            {
                continue;
            }

            var buildText = rowMatch.Groups["build"].Value.Trim();
            var dateText = Regex.Replace(rowMatch.Groups["date"].Value, "<.*?>", string.Empty).Trim();
            var kbText = rowMatch.Groups["kb"].Value.Trim();
            if (!TryParseBuild(buildText, out var rowBuild) || rowBuild.Major != baseBuild)
            {
                continue;
            }

            if (!DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
            {
                continue;
            }

            rows.Add(new ReleaseHistoryRow(
                rowBuild,
                DateTime.SpecifyKind(parsedDate, DateTimeKind.Utc),
                kbText));
        }

        return rows
            .OrderByDescending(item => item.ReleaseDate)
            .ThenByDescending(item => item.Build.Revision)
            .ToList();
    }

    private static bool TryParseBuild(string? value, out BuildNumber build)
    {
        build = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 ||
            !int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision))
        {
            return false;
        }

        build = new BuildNumber(major, revision);
        return true;
    }

    private static DateTimeOffset? ParseCapturedAtUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (DateTimeOffset.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedIso))
        {
            return parsedIso;
        }

        var legacyMatch = LegacyJsonDateRegex.Match(normalized);
        if (!legacyMatch.Success || !long.TryParse(legacyMatch.Groups["ms"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixMillis))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMillis);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private readonly record struct BuildNumber(int Major, int Revision);
    private readonly record struct ReleaseHistoryRow(BuildNumber Build, DateTime ReleaseDate, string KbArticle);
}
