using WindowsClientCenter.Intune.Services.Contracts;

namespace WindowsClientCenter.Intune.Services.Runtime;

public sealed class TargetHostService : ITargetHostService
{
    private readonly object _sync = new();
    private string _currentHost = string.Empty;
    private long _version;
    private CancellationTokenSource _selectionCancellationTokenSource = new();

    public string CurrentHost => _currentHost;

    public event EventHandler<string>? HostChanged;

    public HostSelection CaptureSelection()
    {
        lock (_sync)
        {
            return new HostSelection(_currentHost, _version, _selectionCancellationTokenSource.Token);
        }
    }

    public bool IsCurrent(HostSelection selection)
    {
        lock (_sync)
        {
            return selection.Version == _version &&
                   string.Equals(selection.Host, _currentHost, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetCurrentHost(string host)
    {
        var normalized = host.Trim();
        CancellationTokenSource? previousCancellationTokenSource = null;
        var changed = false;

        lock (_sync)
        {
            if (string.Equals(_currentHost, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _currentHost = normalized;
                return;
            }

            previousCancellationTokenSource = _selectionCancellationTokenSource;
            _selectionCancellationTokenSource = new CancellationTokenSource();
            _version++;
            _currentHost = normalized;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        try
        {
            previousCancellationTokenSource?.Cancel();
        }
        catch
        {
            // Best effort cancellation only.
        }
        finally
        {
            previousCancellationTokenSource?.Dispose();
        }

        HostChanged?.Invoke(this, normalized);
    }
}
