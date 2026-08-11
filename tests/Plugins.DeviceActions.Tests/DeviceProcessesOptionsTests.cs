using WindowsClientCenter.Plugins.DeviceActions.Models;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.DeviceActions;

public sealed class DeviceProcessesOptionsTests
{
    [Fact]
    public void FromSettings_UsesDefaults_WhenNoSettingsExist()
    {
        var options = DeviceProcessesOptions.FromSettings(new Dictionary<string, string>());

        Assert.Equal(ProcessViewMode.List, options.DefaultViewMode);
        Assert.Equal([0, 5, 10, 30, 60], options.RefreshIntervalsSeconds);
        Assert.Equal(0, options.DefaultRefreshIntervalSeconds);
    }

    [Fact]
    public void FromSettings_AppliesConfiguredViewAndIntervals()
    {
        var options = DeviceProcessesOptions.FromSettings(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["defaultViewMode"] = "tree",
            ["refreshIntervals"] = "10,30,60",
            ["defaultRefreshIntervalSeconds"] = "30"
        });

        Assert.Equal(ProcessViewMode.Tree, options.DefaultViewMode);
        Assert.Equal([0, 10, 30, 60], options.RefreshIntervalsSeconds);
        Assert.Equal(30, options.DefaultRefreshIntervalSeconds);
    }
}
