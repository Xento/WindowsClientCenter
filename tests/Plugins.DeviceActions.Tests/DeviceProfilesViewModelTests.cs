using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.DeviceActions.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.DeviceActions;

public sealed class DeviceProfilesViewModelTests
{
    [Fact]
    public async Task LoadAsync_ShowsOnlyRegularProfiles()
    {
        var hostStatus = new FakeHostStatusLogSink();
        var viewModel = CreateViewModel(
            new FakeWindowsProfileManager(
                CreateSnapshot("CLIENT01", ["C:\\Users\\alice", "C:\\Users\\bob"]),
                CreateSnapshot("CLIENT02", ["C:\\Users\\charlie"])),
            new FakeTargetHostService("CLIENT01"),
            hostStatus);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(2, viewModel.Profiles.Count);
        Assert.Equal("Loaded 2 profile(s).", viewModel.Status);
        Assert.Contains(hostStatus.Messages, message => message.Contains("Loaded 2 profile(s).", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostChange_ReloadsAndClearsCachedSizes()
    {
        var targetHostService = new FakeTargetHostService("CLIENT01");
        var manager = new FakeWindowsProfileManager(
            CreateSnapshot("CLIENT01", ["C:\\Users\\alice"]),
            CreateSnapshot("CLIENT02", ["C:\\Users\\charlie"]));
        var viewModel = CreateViewModel(manager, targetHostService, new FakeHostStatusLogSink());

        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.CalculateRawSizeAsync();

        Assert.NotEqual("Not calculated", viewModel.SelectedProfile?.RawSizeDisplay);

        targetHostService.SetCurrentHost("CLIENT02");
        await Task.Delay(50);

        Assert.Single(viewModel.Profiles);
        Assert.Equal(@"C:\Users\charlie", viewModel.SelectedProfile?.LocalPath);
        Assert.Equal("Not calculated", viewModel.SelectedProfile?.RawSizeDisplay);
    }

    [Fact]
    public async Task CalculateRawSizeAsync_UpdatesOnlySelectedRow()
    {
        var viewModel = CreateViewModel(
            new FakeWindowsProfileManager(CreateSnapshot("CLIENT01", ["C:\\Users\\alice", "C:\\Users\\bob"])),
            new FakeTargetHostService("CLIENT01"),
            new FakeHostStatusLogSink());

        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.SelectedProfile = viewModel.Profiles.Single(profile => profile.LocalPath.EndsWith("bob", StringComparison.OrdinalIgnoreCase));

        await viewModel.CalculateRawSizeAsync();

        Assert.Equal("1 GB", viewModel.SelectedProfile.RawSizeDisplay);
        Assert.Equal("Not calculated", viewModel.Profiles.Single(profile => profile.LocalPath.EndsWith("alice", StringComparison.OrdinalIgnoreCase)).RawSizeDisplay);
    }

    [Fact]
    public async Task CalculatePolicySizeAsync_UsesPolicyMode()
    {
        var manager = new FakeWindowsProfileManager(CreateSnapshot("CLIENT01", ["C:\\Users\\alice"]));
        var viewModel = CreateViewModel(manager, new FakeTargetHostService("CLIENT01"), new FakeHostStatusLogSink());

        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.CalculatePolicySizeAsync();

        Assert.Equal(ProfileSizeCalculationMode.PolicyExcluded, manager.LastSizeMode);
        Assert.Equal("800 MB", viewModel.SelectedProfile?.PolicySizeDisplay);
    }

    [Fact]
    public async Task Commands_AreDisabled_WhenNoProfileIsSelected()
    {
        var viewModel = CreateViewModel(
            new FakeWindowsProfileManager(new WindowsProfileSnapshot("CLIENT01", false, [], new WindowsProfilePolicyInfo(null, null, [], "Not configured"), [])),
            new FakeTargetHostService("CLIENT01"),
            new FakeHostStatusLogSink());

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.False(viewModel.CalculateRawSizeCommand.CanExecute(null));
        Assert.False(viewModel.CalculatePolicySizeCommand.CanExecute(null));
        Assert.False(viewModel.DeleteSelectedProfileCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteSelectedProfileAsync_DoesNotCallManager_WhenConfirmationDeclined()
    {
        var manager = new FakeWindowsProfileManager(CreateSnapshot("CLIENT01", ["C:\\Users\\alice"]));
        var viewModel = CreateViewModel(manager, new FakeTargetHostService("CLIENT01"), new FakeHostStatusLogSink(), (_, _) => false);

        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.DeleteSelectedProfileAsync();

        Assert.Equal(0, manager.DeleteCalls);
        Assert.Contains("cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteSelectedProfileAsync_RefreshesAfterSuccess()
    {
        var manager = new FakeWindowsProfileManager(
            CreateSnapshot("CLIENT01", ["C:\\Users\\alice"]),
            CreateSnapshot("CLIENT01", ["C:\\Users\\bob"]));
        var viewModel = CreateViewModel(manager, new FakeTargetHostService("CLIENT01"), new FakeHostStatusLogSink(), (_, _) => true);

        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.DeleteSelectedProfileAsync();

        Assert.Equal(1, manager.DeleteCalls);
        Assert.Equal(@"C:\Users\bob", viewModel.SelectedProfile?.LocalPath);
    }

    private static DeviceProfilesViewModel CreateViewModel(
        FakeWindowsProfileManager manager,
        FakeTargetHostService targetHostService,
        FakeHostStatusLogSink hostStatus,
        Func<string, string, bool>? confirmAction = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<ITargetHostService>(targetHostService)
            .AddSingleton<IWindowsProfileManager>(manager)
            .AddSingleton<IHostStatusLogSink>(hostStatus)
            .BuildServiceProvider();
        return new DeviceProfilesViewModel(new FakePluginContext(services), confirmAction);
    }

    private static WindowsProfileSnapshot CreateSnapshot(string host, IEnumerable<string> localPaths)
    {
        var entries = localPaths
            .Select((path, index) => new WindowsProfileEntry(
                $@"CONTOSO\user{index + 1}",
                $"S-1-5-21-100-{index + 1}",
                path,
                DateTimeOffset.UtcNow.AddHours(-(index + 1)),
                index == 0 ? @"\\profiles\user1" : string.Empty,
                index == 0,
                false,
                index == 0,
                false,
                false,
                false))
            .ToArray();

        return new WindowsProfileSnapshot(
            host,
            false,
            entries,
            new WindowsProfilePolicyInfo(500, true, ["AppData\\Local\\Temp"], @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System"),
            []);
    }

    private sealed class FakePluginContext(IServiceProvider services) : IPluginContext
    {
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IServiceProvider Services { get; } = services;
        public string EnvironmentName { get; } = "Test";
        public IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>();
    }

    private sealed class FakeTargetHostService(string currentHost) : ITargetHostService
    {
        private long _version = 1;
        private CancellationTokenSource _selectionCancellationTokenSource = new();

        public string CurrentHost { get; private set; } = currentHost;
        public event EventHandler<string>? HostChanged;
        public HostSelection CaptureSelection() => new(CurrentHost, _version, _selectionCancellationTokenSource.Token);
        public bool IsCurrent(HostSelection selection) => selection.Version == _version && string.Equals(selection.Host, CurrentHost, StringComparison.OrdinalIgnoreCase);

        public void SetCurrentHost(string host)
        {
            if (!string.Equals(CurrentHost, host, StringComparison.OrdinalIgnoreCase))
            {
                _selectionCancellationTokenSource.Cancel();
                _selectionCancellationTokenSource.Dispose();
                _selectionCancellationTokenSource = new CancellationTokenSource();
                _version++;
            }

            CurrentHost = host;
            HostChanged?.Invoke(this, host);
        }
    }

    private sealed class FakeWindowsProfileManager(params WindowsProfileSnapshot[] snapshots) : IWindowsProfileManager
    {
        private readonly Queue<WindowsProfileSnapshot> _snapshots = new(snapshots);
        private readonly WindowsProfileSnapshot _lastSnapshot = snapshots.LastOrDefault()
            ?? new WindowsProfileSnapshot(string.Empty, false, [], new WindowsProfilePolicyInfo(null, null, [], "Not configured"), []);

        public ProfileSizeCalculationMode? LastSizeMode { get; private set; }
        public int DeleteCalls { get; private set; }

        public ValueTask<WindowsProfileSnapshot> GetProfilesAsync(string host, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(_snapshots.Count > 0 ? _snapshots.Dequeue() : _lastSnapshot);
        }

        public ValueTask<WindowsProfileSizeResult> CalculateProfileSizeAsync(string host, string profileLocalPath, ProfileSizeCalculationMode mode, CancellationToken cancellationToken)
        {
            LastSizeMode = mode;
            var bytes = mode == ProfileSizeCalculationMode.PolicyExcluded ? 800L * 1024 * 1024 : 1024L * 1024 * 1024;
            return ValueTask.FromResult(new WindowsProfileSizeResult(profileLocalPath, mode, bytes, 100, 10, []));
        }

        public ValueTask<DeviceActionResult> DeleteProfileAsync(string host, string sid, string profileLocalPath, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            return ValueTask.FromResult(DeviceActionResult.Ok($"Profile {profileLocalPath} removed."));
        }
    }

    private sealed class FakeHostStatusLogSink : IHostStatusLogSink
    {
        public List<string> Messages { get; } = [];

        public void Append(string message)
        {
            Messages.Add(message);
        }
    }
}
