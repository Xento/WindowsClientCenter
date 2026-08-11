using System.Net.Http.Headers;
using System.Text.Json;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class LiveGraphDeviceQueryService(HttpClient httpClient, IAccessTokenProvider accessTokenProvider) : IDeviceQueryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async ValueTask<IReadOnlyList<DeviceRecord>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken);
        using var request = CreateRequest("deviceManagement/managedDevices?$top=200&$select=id,deviceName,operatingSystem,lastSyncDateTime,complianceState", token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, "Cloud device list lookup");

        var payload = JsonSerializer.Deserialize<ManagedDevicesEnvelope>(body, JsonOptions);
        return payload?.Value?.Select(MapDeviceRecord).ToArray() ?? [];
    }

    public async ValueTask<DeviceRecord?> GetDeviceByIdAsync(string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken);
        var requestUri = $"deviceManagement/managedDevices/{Uri.EscapeDataString(deviceId.Trim())}?$select=id,deviceName,operatingSystem,lastSyncDateTime,complianceState";
        using var request = CreateRequest(requestUri, token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response, body, "Cloud device lookup");
        var payload = JsonSerializer.Deserialize<ManagedDevicePayload>(body, JsonOptions);
        return payload is null ? null : MapDeviceRecord(payload);
    }

    public async ValueTask<DeviceRecord?> GetDeviceByHostAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var normalizedHost = host.Trim();
        var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken);
        var requestUri = "deviceManagement/managedDevices?$filter=deviceName eq '" + EscapeODataValue(normalizedHost) + "'&$select=id,deviceName,operatingSystem,lastSyncDateTime,complianceState";
        using var request = CreateRequest(requestUri, token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, "Cloud device lookup");

        var payload = JsonSerializer.Deserialize<ManagedDevicesEnvelope>(body, JsonOptions);
        var match = payload?.Value?.FirstOrDefault(device =>
            device.DeviceName is not null &&
            device.DeviceName.Equals(normalizedHost, StringComparison.OrdinalIgnoreCase));

        return match is null ? null : MapDeviceRecord(match);
    }

    private static HttpRequestMessage CreateRequest(string requestUri, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{operation} failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}".Trim());
        }
    }

    private static string EscapeODataValue(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static DeviceRecord MapDeviceRecord(ManagedDevicePayload payload)
    {
        return new DeviceRecord(
            payload.Id ?? string.Empty,
            payload.DeviceName ?? "Unknown device",
            payload.OperatingSystem ?? "Unknown",
            payload.LastSyncDateTime ?? DateTimeOffset.MinValue,
            string.IsNullOrWhiteSpace(payload.ComplianceState) ? "unknown" : payload.ComplianceState);
    }

    private sealed class ManagedDevicesEnvelope
    {
        public List<ManagedDevicePayload>? Value { get; init; }
    }

    private sealed class ManagedDevicePayload
    {
        public string? Id { get; init; }
        public string? DeviceName { get; init; }
        public string? OperatingSystem { get; init; }
        public DateTimeOffset? LastSyncDateTime { get; init; }
        public string? ComplianceState { get; init; }
    }
}

internal sealed class LiveGraphDeviceActionService(HttpClient httpClient, IAccessTokenProvider accessTokenProvider) : IDeviceActionService
{
    public async ValueTask<DeviceActionResult> ExecuteActionAsync(DeviceActionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            return DeviceActionResult.Fail("Cloud action failed: managed device id is empty.", "no_device_id");
        }

        var operation = NormalizeAction(request.Action);
        if (operation is null)
        {
            return DeviceActionResult.Fail(
                $"Cloud action '{request.Action}' is not part of the public release yet. Use local-first actions or switch to Mock mode for demos.",
                "cloud_action_unavailable");
        }

        var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken);
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"deviceManagement/managedDevices/{Uri.EscapeDataString(request.DeviceId)}/{operation}");
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(requestMessage, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return DeviceActionResult.Ok(
                $"Cloud action '{request.Action}' was triggered for managed device '{request.DeviceId}'.",
                response.Headers.TryGetValues("request-id", out var requestIds) ? requestIds.FirstOrDefault() : null);
        }

        return DeviceActionResult.Fail(
            $"Cloud action '{request.Action}' failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}".Trim(),
            $"http_{(int)response.StatusCode}");
    }

    private static string? NormalizeAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return null;
        }

        return action.Trim().ToLowerInvariant() switch
        {
            "sync" or "sync-now" => "syncDevice",
            _ => null
        };
    }
}
