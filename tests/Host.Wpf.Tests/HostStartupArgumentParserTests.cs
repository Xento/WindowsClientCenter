using WindowsClientCenter.Host.Runtime;
using Xunit;

namespace WindowsClientCenter.Tests.HostWpf;

public sealed class HostStartupArgumentParserTests
{
    [Fact]
    public void Parse_UsesLocalhostAndReadmeDefaults_ForScreenshotRuns()
    {
        var arguments = HostStartupArgumentParser.Parse(["--capture-screenshots"]);

        Assert.Equal("localhost", arguments.StartupHost);
        Assert.Null(arguments.IntuneModeOverride);
        Assert.NotNull(arguments.ScreenshotCapture);
        Assert.Equal(Path.Combine("docs", "images"), arguments.ScreenshotCapture!.OutputDirectory);
        Assert.Equal("readme", arguments.ScreenshotCapture.ProfileName);
        Assert.Equal("Mock", arguments.ScreenshotCapture.IntuneMode);
    }

    [Fact]
    public void Parse_RespectsExplicitScreenshotOptions()
    {
        var arguments = HostStartupArgumentParser.Parse(
        [
            "--capture-screenshots",
            "--capture-output", "artifacts/screens",
            "--capture-profile", "readme",
            "--capture-intune-mode", "Live",
            "--host", "demo-client"
        ]);

        Assert.Equal("demo-client", arguments.StartupHost);
        Assert.Null(arguments.IntuneModeOverride);
        Assert.NotNull(arguments.ScreenshotCapture);
        Assert.Equal("artifacts/screens", arguments.ScreenshotCapture!.OutputDirectory);
        Assert.Equal("readme", arguments.ScreenshotCapture.ProfileName);
        Assert.Equal("Live", arguments.ScreenshotCapture.IntuneMode);
    }

    [Fact]
    public void Parse_RespectsExplicitRuntimeModeOutsideScreenshotRuns()
    {
        var arguments = HostStartupArgumentParser.Parse(
        [
            "--intune-mode", "Demo",
            "--host", "demo-client-01"
        ]);

        Assert.Equal("demo-client-01", arguments.StartupHost);
        Assert.Equal("Demo", arguments.IntuneModeOverride);
        Assert.Null(arguments.ScreenshotCapture);
    }

    [Fact]
    public void GetCaptureTargets_ReturnsExpectedReadmeTargets()
    {
        var targets = HostStartupArgumentParser.GetCaptureTargets("readme");

        Assert.Collection(
            targets,
            target =>
            {
                Assert.Equal("Device/Overview", target.MenuPath);
                Assert.Equal("shell-overview.png", target.FileName);
            },
            target =>
            {
                Assert.Equal("Windows Update Agent/Overview", target.MenuPath);
                Assert.Equal("windows-update-agent.png", target.FileName);
            },
            target =>
            {
                Assert.Equal("Windows Update Agent/ReportingEvents.log", target.MenuPath);
                Assert.Equal("reporting-events-log.png", target.FileName);
            },
            target =>
            {
                Assert.Equal("Intune Agent/Overview", target.MenuPath);
                Assert.Equal("intune-agent.png", target.FileName);
            });
    }
}
