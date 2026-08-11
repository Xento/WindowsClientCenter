using System.Text.Json;

namespace WindowsClientCenter.Intune.Services.Runtime;

public interface IPowerShellExecutor
{
    ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken);

    async ValueTask<T?> ExecuteJsonForHostAsync<T>(string host, string scriptBody, CancellationToken cancellationToken)
    {
        var execution = await ExecuteForHostAsync(host, scriptBody, cancellationToken);
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(execution.StdErr)
                ? $"PowerShell execution failed with exit code {execution.ExitCode}."
                : execution.StdErr.Trim());
        }

        if (string.IsNullOrWhiteSpace(execution.StdOut))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(execution.StdOut, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }
}
