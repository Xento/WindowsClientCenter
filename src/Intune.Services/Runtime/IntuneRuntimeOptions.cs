namespace WindowsClientCenter.Intune.Services.Runtime;

public sealed class IntuneRuntimeOptions
{
    public IntuneRuntimeMode Mode { get; set; } = IntuneRuntimeMode.Mock;
    public MecmBackendMode MecmBackend { get; set; } = MecmBackendMode.ClientCenterLib;
    public string DemoHostName { get; set; } = "DEMO-CLIENT-01";
    public string DemoTenantId { get; set; } = "demo.example";
    public string DemoUserPrincipalName { get; set; } = "alex.wilson@demo.example";
    public string DemoConnectedUsersText { get; set; } = @"DEMO\alex.wilson, DEMO\helpdesk.ops";
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = "http://localhost";
    public string? Proxy { get; set; }
    public int PowerShellSessionPoolSize { get; set; } = 5;
    public double DefenderSecurityIntelligenceWarningThresholdHours { get; set; } = 36;
    public double DefenderSecurityIntelligenceCriticalThresholdHours { get; set; } = 72;
    public string VpnAdapterDescriptionMatch { get; set; } = string.Empty;
    public string VpnProviderName { get; set; } = string.Empty;
}

public enum MecmBackendMode
{
    ClientCenterLib,
    PowerShell
}
