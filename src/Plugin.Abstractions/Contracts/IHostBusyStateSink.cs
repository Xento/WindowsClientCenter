namespace WindowsClientCenter.Plugin.Abstractions.Contracts;

public interface IHostBusyStateSink
{
    void SetBusyState(string ownerId, string shortStatus, IReadOnlyList<string>? tasks = null);
    void ClearBusyState(string ownerId);
}
