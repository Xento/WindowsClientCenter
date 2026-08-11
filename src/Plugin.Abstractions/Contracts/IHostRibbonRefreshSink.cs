namespace WindowsClientCenter.Plugin.Abstractions.Contracts;

public interface IHostRibbonRefreshSink
{
    void RequestRibbonRefresh(string pluginId);
}
