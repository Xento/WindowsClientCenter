namespace WindowsClientCenter.Intune.Services.Models;

public sealed record DeliveryOptimizationPeerStatus(
    string Content,
    string Status,
    int CandidateCount = 0,
    int ConnectedPeerCount = 0,
    long BytesFromPeers = 0,
    long BytesFromHttp = 0,
    string Details = "");
