using WindowsClientCenter.Plugin.Abstractions.Contracts;

namespace WindowsClientCenter.Host.Runtime;

public sealed class HostStatusLogDispatcher : IHostStatusLogSink
{
    private readonly object _sync = new();
    private readonly List<string> _history = [];

    public event Action<string>? MessageAppended;

    public void Append(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var normalized = message.Trim();
        Action<string>? handlers;
        lock (_sync)
        {
            _history.Add(normalized);
            handlers = MessageAppended;
        }

        handlers?.Invoke(normalized);
    }

    public void ReplayTo(Action<string> receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);

        string[] snapshot;
        lock (_sync)
        {
            snapshot = _history.ToArray();
        }

        foreach (var message in snapshot)
        {
            receiver(message);
        }
    }
}
