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

        var expected = new (string MenuPath, string FileName)[]
        {
            ("Device/Overview", "shell-overview.png"),
            ("Device/BitLocker", "device-bitlocker.png"),
            ("Device/Actions", "device-actions.png"),
            ("Device/AppX Applications", "device-appx-applications.png"),
            ("Device/Installed Software", "device-installed-software.png"),
            ("Device/Delivery Optimization", "device-delivery-optimization.png"),
            ("Device/Port Authentication", "device-port-authentication.png"),
            ("Device/Processes", "device-processes.png"),
            ("Device/Profiles", "device-profiles.png"),
            ("Device/Services", "device-services.png"),
            ("Intune Agent/Overview", "intune-agent.png"),
            ("Intune Agent/Local Diagnostics", "intune-local-diagnostics.png"),
            ("Intune Agent/Enrollment", "intune-enrollment.png"),
            ("Intune Agent/MDM Events", "intune-mdm-events.png"),
            ("Intune Agent/IME Logs", "intune-ime-logs.png"),
            ("Intune Agent/IME Applications", "intune-ime-applications.png"),
            ("Intune Agent/Local Actions", "intune-local-actions.png"),
            ("Intune Agent/Policy Result", "intune-policy-result.png"),
            ("Intune Agent/Cloud", "intune-cloud.png"),
            ("MECM/Overview", "mecm-overview.png"),
            ("MECM/Applications", "mecm-applications.png"),
            ("MECM/Updates/Pending", "mecm-updates-pending.png"),
            ("MECM/Updates/All", "mecm-updates-all.png"),
            ("MECM/Packages", "mecm-packages.png"),
            ("MECM/DCM Baselines", "mecm-dcm-baselines.png"),
            ("Defender/Overview", "defender-overview.png"),
            ("Defender/Protection Status", "defender-protection-status.png"),
            ("Defender/Versions", "defender-versions.png"),
            ("Defender/Scans", "defender-scans.png"),
            ("Defender/Settings", "defender-settings.png"),
            ("Defender/Detections", "defender-detections.png"),
            ("Defender/Device Control", "defender-device-control.png"),
            ("Windows Update Agent/Overview", "windows-update-agent.png"),
            ("Windows Update Agent/Available updates", "windows-update-available-updates.png"),
            ("Windows Update Agent/Update history", "windows-update-history.png"),
            ("Windows Update Agent/ReportingEvents.log", "reporting-events-log.png"),
            ("Windows Update Agent/USO diagnostics", "windows-update-uso-diagnostics.png")
        };

        Assert.Equal(expected, targets.Select(target => (target.MenuPath, target.FileName)));
        Assert.Equal(targets.Count, targets.Select(target => target.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
