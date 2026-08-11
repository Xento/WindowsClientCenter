using WindowsClientCenter.Intune.Services.Runtime;
using Xunit;

namespace WindowsClientCenter.Tests.IntuneServices;

public sealed class TargetHostServiceTests
{
    [Fact]
    public void SetCurrentHost_NewHost_CancelsPreviousSelectionAndCreatesNewGeneration()
    {
        var service = new TargetHostService();
        service.SetCurrentHost("CLIENT01");
        var first = service.CaptureSelection();

        service.SetCurrentHost("CLIENT02");
        var second = service.CaptureSelection();

        Assert.Equal("CLIENT01", first.Host);
        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.Equal("CLIENT02", second.Host);
        Assert.False(second.CancellationToken.IsCancellationRequested);
        Assert.NotEqual(first.Version, second.Version);
        Assert.False(service.IsCurrent(first));
        Assert.True(service.IsCurrent(second));
    }

    [Fact]
    public void SetCurrentHost_SameHost_DoesNotCreateNewGeneration()
    {
        var service = new TargetHostService();
        service.SetCurrentHost("CLIENT01");
        var first = service.CaptureSelection();

        service.SetCurrentHost("client01");
        var second = service.CaptureSelection();

        Assert.Equal(first.Version, second.Version);
        Assert.Equal("client01", second.Host);
        Assert.False(first.CancellationToken.IsCancellationRequested);
        Assert.True(service.IsCurrent(first));
        Assert.True(service.IsCurrent(second));
    }
}
