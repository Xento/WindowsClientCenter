using System.Net.Http.Headers;
using System.Text.Json;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class LiveGraphCloudManagedDeviceService(HttpClient httpClient, IAccessTokenProvider accessTokenProvider) : ICloudManagedDeviceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async ValueTask<CloudManagedDeviceSummary?> FindManagedDeviceByHostAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var normalizedHost = host.Trim();
        var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "deviceManagement/managedDevices?$filter=deviceName eq '" + EscapeODataValue(normalizedHost) + "'&$select=id,deviceName,azureADDeviceId,userPrincipalName,operatingSystem,complianceState,lastSyncDateTime");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Managed device lookup failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}".Trim());
        }

        var payload = JsonSerializer.Deserialize<ManagedDevicesEnvelope>(body, JsonOptions);
        var match = payload?.Value?.FirstOrDefault(device =>
            device.DeviceName is not null &&
            device.DeviceName.Equals(normalizedHost, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return null;
        }

        return new CloudManagedDeviceSummary(
            match.Id ?? string.Empty,
            match.DeviceName ?? normalizedHost,
            match.AzureAdDeviceId,
            match.UserPrincipalName,
            match.OperatingSystem,
            match.ComplianceState,
            match.LastSyncDateTime,
            true,
            "Microsoft Graph");
    }

    public async ValueTask<CloudSyncResult> SyncManagedDeviceAsync(string managedDeviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(managedDeviceId))
        {
            return CloudSyncResult.Fail("Managed device id is empty.", "no_managed_device_id");
        }

        var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"deviceManagement/managedDevices/{Uri.EscapeDataString(managedDeviceId)}/syncDevice");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return CloudSyncResult.Ok(
                $"Cloud sync was triggered for managed device '{managedDeviceId}'.",
                response.Headers.TryGetValues("request-id", out var requestIds) ? requestIds.FirstOrDefault() : null);
        }

        return CloudSyncResult.Fail(
            $"Cloud sync failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}".Trim(),
            $"http_{(int)response.StatusCode}");
    }

    private static string EscapeODataValue(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed class ManagedDevicesEnvelope
    {
        public List<ManagedDevicePayload>? Value { get; init; }
    }

    private sealed class ManagedDevicePayload
    {
        public string? Id { get; init; }
        public string? DeviceName { get; init; }
        public string? AzureAdDeviceId { get; init; }
        public string? UserPrincipalName { get; init; }
        public string? OperatingSystem { get; init; }
        public string? ComplianceState { get; init; }
        public DateTimeOffset? LastSyncDateTime { get; init; }
    }
}
