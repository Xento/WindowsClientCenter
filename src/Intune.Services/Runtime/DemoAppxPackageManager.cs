using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class DemoAppxPackageManager(DemoDataCatalog demoDataCatalog) : IAppxPackageManager
{
    private static readonly IReadOnlyList<AppxPackageEntry> Packages =
    [
        new(
            "Microsoft.WindowsCalculator_11.2502.2.0_x64__8wekyb3d8bbwe",
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe",
            "Microsoft.WindowsCalculator",
            "Windows Calculator",
            "11.2502.2.0",
            "CN=Microsoft Corporation",
            "X64",
            @"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.2502.2.0_x64__8wekyb3d8bbwe",
            false,
            false,
            false,
            false,
            false,
            true,
            "Microsoft.WindowsCalculator_2025.22502.0.0_neutral_~_8wekyb3d8bbwe",
            [
                new("S-1-5-21-1000", @"CONTOSO\Ada", "Installed", true),
                new("S-1-5-21-1001", @"CONTOSO\Grace", "Staged", false)
            ]),
        new(
            "Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe",
            "Microsoft.VCLibs.140.00_8wekyb3d8bbwe",
            "Microsoft.VCLibs.140.00",
            "Microsoft Visual C++ Runtime",
            "14.0.33519.0",
            "CN=Microsoft Corporation",
            "X64",
            @"C:\Program Files\WindowsApps\Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe",
            true,
            false,
            false,
            false,
            true,
            false,
            string.Empty,
            [new("S-1-5-21-1000", @"CONTOSO\Ada", "Installed", true)])
    ];

    public ValueTask<AppxPackageSnapshot> GetPackagesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AppxPackageSnapshot(
            demoDataCatalog.NormalizeHost(host),
            @"CONTOSO\Ada",
            "S-1-5-21-1000",
            Packages,
            []));
    }

    public ValueTask<WingetSearchSnapshot> SearchWingetAsync(string host, string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WingetCatalogEntry> entries =
        [
            new("Microsoft.PowerToys", "Microsoft PowerToys", "0.92.1", "winget"),
            new("9NBLGGH4NNS1", "App Installer", "1.26.400.0", "msstore")
        ];
        return ValueTask.FromResult(new WingetSearchSnapshot(
            entries.Where(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray(),
            []));
    }

    public ValueTask<DeviceActionResult> InstallWingetAsync(string host, WingetCatalogEntry package, WingetInstallScope scope, CancellationToken cancellationToken) =>
        CompleteWingetAction("install", host, package, scope, cancellationToken);

    public ValueTask<DeviceActionResult> UpgradeWingetAsync(string host, WingetCatalogEntry package, WingetInstallScope scope, CancellationToken cancellationToken) =>
        CompleteWingetAction("upgrade", host, package, scope, cancellationToken);

    public ValueTask<DeviceActionResult> RemoveForUserAsync(string host, string packageFullName, string userSid, CancellationToken cancellationToken) =>
        CompleteAction($"Demo removal of '{packageFullName}' for '{userSid}' completed on '{demoDataCatalog.NormalizeHost(host)}'.", cancellationToken);

    public ValueTask<DeviceActionResult> RemoveForAllUsersAsync(string host, string packageFullName, CancellationToken cancellationToken) =>
        CompleteAction($"Demo all-user removal of '{packageFullName}' completed on '{demoDataCatalog.NormalizeHost(host)}'.", cancellationToken);

    public ValueTask<DeviceActionResult> RemoveProvisioningAsync(string host, string provisionedPackageName, CancellationToken cancellationToken) =>
        CompleteAction($"Demo provisioning removal of '{provisionedPackageName}' completed on '{demoDataCatalog.NormalizeHost(host)}'.", cancellationToken);

    public ValueTask<DeviceActionResult> RegisterForActiveUserAsync(string host, string packageFullName, CancellationToken cancellationToken) =>
        CompleteAction($"Demo registration of '{packageFullName}' for the active user completed on '{demoDataCatalog.NormalizeHost(host)}'.", cancellationToken);

    private ValueTask<DeviceActionResult> CompleteWingetAction(string action, string host, WingetCatalogEntry package, WingetInstallScope scope, CancellationToken cancellationToken) =>
        CompleteAction($"Demo WinGet {action} of '{package.Id}' from '{package.Source}' ({scope}) completed on '{demoDataCatalog.NormalizeHost(host)}'.", cancellationToken);

    private static ValueTask<DeviceActionResult> CompleteAction(string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok(message));
    }
}
