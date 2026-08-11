namespace WindowsClientCenter.Host.Runtime;

public sealed class HostRuntimeOptions
{
    public string Environment { get; set; } = "dev";
}

public sealed class HostPluginOptions
{
    public string NativeDirectory { get; set; } = "plugins/native";
}

public sealed class HostExplorerOptions
{
    public List<HostExplorerTargetOptions> Targets { get; set; } = [];
}

public sealed class HostExplorerTargetOptions
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string? MenuPath { get; set; }

    public List<HostExplorerTargetOptions> Children { get; set; } = [];

    public bool IsDefault { get; set; }
}
