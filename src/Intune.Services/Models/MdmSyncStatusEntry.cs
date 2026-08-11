using WindowsClientCenter.Shared.Diagnostics;

namespace WindowsClientCenter.Intune.Services.Models;

public sealed record MdmSyncStatusEntry(
    DateTimeOffset? TimeCreated,
    int EventId,
    string Message,
    string ResultCode)
{
    public string ResultCodeDescription => ErrorCodeResolver.ResolveDescription(ResultCode);
}
