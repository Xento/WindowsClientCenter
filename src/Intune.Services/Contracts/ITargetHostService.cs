namespace WindowsClientCenter.Intune.Services.Contracts;

public interface ITargetHostService
{
    string CurrentHost { get; }
    event EventHandler<string>? HostChanged;
    HostSelection CaptureSelection();
    bool IsCurrent(HostSelection selection);
    void SetCurrentHost(string host);
}
