using WindowsClientCenter.Plugins.DeviceOverview;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.DeviceOverview;

public sealed class DeviceOverviewPluginTests
{
    [Fact]
    public void GetNavigationEntries_IncludesPortAuthenticationEntry()
    {
        var plugin = new DeviceOverviewPlugin();

        var entries = plugin.GetNavigationEntries();

        Assert.Contains(entries, entry => entry.NavigationTarget == "port-authentication" &&
                                          entry.MenuPath == "Device/Port Authentication");
    }
}
