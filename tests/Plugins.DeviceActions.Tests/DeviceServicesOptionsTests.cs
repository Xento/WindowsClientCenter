using WindowsClientCenter.Plugins.DeviceActions.Models;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.DeviceActions;

public sealed class DeviceServicesOptionsTests
{
    [Fact]
    public void FromSettings_UsesDefaults_WhenNoSettingsExist()
    {
        var options = DeviceServicesOptions.FromSettings(new Dictionary<string, string>());

        Assert.True(options.Categories.Count >= 2);
        Assert.Equal("All services", options.Categories[0].DisplayName);
        Assert.True(options.Categories[0].IncludeAllServices);
        Assert.Equal("MECM / Intune related", options.Categories[1].DisplayName);
        Assert.Contains("IntuneManagementExtension", options.Categories[1].ServiceNames);
    }

    [Fact]
    public void FromSettings_ParsesConfiguredCategories_InOrder()
    {
        var options = DeviceServicesOptions.FromSettings(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["filters:0:displayName"] = "Windows Update",
            ["filters:0:serviceNames"] = "wuauserv, BITS; DoSvc",
            ["filters:1:displayName"] = "ConfigMgr",
            ["filters:1:serviceNames"] = "CcmExec\r\nccmsetup"
        });

        Assert.Equal(3, options.Categories.Count);
        Assert.Equal("All services", options.Categories[0].DisplayName);
        Assert.Equal("Windows Update", options.Categories[1].DisplayName);
        Assert.Equal(["wuauserv", "BITS", "DoSvc"], options.Categories[1].ServiceNames);
        Assert.Equal("ConfigMgr", options.Categories[2].DisplayName);
    }

    [Fact]
    public void FromSettings_PreservesConfiguredAllServicesCategory()
    {
        var options = DeviceServicesOptions.FromSettings(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["filters:0:displayName"] = "Everything",
            ["filters:0:includeAllServices"] = "true",
            ["filters:1:displayName"] = "Intune only",
            ["filters:1:serviceNames"] = "IntuneManagementExtension"
        });

        Assert.Equal(2, options.Categories.Count);
        Assert.Equal("Everything", options.Categories[0].DisplayName);
        Assert.True(options.Categories[0].IncludeAllServices);
        Assert.Equal("Everything", options.DefaultCategoryName);
    }
}
