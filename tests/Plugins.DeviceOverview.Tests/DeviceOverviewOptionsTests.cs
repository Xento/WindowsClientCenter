using WindowsClientCenter.Plugins.DeviceOverview.Models;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.DeviceOverview;

public sealed class DeviceOverviewOptionsTests
{
    [Fact]
    public void FromSettings_DefaultsKeepExistingBehaviorEnabled()
    {
        var options = DeviceOverviewOptions.FromSettings(new Dictionary<string, string>());

        Assert.True(options.CloudDevice.Enabled);
        Assert.True(options.LocalSystem.ShowFreeDiskSpace);
        Assert.True(options.PlatformSecurity.Enabled);
        Assert.True(options.SystemRuntime.ShowPendingReboot);
        Assert.True(options.Network.ShowVpnProvider);
        Assert.True(options.Network.ShowPortAuthenticationSummary);
        Assert.True(options.ClientHealth.Checks.Defender.Enabled);
        Assert.True(options.ClientHealth.Checks.FreeDiskSpace.Enabled);
        Assert.True(options.DeliveryOptimization.ShowTransferTimeline);
        Assert.True(options.PortAuthentication.ShowEvents);
        Assert.Equal(20, options.LocalSystem.FreeDiskSpaceWarningGb);
        Assert.Equal(10, options.LocalSystem.FreeDiskSpaceCriticalGb);
        Assert.Equal(14, options.SystemRuntime.UptimeWarningDays);
        Assert.Equal(30, options.SystemRuntime.UptimeCriticalDays);
        Assert.Equal(36, options.ClientHealth.Checks.Defender.SignatureWarningHours);
        Assert.Equal(72, options.ClientHealth.Checks.Defender.SignatureCriticalHours);
    }

    [Fact]
    public void FromSettings_AppliesGroupedVisibilityOptions()
    {
        var options = DeviceOverviewOptions.FromSettings(new Dictionary<string, string>
        {
            ["platformSecurity:enabled"] = "false",
            ["network:showVpnProvider"] = "false",
            ["network:showPortAuthenticationSummary"] = "false",
            ["clientHealth:checks:defender:enabled"] = "false",
            ["clientHealth:checks:defender:showDetail"] = "false",
            ["deliveryOptimization:showNotes"] = "false",
            ["portAuthentication:showEvents"] = "false"
        });

        Assert.False(options.PlatformSecurity.Enabled);
        Assert.True(options.Network.Enabled);
        Assert.False(options.Network.ShowVpnProvider);
        Assert.False(options.Network.ShowPortAuthenticationSummary);
        Assert.False(options.ClientHealth.Checks.Defender.Enabled);
        Assert.False(options.ClientHealth.Checks.Defender.ShowDetail);
        Assert.False(options.DeliveryOptimization.ShowNotes);
        Assert.False(options.PortAuthentication.ShowEvents);
    }

    [Fact]
    public void FromSettings_UsesGroupedThresholds()
    {
        var options = DeviceOverviewOptions.FromSettings(new Dictionary<string, string>
        {
            ["localSystem:freeDiskSpaceWarningGb"] = "50",
            ["localSystem:freeDiskSpaceCriticalGb"] = "25",
            ["systemRuntime:uptimeWarningDays"] = "10",
            ["systemRuntime:uptimeCriticalDays"] = "20",
            ["clientHealth:checks:defender:signatureWarningHours"] = "24",
            ["clientHealth:checks:defender:signatureCriticalHours"] = "48",
            ["clientHealth:checks:defender:scanWarningDays"] = "7"
        });

        Assert.Equal(50, options.LocalSystem.FreeDiskSpaceWarningGb);
        Assert.Equal(25, options.LocalSystem.FreeDiskSpaceCriticalGb);
        Assert.Equal(10, options.SystemRuntime.UptimeWarningDays);
        Assert.Equal(20, options.SystemRuntime.UptimeCriticalDays);
        Assert.Equal(24, options.ClientHealth.Checks.Defender.SignatureWarningHours);
        Assert.Equal(48, options.ClientHealth.Checks.Defender.SignatureCriticalHours);
        Assert.Equal(7, options.ClientHealth.Checks.Defender.ScanWarningDays);
    }

    [Fact]
    public void FromSettings_UsesLegacyThresholdKeysWhenGroupedKeysAreMissing()
    {
        var options = DeviceOverviewOptions.FromSettings(new Dictionary<string, string>
        {
            ["freeDiskSpaceWarningThresholdGb"] = "40",
            ["freeDiskSpaceCriticalThresholdGb"] = "15",
            ["uptimeWarningThresholdDays"] = "21",
            ["uptimeCriticalThresholdDays"] = "45"
        });

        Assert.Equal(40, options.LocalSystem.FreeDiskSpaceWarningGb);
        Assert.Equal(15, options.LocalSystem.FreeDiskSpaceCriticalGb);
        Assert.Equal(21, options.SystemRuntime.UptimeWarningDays);
        Assert.Equal(45, options.SystemRuntime.UptimeCriticalDays);
    }

    [Fact]
    public void FromSettings_NormalizesInvalidAndInvertedThresholds()
    {
        var options = DeviceOverviewOptions.FromSettings(new Dictionary<string, string>
        {
            ["localSystem:freeDiskSpaceWarningGb"] = "5",
            ["localSystem:freeDiskSpaceCriticalGb"] = "30",
            ["systemRuntime:uptimeWarningDays"] = "60",
            ["systemRuntime:uptimeCriticalDays"] = "30",
            ["clientHealth:checks:defender:signatureWarningHours"] = "96",
            ["clientHealth:checks:defender:signatureCriticalHours"] = "48",
            ["clientHealth:checks:defender:scanWarningDays"] = "-1"
        });

        Assert.Equal(30, options.LocalSystem.FreeDiskSpaceWarningGb);
        Assert.Equal(5, options.LocalSystem.FreeDiskSpaceCriticalGb);
        Assert.Equal(30, options.SystemRuntime.UptimeWarningDays);
        Assert.Equal(60, options.SystemRuntime.UptimeCriticalDays);
        Assert.Equal(48, options.ClientHealth.Checks.Defender.SignatureWarningHours);
        Assert.Equal(96, options.ClientHealth.Checks.Defender.SignatureCriticalHours);
        Assert.Equal(14, options.ClientHealth.Checks.Defender.ScanWarningDays);
    }
}
