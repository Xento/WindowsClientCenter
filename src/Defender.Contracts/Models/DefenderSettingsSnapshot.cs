namespace WindowsClientCenter.Defender.Contracts.Models;

public sealed record DefenderSettingsSnapshot(
    DateTimeOffset CapturedAtUtc,
    string Source,
    IReadOnlyList<DefenderSettingItem> Settings,
    IReadOnlyList<string> Notes,
    IReadOnlyList<DefenderAsrRuleItem>? AsrRules = null,
    IReadOnlyList<DefenderExclusionItem>? Exclusions = null);
