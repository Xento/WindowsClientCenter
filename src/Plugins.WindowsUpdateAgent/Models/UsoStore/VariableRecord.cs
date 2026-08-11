namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

public sealed class VariableRecord
{
    public required string Key { get; init; }

    public required string RawValue { get; init; }

    public required int Type { get; init; }

    public required string TypeLabel { get; init; }

    public required string ParsedValue { get; init; }

    public long? ParsedInteger { get; init; }

    public bool? ParsedBoolean { get; init; }

    public DateTime? ParsedDateTimeLocal { get; init; }
}
