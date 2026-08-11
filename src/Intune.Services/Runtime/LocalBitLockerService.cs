using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class LocalBitLockerService(IPowerShellExecutor executor) : ILocalBitLockerService
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new SingleOrArrayListJsonConverterFactory());
        return options;
    }

    public async ValueTask<BitLockerHostSnapshot> GetSnapshotAsync(string host, CancellationToken cancellationToken, bool verboseDiagnostics = false)
    {
        var roundtripStopwatch = Stopwatch.StartNew();
        var execution = await executor.ExecuteForHostAsync(host, BuildSnapshotScript(verboseDiagnostics), cancellationToken);
        roundtripStopwatch.Stop();
        if (execution.ExitCode != 0)
        {
            var failureWarning = NormalizeError(execution);
            if (verboseDiagnostics)
            {
                failureWarning = $"{failureWarning} [verbose] PowerShell roundtrip: {roundtripStopwatch.ElapsedMilliseconds} ms";
            }

            return BuildFailureSnapshot(host, failureWarning);
        }

        try
        {
            var parseStopwatch = Stopwatch.StartNew();
            if (!TryParsePowerShellJsonDocument(execution.StdOut, out var document, out var warning, out var error))
            {
                return BuildFailureSnapshot(host, error);
            }

            using var _ = document;
            var payload = document.RootElement.Deserialize<BitLockerSnapshotPayload>(JsonOptions)
                          ?? throw new InvalidOperationException("BitLocker snapshot payload was empty.");
            parseStopwatch.Stop();
            var verboseLines = verboseDiagnostics
                ? new[]
                {
                    $"[verbose] PowerShell roundtrip: {roundtripStopwatch.ElapsedMilliseconds} ms",
                    $"[verbose] Snapshot parse: {parseStopwatch.ElapsedMilliseconds} ms"
                }
                : [];
            return ToSnapshot(host, payload, warning, verboseLines);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return BuildFailureSnapshot(host, $"Failed to parse BitLocker snapshot payload: {ex.Message}");
        }
    }

    public ValueTask<BitLockerActionResult> SuspendProtectionAsync(
        string host,
        string mountPoint,
        int rebootCount,
        CancellationToken cancellationToken,
        bool verboseDiagnostics = false)
    {
        var clampedRebootCount = Math.Clamp(rebootCount, 0, 15);
        return ExecuteActionAsync(
            host,
            BuildSuspendProtectionScript(mountPoint, clampedRebootCount),
            "Failed to suspend BitLocker protection.",
            cancellationToken,
            verboseDiagnostics);
    }

    public ValueTask<BitLockerActionResult> ResumeProtectionAsync(
        string host,
        string mountPoint,
        CancellationToken cancellationToken,
        bool verboseDiagnostics = false)
    {
        return ExecuteActionAsync(
            host,
            BuildResumeProtectionScript(mountPoint),
            "Failed to resume BitLocker protection.",
            cancellationToken,
            verboseDiagnostics);
    }

    public ValueTask<BitLockerActionResult> AddRecoveryPasswordProtectorAsync(
        string host,
        string mountPoint,
        CancellationToken cancellationToken,
        bool verboseDiagnostics = false)
    {
        return ExecuteActionAsync(
            host,
            BuildAddRecoveryPasswordProtectorScript(mountPoint),
            "Failed to add a recovery-password protector.",
            cancellationToken,
            verboseDiagnostics);
    }

    public ValueTask<BitLockerActionResult> RemoveRecoveryPasswordProtectorAsync(
        string host,
        string mountPoint,
        string protectorId,
        CancellationToken cancellationToken,
        bool verboseDiagnostics = false)
    {
        return ExecuteActionAsync(
            host,
            BuildRemoveRecoveryPasswordProtectorScript(mountPoint, protectorId),
            "Failed to remove the recovery-password protector.",
            cancellationToken,
            verboseDiagnostics);
    }

    public ValueTask<BitLockerActionResult> BackupRecoveryPasswordAsync(
        string host,
        string mountPoint,
        string protectorId,
        CancellationToken cancellationToken,
        bool verboseDiagnostics = false)
    {
        return ExecuteActionAsync(
            host,
            BuildBackupRecoveryPasswordScript(mountPoint, protectorId),
            "Failed to back up the recovery-password protector.",
            cancellationToken,
            verboseDiagnostics);
    }

    public ValueTask<BitLockerActionResult> RotateRecoveryPasswordAsync(
        string host,
        string mountPoint,
        string protectorId,
        CancellationToken cancellationToken,
        bool verboseDiagnostics = false)
    {
        return ExecuteActionAsync(
            host,
            BuildRotateRecoveryPasswordScript(mountPoint, protectorId),
            "Failed to rotate the recovery-password protector.",
            cancellationToken,
            verboseDiagnostics);
    }

    private async ValueTask<BitLockerActionResult> ExecuteActionAsync(
        string host,
        string script,
        string defaultFailureMessage,
        CancellationToken cancellationToken,
        bool verboseDiagnostics)
    {
        var roundtripStopwatch = Stopwatch.StartNew();
        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        roundtripStopwatch.Stop();
        if (execution.ExitCode != 0)
        {
            var details = verboseDiagnostics
                ? new[] { $"[verbose] PowerShell roundtrip: {roundtripStopwatch.ElapsedMilliseconds} ms" }
                : null;
            return BitLockerActionResult.Fail(
                $"{defaultFailureMessage} {NormalizeError(execution)}".Trim(),
                "bitlocker_action_failed",
                details);
        }

        try
        {
            var parseStopwatch = Stopwatch.StartNew();
            if (!TryParsePowerShellJsonDocument(execution.StdOut, out var document, out var parseWarning, out var error))
            {
                return BitLockerActionResult.Fail(error, "bitlocker_action_parse_failed");
            }

            using var documentScope = document;
            var payload = document.RootElement.Deserialize<BitLockerActionPayload>(JsonOptions)
                          ?? throw new InvalidOperationException("BitLocker action payload was empty.");
            parseStopwatch.Stop();

            var details = (payload.Details ?? [])
                .Where(static detail => !string.IsNullOrWhiteSpace(detail))
                .Select(static detail => detail.Trim())
                .ToList();

            if (verboseDiagnostics)
            {
                details.Add($"[verbose] PowerShell roundtrip: {roundtripStopwatch.ElapsedMilliseconds} ms");
                details.Add($"[verbose] Action parse: {parseStopwatch.ElapsedMilliseconds} ms");
            }

            if (payload.Success)
            {
                return BitLockerActionResult.Ok(
                    AppendParseWarning(payload.Message ?? "BitLocker action completed successfully.", parseWarning),
                    payload.NewProtectorId,
                    details);
            }

            if (payload.Warning)
            {
                return BitLockerActionResult.Warn(
                    AppendParseWarning(payload.Message ?? "BitLocker action completed with warnings.", parseWarning),
                    payload.ErrorCode,
                    payload.NewProtectorId,
                    details);
            }

            return BitLockerActionResult.Fail(
                AppendParseWarning(payload.Message ?? defaultFailureMessage, parseWarning),
                payload.ErrorCode,
                details);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return BitLockerActionResult.Fail(
                $"Failed to parse BitLocker action payload: {ex.Message}",
                "bitlocker_action_parse_failed");
        }
    }

    private static BitLockerHostSnapshot ToSnapshot(string host, BitLockerSnapshotPayload payload, string warning, IReadOnlyList<string>? verboseLines = null)
    {
        var capabilityWarnings = new List<string>();
        if (payload.Capabilities?.Warnings is { Count: > 0 })
        {
            capabilityWarnings.AddRange(payload.Capabilities.Warnings.Where(static item => !string.IsNullOrWhiteSpace(item)).Select(static item => item.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(warning))
        {
            capabilityWarnings.Add(warning);
        }

        if (payload.DiagnosticsTimings is { Count: > 0 })
        {
            capabilityWarnings.AddRange(payload.DiagnosticsTimings.Where(static item => !string.IsNullOrWhiteSpace(item)).Select(static item => item.Trim()));
        }

        if (verboseLines is { Count: > 0 })
        {
            capabilityWarnings.AddRange(verboseLines.Where(static item => !string.IsNullOrWhiteSpace(item)).Select(static item => item.Trim()));
        }

        var capabilities = new BitLockerCapabilitySnapshot(
            payload.Capabilities?.IsBitLockerCommandAvailable ?? false,
            payload.Capabilities?.IsAdministrator ?? false,
            payload.Capabilities?.SupportsSuspendProtection ?? false,
            payload.Capabilities?.SupportsResumeProtection ?? false,
            payload.Capabilities?.SupportsRecoveryPasswordProtectorOperations ?? false,
            payload.Capabilities?.SupportsBackupToAd ?? false,
            payload.Capabilities?.SupportsBackupToEntra ?? false,
            payload.Capabilities?.IsDomainJoined ?? false,
            payload.Capabilities?.IsEntraJoined ?? false,
            capabilityWarnings);

        var policies = (payload.Policies ?? [])
            .Select(static item => new BitLockerPolicySettingSnapshot(
                string.IsNullOrWhiteSpace(item.SettingName) ? "Unknown" : item.SettingName.Trim(),
                string.IsNullOrWhiteSpace(item.ValueText) ? "-" : item.ValueText.Trim(),
                string.IsNullOrWhiteSpace(item.Source) ? "Unknown" : item.Source.Trim(),
                string.IsNullOrWhiteSpace(item.Category) ? "General" : item.Category.Trim(),
                string.IsNullOrWhiteSpace(item.SourcePath) ? "-" : item.SourcePath.Trim(),
                ResolvePolicyValueMeaning(item.SettingName, item.ValueText, item.SourcePath)))
            .OrderBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SettingName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var volumes = (payload.Volumes ?? [])
            .Select(ToVolume)
            .OrderBy(volume => volume.MountPoint, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var encryptedCount = volumes.Count(static volume => volume.IsEncrypted);
        var protectedCount = volumes.Count(static volume => volume.IsProtectionOn);
        var suspendedCount = volumes.Count(static volume => volume.IsProtectionSuspended);
        var warningCount = volumes.Count(static volume => volume.HealthLevel.Equals("Yellow", StringComparison.OrdinalIgnoreCase));
        var errorCount = volumes.Count(static volume => volume.HealthLevel.Equals("Red", StringComparison.OrdinalIgnoreCase));
        var overallHealth = errorCount > 0
            ? "Red"
            : warningCount > 0 || capabilityWarnings.Count > 0
                ? "Yellow"
                : volumes.Length == 0
                    ? "Unknown"
                    : "Green";

        return new BitLockerHostSnapshot(
            host,
            string.IsNullOrWhiteSpace(payload.MachineName) ? host : payload.MachineName.Trim(),
            ParseCapturedAtUtc(payload.CapturedAtUtc) ?? DateTimeOffset.UtcNow,
            capabilities,
            policies,
            payload.HasIntunePolicies,
            payload.HasGpoPolicies,
            payload.HasMecmPolicies,
            volumes,
            encryptedCount,
            protectedCount,
            suspendedCount,
            warningCount,
            errorCount,
            overallHealth);
    }

    private static BitLockerVolumeSnapshot ToVolume(BitLockerVolumePayload payload)
    {
        var backupTargetAssessments = (payload.BackupTargetAssessments ?? [])
            .Select(static item => new BitLockerBackupTargetAssessmentSnapshot(
                string.IsNullOrWhiteSpace(item.Target) ? "Unknown" : item.Target.Trim(),
                item.IsConfigured,
                item.HasSuccessEvidence,
                item.HasFailureEvidence,
                string.IsNullOrWhiteSpace(item.Assessment) ? "Unknown" : item.Assessment.Trim(),
                string.IsNullOrWhiteSpace(item.EvidenceText) ? "-" : item.EvidenceText.Trim()))
            .OrderBy(item => item.Target, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var protectors = (payload.Protectors ?? [])
            .Select(item => new BitLockerProtectorSnapshot(
                string.IsNullOrWhiteSpace(item.ProtectorId) ? "-" : item.ProtectorId.Trim(),
                string.IsNullOrWhiteSpace(item.ProtectorType) ? "Unknown" : item.ProtectorType.Trim(),
                string.IsNullOrWhiteSpace(item.FriendlyLabel) ? "Unknown" : item.FriendlyLabel.Trim(),
                item.IsRecoveryPassword,
                item.IsRemovable,
                string.IsNullOrWhiteSpace(item.BackupTargetsText) ? "No backup target available." : item.BackupTargetsText.Trim(),
                string.IsNullOrWhiteSpace(item.LastActionStatusText) ? string.Empty : item.LastActionStatusText.Trim()))
            .OrderBy(item => item.ProtectorType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ProtectorId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var health = string.IsNullOrWhiteSpace(payload.HealthLevel)
            ? payload.IsProtectionOn
                ? "Green"
                : payload.IsProtectionSuspended
                    ? "Yellow"
                    : payload.IsEncrypted
                        ? "Yellow"
                        : "Red"
            : payload.HealthLevel.Trim();

        return new BitLockerVolumeSnapshot(
            string.IsNullOrWhiteSpace(payload.MountPoint) ? "-" : payload.MountPoint.Trim(),
            string.IsNullOrWhiteSpace(payload.VolumeType) ? "Unknown" : payload.VolumeType.Trim(),
            string.IsNullOrWhiteSpace(payload.ProtectionStatusText) ? "Unknown" : payload.ProtectionStatusText.Trim(),
            string.IsNullOrWhiteSpace(payload.VolumeStatusText) ? "Unknown" : payload.VolumeStatusText.Trim(),
            string.IsNullOrWhiteSpace(payload.LockStatusText) ? "Unknown" : payload.LockStatusText.Trim(),
            Math.Max(0, payload.EncryptionPercentage),
            string.IsNullOrWhiteSpace(payload.EncryptionMethodText) ? "Unknown" : payload.EncryptionMethodText.Trim(),
            string.IsNullOrWhiteSpace(payload.AutoUnlockText) ? "Unknown" : payload.AutoUnlockText.Trim(),
            payload.SuspendRebootCount,
            health,
            string.IsNullOrWhiteSpace(payload.ComplianceStatusText) ? "Unknown" : payload.ComplianceStatusText.Trim(),
            string.IsNullOrWhiteSpace(payload.ComplianceDetailsText) ? "No compliance event evidence was collected." : payload.ComplianceDetailsText.Trim(),
            string.IsNullOrWhiteSpace(payload.BackupEligibilityText) ? "No backup target available." : payload.BackupEligibilityText.Trim(),
            string.IsNullOrWhiteSpace(payload.ConfiguredBackupTargetsText) ? "Not configured" : payload.ConfiguredBackupTargetsText.Trim(),
            string.IsNullOrWhiteSpace(payload.BackupAssessmentText) ? "No assessment available." : payload.BackupAssessmentText.Trim(),
            backupTargetAssessments,
            payload.IsEncrypted,
            payload.IsProtectionOn,
            payload.IsProtectionSuspended,
            protectors);
    }

    private static BitLockerHostSnapshot BuildFailureSnapshot(string host, string warning)
    {
        return new BitLockerHostSnapshot(
            host,
            host,
            DateTimeOffset.UtcNow,
            new BitLockerCapabilitySnapshot(
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                [warning]),
            [],
            false,
            false,
            false,
            [],
            0,
            0,
            0,
            0,
            0,
            "Red");
    }

    private static string AppendParseWarning(string message, string warning)
    {
        return string.IsNullOrWhiteSpace(warning)
            ? message
            : $"{message} {warning}";
    }

    private static string NormalizeError(PowershellExecutionResult execution)
    {
        var value = string.IsNullOrWhiteSpace(execution.StdErr) ? execution.StdOut : execution.StdErr;
        return string.IsNullOrWhiteSpace(value)
            ? $"PowerShell exited with code {execution.ExitCode}."
            : value.Trim();
    }

    private static string ResolvePolicyValueMeaning(string? settingName, string? rawValue, string? sourcePath)
    {
        var normalizedSettingName = string.IsNullOrWhiteSpace(settingName) ? string.Empty : settingName.Trim();
        var normalizedValue = string.IsNullOrWhiteSpace(rawValue) ? string.Empty : rawValue.Trim();
        var normalizedSourcePath = string.IsNullOrWhiteSpace(sourcePath) ? string.Empty : sourcePath.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return string.Empty;
        }

        if (bool.TryParse(normalizedValue, out var boolValue))
        {
            if (normalizedSettingName.Contains("Require", StringComparison.OrdinalIgnoreCase))
            {
                return boolValue ? "Required" : "Not required";
            }

            if (normalizedSettingName.Contains("Allow", StringComparison.OrdinalIgnoreCase))
            {
                return boolValue ? "Allowed" : "Not allowed";
            }

            if (normalizedSettingName.Contains("Disable", StringComparison.OrdinalIgnoreCase))
            {
                return boolValue ? "Disabled" : "Not disabled";
            }

            return boolValue ? "Enabled" : "Disabled";
        }

        if (normalizedValue.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
        {
            if (normalizedSettingName.Contains("RequireDeviceEncryption", StringComparison.OrdinalIgnoreCase))
            {
                return "Encryption required";
            }

            return "Policy enabled";
        }

        if (normalizedValue.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            if (normalizedSettingName.Contains("RequireDeviceEncryption", StringComparison.OrdinalIgnoreCase))
            {
                return "Encryption not required";
            }

            return "Policy disabled";
        }

        if (!int.TryParse(normalizedValue, out var numericValue))
        {
            return string.Empty;
        }

        if (normalizedSettingName.Contains("EncryptionMethod", StringComparison.OrdinalIgnoreCase))
        {
            return numericValue switch
            {
                0 => "Use system default",
                1 => "AES-CBC 128-bit with diffuser",
                2 => "AES-CBC 256-bit with diffuser",
                3 => "AES-CBC 128-bit",
                4 => "AES-CBC 256-bit",
                6 => "XTS-AES 128-bit",
                7 => "XTS-AES 256-bit",
                _ => string.Empty
            };
        }

        if (normalizedSettingName.EndsWith("EncryptionType", StringComparison.OrdinalIgnoreCase) ||
            normalizedSettingName.Equals("SystemDrivesEncryptionType", StringComparison.OrdinalIgnoreCase) ||
            normalizedSettingName.Equals("FixedDrivesEncryptionType", StringComparison.OrdinalIgnoreCase) ||
            normalizedSettingName.Equals("RemovableDrivesEncryptionType", StringComparison.OrdinalIgnoreCase))
        {
            return numericValue switch
            {
                0 => "Allow user choice",
                1 => "Require full encryption",
                2 => "Require used-space-only encryption",
                _ => string.Empty
            };
        }

        if (normalizedSettingName.Contains("ConfigureRecoveryPasswordRotation", StringComparison.OrdinalIgnoreCase))
        {
            return numericValue switch
            {
                0 => "Rotation off",
                1 => "Rotate on for Entra-joined devices",
                2 => "Rotate on for Entra-joined and hybrid devices",
                _ => string.Empty
            };
        }

        if (normalizedSettingName.Contains("AllowWarningForOtherDiskEncryption", StringComparison.OrdinalIgnoreCase))
        {
            return numericValue switch
            {
                0 => "No warning prompt",
                1 => "Warning prompt allowed",
                _ => string.Empty
            };
        }

        if (normalizedSettingName.Contains("AllowStandardUserEncryption", StringComparison.OrdinalIgnoreCase))
        {
            return numericValue switch
            {
                0 => "Standard users cannot start encryption",
                1 => "Standard users can start enforced encryption",
                _ => string.Empty
            };
        }

        if (normalizedSettingName.Contains("UseEnhancedPin", StringComparison.OrdinalIgnoreCase) ||
            normalizedSettingName.Contains("EnhancedPIN", StringComparison.OrdinalIgnoreCase))
        {
            return numericValue switch
            {
                0 => "Numeric PIN only",
                1 => "Enhanced PIN allowed",
                _ => string.Empty
            };
        }

        if (normalizedSettingName.Contains("UseTPM", StringComparison.OrdinalIgnoreCase) ||
            normalizedSettingName.Contains("ConfigureTPM", StringComparison.OrdinalIgnoreCase) ||
            normalizedSettingName.Contains("UsePIN", StringComparison.OrdinalIgnoreCase) ||
            normalizedSettingName.Contains("UseTPMPIN", StringComparison.OrdinalIgnoreCase) ||
            normalizedSettingName.Contains("UseTPMKey", StringComparison.OrdinalIgnoreCase) ||
            normalizedSettingName.Contains("UseTPMKeyPIN", StringComparison.OrdinalIgnoreCase))
        {
            return numericValue switch
            {
                0 => "Not allowed",
                1 => "Required",
                2 => "Allowed",
                _ => string.Empty
            };
        }

        if (normalizedSettingName.Contains("RecoveryPasswordUsage", StringComparison.OrdinalIgnoreCase) ||
            normalizedSettingName.Contains("RecoveryKeyUsage", StringComparison.OrdinalIgnoreCase))
        {
            return numericValue switch
            {
                0 => "Not allowed",
                1 => "Required",
                2 => "Allowed",
                _ => string.Empty
            };
        }

        if (normalizedSettingName.Contains("ActiveDirectoryBackupDropDown", StringComparison.OrdinalIgnoreCase))
        {
            return numericValue switch
            {
                0 => "Do not back up",
                1 => "Require backup",
                2 => "Allow backup",
                _ => string.Empty
            };
        }

        if (normalizedSettingName.Contains("PrebootRecoveryInfo", StringComparison.OrdinalIgnoreCase))
        {
            return numericValue switch
            {
                0 => "Recovery options and message",
                1 => "Recovery options only",
                2 => "Recovery message only",
                3 => "Neither shown before boot",
                _ => string.Empty
            };
        }

        if (normalizedSettingName.Contains("MinimumPINLength", StringComparison.OrdinalIgnoreCase))
        {
            return $"Minimum length {numericValue}";
        }

        if (normalizedSettingName.Contains("Encryption", StringComparison.OrdinalIgnoreCase) &&
            normalizedSourcePath.Contains(@"\Microsoft\PolicyManager\", StringComparison.OrdinalIgnoreCase))
        {
            return numericValue switch
            {
                0 => "Not enforced",
                1 => "Enforced",
                _ => string.Empty
            };
        }

        return string.Empty;
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
            warning = $"BitLocker output contained additional console text and was normalized (prefix chars: {prefixLength}, suffix chars: {suffixLength}).";
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
        string value,
        int startIndex,
        out string jsonText,
        out int prefixLength,
        out int suffixLength)
    {
        jsonText = string.Empty;
        prefixLength = startIndex;
        suffixLength = 0;

        if (startIndex < 0 || startIndex >= value.Length)
        {
            return false;
        }

        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;
        for (var i = startIndex; i < value.Length; i++)
        {
            var current = value[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current is '{' or '[')
            {
                stack.Push(current);
                continue;
            }

            if (current is '}' or ']')
            {
                if (stack.Count == 0)
                {
                    return false;
                }

                var opener = stack.Pop();
                if ((opener == '{' && current != '}') || (opener == '[' && current != ']'))
                {
                    return false;
                }

                if (stack.Count == 0)
                {
                    jsonText = value[startIndex..(i + 1)];
                    suffixLength = value.Length - i - 1;
                    return true;
                }
            }
        }

        return false;
    }

    private static DateTimeOffset? ParseCapturedAtUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : null;
    }

    private static string EscapePowerShellString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string BuildSnapshotScript(bool verboseDiagnostics)
    {
        var verboseLiteral = verboseDiagnostics ? "$true" : "$false";
        return $$"""
        $verboseDiagnostics = {{verboseLiteral}}
        $timings = New-Object System.Collections.Generic.List[string]
        function Add-VerboseTiming([string]$name, [long]$elapsedMilliseconds) {
          if (-not $verboseDiagnostics) { return }
          $timings.Add('[verbose] ' + $name + ': ' + [string]$elapsedMilliseconds + ' ms') | Out-Null
        }
        $overallStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        function Convert-ToDisplayString($value) {
          if ($null -eq $value) { return '' }
          if ($value -is [bool]) { return $(if ($value) { 'Enabled' } else { 'Disabled' }) }
          if ($value -is [Array]) {
            return ((@($value) | ForEach-Object { Convert-ToDisplayString $_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join '; ')
          }
          if ($value -is [DateTime]) { return ([DateTime]$value).ToUniversalTime().ToString('o') }
          if ($value -is [DateTimeOffset]) { return ([DateTimeOffset]$value).ToUniversalTime().ToString('o') }
          return ([string]$value).Trim()
        }

        function Get-PolicyCategory([string]$path, [string]$settingName) {
          $normalizedPath = [string]$path
          $normalizedName = [string]$settingName
          if ($normalizedPath -match '(?i)OperatingSystemDrives|OsDrive' -or $normalizedName -match '(?i)WithXtsOs|OsRecovery|OsActiveDirectory|OsPassphrase|OsAllow|OsRequire') { return 'Operating system drive' }
          if ($normalizedPath -match '(?i)FixedDataDrives|FixedDrive|FDV' -or $normalizedName -match '(?i)WithXtsFdv|FdvActiveDirectory|FdvRecovery|FdvPassphrase|FdvAllow|FdvRequire') { return 'Fixed data drive' }
          if ($normalizedPath -match '(?i)RemovableDataDrives|RemovableDrive|RDV' -or $normalizedName -match '(?i)WithXtsRdv|RdvActiveDirectory|RdvRecovery|RdvPassphrase|RdvAllow|RdvRequire') { return 'Removable drive' }
          if ($normalizedPath -match '(?i)Recovery' -or $normalizedName -match '(?i)Recovery|Backup|KeyPackage|RecoveryKey|RecoveryPassword') { return 'Recovery' }
          if ($normalizedName -match '(?i)EncryptionMethod|EncryptDevice|Cipher') { return 'Encryption' }
          if ($normalizedName -match '(?i)Pin|Startup|Tpm|Preboot|Authentication') { return 'Startup authentication' }
          return 'General'
        }

        function Normalize-PolicySourcePath([string]$path) {
          if ([string]::IsNullOrWhiteSpace($path)) { return '' }

          $normalized = $path.Trim()
          $normalized = $normalized -replace '^Microsoft\.PowerShell\.Core\\Registry::HKEY_LOCAL_MACHINE', 'HKLM:'
          $normalized = $normalized -replace '^Microsoft\.PowerShell\.Core\\Registry::HKEY_CURRENT_USER', 'HKCU:'
          $normalized = $normalized -replace '^Registry::HKEY_LOCAL_MACHINE', 'HKLM:'
          $normalized = $normalized -replace '^Registry::HKEY_CURRENT_USER', 'HKCU:'
          return $normalized
        }

        function Add-PolicyObject($collection, [string]$settingName, [string]$valueText, [string]$source, [string]$sourcePath) {
          if ([string]::IsNullOrWhiteSpace($settingName) -or [string]::IsNullOrWhiteSpace($valueText)) { return }
          $normalizedSourcePath = Normalize-PolicySourcePath $sourcePath
          $collection.Add([ordered]@{
            SettingName = $settingName
            ValueText = $valueText
            Source = $source
            Category = Get-PolicyCategory $normalizedSourcePath $settingName
            SourcePath = $normalizedSourcePath
          }) | Out-Null
        }

        function Get-ConfiguredRegistryPolicies([string]$rootPath, [string]$source, [string[]]$ignoredPropertyNames) {
          $items = New-Object System.Collections.Generic.List[object]
          if (-not (Test-Path -LiteralPath $rootPath)) { return $items }

          $stack = New-Object System.Collections.Generic.Stack[string]
          $stack.Push($rootPath)

          while ($stack.Count -gt 0) {
            $current = $stack.Pop()
            $leaf = Split-Path -Path $current -Leaf

            try {
              $properties = (Get-ItemProperty -LiteralPath $current -ErrorAction SilentlyContinue).PSObject.Properties |
                Where-Object { $_.Name -notlike 'PS*' -and $_.Name -ne '(default)' }

              foreach ($property in $properties) {
                if ($ignoredPropertyNames -contains $property.Name) {
                  continue
                }

                $valueText = Convert-ToDisplayString $property.Value
                if ([string]::IsNullOrWhiteSpace($valueText)) {
                  continue
                }

                $settingName =
                  if (($property.Name -eq 'value' -or $property.Name -eq 'Value') -and -not [string]::IsNullOrWhiteSpace($leaf)) {
                    $leaf
                  }
                  elseif ($current -eq $rootPath) {
                    $property.Name
                  }
                  else {
                    "$leaf.$($property.Name)"
                  }

                Add-PolicyObject $items $settingName $valueText $source $current
              }
            } catch {
            }

            foreach ($child in @(Get-ChildItem -LiteralPath $current -ErrorAction SilentlyContinue | Where-Object { $_.PSIsContainer })) {
              $stack.Push($child.PSPath)
            }
          }

          return $items
        }

        function Get-MecmBitLockerPolicies() {
          $items = New-Object System.Collections.Generic.List[object]
          $ccmService = Get-Service -Name 'CcmExec' -ErrorAction SilentlyContinue
          if ($null -eq $ccmService) {
            return $items
          }

          $namespace = 'root\ccm\policy\machine\actualconfig'
          try {
            $classes = @(Get-CimClass -Namespace $namespace -ErrorAction Stop | Where-Object { $_.CimClassName -match '(?i)BitLocker|MBAM|FVE' })
            foreach ($class in $classes) {
              foreach ($instance in @(Get-CimInstance -Namespace $namespace -ClassName $class.CimClassName -ErrorAction SilentlyContinue)) {
                foreach ($property in @($instance.CimInstanceProperties)) {
                  if ($property.Name -match '^(PSComputerName|Cim(Class|System)Properties|Class|InstanceID|InstanceKey|Policy(ID|Instance|RuleID|Source)|SiteSettingsKey|Version|__.*)$') {
                    continue
                  }

                  $valueText = Convert-ToDisplayString $property.Value
                  if ([string]::IsNullOrWhiteSpace($valueText)) {
                    continue
                  }

                  Add-PolicyObject $items "$($class.CimClassName).$($property.Name)" $valueText 'Configuration Manager' "$namespace\$($class.CimClassName)"
                }
              }
            }
          } catch {
            $warnings.Add('Failed to query Configuration Manager BitLocker policies: ' + $_.Exception.Message) | Out-Null
          }

          return $items
        }

        function Test-DsregYes([string]$raw, [string]$fieldName) {
          if ([string]::IsNullOrWhiteSpace($raw) -or [string]::IsNullOrWhiteSpace($fieldName)) { return $false }
          $pattern = '(?m)^\s*' + [regex]::Escape($fieldName) + '\s*[:=]\s*(YES|JA|TRUE|WAHR)\s*$'
          return [regex]::IsMatch($raw, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }

        function Test-TruthyPolicyValue($value) {
          if ($null -eq $value) { return $false }
          if ($value -is [bool]) { return [bool]$value }
          $text = ([string]$value).Trim()
          if ([string]::IsNullOrWhiteSpace($text)) { return $false }
          $number = 0
          if ([int]::TryParse($text, [ref]$number)) { return $number -gt 0 }
          return $text -match '^(?i:true|yes|enabled|required|allow|allowed|present)$'
        }

        function Get-VolumePolicyPrefix([string]$volumeType) {
          if ($volumeType -match '(?i)OperatingSystem') { return 'OS' }
          if ($volumeType -match '(?i)FixedData') { return 'FDV' }
          if ($volumeType -match '(?i)Removable') { return 'RDV' }
          return ''
        }

        function Test-PolicyNameMatch([string]$settingName, [string[]]$candidates) {
          if ([string]::IsNullOrWhiteSpace($settingName)) { return $false }
          foreach ($candidate in @($candidates)) {
            if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
            $escaped = [regex]::Escape($candidate)
            if (
              $settingName -ieq $candidate -or
              $settingName -ilike "$candidate.*" -or
              $settingName -ilike "*.$candidate" -or
              $settingName -ilike "*.$candidate.*" -or
              $settingName -ilike "*${candidate}_*" -or
              [regex]::IsMatch($settingName, "(?i)(^|[._])$escaped([._].*)?$")
            ) {
              return $true
            }
          }

          return $false
        }

        function Test-IsEntraRecoveryPolicy([string]$settingName, [string]$volumeType) {
          if ([string]::IsNullOrWhiteSpace($settingName)) { return $false }
          $candidates = switch -Regex ($volumeType) {
            '(?i)OperatingSystem' { @('SystemDrivesRecoveryOptions', 'SystemDrivesRecoveryOptionsDropDown') }
            '(?i)FixedData' { @('FixedDrivesRecoveryOptions', 'FixedDrivesRecoveryOptionsDropDown') }
            '(?i)Removable' { @('RemovableDrivesRecoveryOptions', 'RemovableDrivesRecoveryOptionsDropDown') }
            default { @() }
          }

          $candidates += @(
            'ConfigureRecoveryPasswordRotation',
            'RecoveryInformationToStore',
            'StoreRecoveryInformationInAzureAD',
            'StoreRecoveryInformationToCloud',
            'BackupRecoveryInformationToCloud',
            'RequireDeviceEncryption')

          foreach ($candidate in $candidates) {
            if (Test-PolicyNameMatch $settingName @($candidate)) {
              return $true
            }
          }

          return $settingName -match '(?i)Entra|AzureAD|Azure AD|\bAAD\b'
        }

        function Get-ConfiguredTargetsForVolume([string]$volumeType, $policyItems) {
          $targets = New-Object System.Collections.Generic.List[string]
          $prefix = Get-VolumePolicyPrefix $volumeType

          $adCandidates = @(
            'ActiveDirectoryBackup',
            'RequireActiveDirectoryBackup',
            'ActiveDirectoryBackupDropDown',
            'RequireActiveDirectoryBackupDropDown')
          if (-not [string]::IsNullOrWhiteSpace($prefix)) {
            $adCandidates += @(
              "$($prefix)ActiveDirectoryBackup",
              "$($prefix)RequireActiveDirectoryBackup",
              "$($prefix)ActiveDirectoryBackupDropDown",
              "$($prefix)RequireActiveDirectoryBackupDropDown")
          }

          $hasAdPolicy = @($policyItems | Where-Object {
            $_.Source -eq 'Group Policy' -and
            $(Test-PolicyNameMatch ([string]$_.SettingName) $adCandidates) -and
            (Test-TruthyPolicyValue $_.ValueText)
          }).Count -gt 0
          if ($hasAdPolicy) {
            $targets.Add('AD DS') | Out-Null
          }

          $hasMbamPolicy = @($policyItems | Where-Object {
            $_.Source -eq 'Group Policy' -and
            $_.SourcePath -match '(?i)MDOPBitLockerManagement' -and
            (
              ($(Test-PolicyNameMatch ([string]$_.SettingName) @('UseMBAMServices')) -and (Test-TruthyPolicyValue $_.ValueText)) -or
              ($(Test-PolicyNameMatch ([string]$_.SettingName) @('UseKeyRecoveryService')) -and (Test-TruthyPolicyValue $_.ValueText)) -or
              ($(Test-PolicyNameMatch ([string]$_.SettingName) @('KeyRecoveryServiceEndPoint')) -and -not [string]::IsNullOrWhiteSpace(([string]$_.ValueText)))
            )
          }).Count -gt 0
          if ($hasMbamPolicy) {
            $targets.Add('MBAM') | Out-Null
          }

          $hasEntraPolicy = @($policyItems | Where-Object {
            $_.Source -eq 'MDM (Intune)' -and
            $(Test-IsEntraRecoveryPolicy ([string]$_.SettingName) $volumeType) -and
            -not [string]::IsNullOrWhiteSpace(([string]$_.ValueText))
          }).Count -gt 0

          if (-not $hasAdPolicy -and -not $hasMbamPolicy -and $hasEntraPolicy) {
            $targets.Add('Microsoft Entra') | Out-Null
          }

          return $targets
        }

        function Get-MbamEvidence() {
          $result = [ordered]@{
            Success = $false
            Failure = $false
            SuccessText = ''
            FailureText = ''
          }

          try {
            $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-MBAM/Operational'; Id = 29, 41, 43 } -MaxEvents 100 -ErrorAction Stop)
            $successEvent = @($events | Where-Object { $_.Id -eq 29 } | Select-Object -First 1)
            $failureEvents = @($events | Where-Object { $_.Id -in 41, 43 })
            if ($successEvent.Count -gt 0) {
              $result.Success = $true
              $result.SuccessText = 'MBAM success event 29 found.'
            }

            if ($failureEvents.Count -gt 0) {
              $result.Failure = $true
              $latestFailure = $failureEvents | Select-Object -First 1
              $result.FailureText = 'MBAM failure event ' + [string]$latestFailure.Id + ' found.'
            }
          } catch {
            $result.SuccessText = 'MBAM event log not available.'
          }

          return $result
        }

        function Get-EntraEvidenceForVolume([string]$mountPoint) {
          $result = [ordered]@{
            Success = $false
            Failure = $false
            SuccessText = ''
            FailureText = ''
          }

          if ([string]::IsNullOrWhiteSpace($mountPoint) -or $mountPoint -eq '-') {
            $result.SuccessText = 'No mount point available for Microsoft Entra evidence lookup.'
            return $result
          }

          try {
            $pastDate = (Get-Date).AddDays(-7)
            $events = @(Get-WinEvent -FilterHashtable @{
              LogName = 'Microsoft-Windows-BitLocker/BitLocker Management'
              ID = 845
              Level = 4
              StartTime = $pastDate
            } -ErrorAction Stop)

            foreach ($event in $events) {
              try {
                $eventXml = [xml]$event.ToXml()
                $volumeMountPoint = @($eventXml.Event.EventData.Data | Where-Object { $_.Name -eq 'VolumeMountPoint' } | Select-Object -ExpandProperty '#text' -First 1)
                if ($volumeMountPoint -eq $mountPoint) {
                  $result.Success = $true
                  $result.SuccessText = "Microsoft Entra escrow success event 845 found for '$mountPoint' in the last 7 days."
                  return $result
                }
              } catch {
              }
            }

            $result.SuccessText = "No local Microsoft Entra escrow success event 845 was found for '$mountPoint' in the last 7 days."
          } catch {
            $result.SuccessText = 'Microsoft Entra escrow event log is not available.'
          }

          return $result
        }

        function Get-BackupTargetAssessmentsForVolume([string]$mountPoint, [string]$volumeType, $policyItems, [bool]$hasRecoveryPasswordProtector, $mbamEvidence) {
          $configuredTargets = Get-ConfiguredTargetsForVolume $volumeType $policyItems
          $assessments = New-Object System.Collections.Generic.List[object]
          $entraEvidence = Get-EntraEvidenceForVolume $mountPoint

          foreach ($target in @('AD DS', 'MBAM', 'Microsoft Entra')) {
            $isConfigured = $configuredTargets -contains $target
            $hasSuccessEvidence = $null
            $hasFailureEvidence = $false
            $assessment = 'NotConfigured'
            $evidenceText = 'Target is not configured by local policy.'

            if ($isConfigured) {
              switch ($target) {
                'MBAM' {
                  $hasSuccessEvidence = [bool]$mbamEvidence.Success
                  $hasFailureEvidence = [bool]$mbamEvidence.Failure
                  if ($hasFailureEvidence) {
                    $assessment = 'ConfiguredWithFailureEvidence'
                    $evidenceText = if ([string]::IsNullOrWhiteSpace($mbamEvidence.FailureText)) { 'MBAM is configured and local failure evidence is present.' } else { $mbamEvidence.FailureText }
                  }
                  elseif ($hasSuccessEvidence) {
                    $assessment = 'ConfiguredAndSuccessEvidencePresent'
                    $evidenceText = if ([string]::IsNullOrWhiteSpace($mbamEvidence.SuccessText)) { 'MBAM is configured and local success evidence is present.' } else { $mbamEvidence.SuccessText }
                  }
                  else {
                    $assessment = 'ConfiguredButNoEvidence'
                    $evidenceText = 'MBAM is configured, but no local success or failure evidence was found.'
                  }
                }
                'AD DS' {
                  $assessment = if ($hasRecoveryPasswordProtector) { 'ConfiguredButNoEvidence' } else { 'ConfiguredButNoEvidence' }
                  $evidenceText = 'AD DS is configured by local policy, but no local escrow proof is evaluated.'
                }
                'Microsoft Entra' {
                  $hasSuccessEvidence = [bool]$entraEvidence.Success
                  if ($hasSuccessEvidence) {
                    $assessment = 'ConfiguredAndSuccessEvidencePresent'
                    $evidenceText = if ([string]::IsNullOrWhiteSpace($entraEvidence.SuccessText)) { 'Microsoft Entra is configured and local success evidence is present.' } else { $entraEvidence.SuccessText }
                  }
                  else {
                    $assessment = 'ConfiguredButNoEvidence'
                    $evidenceText = if ([string]::IsNullOrWhiteSpace($entraEvidence.SuccessText)) { 'Microsoft Entra is configured by local MDM recovery policy, but no local escrow proof was found.' } else { $entraEvidence.SuccessText }
                  }
                }
              }
            }

            $assessments.Add([ordered]@{
              Target = $target
              IsConfigured = $isConfigured
              HasSuccessEvidence = $hasSuccessEvidence
              HasFailureEvidence = $hasFailureEvidence
              Assessment = $assessment
              EvidenceText = $evidenceText
            }) | Out-Null
          }

          return $assessments
        }

        function Get-ConfiguredTargetsText($assessments) {
          $targets = @($assessments | Where-Object { $_.IsConfigured } | ForEach-Object { [string]$_.Target })
          if ($targets.Count -eq 0) { return 'Not configured by local policy' }
          return 'Configured: ' + ($targets -join ', ')
        }

        function Merge-HealthLevels([string]$left, [string]$right) {
          $rank = @{
            'Unknown' = 0
            'Green' = 1
            'Yellow' = 2
            'Red' = 3
          }

          $leftValue = if ($rank.ContainsKey($left)) { [int]$rank[$left] } else { 0 }
          $rightValue = if ($rank.ContainsKey($right)) { [int]$rank[$right] } else { 0 }
          if ($rightValue -gt $leftValue) {
            return $right
          }

          return $left
        }

        function Get-BitLockerComplianceState([string]$mountPoint, [string]$volumeType) {
          $result = [ordered]@{
            HealthLevel = 'Green'
            StatusText = 'Compliant'
            DetailsText = 'No unresolved BitLocker recovery event was detected.'
          }

          try {
            $events = @(Get-WinEvent -FilterHashtable @{
              LogName = 'System'
              ProviderName = 'Microsoft-Windows-BitLocker-Driver'
              Id = 24620, 24635, 24636, 24652
              StartTime = (Get-Date).AddDays(-7)
            } -ErrorAction Stop)

            $matchingEvents = New-Object System.Collections.Generic.List[object]
            foreach ($event in $events) {
              $matchesMountPoint = $false
              try {
                $eventXml = [xml]$event.ToXml()
                $dataValues = @($eventXml.Event.EventData.Data | ForEach-Object { [string]$_.'#text' })
                if ($dataValues.Count -gt 0) {
                  $matchesMountPoint = $dataValues -contains $mountPoint
                }
              } catch {
              }

              if (-not $matchesMountPoint -and $volumeType -match '(?i)OperatingSystem' -and $event.Id -in 24620, 24635, 24636, 24652) {
                $matchesMountPoint = $true
              }

              if ($matchesMountPoint) {
                $matchingEvents.Add($event) | Out-Null
              }
            }

            $latestEvent = @($matchingEvents | Sort-Object TimeCreated -Descending | Select-Object -First 1)
            if ($latestEvent.Count -eq 0) {
              return $result
            }

            switch ([int]$latestEvent[0].Id) {
              24636 {
                $result.HealthLevel = 'Red'
                $result.StatusText = 'Recovery required'
                $result.DetailsText = 'Event 24636 indicates that BitLocker recovery is currently required.'
              }
              24635 {
                $result.HealthLevel = 'Red'
                $result.StatusText = 'TPM unlock failed'
                $result.DetailsText = 'Event 24635 indicates that TPM-based unlock failed.'
              }
              24620 {
                $result.HealthLevel = 'Yellow'
                $result.StatusText = 'Recovery risk detected'
                $result.DetailsText = 'Event 24620 indicates that a BitLocker-relevant change was detected.'
              }
              24652 {
                $result.HealthLevel = 'Green'
                $result.StatusText = 'Recovered'
                $result.DetailsText = 'A later recovery-password event 24652 indicates that the previous recovery state was cleared.'
              }
            }
          } catch {
            $result.DetailsText = 'BitLocker compliance events could not be queried.'
          }

          return $result
        }

        function Get-BackupAssessmentText($assessments, [bool]$hasRecoveryPasswordProtector) {
          if (-not $hasRecoveryPasswordProtector) {
            return 'No recovery-password protector is present.'
          }

          $configured = @($assessments | Where-Object { $_.IsConfigured })
          if ($configured.Count -eq 0) {
            return 'No backup target is configured by local policy.'
          }

          $parts = New-Object System.Collections.Generic.List[string]
          foreach ($entry in $configured) {
            $summary = switch ([string]$entry.Assessment) {
              'ConfiguredAndSuccessEvidencePresent' { 'success evidence present' }
              'ConfiguredWithFailureEvidence' { 'failure evidence present' }
              'ConfiguredButNoEvidence' { 'no local evidence' }
              default { 'not configured' }
            }
            $parts.Add(([string]$entry.Target) + ': ' + $summary) | Out-Null
          }

          return $parts -join ' | '
        }

        function Get-ProtectorsForVolume($volume, [string]$backupTargetsText) {
          $items = New-Object System.Collections.Generic.List[object]
          foreach ($protector in @($volume.KeyProtector)) {
            $protectorId = Convert-ToDisplayString $protector.KeyProtectorId
            $protectorType = Convert-ToDisplayString $protector.KeyProtectorType
            if ([string]::IsNullOrWhiteSpace($protectorType)) { $protectorType = 'Unknown' }
            $isRecovery = $protectorType -match '(?i)RecoveryPassword'
            $friendlyLabel = if ($isRecovery) { 'Recovery password' } else { $protectorType }
            $isRemovable = $isRecovery
            $items.Add([ordered]@{
              ProtectorId = if ([string]::IsNullOrWhiteSpace($protectorId)) { '-' } else { $protectorId }
              ProtectorType = $protectorType
              FriendlyLabel = $friendlyLabel
              IsRecoveryPassword = $isRecovery
              IsRemovable = $isRemovable
              BackupTargetsText = if ($isRecovery) { $backupTargetsText } else { 'Not applicable' }
              LastActionStatusText = ''
            }) | Out-Null
          }

          return $items
        }

        $warnings = New-Object System.Collections.Generic.List[string]
        $machineName = $env:COMPUTERNAME
        $capturedAt = (Get-Date).ToUniversalTime().ToString('o')
        $hasBitLockerCommand = $null -ne (Get-Command -Name 'Get-BitLockerVolume' -ErrorAction SilentlyContinue)
        $hasBackupToAd = $null -ne (Get-Command -Name 'Backup-BitLockerKeyProtector' -ErrorAction SilentlyContinue)
        $hasBackupToEntra = $null -ne (Get-Command -Name 'BackupToAAD-BitLockerKeyProtector' -ErrorAction SilentlyContinue)
        $isAdmin = $false
        try {
          $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
          $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        } catch {
          $warnings.Add('Failed to determine whether the current session runs with administrative rights.') | Out-Null
        }

        $isDomainJoined = $false
        try {
          $computerSystem = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
          if ($null -ne $computerSystem) {
            $isDomainJoined = [bool]$computerSystem.PartOfDomain
          }
        } catch {
          $warnings.Add('Failed to determine domain-join state.') | Out-Null
        }

        $dsregStatus = ''
        $isEntraJoined = $false
        try {
          $dsregStatus = (cmd /c 'dsregcmd /status') | Out-String
          $isEntraJoined = Test-DsregYes $dsregStatus 'AzureAdJoined'
        } catch {
          $warnings.Add('Failed to determine Microsoft Entra join state.') | Out-Null
        }

        if (-not $hasBitLockerCommand) {
          $warnings.Add('Get-BitLockerVolume is not available on the target host.') | Out-Null
        }
        if (-not $isAdmin) {
          $warnings.Add('BitLocker actions usually require administrative rights on the target host.') | Out-Null
        }

        $phaseStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $policies = New-Object System.Collections.Generic.List[object]
        foreach ($policy in @(Get-ConfiguredRegistryPolicies 'HKLM:\SOFTWARE\Policies\Microsoft\FVE' 'Group Policy' @())) {
          $policies.Add($policy) | Out-Null
        }

        foreach ($policy in @(Get-ConfiguredRegistryPolicies 'HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\BitLocker' 'MDM (Intune)' @('LastWriteTime','LastWriteTimeUtc','WinningProvider','_ProviderSet'))) {
          $policies.Add($policy) | Out-Null
        }

        $providersRoot = 'HKLM:\SOFTWARE\Microsoft\PolicyManager\providers'
        if (Test-Path -LiteralPath $providersRoot) {
          foreach ($provider in @(Get-ChildItem -LiteralPath $providersRoot -ErrorAction SilentlyContinue | Where-Object { $_.PSIsContainer })) {
            foreach ($providerPath in @(
              "$($provider.PSPath)\default\Device\BitLocker",
              "$($provider.PSPath)\current\device\BitLocker")) {
              foreach ($policy in @(Get-ConfiguredRegistryPolicies $providerPath 'MDM (Intune)' @('LastWriteTime','LastWriteTimeUtc','WinningProvider','_ProviderSet'))) {
                $policies.Add($policy) | Out-Null
              }
            }
          }
        }

        foreach ($policy in @(Get-MecmBitLockerPolicies)) {
          $policies.Add($policy) | Out-Null
        }

        $deduplicatedPolicies = New-Object System.Collections.Generic.List[object]
        $seenPolicies = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($policy in @($policies | Sort-Object Source, Category, SettingName, ValueText, SourcePath)) {
          $dedupeKey = [string]::Join('|', @(
            (Convert-ToDisplayString $policy.Source),
            (Convert-ToDisplayString $policy.Category),
            (Convert-ToDisplayString $policy.SettingName),
            (Convert-ToDisplayString $policy.ValueText),
            (Convert-ToDisplayString $policy.SourcePath)))
          if ($seenPolicies.Add($dedupeKey)) {
            $deduplicatedPolicies.Add($policy) | Out-Null
          }
        }

        $hasIntunePolicies = @($deduplicatedPolicies | Where-Object { $_.Source -eq 'MDM (Intune)' }).Count -gt 0
        $hasGpoPolicies = @($deduplicatedPolicies | Where-Object { $_.Source -eq 'Group Policy' }).Count -gt 0
        $hasMecmPolicies = @($deduplicatedPolicies | Where-Object { $_.Source -eq 'Configuration Manager' }).Count -gt 0
        Add-VerboseTiming 'Policy discovery' $phaseStopwatch.ElapsedMilliseconds
        $phaseStopwatch.Restart()
        $mbamEvidence = Get-MbamEvidence
        Add-VerboseTiming 'MBAM evidence lookup' $phaseStopwatch.ElapsedMilliseconds
        $phaseStopwatch.Restart()

        $volumes = New-Object System.Collections.Generic.List[object]
        if ($hasBitLockerCommand) {
          try {
            $encryptableVolumes = @{}
            try {
              foreach ($encryptable in @(Get-CimInstance -Namespace 'root\cimv2\Security\MicrosoftVolumeEncryption' -ClassName 'Win32_EncryptableVolume' -ErrorAction SilentlyContinue)) {
                if ($null -ne $encryptable.DriveLetter) {
                  $encryptableVolumes[[string]$encryptable.DriveLetter] = $encryptable
                }
              }
            } catch {
              $warnings.Add('Suspend-count details are unavailable because Win32_EncryptableVolume could not be queried.') | Out-Null
            }

            foreach ($volume in @(Get-BitLockerVolume -ErrorAction Stop | Sort-Object MountPoint)) {
              $volumeStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
              $mountPoint = Convert-ToDisplayString $volume.MountPoint
              if ([string]::IsNullOrWhiteSpace($mountPoint)) { $mountPoint = '-' }
              $volumeType = Convert-ToDisplayString $volume.VolumeType
              $protectionStatus = Convert-ToDisplayString $volume.ProtectionStatus
              $volumeStatus = Convert-ToDisplayString $volume.VolumeStatus
              $lockStatus = Convert-ToDisplayString $volume.LockStatus
              $encryptionPercentage = 0
              if ($null -ne $volume.EncryptionPercentage) { $encryptionPercentage = [int]$volume.EncryptionPercentage }
              $encryptionMethod = Convert-ToDisplayString $volume.EncryptionMethod
              $autoUnlockText = if ($null -eq $volume.AutoUnlockEnabled) { 'Unknown' } else { $(if ([bool]$volume.AutoUnlockEnabled) { 'Enabled' } else { 'Disabled' }) }
              $isProtectionOn = $protectionStatus -match '^(?i:1|on)$'
              $isEncrypted = $encryptionPercentage -gt 0 -or $volumeStatus -match '(?i)encrypt'
              $isProtectionSuspended = -not $isProtectionOn -and $isEncrypted

              $suspendCount = $null
              if ($encryptableVolumes.ContainsKey($mountPoint)) {
                try {
                  $suspendResult = Invoke-CimMethod -InputObject $encryptableVolumes[$mountPoint] -MethodName 'GetSuspendCount' -ErrorAction Stop
                  if ($null -ne $suspendResult -and $suspendResult.ReturnValue -eq 0 -and $null -ne $suspendResult.SuspendCount) {
                    $suspendCount = [int]$suspendResult.SuspendCount
                  }
                } catch {
                }
              }

              $hasRecoveryPasswordProtector = @($volume.KeyProtector | Where-Object { $_.KeyProtectorType -match '(?i)RecoveryPassword' }).Count -gt 0
              $backupTargetAssessments = Get-BackupTargetAssessmentsForVolume $mountPoint $volumeType $deduplicatedPolicies $hasRecoveryPasswordProtector $mbamEvidence
              $configuredBackupTargetsText = Get-ConfiguredTargetsText $backupTargetAssessments
              $backupAssessmentText = Get-BackupAssessmentText $backupTargetAssessments $hasRecoveryPasswordProtector
              $complianceState = Get-BitLockerComplianceState $mountPoint $volumeType

              $baseHealthLevel =
                if ($isProtectionOn) { 'Green' }
                elseif ($isProtectionSuspended) { 'Yellow' }
                elseif ($isEncrypted) { 'Yellow' }
                else { 'Red' }
              $healthLevel = Merge-HealthLevels $baseHealthLevel ([string]$complianceState.HealthLevel)

              $volumes.Add([ordered]@{
                MountPoint = $mountPoint
                VolumeType = if ([string]::IsNullOrWhiteSpace($volumeType)) { 'Unknown' } else { $volumeType }
                ProtectionStatusText = if ($isProtectionOn) { 'Protected' } elseif ($isProtectionSuspended) { 'Protection suspended' } elseif ([string]::IsNullOrWhiteSpace($protectionStatus)) { 'Unknown' } else { $protectionStatus }
                VolumeStatusText = if ([string]::IsNullOrWhiteSpace($volumeStatus)) { 'Unknown' } else { $volumeStatus }
                LockStatusText = if ([string]::IsNullOrWhiteSpace($lockStatus)) { 'Unknown' } else { $lockStatus }
                EncryptionPercentage = $encryptionPercentage
                EncryptionMethodText = if ([string]::IsNullOrWhiteSpace($encryptionMethod)) { 'Unknown' } else { $encryptionMethod }
                AutoUnlockText = $autoUnlockText
                SuspendRebootCount = $suspendCount
                HealthLevel = $healthLevel
                ComplianceStatusText = [string]$complianceState.StatusText
                ComplianceDetailsText = [string]$complianceState.DetailsText
                BackupEligibilityText = $backupAssessmentText
                ConfiguredBackupTargetsText = $configuredBackupTargetsText
                BackupAssessmentText = $backupAssessmentText
                BackupTargetAssessments = $backupTargetAssessments
                IsEncrypted = $isEncrypted
                IsProtectionOn = $isProtectionOn
                IsProtectionSuspended = $isProtectionSuspended
                Protectors = Get-ProtectorsForVolume $volume $configuredBackupTargetsText
              }) | Out-Null
              Add-VerboseTiming ("Volume analysis " + $mountPoint) $volumeStopwatch.ElapsedMilliseconds
            }
            Add-VerboseTiming 'Volume inventory' $phaseStopwatch.ElapsedMilliseconds
          } catch {
            $warnings.Add('Failed to query BitLocker volumes: ' + $_.Exception.Message) | Out-Null
          }
        }
        Add-VerboseTiming 'Snapshot total PowerShell work' $overallStopwatch.ElapsedMilliseconds

        $result = [ordered]@{
          MachineName = $machineName
          CapturedAtUtc = $capturedAt
          Capabilities = [ordered]@{
            IsBitLockerCommandAvailable = $hasBitLockerCommand
            IsAdministrator = $isAdmin
            SupportsSuspendProtection = $hasBitLockerCommand
            SupportsResumeProtection = $hasBitLockerCommand
            SupportsRecoveryPasswordProtectorOperations = $hasBitLockerCommand
            SupportsBackupToAd = $hasBackupToAd
            SupportsBackupToEntra = $hasBackupToEntra
            IsDomainJoined = $isDomainJoined
            IsEntraJoined = $isEntraJoined
            Warnings = $warnings
          }
          Policies = $deduplicatedPolicies
          DiagnosticsTimings = $timings
          HasIntunePolicies = $hasIntunePolicies
          HasGpoPolicies = $hasGpoPolicies
          HasMecmPolicies = $hasMecmPolicies
          Volumes = $volumes
        }

        $result | ConvertTo-Json -Depth 10 -Compress
        """;
    }

    private static string BuildSuspendProtectionScript(string mountPoint, int rebootCount)
    {
        var escapedMountPoint = EscapePowerShellString(mountPoint);
        return
            "function New-ActionResult($success, $warning, [string]$message, [string]$errorCode, [string]$newProtectorId, [string[]]$details) {" +
            "  [ordered]@{ Success=$success; Warning=$warning; Message=$message; ErrorCode=$errorCode; NewProtectorId=$newProtectorId; Details=$details }" +
            "};" +
            $"$mountPoint='{escapedMountPoint}';" +
            $"$rebootCount={rebootCount};" +
            "Suspend-BitLocker -MountPoint $mountPoint -RebootCount $rebootCount -ErrorAction Stop | Out-Null;" +
            "$message = if ($rebootCount -eq 0) { \"BitLocker protection was suspended on '$mountPoint' until it is resumed manually.\" } else { \"BitLocker protection was suspended on '$mountPoint' for $rebootCount reboot(s).\" };" +
            "(New-ActionResult $true $false $message $null $null @()) | ConvertTo-Json -Depth 5 -Compress;";
    }

    private static string BuildResumeProtectionScript(string mountPoint)
    {
        var escapedMountPoint = EscapePowerShellString(mountPoint);
        return
            "function New-ActionResult($success, $warning, [string]$message, [string]$errorCode, [string]$newProtectorId, [string[]]$details) {" +
            "  [ordered]@{ Success=$success; Warning=$warning; Message=$message; ErrorCode=$errorCode; NewProtectorId=$newProtectorId; Details=$details }" +
            "};" +
            $"$mountPoint='{escapedMountPoint}';" +
            "Resume-BitLocker -MountPoint $mountPoint -ErrorAction Stop | Out-Null;" +
            "(New-ActionResult $true $false (\"BitLocker protection was resumed on '$mountPoint'.\") $null $null @()) | ConvertTo-Json -Depth 5 -Compress;";
    }

    private static string BuildAddRecoveryPasswordProtectorScript(string mountPoint)
    {
        var escapedMountPoint = EscapePowerShellString(mountPoint);
        return BuildRecoveryProtectorMutationScript(
            escapedMountPoint,
            "add",
            null);
    }

    private static string BuildRemoveRecoveryPasswordProtectorScript(string mountPoint, string protectorId)
    {
        var escapedMountPoint = EscapePowerShellString(mountPoint);
        var escapedProtectorId = EscapePowerShellString(protectorId);
        return BuildRecoveryProtectorMutationScript(
            escapedMountPoint,
            "remove",
            escapedProtectorId);
    }

    private static string BuildBackupRecoveryPasswordScript(string mountPoint, string protectorId)
    {
        var escapedMountPoint = EscapePowerShellString(mountPoint);
        var escapedProtectorId = EscapePowerShellString(protectorId);
        return BuildBackupScript(escapedMountPoint, escapedProtectorId, rotationMode: false);
    }

    private static string BuildRotateRecoveryPasswordScript(string mountPoint, string protectorId)
    {
        var escapedMountPoint = EscapePowerShellString(mountPoint);
        var escapedProtectorId = EscapePowerShellString(protectorId);
        return BuildBackupScript(escapedMountPoint, escapedProtectorId, rotationMode: true);
    }

    private static string BuildRecoveryProtectorMutationScript(string escapedMountPoint, string mode, string? escapedProtectorId)
    {
        var protectorAssignment = escapedProtectorId is null
            ? "$protectorId = $null;"
            : $"$protectorId='{escapedProtectorId}';";

        return
            """
            function New-ActionResult($success, $warning, [string]$message, [string]$errorCode, [string]$newProtectorId, [string[]]$details) {
              [ordered]@{
                Success = $success
                Warning = $warning
                Message = $message
                ErrorCode = $errorCode
                NewProtectorId = $newProtectorId
                Details = $details
              }
            }
            """ +
            $"$mountPoint='{escapedMountPoint}';" +
            protectorAssignment +
            $"$mode='{mode}';" +
            """
            $volume = @(Get-BitLockerVolume -MountPoint $mountPoint -ErrorAction Stop) | Select-Object -First 1
            if ($null -eq $volume) {
              throw "BitLocker volume '$mountPoint' was not found."
            }

            $recoveryProtectors = @($volume.KeyProtector | Where-Object { $_.KeyProtectorType -match '(?i)RecoveryPassword' })
            if ($mode -eq 'add') {
              $beforeIds = @($recoveryProtectors | ForEach-Object { [string]$_.KeyProtectorId })
              Add-BitLockerKeyProtector -MountPoint $mountPoint -RecoveryPasswordProtector -ErrorAction Stop | Out-Null
              $afterVolume = @(Get-BitLockerVolume -MountPoint $mountPoint -ErrorAction Stop) | Select-Object -First 1
              $afterProtectors = @($afterVolume.KeyProtector | Where-Object { $_.KeyProtectorType -match '(?i)RecoveryPassword' })
              $newId = @($afterProtectors | ForEach-Object { [string]$_.KeyProtectorId } | Where-Object { $_ -and ($_ -notin $beforeIds) } | Select-Object -First 1)
              $message = "Added a new recovery-password protector on '$mountPoint'."
              (New-ActionResult $true $false $message $null $newId @()) | ConvertTo-Json -Depth 5 -Compress
              return
            }

            $selectedProtector = $recoveryProtectors | Where-Object { [string]$_.KeyProtectorId -eq $protectorId } | Select-Object -First 1
            if ($null -eq $selectedProtector) {
              (New-ActionResult $false $false "The selected recovery-password protector was not found." 'protector_not_found' $null @()) | ConvertTo-Json -Depth 5 -Compress
              return
            }

            if ($recoveryProtectors.Count -le 1) {
              (New-ActionResult $false $false "The last recovery-password protector cannot be removed." 'last_recovery_protector' $null @()) | ConvertTo-Json -Depth 5 -Compress
              return
            }

            Remove-BitLockerKeyProtector -MountPoint $mountPoint -KeyProtectorId $protectorId -ErrorAction Stop | Out-Null
            (New-ActionResult $true $false "Removed the selected recovery-password protector." $null $null @()) | ConvertTo-Json -Depth 5 -Compress
            """;
    }

    private static string BuildBackupScript(string escapedMountPoint, string escapedProtectorId, bool rotationMode)
    {
        var rotationText = rotationMode ? "$true" : "$false";
        return
            """
            function New-ActionResult($success, $warning, [string]$message, [string]$errorCode, [string]$newProtectorId, [string[]]$details) {
              [ordered]@{
                Success = $success
                Warning = $warning
                Message = $message
                ErrorCode = $errorCode
                NewProtectorId = $newProtectorId
                Details = $details
              }
            }

            function Test-DsregYes([string]$raw, [string]$fieldName) {
              if ([string]::IsNullOrWhiteSpace($raw) -or [string]::IsNullOrWhiteSpace($fieldName)) { return $false }
              $pattern = '(?m)^\s*' + [regex]::Escape($fieldName) + '\s*[:=]\s*(YES|JA|TRUE|WAHR)\s*$'
              return [regex]::IsMatch($raw, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            }

            function Invoke-BackupOperations([string]$mountPoint, [string]$protectorId) {
              $details = New-Object System.Collections.Generic.List[string]
              $successTargets = New-Object System.Collections.Generic.List[string]
              $isDomainJoined = $false
              try {
                $computerSystem = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
                if ($null -ne $computerSystem) { $isDomainJoined = [bool]$computerSystem.PartOfDomain }
              } catch {
              }

              $isEntraJoined = $false
              try {
                $dsreg = (cmd /c 'dsregcmd /status') | Out-String
                $isEntraJoined = Test-DsregYes $dsreg 'AzureAdJoined'
              } catch {
              }

              if ($isEntraJoined -and $null -ne (Get-Command -Name 'BackupToAAD-BitLockerKeyProtector' -ErrorAction SilentlyContinue)) {
                try {
                  BackupToAAD-BitLockerKeyProtector -MountPoint $mountPoint -KeyProtectorId $protectorId -ErrorAction Stop | Out-Null
                  $successTargets.Add('Microsoft Entra') | Out-Null
                  $details.Add('Microsoft Entra backup succeeded.') | Out-Null
                } catch {
                  $details.Add('Microsoft Entra backup failed: ' + $_.Exception.Message) | Out-Null
                }
              }

              if ($isDomainJoined -and $null -ne (Get-Command -Name 'Backup-BitLockerKeyProtector' -ErrorAction SilentlyContinue)) {
                try {
                  Backup-BitLockerKeyProtector -MountPoint $mountPoint -KeyProtectorId $protectorId -ErrorAction Stop | Out-Null
                  $successTargets.Add('AD DS') | Out-Null
                  $details.Add('AD DS backup succeeded.') | Out-Null
                } catch {
                  $details.Add('AD DS backup failed: ' + $_.Exception.Message) | Out-Null
                }
              }

              return [ordered]@{
                SuccessTargets = $successTargets
                Details = $details
              }
            }
            """ +
            $"$mountPoint='{escapedMountPoint}';" +
            $"$protectorId='{escapedProtectorId}';" +
            $"$rotationMode={rotationText};" +
            """
            $volume = @(Get-BitLockerVolume -MountPoint $mountPoint -ErrorAction Stop) | Select-Object -First 1
            if ($null -eq $volume) {
              throw "BitLocker volume '$mountPoint' was not found."
            }

            $recoveryProtectors = @($volume.KeyProtector | Where-Object { $_.KeyProtectorType -match '(?i)RecoveryPassword' })
            $selectedProtector = $recoveryProtectors | Where-Object { [string]$_.KeyProtectorId -eq $protectorId } | Select-Object -First 1
            if ($null -eq $selectedProtector) {
              (New-ActionResult $false $false "The selected recovery-password protector was not found." 'protector_not_found' $null @()) | ConvertTo-Json -Depth 6 -Compress
              return
            }

            if (-not $rotationMode) {
              $backupResult = Invoke-BackupOperations $mountPoint $protectorId
              $successTargets = @($backupResult.SuccessTargets)
              $details = @($backupResult.Details)
              if ($successTargets.Count -gt 0) {
                $message = 'Backed up the selected recovery-password protector to ' + ($successTargets -join ', ') + '.'
                (New-ActionResult $true $false $message $null $null $details) | ConvertTo-Json -Depth 6 -Compress
              }
              else {
                (New-ActionResult $false $true 'No BitLocker backup target accepted the selected recovery-password protector.' 'backup_failed' $null $details) | ConvertTo-Json -Depth 6 -Compress
              }
              return
            }

            $beforeIds = @($recoveryProtectors | ForEach-Object { [string]$_.KeyProtectorId })
            Add-BitLockerKeyProtector -MountPoint $mountPoint -RecoveryPasswordProtector -ErrorAction Stop | Out-Null
            $afterVolume = @(Get-BitLockerVolume -MountPoint $mountPoint -ErrorAction Stop) | Select-Object -First 1
            $afterProtectors = @($afterVolume.KeyProtector | Where-Object { $_.KeyProtectorType -match '(?i)RecoveryPassword' })
            $newId = @($afterProtectors | ForEach-Object { [string]$_.KeyProtectorId } | Where-Object { $_ -and ($_ -notin $beforeIds) } | Select-Object -First 1)
            if ([string]::IsNullOrWhiteSpace($newId)) {
              (New-ActionResult $false $false 'A new recovery-password protector was created, but its identifier could not be determined.' 'new_protector_not_found' $null @()) | ConvertTo-Json -Depth 6 -Compress
              return
            }

            $backupResult = Invoke-BackupOperations $mountPoint $newId
            $successTargets = @($backupResult.SuccessTargets)
            $details = @($backupResult.Details)
            if ($successTargets.Count -eq 0) {
              $details += 'The old recovery-password protector was kept because backup of the new protector failed.'
              (New-ActionResult $false $true 'Added a new recovery-password protector, but backup failed. The old protector was kept.' 'backup_failed' $newId $details) | ConvertTo-Json -Depth 6 -Compress
              return
            }

            try {
              Remove-BitLockerKeyProtector -MountPoint $mountPoint -KeyProtectorId $protectorId -ErrorAction Stop | Out-Null
              $details += 'The previous recovery-password protector was removed after backup succeeded.'
              $message = 'Rotated the recovery-password protector and backed up the new protector to ' + ($successTargets -join ', ') + '.'
              (New-ActionResult $true $false $message $null $newId $details) | ConvertTo-Json -Depth 6 -Compress
            } catch {
              $details += 'Backup succeeded, but removal of the previous recovery-password protector failed: ' + $_.Exception.Message
              (New-ActionResult $false $true 'Added and backed up a new recovery-password protector, but the previous protector could not be removed.' 'old_protector_remove_failed' $newId $details) | ConvertTo-Json -Depth 6 -Compress
            }
            """;
    }

    private sealed class BitLockerSnapshotPayload
    {
        public string? MachineName { get; init; }
        public string? CapturedAtUtc { get; init; }
        public BitLockerCapabilityPayload? Capabilities { get; init; }
        public List<BitLockerPolicyPayload>? Policies { get; init; }
        public List<string>? DiagnosticsTimings { get; init; }
        public bool HasIntunePolicies { get; init; }
        public bool HasGpoPolicies { get; init; }
        public bool HasMecmPolicies { get; init; }
        public List<BitLockerVolumePayload>? Volumes { get; init; }
    }

    private sealed class BitLockerCapabilityPayload
    {
        public bool IsBitLockerCommandAvailable { get; init; }
        public bool IsAdministrator { get; init; }
        public bool SupportsSuspendProtection { get; init; }
        public bool SupportsResumeProtection { get; init; }
        public bool SupportsRecoveryPasswordProtectorOperations { get; init; }
        public bool SupportsBackupToAd { get; init; }
        public bool SupportsBackupToEntra { get; init; }
        public bool IsDomainJoined { get; init; }
        public bool IsEntraJoined { get; init; }
        public List<string>? Warnings { get; init; }
    }

    private sealed class BitLockerVolumePayload
    {
        public string? MountPoint { get; init; }
        public string? VolumeType { get; init; }
        public string? ProtectionStatusText { get; init; }
        public string? VolumeStatusText { get; init; }
        public string? LockStatusText { get; init; }
        public int EncryptionPercentage { get; init; }
        public string? EncryptionMethodText { get; init; }
        public string? AutoUnlockText { get; init; }
        public int? SuspendRebootCount { get; init; }
        public string? HealthLevel { get; init; }
        public string? ComplianceStatusText { get; init; }
        public string? ComplianceDetailsText { get; init; }
        public string? BackupEligibilityText { get; init; }
        public string? ConfiguredBackupTargetsText { get; init; }
        public string? BackupAssessmentText { get; init; }
        public List<BitLockerBackupTargetAssessmentPayload>? BackupTargetAssessments { get; init; }
        public bool IsEncrypted { get; init; }
        public bool IsProtectionOn { get; init; }
        public bool IsProtectionSuspended { get; init; }
        public List<BitLockerProtectorPayload>? Protectors { get; init; }
    }

    private sealed class BitLockerBackupTargetAssessmentPayload
    {
        public string? Target { get; init; }
        public bool IsConfigured { get; init; }
        public bool? HasSuccessEvidence { get; init; }
        public bool HasFailureEvidence { get; init; }
        public string? Assessment { get; init; }
        public string? EvidenceText { get; init; }
    }

    private sealed class BitLockerPolicyPayload
    {
        public string? SettingName { get; init; }
        public string? ValueText { get; init; }
        public string? Source { get; init; }
        public string? Category { get; init; }
        public string? SourcePath { get; init; }
    }

    private sealed class BitLockerProtectorPayload
    {
        public string? ProtectorId { get; init; }
        public string? ProtectorType { get; init; }
        public string? FriendlyLabel { get; init; }
        public bool IsRecoveryPassword { get; init; }
        public bool IsRemovable { get; init; }
        public string? BackupTargetsText { get; init; }
        public string? LastActionStatusText { get; init; }
    }

    private sealed class BitLockerActionPayload
    {
        public bool Success { get; init; }
        public bool Warning { get; init; }
        public string? Message { get; init; }
        public string? ErrorCode { get; init; }
        public string? NewProtectorId { get; init; }
        public List<string>? Details { get; init; }
    }

    private sealed class SingleOrArrayListJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
        {
            return typeToConvert.IsGenericType &&
                   typeToConvert.GetGenericTypeDefinition() == typeof(List<>);
        }

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var itemType = typeToConvert.GetGenericArguments()[0];
            var converterType = typeof(SingleOrArrayListJsonConverter<>).MakeGenericType(itemType);
            return (JsonConverter)Activator.CreateInstance(converterType)!;
        }
    }

    private sealed class SingleOrArrayListJsonConverter<TItem> : JsonConverter<List<TItem>>
    {
        public override List<TItem> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return [];
            }

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var items = new List<TItem>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        return items;
                    }

                    var item = JsonSerializer.Deserialize<TItem>(ref reader, options);
                    if (item is not null)
                    {
                        items.Add(item);
                    }
                }

                throw new JsonException("Unexpected end of JSON while reading an array.");
            }

            var singleItem = JsonSerializer.Deserialize<TItem>(ref reader, options);
            return singleItem is null ? [] : [singleItem];
        }

        public override void Write(Utf8JsonWriter writer, List<TItem> value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }
}
