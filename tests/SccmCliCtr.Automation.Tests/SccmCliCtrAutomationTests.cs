using System.Reflection;
using sccmclictr.automation;
using Xunit;

namespace WindowsClientCenter.Tests.SccmCliCtrAutomation;

public sealed class SccmCliCtrAutomationTests
{
    [Fact]
    public void EmbeddedResourceScripts_AreAccessible()
    {
        var resourcesType = typeof(SCCMAgent).Assembly.GetType("sccmclictr.automation.Properties.Resources", throwOnError: true);
        var healthCheck = Assert.IsType<string>(resourcesType!.GetProperty("HealthCheck", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        var cacheCleanup = Assert.IsType<string>(resourcesType.GetProperty("CacheCleanup", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        var secretDecode = Assert.IsType<string>(resourcesType.GetProperty("SecretDecode", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));

        Assert.Contains("CCM_SoftwareUpdate", healthCheck, StringComparison.Ordinal);
        Assert.Contains("CacheInfoEx", cacheCleanup, StringComparison.Ordinal);
        Assert.Contains("UnProtect-PolicySecret", secretDecode, StringComparison.Ordinal);
    }

    [Fact]
    public void Common_WmiDateToDateTime_ParsesExpectedValue()
    {
        var value = common.WMIDateToDateTime("20260423153000.000000+120");

        Assert.NotNull(value);
        Assert.Equal(2026, value!.Value.Year);
        Assert.Equal(4, value.Value.Month);
        Assert.Equal(23, value.Value.Day);
    }

    [Fact]
    public void Assembly_ContainsWsManHelper()
    {
        var wsManType = typeof(SCCMAgent).Assembly.GetType("sccmclictr.automation.WSMan", throwOnError: true);

        Assert.NotNull(wsManType);
        Assert.Contains(wsManType!.GetMethods(BindingFlags.Static | BindingFlags.NonPublic), method => method.Name == "openRunspace");
        Assert.Contains(wsManType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic), method => method.Name == "RunPSScript");
    }
}
