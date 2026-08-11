using WindowsClientCenter.Shared.Diagnostics;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models;

public sealed record WindowsUpdateHistoryEntry(
    string Date,
    string Operation,
    string Result,
    string HResult,
    string Title,
    string UpdateId,
    int Revision,
    string ClientApplicationId,
    string ServiceId,
    string PackageName = "")
{
    public string HResultDescription => ErrorCodeResolver.ResolveDescription(HResult);
}
