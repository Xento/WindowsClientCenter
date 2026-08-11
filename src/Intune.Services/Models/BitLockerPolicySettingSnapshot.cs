namespace WindowsClientCenter.Intune.Services.Models;

public sealed record BitLockerPolicySettingSnapshot(
    string SettingName,
    string ValueText,
    string Source,
    string Category,
    string SourcePath,
    string ValueMeaningText)
{
    public string ValueDisplayText =>
        string.IsNullOrWhiteSpace(ValueMeaningText)
            ? ValueText
            : $"{ValueText} ({ValueMeaningText})";
}
