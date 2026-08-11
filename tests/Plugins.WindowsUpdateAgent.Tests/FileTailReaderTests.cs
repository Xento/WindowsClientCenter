using System.Collections.Concurrent;
using WindowsClientCenter.Shared.Diagnostics;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.WindowsUpdateAgent;

public sealed class FileTailReaderTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"icc-filetail-tests-{Guid.NewGuid():N}");

    public FileTailReaderTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task FollowLinesAsync_AppendedLine_ReportsOnlyNewContent()
    {
        var path = Path.Combine(_tempDirectory, "reporting.log");
        await File.WriteAllTextAsync(path, "seed" + Environment.NewLine);

        var observedLines = new ConcurrentQueue<string>();
        using var cts = new CancellationTokenSource();
        var followTask = FileTailReader.FollowLinesAsync(
            path,
            startPosition: null,
            observedLines.Enqueue,
            _ => { },
            pollDelayMilliseconds: 25,
            cts.Token);

        await Task.Delay(150);
        await File.AppendAllTextAsync(path, "first" + Environment.NewLine);

        await WaitUntilAsync(
            () => observedLines.Any(line => string.Equals(line, "first", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(3));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await followTask);
        Assert.DoesNotContain("seed", observedLines);
    }

    [Fact]
    public async Task FollowLinesAsync_TruncatedFile_RestartsFromBeginning()
    {
        var path = Path.Combine(_tempDirectory, "install-progress.log");
        await File.WriteAllTextAsync(path, "seed" + Environment.NewLine);

        var observedLines = new ConcurrentQueue<string>();
        using var cts = new CancellationTokenSource();
        var followTask = FileTailReader.FollowLinesAsync(
            path,
            startPosition: null,
            observedLines.Enqueue,
            _ => { },
            pollDelayMilliseconds: 25,
            cts.Token);

        await Task.Delay(150);
        await File.AppendAllTextAsync(path, "before-reset" + Environment.NewLine);
        await WaitUntilAsync(
            () => observedLines.Any(line => string.Equals(line, "before-reset", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(3));

        await File.WriteAllTextAsync(path, "after-reset" + Environment.NewLine);
        await WaitUntilAsync(
            () => observedLines.Any(line => string.Equals(line, "after-reset", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(3));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await followTask);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition(), "Condition was not satisfied within the allotted time.");
    }
}
