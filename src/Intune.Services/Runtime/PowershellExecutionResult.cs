namespace WindowsClientCenter.Intune.Services.Runtime;

public sealed record PowershellExecutionResult(int ExitCode, string StdOut, string StdErr);
