using System.Reflection;
using WindowsClientCenter.Plugins.DeviceOverview.ViewModels;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.DeviceOverview;

public sealed class DeviceOverviewViewModelTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("overview", 0)]
    [InlineData("delivery-optimization", 1)]
    [InlineData("port-authentication", 2)]
    public void MapNavigationTargetToSectionIndex_ReturnsExpectedValue(string? target, int expected)
    {
        var method = typeof(DeviceOverviewViewModel).GetMethod(
            "MapNavigationTargetToSectionIndex",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MapNavigationTargetToSectionIndex not found.");

        var result = Assert.IsType<int>(method.Invoke(null, [target]));

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("SUCCESS", true)]
    [InlineData("success", true)]
    [InlineData("FAILED", false)]
    [InlineData("ERROR", false)]
    [InlineData("", false)]
    public void IsSuccessfulDeviceAuthStatus_ReturnsExpectedValue(string deviceAuthStatus, bool expected)
    {
        var method = typeof(DeviceOverviewViewModel).GetMethod(
            "IsSuccessfulDeviceAuthStatus",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("IsSuccessfulDeviceAuthStatus not found.");

        var result = Assert.IsType<bool>(method.Invoke(null, [deviceAuthStatus]));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildDeviceAuthStatusDetail_IncludesReportedStatus()
    {
        var method = typeof(DeviceOverviewViewModel).GetMethod(
            "BuildDeviceAuthStatusDetail",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildDeviceAuthStatusDetail not found.");

        var detail = Assert.IsType<string>(method.Invoke(null, ["FAILED. ERROR"]));

        Assert.Contains("DeviceAuthStatus is 'FAILED. ERROR'", detail, StringComparison.Ordinal);
        Assert.Contains("failing or incomplete", detail, StringComparison.Ordinal);
    }
}
