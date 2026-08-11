using System.IO;
using System.Collections.ObjectModel;

namespace WindowsClientCenter.Host.Runtime;

public sealed record HostStartupArguments(
    string? StartupHost,
    string? IntuneModeOverride,
    ScreenshotCaptureOptions? ScreenshotCapture);

public sealed record ScreenshotCaptureOptions(
    string OutputDirectory,
    string ProfileName,
    string IntuneMode);

public sealed record ScreenshotCaptureTarget(
    string MenuPath,
    string FileName);

public static class HostStartupArgumentParser
{
    public static HostStartupArguments Parse(string[] args)
    {
        var screenshotRequested = HasFlag(args, "--capture-screenshots");
        var startupHost = ParseStartupHost(args);
        var intuneModeOverride = ParseOptionValue(args, "--intune-mode");
        var captureOutput = ParseOptionValue(args, "--capture-output");
        var captureProfile = ParseOptionValue(args, "--capture-profile");
        var captureIntuneMode = ParseOptionValue(args, "--capture-intune-mode");

        ScreenshotCaptureOptions? screenshotCapture = null;
        if (screenshotRequested)
        {
            screenshotCapture = new ScreenshotCaptureOptions(
                OutputDirectory: string.IsNullOrWhiteSpace(captureOutput) ? Path.Combine("docs", "images") : captureOutput.Trim(),
                ProfileName: string.IsNullOrWhiteSpace(captureProfile) ? "readme" : captureProfile.Trim(),
                IntuneMode: string.IsNullOrWhiteSpace(captureIntuneMode) ? "Mock" : captureIntuneMode.Trim());
            startupHost ??= "localhost";
        }

        return new HostStartupArguments(startupHost, intuneModeOverride, screenshotCapture);
    }

    public static IReadOnlyList<ScreenshotCaptureTarget> GetCaptureTargets(string profileName)
    {
        if (!string.Equals(profileName?.Trim(), "readme", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unknown screenshot capture profile '{profileName}'.");
        }

        return ReadmeTargets;
    }

    private static ReadOnlyCollection<ScreenshotCaptureTarget> ReadmeTargets { get; } =
        new(
        [
            new ScreenshotCaptureTarget("Device/Overview", "shell-overview.png"),
            new ScreenshotCaptureTarget("Device/BitLocker", "device-bitlocker.png"),
            new ScreenshotCaptureTarget("Device/Actions", "device-actions.png"),
            new ScreenshotCaptureTarget("Device/AppX Applications", "device-appx-applications.png"),
            new ScreenshotCaptureTarget("Device/Installed Software", "device-installed-software.png"),
            new ScreenshotCaptureTarget("Device/Delivery Optimization", "device-delivery-optimization.png"),
            new ScreenshotCaptureTarget("Device/Port Authentication", "device-port-authentication.png"),
            new ScreenshotCaptureTarget("Device/Processes", "device-processes.png"),
            new ScreenshotCaptureTarget("Device/Profiles", "device-profiles.png"),
            new ScreenshotCaptureTarget("Device/Services", "device-services.png"),
            new ScreenshotCaptureTarget("Intune Agent/Overview", "intune-agent.png"),
            new ScreenshotCaptureTarget("Intune Agent/Local Diagnostics", "intune-local-diagnostics.png"),
            new ScreenshotCaptureTarget("Intune Agent/Enrollment", "intune-enrollment.png"),
            new ScreenshotCaptureTarget("Intune Agent/MDM Events", "intune-mdm-events.png"),
            new ScreenshotCaptureTarget("Intune Agent/IME Logs", "intune-ime-logs.png"),
            new ScreenshotCaptureTarget("Intune Agent/IME Applications", "intune-ime-applications.png"),
            new ScreenshotCaptureTarget("Intune Agent/Local Actions", "intune-local-actions.png"),
            new ScreenshotCaptureTarget("Intune Agent/Policy Result", "intune-policy-result.png"),
            new ScreenshotCaptureTarget("Intune Agent/Cloud", "intune-cloud.png"),
            new ScreenshotCaptureTarget("MECM/Overview", "mecm-overview.png"),
            new ScreenshotCaptureTarget("MECM/Applications", "mecm-applications.png"),
            new ScreenshotCaptureTarget("MECM/Updates/Pending", "mecm-updates-pending.png"),
            new ScreenshotCaptureTarget("MECM/Updates/All", "mecm-updates-all.png"),
            new ScreenshotCaptureTarget("MECM/Packages", "mecm-packages.png"),
            new ScreenshotCaptureTarget("MECM/DCM Baselines", "mecm-dcm-baselines.png"),
            new ScreenshotCaptureTarget("Defender/Overview", "defender-overview.png"),
            new ScreenshotCaptureTarget("Defender/Protection Status", "defender-protection-status.png"),
            new ScreenshotCaptureTarget("Defender/Versions", "defender-versions.png"),
            new ScreenshotCaptureTarget("Defender/Scans", "defender-scans.png"),
            new ScreenshotCaptureTarget("Defender/Settings", "defender-settings.png"),
            new ScreenshotCaptureTarget("Defender/Detections", "defender-detections.png"),
            new ScreenshotCaptureTarget("Defender/Device Control", "defender-device-control.png"),
            new ScreenshotCaptureTarget("Windows Update Agent/Overview", "windows-update-agent.png"),
            new ScreenshotCaptureTarget("Windows Update Agent/Available updates", "windows-update-available-updates.png"),
            new ScreenshotCaptureTarget("Windows Update Agent/Update history", "windows-update-history.png"),
            new ScreenshotCaptureTarget("Windows Update Agent/ReportingEvents.log", "reporting-events-log.png"),
            new ScreenshotCaptureTarget("Windows Update Agent/USO diagnostics", "windows-update-uso-diagnostics.png")
        ]);

    private static bool HasFlag(string[] args, string longName)
    {
        return args.Any(arg =>
            arg.Equals(longName, StringComparison.OrdinalIgnoreCase) ||
            arg.Equals(longName.Replace("--", "/"), StringComparison.OrdinalIgnoreCase));
    }

    private static string? ParseOptionValue(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals(optionName, StringComparison.OrdinalIgnoreCase) ||
                arg.Equals(optionName.Replace("--", "/"), StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                return null;
            }

            var equalsPrefix = optionName + "=";
            if (arg.StartsWith(equalsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[equalsPrefix.Length..];
            }

            var colonPrefix = optionName.Replace("--", "/") + ":";
            if (arg.StartsWith(colonPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[colonPrefix.Length..];
            }
        }

        return null;
    }

    private static string? ParseStartupHost(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--host", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-host", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/host", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    return NormalizeHost(args[i + 1]);
                }
            }

            if (arg.StartsWith("--host=", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeHost(arg["--host=".Length..]);
            }

            if (arg.StartsWith("/host:", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeHost(arg["/host:".Length..]);
            }
        }

        foreach (var arg in args)
        {
            if (!arg.StartsWith("-") && !arg.StartsWith("/"))
            {
                return NormalizeHost(arg);
            }
        }

        return null;
    }

    private static string? NormalizeHost(string? host)
    {
        return string.IsNullOrWhiteSpace(host) ? null : host.Trim();
    }
}
