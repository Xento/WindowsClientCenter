namespace WindowsClientCenter.Defender.Contracts.Models;

public sealed record DefenderVersionInfo(
    string EngineVersion,
    string ProductVersion,
    string AntivirusSignatureVersion,
    string AntispywareSignatureVersion,
    string NisEngineVersion,
    string NisSignatureVersion,
    DateTimeOffset? SignatureLastUpdatedUtc,
    double SignatureAgeHours,
    bool SignaturesOutdated,
    double SignatureWarningThresholdHours = 36,
    double SignatureCriticalThresholdHours = 72);
