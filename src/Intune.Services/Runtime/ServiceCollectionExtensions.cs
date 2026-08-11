using WindowsClientCenter.Defender.Contracts;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;

namespace WindowsClientCenter.Intune.Services.Runtime;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIntuneRuntime(this IServiceCollection services, IntuneRuntimeOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<ITargetHostService, TargetHostService>();
        services.AddSingleton<DemoDataCatalog>();

        if (options.Mode == IntuneRuntimeMode.Demo)
        {
            services.AddSingleton<IHostConnectivityService, DemoHostConnectivityService>();
            services.AddSingleton<IPowerShellExecutor, DemoPowerShellExecutor>();
            services.AddSingleton<ILocalDeviceActionService, DemoLocalDeviceActionService>();
            services.AddSingleton<IMecmClientService, DemoMecmClientService>();
            services.AddSingleton<IInstalledSoftwareManager, DemoInstalledSoftwareManager>();
            services.AddSingleton<IAppxPackageManager, DemoAppxPackageManager>();
            services.AddSingleton<IWindowsServiceManager, DemoWindowsServiceManager>();
            services.AddSingleton<IWindowsProcessManager, DemoWindowsProcessManager>();
            services.AddSingleton<IWindowsProfileManager, DemoWindowsProfileManager>();
            services.AddSingleton<ILocalBitLockerService, DemoLocalBitLockerService>();
            services.AddSingleton<ILocalIntuneDiagnosticsService, DemoLocalIntuneDiagnosticsService>();
            services.AddSingleton<IDefenderDiagnosticsService, DemoDefenderDiagnosticsService>();
            services.AddSingleton<ILocalIntuneEnrollmentService, DemoLocalIntuneEnrollmentService>();
            services.AddSingleton<ILocalIntuneActionService, DemoLocalIntuneActionService>();
            services.AddSingleton<DemoAuthService>();
            services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<DemoAuthService>());
            services.AddSingleton<IAccessTokenProvider>(sp => sp.GetRequiredService<DemoAuthService>());
            services.AddSingleton<IDeviceQueryService, DemoDeviceQueryService>();
            services.AddSingleton<IDeviceActionService, DemoDeviceActionService>();
            services.AddSingleton<ICloudManagedDeviceService, DemoCloudManagedDeviceService>();
            return services;
        }

        services.AddSingleton<IHostConnectivityService, HostConnectivityService>();
        services.AddSingleton(sp => new LocalPowerShellExecutor(
            sp.GetRequiredService<IHostConnectivityService>(),
            sp.GetRequiredService<ITargetHostService>(),
            sp.GetRequiredService<IntuneRuntimeOptions>(),
            sp.GetService<IHostStatusLogSink>(),
            () => sp.GetService<IHostBusyStateSink>()));
        services.AddSingleton<IPowerShellExecutor>(sp => sp.GetRequiredService<LocalPowerShellExecutor>());
        services.AddSingleton<IMecmClientService>(sp =>
            options.MecmBackend == MecmBackendMode.PowerShell
                ? new MecmClientService(sp.GetRequiredService<IPowerShellExecutor>())
                : new SccmClientCenterMecmService(
                    sp.GetRequiredService<IPowerShellExecutor>(),
                    sp.GetService<ILogger<SccmClientCenterMecmService>>()));
        services.AddSingleton<IInstalledSoftwareManager, InstalledSoftwareManager>();
        services.AddSingleton<IAppxPackageManager, AppxPackageManager>();
        services.AddSingleton<IWindowsServiceManager, WindowsServiceManager>();
        services.AddSingleton<IWindowsProcessManager, WindowsProcessManager>();
        services.AddSingleton<IWindowsProfileManager, WindowsProfileManager>();
        services.AddSingleton(_ =>
        {
            var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(options.Proxy))
            {
                var proxyValue = options.Proxy.Trim();
                if (!proxyValue.Contains("://", StringComparison.Ordinal))
                {
                    proxyValue = "http://" + proxyValue;
                }

                if (Uri.TryCreate(proxyValue, UriKind.Absolute, out var proxyUri))
                {
                    handler.Proxy = new WebProxy(proxyUri);
                    handler.UseProxy = true;
                }
            }

            return new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
            };
        });
        services.AddSingleton<ILocalDeviceActionService, WinRmLocalDeviceActionService>();
        services.AddSingleton<ILocalBitLockerService, LocalBitLockerService>();
        services.AddSingleton<ILocalIntuneDiagnosticsService>(sp =>
            new LocalIntuneDiagnosticsService(
                sp.GetRequiredService<IPowerShellExecutor>(),
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IntuneRuntimeOptions>()));
        services.AddSingleton<IDefenderDiagnosticsService>(sp =>
            new LocalDefenderDiagnosticsService(
                sp.GetRequiredService<IPowerShellExecutor>(),
                sp.GetService<HttpClient>(),
                sp.GetRequiredService<IntuneRuntimeOptions>()));
        services.AddSingleton<ILocalIntuneEnrollmentService, LocalIntuneEnrollmentService>();
        services.AddSingleton<ILocalIntuneActionService, LocalIntuneActionService>();

        if (options.Mode == IntuneRuntimeMode.Mock)
        {
            services.AddSingleton<MockAuthService>();
            services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<MockAuthService>());
            services.AddSingleton<IAccessTokenProvider>(sp => sp.GetRequiredService<MockAuthService>());
            services.AddSingleton<IDeviceQueryService, MockDeviceQueryService>();
            services.AddSingleton<IDeviceActionService, MockDeviceActionService>();
            services.AddSingleton<ICloudManagedDeviceService, MockCloudManagedDeviceService>();
        }
        else if (HasLiveGraphConfiguration(options))
        {
            services.AddSingleton<LiveGraphAuthService>();
            services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<LiveGraphAuthService>());
            services.AddSingleton<IAccessTokenProvider>(sp => sp.GetRequiredService<LiveGraphAuthService>());
            services.AddSingleton<IDeviceQueryService, LiveGraphDeviceQueryService>();
            services.AddSingleton<IDeviceActionService, LiveGraphDeviceActionService>();
            services.AddSingleton<ICloudManagedDeviceService, LiveGraphCloudManagedDeviceService>();
        }
        else
        {
            services.AddSingleton<IDeviceQueryService, DisabledDeviceQueryService>();
        }

        return services;
    }

    private static bool HasLiveGraphConfiguration(IntuneRuntimeOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.ClientId) &&
               !options.ClientId.Equals("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase);
    }
}
