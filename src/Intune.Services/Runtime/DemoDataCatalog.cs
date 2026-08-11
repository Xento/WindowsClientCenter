using System.IO;
using System.Linq;
using WindowsClientCenter.Defender.Contracts.Models;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

public sealed record DemoWindowsUpdateAvailableItem(
    string Title,
    string Type,
    string Status,
    bool IsInstalled,
    bool IsHidden,
    string KbArticles,
    bool IsDownloaded,
    bool IsMandatory,
    bool EulaAccepted,
    string Categories,
    string Deadline,
    string UpdateId,
    int Revision);

public sealed record DemoWindowsUpdateProviderItem(
    string Name,
    string ServiceId,
    bool IsDefault,
    bool IsRegisteredWithAutomaticUpdates,
    bool OffersWindowsUpdates,
    bool IsManaged);

public sealed record DemoWindowsUpdateHistoryItem(
    string Date,
    string Operation,
    string Result,
    string HResult,
    string Title,
    string UpdateId,
    int Revision,
    string ClientApplicationId,
    string ServiceId,
    string PackageName = "");

public sealed record DemoWindowsUpdateSnapshot(
    IReadOnlyList<string> ReportingEventsLines,
    IReadOnlyList<DemoWindowsUpdateAvailableItem> AvailableUpdates,
    IReadOnlyList<DemoWindowsUpdateProviderItem> Providers,
    IReadOnlyList<DemoWindowsUpdateHistoryItem> HistoryEntries,
    IReadOnlyList<string> BaseInstallProgressLines,
    string LastScanInfo,
    string DefaultInstallTaskState,
    string DefaultInstallTaskStatusText,
    string DefaultInstallTaskPhaseText,
    string DefaultInstallTaskDetail,
    bool IsInstallTaskRunning);

public sealed class DemoDataCatalog(IntuneRuntimeOptions options)
{
    private static readonly DateTimeOffset ReferenceTimeUtc = new(2026, 4, 18, 8, 15, 0, TimeSpan.Zero);

    public string DemoHostName => string.IsNullOrWhiteSpace(options.DemoHostName)
        ? "DEMO-CLIENT-01"
        : options.DemoHostName.Trim().ToUpperInvariant();

    public string DemoTenantId => string.IsNullOrWhiteSpace(options.DemoTenantId)
        ? "demo.example"
        : options.DemoTenantId.Trim();

    public string DemoUserPrincipalName => string.IsNullOrWhiteSpace(options.DemoUserPrincipalName)
        ? "alex.wilson@demo.example"
        : options.DemoUserPrincipalName.Trim();

    public string DemoConnectedUsersText => string.IsNullOrWhiteSpace(options.DemoConnectedUsersText)
        ? @"DEMO\alex.wilson, DEMO\helpdesk.ops"
        : options.DemoConnectedUsersText.Trim();

    public string NormalizeHost(string? host)
    {
        return string.IsNullOrWhiteSpace(host)
            ? DemoHostName
            : host.Trim().ToUpperInvariant();
    }

    public IReadOnlyList<string> GetConnectedUsers()
    {
        return DemoConnectedUsersText
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
    }

    public HostConnectivityStatus GetConnectivityStatus(string? host)
    {
        return new HostConnectivityStatus(
            PingSucceeded: true,
            PingRoundtripTimeMs: 2,
            PingDetail: "ok",
            ResolvedIp: "192.0.2.42",
            SmbReachable: true,
            WinRmHttpReachable: true,
            WinRmHttpsReachable: true);
    }

    public AuthSession GetAuthSession()
    {
        return new AuthSession(
            TenantId: DemoTenantId,
            UserPrincipalName: DemoUserPrincipalName,
            ExpiresAt: new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero),
            IsMock: true);
    }

    public IReadOnlyList<DeviceRecord> GetDevices()
    {
        return
        [
            CreateDeviceRecord(DemoHostName),
            CreateDeviceRecord("DEMO-CLIENT-02", complianceState: "InGracePeriod"),
            CreateDeviceRecord("DEMO-KIOSK-01", complianceState: "NonCompliant")
        ];
    }

    public DeviceRecord CreateDeviceRecord(string? host, string complianceState = "Compliant")
    {
        var normalizedHost = NormalizeHost(host);
        return new DeviceRecord(
            DeviceId: $"demo-device-{normalizedHost.ToLowerInvariant()}",
            DeviceName: normalizedHost,
            Platform: "Windows",
            LastSync: ReferenceTimeUtc.AddMinutes(-12),
            ComplianceState: complianceState);
    }

    public CloudManagedDeviceSummary CreateCloudManagedDeviceSummary(string? host)
    {
        var normalizedHost = NormalizeHost(host);
        return new CloudManagedDeviceSummary(
            ManagedDeviceId: $"demo-managed-{normalizedHost.ToLowerInvariant()}",
            DeviceName: normalizedHost,
            AzureAdDeviceId: $"demo-aad-{normalizedHost.ToLowerInvariant()}",
            UserPrincipalName: DemoUserPrincipalName,
            OperatingSystem: "Windows",
            ComplianceState: "Compliant",
            LastSyncDateTime: ReferenceTimeUtc.AddMinutes(-12),
            IsExactMatch: true,
            Source: "Demo");
    }

    public LocalIntuneSnapshot CreateLocalSnapshot(string? host)
    {
        var normalizedHost = NormalizeHost(host);
        return new LocalIntuneSnapshot(
            Host: normalizedHost,
            MachineName: normalizedHost,
            CapturedAt: ReferenceTimeUtc,
            IsLocalHost: false,
            LastSyncText: "2026-04-18 07:55:00Z",
            RegistrationSummary: "AzureAdJoined : YES | DomainJoined : YES | MDM enrollment detected",
            DsregStatusText: """
                Device State
                AzureAdJoined : YES
                DomainJoined  : YES
                AzureAdPrt    : YES
                DeviceAuthStatus : SUCCESS
                MdmUrl        : https://enrollment.manage.microsoft.com
                """,
            DsregHighlights:
            [
                "[OK] AzureAdJoined: YES",
                "[OK] DomainJoined: YES",
                "[OK] AzureAdPrt: YES",
                "[OK] DeviceAuthStatus: SUCCESS"
            ],
            EnrollmentArtifacts:
            [
                new EnrollmentArtifact(
                    "Registry",
                    @"HKLM:\SOFTWARE\Microsoft\Enrollments\11111111-1111-1111-1111-111111111111",
                    "Primary demo MDM enrollment",
                    "11111111-1111-1111-1111-111111111111",
                    IsRemovable: true)
            ],
            EnterpriseMgmtTasks:
            [
                @"\Microsoft\Windows\EnterpriseMgmt\11111111-1111-1111-1111-111111111111\PushLaunch",
                @"\Microsoft\Windows\EnterpriseMgmt\11111111-1111-1111-1111-111111111111\Schedule #3 created by enrollment client"
            ],
            CertificateSummaries:
            [
                "CN=MS-Organization-Access | CN=Demo Issuing CA | DEADBEEF1234",
                "CN=MS-Organization-P2P-Access | CN=Demo Issuing CA | BEEFDEAD5678"
            ],
            ServiceValues:
            [
                new NameValueItem("MdmServerUrl", "https://enrollment.manage.microsoft.com"),
                new NameValueItem("EnrollmentServerUrl", "https://portal.manage.microsoft.com")
            ],
            Notes:
            [
                "Demo mode active. Local diagnostics are simulated and deterministic.",
                "No remote PowerShell, registry, or file access was performed."
            ],
            MdmLastSyncText: "2026-04-18 07:55:00Z",
            ImeLastSyncText: "2026-04-18 08:02:00Z",
            WindowsVersionText: "Windows 11 Enterprise",
            WindowsBuildText: "23H2 (22631.3527)",
            FreeDiskSpaceText: "178.4 GB free",
            PatchStatusText: "1 quality update pending installation.",
            PatchStatusLevel: "Warning",
            DeliveryOptimization: CreateDeliveryOptimizationSnapshot(),
            PlatformSecurity: CreatePlatformSecuritySnapshot(),
            SystemRuntime: CreateSystemRuntimeSnapshot(),
            NetworkConnectivity: CreateNetworkConnectivitySnapshot(host),
            ManufacturerText: "Fabrikam Devices",
            ModelText: "Northwind Pro 14",
            SerialNumberText: "NW-240418-DEMO",
            AdJoinPathText: @"OU=Demo,OU=Workstations,DC=demo,DC=example",
            UpdateRingText: "Windows Autopatch - Pilot");
    }

    public PlatformSecuritySnapshot CreatePlatformSecuritySnapshot()
    {
        return new PlatformSecuritySnapshot(
            BitLockerStatusText: "Protected",
            BitLockerDetailText: "C: | FullyEncrypted | 100% encrypted | XtsAes256",
            TpmStatusText: "Ready",
            TpmVersionText: "2.0",
            TpmDetailText: "Present: Yes | Ready: Yes | Enabled: Yes | Activated: Yes | Manufacturer: IFX",
            SecureBootStatusText: "Enabled",
            CredentialGuardStatusText: "Running",
            VbsStatusText: "Running",
            MemoryIntegrityStatusText: "Running");
    }

    public SystemRuntimeSnapshot CreateSystemRuntimeSnapshot()
    {
        return new SystemRuntimeSnapshot(
            UptimeText: "12d 04h 18m",
            LastBootText: "2026-04-06 04:12:00Z",
            InstallDateText: "2025-11-14 09:30:00Z",
            PendingRebootStatusText: "Restart required",
            PendingRebootDetailText: "Windows Update and MECM both require a restart.",
            WindowsUpdateScheduledRestartStatusText: "Scheduled",
            WindowsUpdateScheduledRestartTimeText: "2026-04-18 21:00:00 +02:00",
            MecmScheduledRestartTimeText: "2026-04-18 22:30:00 +02:00",
            SessionLockStatusText: "Locked",
            SessionLockedSinceText: "2026-04-18 07:48:00 +02:00");
    }

    public NetworkConnectivitySnapshot CreateNetworkConnectivitySnapshot(string? host = null)
    {
        var portAuthentication = CreatePortAuthenticationSnapshot(host);
        return new NetworkConnectivitySnapshot(
            PrimaryConnectionText: "LAN",
            PrimaryAdapterText: "Intel(R) Ethernet Connection I219-LM",
            WiFiSsidText: "Not connected",
            VpnStatusText: "Not detected",
            VpnProviderText: "-",
            IsCheckpointVpnDetected: false,
            PortAuthenticationStatusText: portAuthentication.OverallStatusText,
            PortAuthenticationDetailText: portAuthentication.OverallDetailText);
    }

    public PortAuthenticationSnapshot CreatePortAuthenticationSnapshot(string? host = null)
    {
        var normalizedHost = NormalizeHost(host);
        var fqdn = $"{normalizedHost.ToLowerInvariant()}.demo.example";
        var hasIssues = normalizedHost.EndsWith("02", StringComparison.OrdinalIgnoreCase) ||
                        normalizedHost.Contains("KIOSK", StringComparison.OrdinalIgnoreCase);

        if (hasIssues)
        {
            return new PortAuthenticationSnapshot(
                CapturedAtUtc: ReferenceTimeUtc,
                OverallStatusText: "Unhealthy",
                OverallStatusLevel: "Red",
                OverallDetailText: "802.1X is configured but authentication is currently failing because the computer certificate does not match the client FQDN and recent Wired AutoConfig events report certificate validation issues.",
                ApplicabilityText: "Applicable",
                Fqdn: fqdn,
                ActiveInterfaceName: "Ethernet",
                ActiveInterfaceDescription: "Intel(R) Ethernet Connection I219-LM",
                AuthenticationStateText: "Not authenticated",
                TracingModeText: "Disabled",
                LastSuccessfulAuthenticationText: "No successful wired authentication event found in the last 7 days.",
                Checks:
                [
                    new PortAuthenticationCheckEntry("Applicability", "Applicable", "Green", "An active wired adapter is present."),
                    new PortAuthenticationCheckEntry("Services", "Healthy", "Green", "dot3svc and EapHost are both running."),
                    new PortAuthenticationCheckEntry("Profile", "Healthy", "Green", "A wired 802.1X profile is present and XML parsing succeeded."),
                    new PortAuthenticationCheckEntry("Certificate", "Unhealthy", "Red", "A client authentication certificate exists, but SAN/CN does not match the local FQDN."),
                    new PortAuthenticationCheckEntry("Authentication state", "Unhealthy", "Red", "The interface reports 'Not authenticated'."),
                    new PortAuthenticationCheckEntry("Events", "Unhealthy", "Red", "Recent Wired AutoConfig and CAPI2 events indicate certificate validation failures.")
                ],
                Profiles:
                [
                    new PortAuthenticationProfileEntry("Corp Wired 802.1X", "Ethernet", "machineOnly", "preLogon", "Yes", "Yes", "EAP-TLS", "Valid XML", "Green")
                ],
                Certificates:
                [
                    new PortAuthenticationCertificateEntry(
                        Subject: "CN=DEMO-CLIENT-02.demo.example",
                        SanDns: "wronghost.demo.example",
                        Thumbprint: "9A9A9A9A9A9A9A9A9A9A9A9A9A9A9A9A9A9A9A9A",
                        Issuer: "CN=Contoso Issuing CA 01",
                        StoreName: @"LocalMachine\My",
                        HasPrivateKeyText: "Yes",
                        ValidityText: "Valid until 2027-10-31",
                        ChainStatusText: "Chain valid",
                        FqdnMatchText: "No",
                        StatusLevel: "Red")
                ],
                Events:
                [
                    new PortAuthenticationEventEntry(
                        ReferenceTimeUtc.AddMinutes(-30),
                        "Microsoft-Windows-Wired-AutoConfig/Operational",
                        12013,
                        "Error",
                        "Red",
                        "802.1X authentication failed during computer authentication.",
                        "Verify the wired profile and the machine certificate presented for EAP-TLS.",
                        "Demo event: the wired 802.1X handshake failed during certificate-based computer authentication."),
                    new PortAuthenticationEventEntry(
                        ReferenceTimeUtc.AddMinutes(-28),
                        "Microsoft-Windows-CAPI2/Operational",
                        11,
                        "Error",
                        "Red",
                        "Certificate chain or identity validation failed.",
                        "Review the certificate chain, EKU, SAN/CN and trust path on the client.",
                        "Demo event: certificate validation failed because the requested identity does not match the local FQDN.")
                ]);
        }

        return new PortAuthenticationSnapshot(
            CapturedAtUtc: ReferenceTimeUtc,
            OverallStatusText: "Healthy",
            OverallStatusLevel: "Green",
            OverallDetailText: "The active wired interface is authenticated, a machine EAP-TLS profile is present, and a valid client authentication certificate matches the local FQDN.",
            ApplicabilityText: "Applicable",
            Fqdn: fqdn,
            ActiveInterfaceName: "Ethernet",
            ActiveInterfaceDescription: "Intel(R) Ethernet Connection I219-LM",
            AuthenticationStateText: "Authenticated",
            TracingModeText: "Disabled",
            LastSuccessfulAuthenticationText: "2026-04-18 08:03:00Z | Wired AutoConfig reported successful authentication.",
            Checks:
            [
                new PortAuthenticationCheckEntry("Applicability", "Applicable", "Green", "An active wired adapter is present."),
                new PortAuthenticationCheckEntry("Services", "Healthy", "Green", "dot3svc and EapHost are both running."),
                new PortAuthenticationCheckEntry("Profile", "Healthy", "Green", "A wired 802.1X profile is present and XML parsing succeeded."),
                new PortAuthenticationCheckEntry("Certificate", "Healthy", "Green", "A valid machine client-authentication certificate matches the local FQDN."),
                new PortAuthenticationCheckEntry("Authentication state", "Healthy", "Green", "The interface reports 'Authenticated'."),
                new PortAuthenticationCheckEntry("Events", "Healthy", "Green", "No recent blocking Wired AutoConfig, EapHost or CAPI2 errors were found.")
            ],
            Profiles:
            [
                new PortAuthenticationProfileEntry("Corp Wired 802.1X", "Ethernet", "machineOnly", "preLogon", "Yes", "Yes", "EAP-TLS", "Valid XML", "Green")
            ],
            Certificates:
            [
                new PortAuthenticationCertificateEntry(
                    Subject: $"CN={normalizedHost}.demo.example",
                    SanDns: fqdn,
                    Thumbprint: "1111111111111111111111111111111111111111",
                    Issuer: "CN=Contoso Issuing CA 01",
                    StoreName: @"LocalMachine\My",
                    HasPrivateKeyText: "Yes",
                    ValidityText: "Valid until 2027-12-31",
                    ChainStatusText: "Chain valid",
                    FqdnMatchText: "Yes",
                    StatusLevel: "Green")
            ],
            Events:
            [
                new PortAuthenticationEventEntry(
                    ReferenceTimeUtc.AddMinutes(-12),
                    "Microsoft-Windows-Wired-AutoConfig/Operational",
                    11004,
                    "Information",
                    "Green",
                    "Wired authentication completed successfully.",
                    "No action required.",
                    "Demo event: wired authentication completed successfully using machine EAP-TLS.")
            ]);
    }

    public DeliveryOptimizationSnapshot CreateDeliveryOptimizationSnapshot()
    {
        return new DeliveryOptimizationSnapshot(
            IsAvailable: true,
            CapturedAtUtc: ReferenceTimeUtc,
            SourceStats:
            [
                new DeliveryOptimizationSourceStat("HTTP/CDN", 73400320, 4),
                new DeliveryOptimizationSourceStat("Peer (LAN)", 14680064, 2)
            ],
            Transfers:
            [
                new DeliveryOptimizationTransferEntry(ReferenceTimeUtc.AddMinutes(-30), "HTTP/CDN", 62914560, "Feature on Demand content"),
                new DeliveryOptimizationTransferEntry(ReferenceTimeUtc.AddMinutes(-22), "Peer (LAN)", 14680064, "Microsoft 365 Apps delta")
            ],
            Notes:
            [
                "Delivery Optimization telemetry is simulated.",
                "The peer summary represents one active LAN peer."
            ],
            SupportsTimeRangeFiltering: true,
            DataStartUtc: ReferenceTimeUtc.AddDays(-1),
            DataEndUtc: ReferenceTimeUtc,
            CurrentMetrics:
            [
                new NameValueItem("DownloadMode", "2"),
                new NameValueItem("HttpBytes", "73400320"),
                new NameValueItem("PeerBytes", "14680064")
            ],
            MonthlyMetrics:
            [
                new NameValueItem("TotalBytes", "209715200"),
                new NameValueItem("PeerBytes", "62914560")
            ],
            Configuration:
            [
                new NameValueItem("DODownloadMode", "2"),
                new NameValueItem("DOGroupID", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
            ],
            PeerStatuses:
            [
                new DeliveryOptimizationPeerStatus("Microsoft 365 Apps delta", "Downloading", 3, 1, 14680064, 0, "PeerType=LAN")
            ],
            ActiveJobs:
            [
                new DeliveryOptimizationJobStatus("Feature on Demand content", "Downloading", 104857600, 62914560, 262144, "HTTP/CDN fallback after peer search")
            ]);
    }

    public IReadOnlyList<IntuneLogEntry> CreateLogEntries(string logName, int maxEntries)
    {
        var entries =
            new[]
            {
                new IntuneLogEntry(logName, ReferenceTimeUtc.AddMinutes(-18), 1001, "Information", "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider", "Demo compliance sync completed successfully."),
                new IntuneLogEntry(logName, ReferenceTimeUtc.AddMinutes(-7), 1002, "Warning", "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider", "Demo IME assignment refresh is waiting for the next maintenance window."),
                new IntuneLogEntry(logName, ReferenceTimeUtc.AddMinutes(-2), 1003, "Information", "IntuneManagementExtension", "Demo inventory snapshot uploaded.")
            };

        return entries.Take(Math.Max(1, maxEntries)).ToArray();
    }

    public IReadOnlyList<MdmEventAnalysisEntry> CreateMdmEvents(int maxEntries)
    {
        var entries =
            new[]
            {
                new MdmEventAnalysisEntry(
                    "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin",
                    ReferenceTimeUtc.AddMinutes(-20),
                    410,
                    201,
                    "Information",
                    "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider",
                    MdmEventSeverity.Information,
                    false,
                    "Demo policy processing completed.",
                    "0x00000000",
                    "Success.",
                    "PolicySet",
                    "System",
                    "./Device/Vendor/MSFT/Policy/Config/System/AllowTelemetry",
                    "11111111-1111-1111-1111-111111111111",
                    "No action required.",
                    "Demo policy processing completed successfully."),
                new MdmEventAnalysisEntry(
                    "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin",
                    ReferenceTimeUtc.AddMinutes(-11),
                    411,
                    404,
                    "Error",
                    "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider",
                    MdmEventSeverity.Error,
                    true,
                    "Demo policy reported a missing optional file.",
                    "0x80070002",
                    "The system cannot find the file specified.",
                    "Homepage",
                    "Browser",
                    "./Device/Vendor/MSFT/Policy/Config/Browser/Homepage",
                    "11111111-1111-1111-1111-111111111111",
                    "Verify referenced content files in the deployment package.",
                    "Synthetic demo failure for troubleshooting exercises."),
                new MdmEventAnalysisEntry(
                    "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin",
                    ReferenceTimeUtc.AddMinutes(-4),
                    412,
                    409,
                    "Warning",
                    "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider",
                    MdmEventSeverity.Warning,
                    true,
                    "Demo remediation retry scheduled.",
                    "0x87D1FDE8",
                    "A retry is required.",
                    "DeviceLock",
                    "Security",
                    "./Device/Vendor/MSFT/Policy/Config/DeviceLock/MaxDevicePasswordFailedAttempts",
                    "11111111-1111-1111-1111-111111111111",
                    "Retry after the next policy sync.",
                    "Demo remediation will retry automatically.")
            };

        return entries.Take(Math.Max(1, maxEntries)).ToArray();
    }

    public EnrollmentStatus CreateEnrollmentStatus(string? host)
    {
        var normalizedHost = NormalizeHost(host);
        return new EnrollmentStatus(
            Host: normalizedHost,
            IsLocalHost: false,
            WinRmAvailable: true,
            IsAdminContext: true,
            EnrollmentDetected: true,
            LastSyncText: "2026-04-18 07:55:00Z",
            RegistrationSummary: "AzureAdJoined : YES",
            EnrollmentIds: ["11111111-1111-1111-1111-111111111111"],
            Checks:
            [
                "Administrative context confirmed.",
                "Primary MDM enrollment detected.",
                "Demo service is simulating Intune enrollment state."
            ],
            Warnings: [],
            Artifacts:
            [
                new EnrollmentArtifact("Registry", @"HKLM:\SOFTWARE\Microsoft\Enrollments\11111111-1111-1111-1111-111111111111", "Primary demo MDM enrollment", "11111111-1111-1111-1111-111111111111", true)
            ],
            EnrollmentUrls: new EnrollmentUrlsStatus(
                TenantInfoDetected: true,
                AreConfigured: true,
                AreExpected: true,
                Summary: "Enrollment URLs are configured correctly for the demo tenant.",
                Checks:
                [
                    "MdmEnrollmentUrl matches the expected Intune discovery endpoint.",
                    "TermsOfUseUrl matches the expected portal endpoint.",
                    "ComplianceUrl matches the expected compliance endpoint."
                ],
                Warnings: [],
                EnrollmentUrl: EnrollmentUrlTargets.EnrollmentUrl,
                TermsOfUseUrl: EnrollmentUrlTargets.TermsOfUseUrl,
                ComplianceUrl: EnrollmentUrlTargets.ComplianceUrl,
                CanRepair: true),
            CanTriggerSync: true,
            CanReenroll: true);
    }

    public EnrollmentRepairPreview CreateEnrollmentRepairPreview(string? host)
    {
        var normalizedHost = NormalizeHost(host);
        return new EnrollmentRepairPreview(
            Host: normalizedHost,
            CanExecute: true,
            ConfirmationText: $"REENROLL {normalizedHost}",
            Summary: "Demo preview found one removable enrollment artifact.",
            Blockers: [],
            Steps:
            [
                "Remove stale demo enrollment registry keys.",
                "Start DeviceEnroller.exe with the standard MDM arguments.",
                "Verify that the demo enrollment tasks are recreated."
            ],
            ArtifactsToRemove:
            [
                new EnrollmentArtifact("Registry", @"HKLM:\SOFTWARE\Microsoft\Enrollments\11111111-1111-1111-1111-111111111111", "Demo enrollment root", "11111111-1111-1111-1111-111111111111", true)
            ]);
    }

    public ImeLogTimelineSnapshot CreateImeLogTimelineSnapshot()
    {
        return new ImeLogTimelineSnapshot("demo-ime-fingerprint-v1", CreateImeTimelineEntries());
    }

    public ImeLogAnalysisResult CreateImeLogAnalysisResult()
    {
        var timeline = CreateImeTimelineEntries();
        return new ImeLogAnalysisResult("demo-ime-fingerprint-v1", timeline, CreateImeApplicationStatuses());
    }

    public IReadOnlyList<ImeLogTimelineEntry> CreateImeTimelineEntries()
    {
        return
        [
            new ImeLogTimelineEntry(
                TimeCreated: ReferenceTimeUtc.AddMinutes(-19),
                Severity: "Information",
                Component: "AppWorkload",
                Message: "Get policies = [{\"Id\":\"policy-01\",\"Name\":\"7-Zip\"}]",
                SourceFile: "AppWorkload.log",
                LineNumber: 142,
                RawLine: "<![LOG[Get policies = [{\"Id\":\"policy-01\",\"Name\":\"7-Zip\"}]]LOG]!>",
                IsPolicyPayload: true,
                PolicyJson: "[{\"Id\":\"policy-01\",\"Name\":\"7-Zip\"}]",
                Flow: "Policy Sync",
                Phase: "policy_sync",
                Effect: "Fetch assignments",
                CorrelationSummary: "App 7-Zip",
                EntityType: "App",
                EntityId: "11111111-1111-1111-1111-111111111111",
                PolicyId: "policy-01",
                SessionId: "session-01"),
            new ImeLogTimelineEntry(
                TimeCreated: ReferenceTimeUtc.AddMinutes(-6),
                Severity: "Warning",
                Component: "AgentExecutor",
                Message: "Install command returned synthetic exit code 0x87D300C9.",
                SourceFile: "IntuneManagementExtension.log",
                LineNumber: 918,
                RawLine: "<![LOG[Install command returned synthetic exit code 0x87D300C9.]LOG]!>",
                IsPolicyPayload: false,
                PolicyJson: string.Empty,
                Flow: "App Install",
                Phase: "enforcement",
                Effect: "Retry pending",
                CorrelationSummary: "App Contoso VPN Client",
                EntityType: "App",
                EntityId: "22222222-2222-2222-2222-222222222222",
                PolicyId: "policy-02",
                SessionId: "session-02",
                ResultCode: "0x87D300C9")
        ];
    }

    public IReadOnlyList<ImeApplicationStatusEntry> CreateImeApplicationStatuses()
    {
        return
        [
            new ImeApplicationStatusEntry(
                AppId: "11111111-1111-1111-1111-111111111111",
                AppName: "7-Zip 24.08 (Demo)",
                Intent: "Required",
                TargetInstallContext: "System",
                InstallStatus: "Installed",
                LastUpdated: ReferenceTimeUtc.AddMinutes(-15),
                ResultCode: "0x00000000",
                SourceFile: "AppWorkload.log",
                LastMessage: "Application installed successfully in demo mode.",
                IsInstalledForAnyIdentity: true,
                IdentityStatuses:
                [
                    new ImeApplicationIdentityStatusEntry(
                        "00000000-0000-0000-0000-000000000000",
                        "System",
                        "Installed",
                        ReferenceTimeUtc.AddMinutes(-15),
                        "0x00000000",
                        "Registry Win32Apps",
                        "applicabilitystate=0 dependency=satisfied")
                ]),
            new ImeApplicationStatusEntry(
                AppId: "22222222-2222-2222-2222-222222222222",
                AppName: "Contoso VPN Client (Demo)",
                Intent: "Available",
                TargetInstallContext: "User",
                InstallStatus: "Failed",
                LastUpdated: ReferenceTimeUtc.AddMinutes(-6),
                ResultCode: "0x87D300C9",
                SourceFile: "IntuneManagementExtension.log",
                LastMessage: "Synthetic install failure used for demo troubleshooting.",
                IsInstalledForAnyIdentity: false,
                IdentityStatuses:
                [
                    new ImeApplicationIdentityStatusEntry(
                        "S-1-5-21-demo",
                        "User",
                        "Failed",
                        ReferenceTimeUtc.AddMinutes(-6),
                        "0x87D300C9",
                        "Registry Win32Apps",
                        "applicabilitystate=0 dependency=missing")
                ])
        ];
    }

    public IntunePolicyResultReport CreatePolicyResultReport(string? host, string outputDirectory)
    {
        var normalizedHost = NormalizeHost(host);
        return new IntunePolicyResultReport(
            Host: normalizedHost,
            GeneratedAtUtc: ReferenceTimeUtc,
            ReportDirectory: outputDirectory,
            XmlPath: Path.Combine(outputDirectory, "MDMDiagReport.xml"),
            HtmlPath: Path.Combine(outputDirectory, "MDMDiagReport.html"),
            Source: "Demo",
            Summary: new IntunePolicyResultSummary(3, 2, 1, 0, 2, 1, 0),
            Entries:
            [
                new IntunePolicyResultEntry("Device", "Browser", "Homepage", "./Device/Vendor/MSFT/Policy/Config/Browser/Homepage", "https://intranet.demo.example", "Applied", "0x00000000"),
                new IntunePolicyResultEntry("Device", "DeviceLock", "MaxDevicePasswordFailedAttempts", "./Device/Vendor/MSFT/Policy/Config/DeviceLock/MaxDevicePasswordFailedAttempts", "10", "Applied", "0x00000000"),
                new IntunePolicyResultEntry("User", "Defender", "PUAProtection", "./User/Vendor/MSFT/Policy/Config/Defender/PUAProtection", "1", "Failed", "0x87D1FDE8", AdditionalDetails: "Synthetic retry pending for demo mode.")
            ],
            ExportHtmlPath: Path.Combine(outputDirectory, "intune-policy-result.html"),
            ExportJsonPath: Path.Combine(outputDirectory, "intune-policy-result.json"),
            Warnings: ["Demo mode returns simulated policy-result content."],
            Timings: ["Demo policy-result generation completed in 12 ms."]);
    }

    public PowerStateSnapshot CreatePowerStateSnapshot(string? host, string? activeSchemeId = null)
    {
        var normalizedHost = NormalizeHost(host);
        var effectiveSchemeId = string.IsNullOrWhiteSpace(activeSchemeId)
            ? "381b4222-f694-41f0-9685-ff5bb260df2e"
            : activeSchemeId;
        var schemes =
            new[]
            {
                new PowerSchemeSnapshot("381b4222-f694-41f0-9685-ff5bb260df2e", "Balanced", effectiveSchemeId == "381b4222-f694-41f0-9685-ff5bb260df2e"),
                new PowerSchemeSnapshot("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "High performance", effectiveSchemeId == "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
                new PowerSchemeSnapshot("a1841308-3541-4fab-bc81-f71556f20b4a", "Power saver", effectiveSchemeId == "a1841308-3541-4fab-bc81-f71556f20b4a")
            };

        var activeScheme = schemes.First(scheme => scheme.IsActive);
        return new PowerStateSnapshot(
            Host: normalizedHost,
            IsLocalHost: false,
            ActiveSchemeId: activeScheme.SchemeId,
            ActiveSchemeName: activeScheme.Name,
            Schemes: schemes,
            Warnings: ["Demo mode does not change the actual power plan on any machine."]);
    }

    public BitLockerHostSnapshot CreateBitLockerSnapshot(string? host)
    {
        var normalizedHost = NormalizeHost(host);
        return new BitLockerHostSnapshot(
            normalizedHost,
            normalizedHost,
            ReferenceTimeUtc,
            new BitLockerCapabilitySnapshot(
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                []),
            [
                new BitLockerPolicySettingSnapshot(
                    "RequireDeviceEncryption",
                    "Enabled",
                    "MDM (Intune)",
                    "Encryption",
                    @"HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\BitLocker",
                    "Encryption required"),
                new BitLockerPolicySettingSnapshot(
                    "EncryptionMethodWithXtsOs",
                    "7",
                    "Group Policy",
                    "Operating system drive",
                    @"HKLM:\SOFTWARE\Policies\Microsoft\FVE",
                    "XTS-AES 256-bit")
            ],
            true,
            true,
            false,
            [
                new BitLockerVolumeSnapshot(
                    "C:",
                    "OperatingSystem",
                    "Protected",
                    "FullyEncrypted",
                    "Unlocked",
                    100,
                    "XtsAes256",
                    "Disabled",
                    null,
                    "Green",
                    "Compliant",
                    "No unresolved BitLocker recovery event was detected.",
                    "AD DS: no local evidence | Microsoft Entra: no local evidence",
                    "Configured: AD DS, Microsoft Entra",
                    "AD DS: no local evidence | Microsoft Entra: no local evidence",
                    [
                        new BitLockerBackupTargetAssessmentSnapshot("AD DS", true, null, false, "ConfiguredButNoEvidence", "AD DS is configured by local policy, but no local escrow proof is evaluated."),
                        new BitLockerBackupTargetAssessmentSnapshot("MBAM", false, null, false, "NotConfigured", "Target is not configured by local policy."),
                        new BitLockerBackupTargetAssessmentSnapshot("Microsoft Entra", true, null, false, "ConfiguredButNoEvidence", "Microsoft Entra is configured by local MDM recovery policy, but no local escrow proof was found.")
                    ],
                    true,
                    true,
                    false,
                    [
                        new BitLockerProtectorSnapshot("tpm-c", "Tpm", "TPM", false, false, "Not applicable"),
                        new BitLockerProtectorSnapshot("rec-c-1", "RecoveryPassword", "Recovery password", true, true, "Configured: AD DS, Microsoft Entra"),
                        new BitLockerProtectorSnapshot("rec-c-2", "RecoveryPassword", "Recovery password", true, true, "Configured: AD DS, Microsoft Entra")
                    ]),
                new BitLockerVolumeSnapshot(
                    "D:",
                    "FixedData",
                    "Protection suspended",
                    "FullyEncrypted",
                    "Unlocked",
                    100,
                    "XtsAes128",
                    "Enabled",
                    2,
                    "Yellow",
                    "Recovered",
                    "A later recovery-password event indicates that the previous recovery state was cleared.",
                    "MBAM: success evidence present",
                    "Configured: MBAM",
                    "MBAM: success evidence present",
                    [
                        new BitLockerBackupTargetAssessmentSnapshot("AD DS", false, null, false, "NotConfigured", "Target is not configured by local policy."),
                        new BitLockerBackupTargetAssessmentSnapshot("MBAM", true, true, false, "ConfiguredAndSuccessEvidencePresent", "MBAM success event 29 found."),
                        new BitLockerBackupTargetAssessmentSnapshot("Microsoft Entra", false, null, false, "NotConfigured", "Target is not configured by local policy.")
                    ],
                    true,
                    false,
                    true,
                    [
                        new BitLockerProtectorSnapshot("rec-d-1", "RecoveryPassword", "Recovery password", true, true, "Configured: MBAM")
                    ])
            ],
            2,
            1,
            1,
            1,
            0,
            "Yellow");
    }

    public DefenderSnapshot CreateDefenderSnapshot(string? host)
    {
        var normalizedHost = NormalizeHost(host);
        var latestVersionInfo = new DefenderLatestVersionInfo(
            SourceUrl: "https://www.microsoft.com/en-us/wdsi/defenderupdates",
            ReleaseNotesUrl: "https://www.microsoft.com/en-us/wdsi/definitions/antimalware-definition-release-notes",
            RetrievedAtUtc: ReferenceTimeUtc,
            SecurityIntelligenceVersion: "1.421.999.0",
            EngineVersion: "1.1.24000.1",
            PlatformVersion: "4.18.24000.6",
            ReleasedAtUtc: ReferenceTimeUtc.AddDays(-1));

        return new DefenderSnapshot(
            Host: normalizedHost,
            MachineName: normalizedHost,
            CapturedAtUtc: ReferenceTimeUtc,
            IsLocalHost: false,
            IsManaged: true,
            ManagedBy: "MDM (Intune)",
            Protection: new DefenderProtectionStatus(true, true, true, true, true, true, true, "Normal"),
            Versions: new DefenderVersionInfo(
                EngineVersion: "1.1.24000.1",
                ProductVersion: "4.18.24000.6",
                AntivirusSignatureVersion: "1.421.123.0",
                AntispywareSignatureVersion: "1.421.123.0",
                NisEngineVersion: "1.1.24000.1",
                NisSignatureVersion: "1.421.123.0",
                SignatureLastUpdatedUtc: ReferenceTimeUtc.AddHours(-8),
                SignatureAgeHours: 8,
                SignaturesOutdated: false),
            Scans: new DefenderScanInfo(
                QuickScanStartUtc: ReferenceTimeUtc.AddHours(-2),
                QuickScanEndUtc: ReferenceTimeUtc.AddHours(-1),
                FullScanStartUtc: ReferenceTimeUtc.AddDays(-7),
                FullScanEndUtc: ReferenceTimeUtc.AddDays(-7).AddHours(1),
                LastScanUtc: ReferenceTimeUtc.AddHours(-1)),
            ActiveDetectionCount: 1,
            ActiveHighOrCriticalDetectionCount: 0,
            HealthLevel: "Yellow",
            HealthSummary: "One low-severity demo detection remains active.",
            Notes:
            [
                "Demo mode simulates a managed Microsoft Defender profile.",
                "No live security state was queried."
            ],
            LatestVersionInfo: latestVersionInfo);
    }

    public DefenderSettingsSnapshot CreateDefenderSettingsSnapshot()
    {
        return new DefenderSettingsSnapshot(
            CapturedAtUtc: ReferenceTimeUtc,
            Source: "Demo",
            Settings:
            [
                new DefenderSettingItem("DisableRealtimeMonitoring", "False"),
                new DefenderSettingItem("PUAProtection", "1"),
                new DefenderSettingItem("MAPSReporting", "Advanced")
            ],
            Notes: ["Defender settings are simulated in demo mode."],
            AsrRules:
            [
                new DefenderAsrRuleItem("D4F940AB-401B-4EFC-AADC-AD5F3C50688A", "Block Office child processes", "Block", string.Empty)
            ],
            Exclusions:
            [
                new DefenderExclusionItem("Path", @"C:\DemoTools")
            ]);
    }

    public IReadOnlyList<DefenderDetectionEntry> CreateDefenderDetections()
    {
        return
        [
            new DefenderDetectionEntry(
                DetectedAtUtc: ReferenceTimeUtc.AddHours(-5),
                LastStatusChangeUtc: ReferenceTimeUtc.AddHours(-4),
                ThreatName: "Demo.TestFile",
                ThreatId: 2147483001,
                Severity: "Low",
                Category: "Testing",
                Action: "Quarantined",
                ActionSuccess: true,
                IsActive: true,
                Source: "Realtime protection",
                Details: "Synthetic demo detection used for UI preview.")
        ];
    }

    public DefenderDeviceControlSnapshot CreateDefenderDeviceControlSnapshot()
    {
        return new DefenderDeviceControlSnapshot(
            CapturedAtUtc: ReferenceTimeUtc,
            Source: "Demo",
            Notes: ["Device control events are simulated for demo mode."],
            Events:
            [
                new DefenderDeviceControlEventEntry(
                    TimeCreatedUtc: ReferenceTimeUtc.AddMinutes(-28),
                    EventId: 3077,
                    Provider: "Microsoft-Windows-Windows Defender",
                    LogName: "Microsoft-Windows-Windows Defender/Operational",
                    Level: "Warning",
                    DeviceType: "USB Storage",
                    DeviceName: "Demo USB Drive",
                    FriendlyName: "Demo USB Drive",
                    Manufacturer: "DemoVendor",
                    DeviceId: @"USBSTOR\DISK&VEN_DEMOVENDOR&PROD_DEMO_USB",
                    DeviceInstanceId: @"USB\VID_1234&PID_5678\DEMO0001",
                    HardwareIds: @"USB\VID_1234&PID_5678",
                    VendorId: "1234",
                    ProductId: "5678",
                    SerialNumber: "DEMO0001",
                    ClassGuid: "{36fc9e60-c465-11cf-8056-444553540000}",
                    User: DemoUserPrincipalName,
                    Sid: "S-1-5-21-demo",
                    PolicyName: "Block removable storage",
                    PolicyId: "policy-usb-block",
                    PolicyRuleId: "rule-usb-01",
                    PolicyVerdict: "Blocked",
                    Access: "Write",
                    Action: "Block",
                    IsBlocked: true,
                    Message: "Demo device control blocked write access to the removable drive.")
            ],
            DeviceSummaries:
            [
                new DefenderDeviceControlDeviceSummary(
                    DeviceKey: "demo-usb-drive",
                    DeviceType: "USB Storage",
                    DisplayName: "Demo USB Drive",
                    BlockedCount: 1,
                    FirstBlockedUtc: ReferenceTimeUtc.AddMinutes(-28),
                    LastBlockedUtc: ReferenceTimeUtc.AddMinutes(-28),
                    DeviceId: @"USBSTOR\DISK&VEN_DEMOVENDOR&PROD_DEMO_USB",
                    DeviceInstanceId: @"USB\VID_1234&PID_5678\DEMO0001",
                    HardwareIds: @"USB\VID_1234&PID_5678",
                    VendorId: "1234",
                    ProductId: "5678",
                    SerialNumber: "DEMO0001",
                    ClassGuid: "{36fc9e60-c465-11cf-8056-444553540000}",
                    PolicyName: "Block removable storage",
                    PolicyId: "policy-usb-block",
                    PolicyRuleId: "rule-usb-01",
                    PolicyVerdict: "Blocked",
                    Access: "Write",
                    LastUser: DemoUserPrincipalName)
            ]);
    }

    public DemoWindowsUpdateSnapshot CreateWindowsUpdateSnapshot(string? host)
    {
        var normalizedHost = NormalizeHost(host);
        return new DemoWindowsUpdateSnapshot(
            ReportingEventsLines:
            [
                $"1001 2026-04-18 07:42:11.000 1 43 [Scan] MU SyncService kb5037765 200 0x00000000 KB5037765 Succeeded Client Search Demo scan completed successfully demo.correlation.001",
                $"1002 2026-04-18 07:47:18.000 1 44 [Download] MU ContentService kb5037765 200 0x00000000 KB5037765 Succeeded Client Download Demo content download completed demo.correlation.002",
                $"1003 2026-04-18 08:03:27.000 1 31 [Install] MU Orchestrator kb5037765 200 0x00000000 KB5037765 PendingRestart Client Install Restart pending after demo installation demo.correlation.003"
            ],
            AvailableUpdates:
            [
                new DemoWindowsUpdateAvailableItem("2026-04 Cumulative Update for Windows 11 (KB5037765)", "Software", "Pending download", false, false, "KB5037765", false, true, true, "Security Updates", "2026-04-19 20:00:00 +02:00", "demo-kb5037765", 200),
                new DemoWindowsUpdateAvailableItem("Intel - Net - 1.0.3.14", "Driver", "Ready to install", false, false, "N/A", true, false, true, "Drivers", "No deadline", "demo-intel-net", 14)
            ],
            Providers:
            [
                new DemoWindowsUpdateProviderItem("Windows Update", "7971f918-a847-4430-9279-4a52d1efe18d", true, true, true, true),
                new DemoWindowsUpdateProviderItem("Microsoft Update", "9482f4b4-e343-43b6-b170-9a65bc822c77", false, true, true, true)
            ],
            HistoryEntries:
            [
                new DemoWindowsUpdateHistoryItem("2026-04-17 22:14:00", "Installation", "Succeeded", "0x00000000", "2026-04 Security Intelligence Update", "demo-defs-20260417", 1, "USO", "Windows Update"),
                new DemoWindowsUpdateHistoryItem("2026-04-15 19:02:00", "Installation", "Succeeded", "0x00000000", "2026-04 Cumulative Update Preview (KB5037001)", "demo-kb5037001", 143, "USO", "Windows Update", "Package_for_RollupFix~31bf3856ad364e35")
            ],
            BaseInstallProgressLines:
            [
                "[2026-04-18 08:04:10] Demo install pipeline initialized.",
                $"[2026-04-18 08:04:35] Host '{normalizedHost}' is waiting for a maintenance window."
            ],
            LastScanInfo: "Last scan: 2026-04-18 07:42:11 +02:00",
            DefaultInstallTaskState: "No install task started.",
            DefaultInstallTaskStatusText: "Task: Unknown",
            DefaultInstallTaskPhaseText: "Phase: unknown",
            DefaultInstallTaskDetail: "Select one or more updates to simulate an installation run.",
            IsInstallTaskRunning: false);
    }
}
