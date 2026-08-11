namespace WindowsClientCenter.Plugin.Abstractions.Contracts;

public interface IHostStatusLogSink
{
    void Append(string message);
}
