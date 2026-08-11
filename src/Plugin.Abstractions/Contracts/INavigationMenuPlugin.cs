using WindowsClientCenter.Plugin.Abstractions.Models;

namespace WindowsClientCenter.Plugin.Abstractions.Contracts;

public interface INavigationMenuPlugin
{
    IReadOnlyList<PluginNavigationEntry> GetNavigationEntries();
}
