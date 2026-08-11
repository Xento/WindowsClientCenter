using System.Globalization;

namespace WindowsClientCenter.Plugins.DeviceOverview.Models;

public sealed class DeviceOverviewOptions
{
    public CloudDeviceOptions CloudDevice { get; init; } = new();

    public LocalSystemOptions LocalSystem { get; init; } = new();

    public PlatformSecurityOptions PlatformSecurity { get; init; } = new();

    public SystemRuntimeOptions SystemRuntime { get; init; } = new();

    public NetworkOptions Network { get; init; } = new();

    public ClientHealthOptions ClientHealth { get; init; } = new();

    public DeliveryOptimizationOptions DeliveryOptimization { get; init; } = new();

    public PortAuthenticationOptions PortAuthentication { get; init; } = new();

    public static DeviceOverviewOptions FromSettings(IReadOnlyDictionary<string, string> settings)
    {
        var options = new DeviceOverviewOptions();
        options.CloudDevice.Apply(settings, "cloudDevice");
        options.LocalSystem.Apply(settings, "localSystem");
        options.PlatformSecurity.Apply(settings, "platformSecurity");
        options.SystemRuntime.Apply(settings, "systemRuntime");
        options.Network.Apply(settings, "network");
        options.ClientHealth.Apply(settings, "clientHealth");
        options.DeliveryOptimization.Apply(settings, "deliveryOptimization");
        options.PortAuthentication.Apply(settings, "portAuthentication");

        options.LocalSystem.FreeDiskSpaceWarningGb = GetPositiveDouble(
            settings,
            "localSystem:freeDiskSpaceWarningGb",
            GetPositiveDouble(settings, "freeDiskSpaceWarningThresholdGb", options.LocalSystem.FreeDiskSpaceWarningGb));
        options.LocalSystem.FreeDiskSpaceCriticalGb = GetPositiveDouble(
            settings,
            "localSystem:freeDiskSpaceCriticalGb",
            GetPositiveDouble(settings, "freeDiskSpaceCriticalThresholdGb", options.LocalSystem.FreeDiskSpaceCriticalGb));
        if (options.LocalSystem.FreeDiskSpaceCriticalGb > options.LocalSystem.FreeDiskSpaceWarningGb)
        {
            (options.LocalSystem.FreeDiskSpaceWarningGb, options.LocalSystem.FreeDiskSpaceCriticalGb) =
                (options.LocalSystem.FreeDiskSpaceCriticalGb, options.LocalSystem.FreeDiskSpaceWarningGb);
        }

        options.SystemRuntime.UptimeWarningDays = GetPositiveDouble(
            settings,
            "systemRuntime:uptimeWarningDays",
            GetPositiveDouble(settings, "uptimeWarningThresholdDays", options.SystemRuntime.UptimeWarningDays));
        options.SystemRuntime.UptimeCriticalDays = GetPositiveDouble(
            settings,
            "systemRuntime:uptimeCriticalDays",
            GetPositiveDouble(settings, "uptimeCriticalThresholdDays", options.SystemRuntime.UptimeCriticalDays));
        if (options.SystemRuntime.UptimeCriticalDays < options.SystemRuntime.UptimeWarningDays)
        {
            (options.SystemRuntime.UptimeWarningDays, options.SystemRuntime.UptimeCriticalDays) =
                (options.SystemRuntime.UptimeCriticalDays, options.SystemRuntime.UptimeWarningDays);
        }

        var defender = options.ClientHealth.Checks.Defender;
        defender.SignatureWarningHours = GetPositiveDouble(settings, "clientHealth:checks:defender:signatureWarningHours", defender.SignatureWarningHours);
        defender.SignatureCriticalHours = GetPositiveDouble(settings, "clientHealth:checks:defender:signatureCriticalHours", defender.SignatureCriticalHours);
        if (defender.SignatureCriticalHours < defender.SignatureWarningHours)
        {
            (defender.SignatureWarningHours, defender.SignatureCriticalHours) =
                (defender.SignatureCriticalHours, defender.SignatureWarningHours);
        }

        defender.ScanWarningDays = GetPositiveDouble(settings, "clientHealth:checks:defender:scanWarningDays", defender.ScanWarningDays);
        return options;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> settings, string key, bool defaultValue)
    {
        return settings.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static double GetPositiveDouble(IReadOnlyDictionary<string, string> settings, string key, double defaultValue)
    {
        return settings.TryGetValue(key, out var value) &&
               double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
               parsed > 0
            ? parsed
            : defaultValue;
    }

    public sealed class CloudDeviceOptions
    {
        public bool Enabled { get; set; } = true;

        public bool ShowDevice { get; set; } = true;

        public bool ShowPlatform { get; set; } = true;

        public bool ShowCompliance { get; set; } = true;

        public bool ShowCloudLastSync { get; set; } = true;

        public bool ShowMdmLastSync { get; set; } = true;

        public bool ShowImeLastSync { get; set; } = true;

        public bool ShowIntuneStatus { get; set; } = true;

        internal void Apply(IReadOnlyDictionary<string, string> settings, string prefix)
        {
            Enabled = GetBool(settings, $"{prefix}:enabled", Enabled);
            ShowDevice = GetBool(settings, $"{prefix}:showDevice", ShowDevice);
            ShowPlatform = GetBool(settings, $"{prefix}:showPlatform", ShowPlatform);
            ShowCompliance = GetBool(settings, $"{prefix}:showCompliance", ShowCompliance);
            ShowCloudLastSync = GetBool(settings, $"{prefix}:showCloudLastSync", ShowCloudLastSync);
            ShowMdmLastSync = GetBool(settings, $"{prefix}:showMdmLastSync", ShowMdmLastSync);
            ShowImeLastSync = GetBool(settings, $"{prefix}:showImeLastSync", ShowImeLastSync);
            ShowIntuneStatus = GetBool(settings, $"{prefix}:showIntuneStatus", ShowIntuneStatus);
        }
    }

    public sealed class LocalSystemOptions
    {
        public bool Enabled { get; set; } = true;

        public bool ShowManufacturer { get; set; } = true;

        public bool ShowModel { get; set; } = true;

        public bool ShowSerialNumber { get; set; } = true;

        public bool ShowWindowsVersion { get; set; } = true;

        public bool ShowWindowsBuild { get; set; } = true;

        public bool ShowUpdateRing { get; set; } = true;

        public bool ShowPatchStatus { get; set; } = true;

        public bool ShowFreeDiskSpace { get; set; } = true;

        public double FreeDiskSpaceWarningGb { get; set; } = 20;

        public double FreeDiskSpaceCriticalGb { get; set; } = 10;

        internal void Apply(IReadOnlyDictionary<string, string> settings, string prefix)
        {
            Enabled = GetBool(settings, $"{prefix}:enabled", Enabled);
            ShowManufacturer = GetBool(settings, $"{prefix}:showManufacturer", ShowManufacturer);
            ShowModel = GetBool(settings, $"{prefix}:showModel", ShowModel);
            ShowSerialNumber = GetBool(settings, $"{prefix}:showSerialNumber", ShowSerialNumber);
            ShowWindowsVersion = GetBool(settings, $"{prefix}:showWindowsVersion", ShowWindowsVersion);
            ShowWindowsBuild = GetBool(settings, $"{prefix}:showWindowsBuild", ShowWindowsBuild);
            ShowUpdateRing = GetBool(settings, $"{prefix}:showUpdateRing", ShowUpdateRing);
            ShowPatchStatus = GetBool(settings, $"{prefix}:showPatchStatus", ShowPatchStatus);
            ShowFreeDiskSpace = GetBool(settings, $"{prefix}:showFreeDiskSpace", ShowFreeDiskSpace);
        }
    }

    public sealed class PlatformSecurityOptions
    {
        public bool Enabled { get; set; } = true;

        public bool ShowBitLocker { get; set; } = true;

        public bool ShowBitLockerDetail { get; set; } = true;

        public bool ShowTpm { get; set; } = true;

        public bool ShowTpmVersion { get; set; } = true;

        public bool ShowTpmDetail { get; set; } = true;

        public bool ShowSecureBoot { get; set; } = true;

        public bool ShowCredentialGuard { get; set; } = true;

        public bool ShowVbs { get; set; } = true;

        public bool ShowMemoryIntegrity { get; set; } = true;

        internal void Apply(IReadOnlyDictionary<string, string> settings, string prefix)
        {
            Enabled = GetBool(settings, $"{prefix}:enabled", Enabled);
            ShowBitLocker = GetBool(settings, $"{prefix}:showBitLocker", ShowBitLocker);
            ShowBitLockerDetail = GetBool(settings, $"{prefix}:showBitLockerDetail", ShowBitLockerDetail);
            ShowTpm = GetBool(settings, $"{prefix}:showTpm", ShowTpm);
            ShowTpmVersion = GetBool(settings, $"{prefix}:showTpmVersion", ShowTpmVersion);
            ShowTpmDetail = GetBool(settings, $"{prefix}:showTpmDetail", ShowTpmDetail);
            ShowSecureBoot = GetBool(settings, $"{prefix}:showSecureBoot", ShowSecureBoot);
            ShowCredentialGuard = GetBool(settings, $"{prefix}:showCredentialGuard", ShowCredentialGuard);
            ShowVbs = GetBool(settings, $"{prefix}:showVbs", ShowVbs);
            ShowMemoryIntegrity = GetBool(settings, $"{prefix}:showMemoryIntegrity", ShowMemoryIntegrity);
        }
    }

    public sealed class SystemRuntimeOptions
    {
        public bool Enabled { get; set; } = true;

        public bool ShowUptime { get; set; } = true;

        public bool ShowLastReboot { get; set; } = true;

        public bool ShowInstallDate { get; set; } = true;

        public bool ShowPendingReboot { get; set; } = true;

        public bool ShowPendingRebootDetail { get; set; } = true;

        public bool ShowWindowsUpdateRestart { get; set; } = true;

        public bool ShowScheduledRestartTime { get; set; } = true;

        public bool ShowSessionLock { get; set; } = true;

        public bool ShowLockedSince { get; set; } = true;

        public double UptimeWarningDays { get; set; } = 14;

        public double UptimeCriticalDays { get; set; } = 30;

        internal void Apply(IReadOnlyDictionary<string, string> settings, string prefix)
        {
            Enabled = GetBool(settings, $"{prefix}:enabled", Enabled);
            ShowUptime = GetBool(settings, $"{prefix}:showUptime", ShowUptime);
            ShowLastReboot = GetBool(settings, $"{prefix}:showLastReboot", ShowLastReboot);
            ShowInstallDate = GetBool(settings, $"{prefix}:showInstallDate", ShowInstallDate);
            ShowPendingReboot = GetBool(settings, $"{prefix}:showPendingReboot", ShowPendingReboot);
            ShowPendingRebootDetail = GetBool(settings, $"{prefix}:showPendingRebootDetail", ShowPendingRebootDetail);
            ShowWindowsUpdateRestart = GetBool(settings, $"{prefix}:showWindowsUpdateRestart", ShowWindowsUpdateRestart);
            ShowScheduledRestartTime = GetBool(settings, $"{prefix}:showScheduledRestartTime", ShowScheduledRestartTime);
            ShowSessionLock = GetBool(settings, $"{prefix}:showSessionLock", ShowSessionLock);
            ShowLockedSince = GetBool(settings, $"{prefix}:showLockedSince", ShowLockedSince);
        }
    }

    public sealed class NetworkOptions
    {
        public bool Enabled { get; set; } = true;

        public bool ShowConnectionType { get; set; } = true;

        public bool ShowActiveAdapter { get; set; } = true;

        public bool ShowWifiSsid { get; set; } = true;

        public bool ShowVpn { get; set; } = true;

        public bool ShowVpnProvider { get; set; } = true;

        public bool ShowPortAuthenticationSummary { get; set; } = true;

        internal void Apply(IReadOnlyDictionary<string, string> settings, string prefix)
        {
            Enabled = GetBool(settings, $"{prefix}:enabled", Enabled);
            ShowConnectionType = GetBool(settings, $"{prefix}:showConnectionType", ShowConnectionType);
            ShowActiveAdapter = GetBool(settings, $"{prefix}:showActiveAdapter", ShowActiveAdapter);
            ShowWifiSsid = GetBool(settings, $"{prefix}:showWifiSsid", ShowWifiSsid);
            ShowVpn = GetBool(settings, $"{prefix}:showVpn", ShowVpn);
            ShowVpnProvider = GetBool(settings, $"{prefix}:showVpnProvider", ShowVpnProvider);
            ShowPortAuthenticationSummary = GetBool(settings, $"{prefix}:showPortAuthenticationSummary", ShowPortAuthenticationSummary);
        }
    }

    public sealed class PortAuthenticationOptions
    {
        public bool Enabled { get; set; } = true;

        public bool ShowSummary { get; set; } = true;

        public bool ShowChecks { get; set; } = true;

        public bool ShowProfiles { get; set; } = true;

        public bool ShowCertificates { get; set; } = true;

        public bool ShowEvents { get; set; } = true;

        public bool ShowRemediation { get; set; } = true;

        internal void Apply(IReadOnlyDictionary<string, string> settings, string prefix)
        {
            Enabled = GetBool(settings, $"{prefix}:enabled", Enabled);
            ShowSummary = GetBool(settings, $"{prefix}:showSummary", ShowSummary);
            ShowChecks = GetBool(settings, $"{prefix}:showChecks", ShowChecks);
            ShowProfiles = GetBool(settings, $"{prefix}:showProfiles", ShowProfiles);
            ShowCertificates = GetBool(settings, $"{prefix}:showCertificates", ShowCertificates);
            ShowEvents = GetBool(settings, $"{prefix}:showEvents", ShowEvents);
            ShowRemediation = GetBool(settings, $"{prefix}:showRemediation", ShowRemediation);
        }
    }

    public sealed class ClientHealthOptions
    {
        public bool Enabled { get; set; } = true;

        public bool ShowOverallHealth { get; set; } = true;

        public bool ShowSummary { get; set; } = true;

        public ClientHealthChecksOptions Checks { get; } = new();

        internal void Apply(IReadOnlyDictionary<string, string> settings, string prefix)
        {
            Enabled = GetBool(settings, $"{prefix}:enabled", Enabled);
            ShowOverallHealth = GetBool(settings, $"{prefix}:showOverallHealth", ShowOverallHealth);
            ShowSummary = GetBool(settings, $"{prefix}:showSummary", ShowSummary);
            Checks.Apply(settings, $"{prefix}:checks");
        }
    }

    public sealed class ClientHealthChecksOptions
    {
        public DefenderCheckOptions Defender { get; } = new();

        public StatusCheckOptions EntraJoin { get; } = new();

        public StatusCheckOptions AdJoin { get; } = new();

        public StatusCheckOptions IntuneEnrollment { get; } = new();

        public SimpleCheckOptions EnrollmentUrls { get; } = new();

        public SimpleCheckOptions FreeDiskSpace { get; } = new();

        public SimpleCheckOptions Uptime { get; } = new();

        internal void Apply(IReadOnlyDictionary<string, string> settings, string prefix)
        {
            Defender.Apply(settings, $"{prefix}:defender");
            EntraJoin.Apply(settings, $"{prefix}:entraJoin");
            AdJoin.Apply(settings, $"{prefix}:adJoin");
            IntuneEnrollment.Apply(settings, $"{prefix}:intuneEnrollment");
            EnrollmentUrls.Apply(settings, $"{prefix}:enrollmentUrls");
            FreeDiskSpace.Apply(settings, $"{prefix}:freeDiskSpace");
            Uptime.Apply(settings, $"{prefix}:uptime");
        }
    }

    public class SimpleCheckOptions
    {
        public bool Enabled { get; set; } = true;

        internal virtual void Apply(IReadOnlyDictionary<string, string> settings, string prefix)
        {
            Enabled = GetBool(settings, $"{prefix}:enabled", Enabled);
        }
    }

    public class StatusCheckOptions : SimpleCheckOptions
    {
        public bool ShowStatus { get; set; } = true;

        internal override void Apply(IReadOnlyDictionary<string, string> settings, string prefix)
        {
            base.Apply(settings, prefix);
            ShowStatus = GetBool(settings, $"{prefix}:showStatus", ShowStatus);
        }
    }

    public sealed class DefenderCheckOptions : StatusCheckOptions
    {
        public bool ShowDetail { get; set; } = true;

        public bool ShowDefinitionAge { get; set; } = true;

        public double SignatureWarningHours { get; set; } = 36;

        public double SignatureCriticalHours { get; set; } = 72;

        public double ScanWarningDays { get; set; } = 14;

        internal override void Apply(IReadOnlyDictionary<string, string> settings, string prefix)
        {
            base.Apply(settings, prefix);
            ShowDetail = GetBool(settings, $"{prefix}:showDetail", ShowDetail);
            ShowDefinitionAge = GetBool(settings, $"{prefix}:showDefinitionAge", ShowDefinitionAge);
        }
    }

    public sealed class DeliveryOptimizationOptions
    {
        public bool Enabled { get; set; } = true;

        public bool ShowSummary { get; set; } = true;

        public bool ShowActiveJobs { get; set; } = true;

        public bool ShowCurrentMetrics { get; set; } = true;

        public bool ShowMonthlyMetrics { get; set; } = true;

        public bool ShowPeerSnapshot { get; set; } = true;

        public bool ShowConfiguration { get; set; } = true;

        public bool ShowSourceDistribution { get; set; } = true;

        public bool ShowTransferTimeline { get; set; } = true;

        public bool ShowNotes { get; set; } = true;

        internal void Apply(IReadOnlyDictionary<string, string> settings, string prefix)
        {
            Enabled = GetBool(settings, $"{prefix}:enabled", Enabled);
            ShowSummary = GetBool(settings, $"{prefix}:showSummary", ShowSummary);
            ShowActiveJobs = GetBool(settings, $"{prefix}:showActiveJobs", ShowActiveJobs);
            ShowCurrentMetrics = GetBool(settings, $"{prefix}:showCurrentMetrics", ShowCurrentMetrics);
            ShowMonthlyMetrics = GetBool(settings, $"{prefix}:showMonthlyMetrics", ShowMonthlyMetrics);
            ShowPeerSnapshot = GetBool(settings, $"{prefix}:showPeerSnapshot", ShowPeerSnapshot);
            ShowConfiguration = GetBool(settings, $"{prefix}:showConfiguration", ShowConfiguration);
            ShowSourceDistribution = GetBool(settings, $"{prefix}:showSourceDistribution", ShowSourceDistribution);
            ShowTransferTimeline = GetBool(settings, $"{prefix}:showTransferTimeline", ShowTransferTimeline);
            ShowNotes = GetBool(settings, $"{prefix}:showNotes", ShowNotes);
        }
    }
}
