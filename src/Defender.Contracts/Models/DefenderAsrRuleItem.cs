namespace WindowsClientCenter.Defender.Contracts.Models;

public sealed record DefenderAsrRuleItem(
    string RuleId,
    string RuleName,
    string Action,
    string RuleSpecificExclusions);
