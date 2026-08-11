namespace WindowsClientCenter.Plugin.Abstractions.Contracts;

public interface IViewPlugin : IClientCenterPlugin
{
    object CreateView();
}
