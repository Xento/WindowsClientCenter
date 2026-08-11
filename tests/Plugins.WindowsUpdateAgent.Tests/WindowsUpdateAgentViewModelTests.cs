using System.Reflection;
using System.Text.Json;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Intune.Services.Runtime;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.Models;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.WindowsUpdateAgent;

public sealed class WindowsUpdateAgentViewModelTests
{
    [Fact]
    public void ParseAvailableUpdatesPayload_LegacyPayload_MapsExpectedFields()
    {
        const string json = """
        {
          "searchSource": "wua com cache",
          "lastSearchSuccessDate": "2026-04-25T10:15:00Z",
          "updateCount": 1,
          "updates": [
            {
              "title": "2026-04 Cumulative Update (KB5037001)",
              "type": "Software",
              "status": "Downloaded",
              "isInstalled": false,
              "isHidden": false,
              "kbArticles": "5037001",
              "isDownloaded": true,
              "isMandatory": true,
              "eulaAccepted": true,
              "categories": "Security Updates; Windows 11",
              "deadline": "",
              "updateId": "demo-update-id:143",
              "revision": 143
            }
          ],
          "providers": [
            {
              "name": "Windows Update",
              "serviceId": "9482f4b4-e343-43b6-b170-9a65bc822c77",
              "isDefault": true,
              "isRegisteredWithAU": true,
              "offersWindowsUpdates": true,
              "isManaged": false
            }
          ]
        }
        """;

        var payload = InvokeParseAvailableUpdatesPayload(json);
        var updates = ReadCollectionProperty(payload, "Updates");
        var providers = ReadCollectionProperty(payload, "Providers");

        Assert.Equal(1, (int)payload.GetType().GetProperty("UpdateCount")!.GetValue(payload)!);
        Assert.Equal("wua com cache", payload.GetType().GetProperty("SearchSource")!.GetValue(payload));
        Assert.Equal("2026-04-25T10:15:00Z", payload.GetType().GetProperty("LastSearchSuccessDate")!.GetValue(payload));

        var update = Assert.Single(updates);
        Assert.Equal("2026-04 Cumulative Update (KB5037001)", update.GetType().GetProperty("Title")!.GetValue(update));
        Assert.Equal("Software", update.GetType().GetProperty("Type")!.GetValue(update));
        Assert.Equal("Downloaded", update.GetType().GetProperty("Status")!.GetValue(update));
        Assert.Equal("5037001", update.GetType().GetProperty("KbArticles")!.GetValue(update));
        Assert.Equal("demo-update-id:143", update.GetType().GetProperty("UpdateId")!.GetValue(update));
        Assert.Equal(143, update.GetType().GetProperty("Revision")!.GetValue(update));

        var provider = Assert.Single(providers);
        Assert.Equal("Windows Update", provider.GetType().GetProperty("Name")!.GetValue(provider));
        Assert.Equal(true, provider.GetType().GetProperty("IsDefault")!.GetValue(provider));
    }

    [Fact]
    public void ParseWinRtAvailableUpdatesPayload_WinRtInventory_MapsExpectedFields()
    {
        const string json = """
        {
          "managerSnapshot": {
            "managerStatus": {
              "lastSuccessfulScanTimestamp": "2026-04-25T09:00:00Z",
              "providerIds": [ "provider-1" ]
            },
            "updates": [
              {
                "title": "2026-04 Security Update",
                "updateId": "winrt-update-id:7",
                "isDriver": false,
                "isFeatureUpdate": false,
                "isMandatory": true,
                "isSecurity": true,
                "deadline": "2026-04-30T00:00:00Z",
                "currentAction": "Install"
              }
            ],
            "softwareUpdates": [
              {
                "title": "2026-04 Security Update",
                "updateId": "winrt-update-id:7",
                "currentAction": "Install"
              }
            ]
          }
        }
        """;

        var providers = new[]
        {
            CreateProvider("Microsoft Update", "7971f918-a847-4430-9279-4a52d1efe18d", isDefault: true)
        };

        var payload = InvokeParseWinRtAvailableUpdatesPayload(json, providers);
        var updates = ReadCollectionProperty(payload, "Updates");

        Assert.Equal(1, (int)payload.GetType().GetProperty("UpdateCount")!.GetValue(payload)!);
        Assert.Equal("winrt inventory", payload.GetType().GetProperty("SearchSource")!.GetValue(payload));
        Assert.Equal("2026-04-25T09:00:00Z", payload.GetType().GetProperty("LastSearchSuccessDate")!.GetValue(payload));

        var update = Assert.Single(updates);
        Assert.Equal("2026-04 Security Update", update.GetType().GetProperty("Title")!.GetValue(update));
        Assert.Equal("Software", update.GetType().GetProperty("Type")!.GetValue(update));
        Assert.Equal("Install", update.GetType().GetProperty("Status")!.GetValue(update));
        Assert.Equal(true, update.GetType().GetProperty("IsDownloaded")!.GetValue(update));
        Assert.Equal(true, update.GetType().GetProperty("IsMandatory")!.GetValue(update));
        Assert.Equal("Software; Security; Mandatory", update.GetType().GetProperty("Categories")!.GetValue(update));
        Assert.Equal("winrt-update-id", update.GetType().GetProperty("UpdateId")!.GetValue(update));
        Assert.Equal(7, update.GetType().GetProperty("Revision")!.GetValue(update));
    }

    [Fact]
    public async Task Constructor_DefaultOverview_LoadsRegisteredUpdateProvidersImmediately()
    {
        const string json = """
        {
          "providers": [
            {
              "name": "Microsoft Update",
              "serviceId": "7971f918-a847-4430-9279-4a52d1efe18d",
              "isDefault": true,
              "isRegisteredWithAU": true,
              "offersWindowsUpdates": true,
              "isManaged": false
            }
          ]
        }
        """;

        var executor = new StaticJsonPowerShellExecutor(json);
        using var viewModel = new WindowsUpdateAgentViewModel(new FakePluginContext(BuildServices(executor)));

        await WaitForConditionAsync(() => executor.ExecuteCalls > 0 && viewModel.RegisteredUpdateProviders.Count == 1);

        Assert.Equal(1, executor.ExecuteCalls);
        var provider = Assert.Single(viewModel.RegisteredUpdateProviders);
        Assert.Equal("Microsoft Update", provider.Name);
        Assert.True(provider.IsDefault);
    }

    [Fact]
    public void WindowsUpdateInstallMonitorSnapshot_Deserializes_ObjectProgressLines()
    {
        const string json = """
        {
          "taskStatus": "Running",
          "progressLines": [
            { "message": "Installing KB1", "percent": 25 },
            "Installing KB2",
            42,
            true
          ]
        }
        """;

        var snapshot = JsonSerializer.Deserialize<WindowsUpdateInstallMonitorSnapshot>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(snapshot);
        Assert.Equal(4, snapshot.ProgressLines.Length);
        Assert.Contains(@"""message"": ""Installing KB1""", snapshot.ProgressLines[0], StringComparison.Ordinal);
        Assert.Contains(@"""percent"": 25", snapshot.ProgressLines[0], StringComparison.Ordinal);
        Assert.Equal("Installing KB2", snapshot.ProgressLines[1]);
        Assert.Equal("42", snapshot.ProgressLines[2]);
        Assert.Equal("True", snapshot.ProgressLines[3]);
    }

    [Fact]
    public async Task RefreshInstallTaskStatusAsync_RemoteHost_UsesPowerShellSnapshot()
    {
        var executor = new RecordingPowerShellExecutor(
            new WindowsUpdateInstallMonitorSnapshot
            {
                TaskStatus = "Running",
                Phase = "installing",
                Message = "Installing selected updates.",
                CurrentTitle = "KB5030219",
                TotalCount = 4,
                CompletedCount = 2,
                InstalledCount = 2,
                FailedCount = 0,
                RebootRequired = false,
                LastUpdatedUtc = "2026-04-16T08:15:00Z",
                ProgressCursor = 128
            });

        var viewModel = new WindowsUpdateAgentViewModel(new FakePluginContext(BuildServices(executor)));
        SetPrivateField(viewModel, "_activeInstallHost", "CLIENT01.remote");
        SetPrivateField(viewModel, "_activeInstallStatusPath", @"C:\ProgramData\WindowsClientCenter\WindowsUpdateAgent\install-status.json");
        SetPrivateField(viewModel, "_activeInstallProgressLogPath", @"C:\ProgramData\WindowsClientCenter\WindowsUpdateAgent\install-progress.log");

        await viewModel.RefreshInstallTaskStatusAsync();

        Assert.Equal(1, executor.ExecuteCalls);
        Assert.Equal("Task: Running", viewModel.InstallTaskStatusText);
        Assert.Equal("Phase: installing", viewModel.InstallTaskPhaseText);
        Assert.Contains("Installing selected updates.", viewModel.InstallTaskDetail, StringComparison.Ordinal);
        Assert.Contains("Current: KB5030219", viewModel.InstallTaskDetail, StringComparison.Ordinal);
        Assert.Contains("Progress: 2/4", viewModel.InstallTaskDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshInstallTaskStatusAsync_RemoteHost_ManualReloadRequestsFullProgressSnapshot()
    {
        var executor = new RecordingPowerShellExecutor(new WindowsUpdateInstallMonitorSnapshot
        {
            TaskStatus = "Queued",
            Phase = "queued",
            ProgressCursor = 256
        });

        var viewModel = new WindowsUpdateAgentViewModel(new FakePluginContext(BuildServices(executor)));
        SetPrivateField(viewModel, "_activeInstallHost", "CLIENT01.remote");
        SetPrivateField(viewModel, "_activeInstallStatusPath", @"C:\ProgramData\WindowsClientCenter\WindowsUpdateAgent\install-status.json");
        SetPrivateField(viewModel, "_activeInstallProgressLogPath", @"C:\ProgramData\WindowsClientCenter\WindowsUpdateAgent\install-progress.log");
        SetPrivateField(viewModel, "_remoteInstallProgressCursor", 99L);

        await viewModel.RefreshInstallTaskStatusAsync();

        Assert.Contains("$cursor = [int64]-1;", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRemoteInstallMonitorScript_EmptyProgressPath_GuardsLiteralPathAccess()
    {
        var method = typeof(WindowsUpdateAgentViewModel).GetMethod(
            "BuildRemoteInstallMonitorScript",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildRemoteInstallMonitorScript not found.");

        var script = Assert.IsType<string>(method.Invoke(null, ["TaskName", @"C:\state.json", string.Empty, -1L, 0]));

        Assert.Contains("-not [string]::IsNullOrWhiteSpace($progressPath)", script, StringComparison.Ordinal);
        Assert.Contains("-not [string]::IsNullOrWhiteSpace($statePath)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstallUpdatesAsyncScriptBody_DefinesUpdateLookupHelpers()
    {
        var method = typeof(WindowsUpdateAgentViewModel).GetMethod(
            "BuildInstallUpdatesAsyncScriptBody",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildInstallUpdatesAsyncScriptBody not found.");

        var script = Assert.IsType<string>(method.Invoke(null, []));

        Assert.Contains("function Get-UpdateLookupKeys {", script, StringComparison.Ordinal);
        Assert.Contains("function Add-AvailableUpdateIndexEntry {", script, StringComparison.Ordinal);
        Assert.Contains("Add-AvailableUpdateIndexEntry -Index $availableByKey", script, StringComparison.Ordinal);
    }

    private static IServiceProvider BuildServices(IPowerShellExecutor executor)
    {
        return new ServiceCollection()
            .AddSingleton<ITargetHostService>(new FakeTargetHostService("CLIENT01.remote"))
            .AddSingleton<ILocalIntuneDiagnosticsService>(new FakeDiagnosticsService())
            .AddSingleton<IPowerShellExecutor>(executor)
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();
    }

    private static void SetPrivateField(object instance, string fieldName, object? value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found.");
        field.SetValue(instance, value);
    }

    private static object InvokeParseAvailableUpdatesPayload(string json)
    {
        var method = typeof(WindowsUpdateAgentViewModel).GetMethod(
            "ParseAvailableUpdatesPayload",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ParseAvailableUpdatesPayload not found.");

        return method.Invoke(null, [json, null])!;
    }

    private static object InvokeParseWinRtAvailableUpdatesPayload(string json, WindowsUpdateProviderEntry[] providers)
    {
        var method = typeof(WindowsUpdateAgentViewModel).GetMethod(
            "ParseWinRtAvailableUpdatesPayload",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ParseWinRtAvailableUpdatesPayload not found.");

        return method.Invoke(null, [json, providers])!;
    }

    private static object[] ReadCollectionProperty(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Property '{propertyName}' not found on '{target.GetType().Name}'.");
        var value = property.GetValue(target) as System.Collections.IEnumerable
            ?? throw new InvalidOperationException($"Property '{propertyName}' is not enumerable.");
        return value.Cast<object>().ToArray();
    }

    private static WindowsUpdateProviderEntry CreateProvider(string name, string serviceId, bool isDefault)
    {
        return new WindowsUpdateProviderEntry(name, serviceId, isDefault, true, true, false);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int attempts = 20, int delayMilliseconds = 50)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(delayMilliseconds);
        }

        Assert.True(condition(), "Timed out waiting for the expected asynchronous condition.");
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

    private sealed class FakeDiagnosticsService : ILocalIntuneDiagnosticsService
    {
        public ValueTask<LocalIntuneSnapshot> GetSnapshotAsync(string host, CancellationToken cancellationToken) => ValueTask.FromResult(CreateSnapshot(host));
        public ValueTask<LocalIntuneSnapshotDiagnosticsResult> GetSnapshotDiagnosticsAsync(string host, CancellationToken cancellationToken) => ValueTask.FromResult(new LocalIntuneSnapshotDiagnosticsResult(CreateSnapshot(host), []));
        public ValueTask<LocalIntuneSnapshot> GetOverviewCoreSnapshotAsync(string host, CancellationToken cancellationToken) => ValueTask.FromResult(CreateSnapshot(host));
        public ValueTask<PlatformSecuritySnapshot?> GetPlatformSecuritySnapshotAsync(string host, CancellationToken cancellationToken) => ValueTask.FromResult<PlatformSecuritySnapshot?>(null);
        public ValueTask<SystemRuntimeSnapshot?> GetSystemRuntimeSnapshotAsync(string host, CancellationToken cancellationToken) => ValueTask.FromResult<SystemRuntimeSnapshot?>(null);
        public ValueTask<NetworkConnectivitySnapshot?> GetNetworkConnectivitySnapshotAsync(string host, CancellationToken cancellationToken) => ValueTask.FromResult<NetworkConnectivitySnapshot?>(null);
        public ValueTask<PortAuthenticationSnapshot?> GetPortAuthenticationSnapshotAsync(string host, CancellationToken cancellationToken) => ValueTask.FromResult<PortAuthenticationSnapshot?>(null);
        public ValueTask<DeliveryOptimizationSnapshot?> GetDeliveryOptimizationSnapshotAsync(string host, CancellationToken cancellationToken) => ValueTask.FromResult<DeliveryOptimizationSnapshot?>(null);
        public ValueTask<IReadOnlyList<IntuneLogEntry>> GetLogEntriesAsync(string host, string logName, int maxEntries, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<IntuneLogEntry>>([]);
        public ValueTask<IReadOnlyList<MdmEventAnalysisEntry>> GetMdmAdminEventsAsync(string host, int maxEntries, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<MdmEventAnalysisEntry>>([]);
        public ValueTask<string> ExportSnapshotAsync(string host, string outputDirectory, CancellationToken cancellationToken) => ValueTask.FromResult(string.Empty);
        public ValueTask<string> ExportMdmDiagnosticsAsync(string host, string outputDirectory, CancellationToken cancellationToken) => ValueTask.FromResult(string.Empty);

        private static LocalIntuneSnapshot CreateSnapshot(string host)
        {
            return new LocalIntuneSnapshot(
                host,
                host,
                DateTimeOffset.UtcNow,
                false,
                "Unknown",
                "Unknown",
                "Unknown",
                [],
                [],
                [],
                [],
                [],
                [],
                UpdateRingText: "Unknown");
        }
    }

    private sealed class RecordingPowerShellExecutor(WindowsUpdateInstallMonitorSnapshot snapshot) : IPowerShellExecutor
    {
        public int ExecuteCalls { get; private set; }
        public string LastScript { get; private set; } = string.Empty;

        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            LastScript = scriptBody;
            return ValueTask.FromResult(new PowershellExecutionResult(
                0,
                System.Text.Json.JsonSerializer.Serialize(snapshot),
                string.Empty));
        }
    }

    private sealed class StaticJsonPowerShellExecutor(string stdOut) : IPowerShellExecutor
    {
        public int ExecuteCalls { get; private set; }
        public string LastScript { get; private set; } = string.Empty;

        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            LastScript = scriptBody;
            return ValueTask.FromResult(new PowershellExecutionResult(0, stdOut, string.Empty));
        }
    }
}
