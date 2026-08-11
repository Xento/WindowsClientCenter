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
            new ScreenshotCaptureTarget("Windows Update Agent/Overview", "windows-update-agent.png"),
            new ScreenshotCaptureTarget("Windows Update Agent/ReportingEvents.log", "reporting-events-log.png"),
            new ScreenshotCaptureTarget("Intune Agent/Overview", "intune-agent.png")
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
