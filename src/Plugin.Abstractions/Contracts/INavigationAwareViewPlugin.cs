namespace WindowsClientCenter.Plugin.Abstractions.Contracts;

public interface INavigationAwareViewPlugin
{
    void SetNavigationTarget(string? navigationTarget);
}
