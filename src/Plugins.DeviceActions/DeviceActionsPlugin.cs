using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsClientCenter.Plugins.DeviceActions;

public sealed class DeviceActionsPlugin : IActionPlugin
{
    private IServiceProvider? _services;
    private ILocalDeviceActionService? _localDeviceActionService;
    private ITargetHostService? _targetHostService;

    public IPluginManifest Manifest { get; } = new PluginManifest(
        Id: "device-actions",
        DisplayName: "Run Sync (Local WinRM)",
        Version: "1.0.0",
        Capability: PluginCapability.Action,
        MenuPath: "Device/Actions",
        MinHostVersion: "1.0.0");

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _services = context.Services;
        _localDeviceActionService = context.Services.GetRequiredService<ILocalDeviceActionService>();
        _targetHostService = context.Services.GetRequiredService<ITargetHostService>();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<PluginActionResult> ExecuteAsync(PluginActionContext context, CancellationToken cancellationToken)
    {
        if (_services is null || _localDeviceActionService is null || _targetHostService is null)
        {
            return PluginActionResult.Fail("Plugin is not initialized.", "not_initialized");
        }

        string? hostFromArgs = null;
        if (context.Arguments is not null &&
            context.Arguments.TryGetValue("host", out var argHost) &&
            !string.IsNullOrWhiteSpace(argHost))
        {
            hostFromArgs = argHost;
        }

        var host = hostFromArgs ?? _targetHostService.CurrentHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            return PluginActionResult.Fail("Client is not connected. Click Connect first.", "no_host");
        }

        var normalizedAction = string.IsNullOrWhiteSpace(context.ActionName) ? "sync-now" : context.ActionName.Trim();
        if (IsLocalFirstAction(normalizedAction))
        {
            var localResult = await _localDeviceActionService.ExecuteLocalActionAsync(
                host,
                normalizedAction,
                context.Arguments,
                cancellationToken);

            return localResult.Success
                ? PluginActionResult.Ok(localResult.Message)
                : PluginActionResult.Fail(localResult.Message, localResult.ErrorCode);
        }

        IAuthService authService;
        IDeviceActionService deviceActionService;
        IDeviceQueryService deviceQueryService;

        try
        {
            authService = _services.GetRequiredService<IAuthService>();
            deviceActionService = _services.GetRequiredService<IDeviceActionService>();
            deviceQueryService = _services.GetRequiredService<IDeviceQueryService>();
        }
        catch (Exception ex)
        {
            return PluginActionResult.Fail(
                $"Cloud services required for '{normalizedAction}' are not available: {ex.Message}",
                "cloud_services_unavailable");
        }

        try
        {
            var session = await authService.GetCurrentSessionAsync(cancellationToken);
            if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                session = await authService.LoginAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            return PluginActionResult.Fail($"Cloud login required for '{normalizedAction}' but failed: {ex.Message}", "cloud_login_failed");
        }

        var device = await deviceQueryService.GetDeviceByHostAsync(host, cancellationToken);
        if (device is null)
        {
            return PluginActionResult.Fail($"No device found for host '{host}'.", "no_device");
        }

        var result = await deviceActionService.ExecuteActionAsync(
            new DeviceActionRequest(device.DeviceId, normalizedAction, context.Arguments),
            cancellationToken);
        return result.Success
            ? PluginActionResult.Ok(result.Message)
            : PluginActionResult.Fail(result.Message, result.ErrorCode);
    }

    public ValueTask DisposeAsync()
    {
        _services = null;
        _localDeviceActionService = null;
        _targetHostService = null;
        return ValueTask.CompletedTask;
    }

    private static bool IsLocalFirstAction(string actionName)
    {
        return actionName.Equals("sync-now", StringComparison.OrdinalIgnoreCase) ||
               actionName.Equals("sync", StringComparison.OrdinalIgnoreCase);
    }
}
