using System.Diagnostics;
using System.IO;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Runtime;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;
using WindowsClientCenter.Plugins.PowerShellScripts.Dialog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WindowsClientCenter.Plugins.PowerShellScripts;

public sealed class PowerShellScriptsPlugin : IClientCenterPlugin, IRibbonControlPlugin
{
    private const string ControlId = "scripts-menu";
    private const string DefaultRelativeScriptDirectory = "PSScripts";
    private const string LoadingMenuItemId = "loading-scripts";
    private const string LoadFailedMenuItemId = "scripts-load-failed";
    private const string NoScriptsMenuItemId = "no-scripts";

    private readonly IPowerShellScriptMetadataProvider _metadataProvider;
    private readonly IPowerShellScriptLauncher _launcher;
    private readonly object _stateSync = new();
    private readonly Dictionary<string, PowerShellScriptCatalogEntry> _scriptsById = new(StringComparer.OrdinalIgnoreCase);

    private IPluginContext? _context;
    private IHostStatusLogSink? _hostStatusLogSink;
    private IHostRibbonRefreshSink? _hostRibbonRefreshSink;
    private CancellationTokenSource? _scriptLoadCancellationTokenSource;
    private string _scriptDirectory = string.Empty;
    private IReadOnlyList<PluginRibbonGroup> _ribbonGroups = [];
    private int _loadGeneration;

    public PowerShellScriptsPlugin()
        : this(new PowerShellScriptMetadataProvider(), new PowerShellScriptLauncher())
    {
    }

    public PowerShellScriptsPlugin(IPowerShellScriptMetadataProvider metadataProvider, IPowerShellScriptLauncher launcher)
    {
        _metadataProvider = metadataProvider;
        _launcher = launcher;
    }

    public IPluginManifest Manifest { get; } = new PluginManifest(
        Id: "powershell-scripts",
        DisplayName: "PowerShell Scripts",
        Version: "1.0.0",
        Capability: PluginCapability.Ribbon,
        MenuPath: "Powershell",
        MinHostVersion: "1.0.0");

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        CancelScriptLoad();
        _context = context;
        _hostStatusLogSink = context.Services.GetService<IHostStatusLogSink>();
        _hostRibbonRefreshSink = context.Services.GetService<IHostRibbonRefreshSink>();
        _scriptDirectory = ResolveScriptDirectory(context);
        var stopwatch = Stopwatch.StartNew();
        var loadGeneration = Interlocked.Increment(ref _loadGeneration);
        var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _scriptLoadCancellationTokenSource = linkedCancellationTokenSource;
        SetRibbonState([], BuildStatusRibbonGroups("Loading scripts...", LoadingMenuItemId));
        _hostStatusLogSink?.Append($"Loading PowerShell script metadata from '{_scriptDirectory}'...");

        var loadOperation = _metadataProvider.LoadAsync(_scriptDirectory, linkedCancellationTokenSource.Token);
        if (loadOperation.IsCompletedSuccessfully)
        {
            stopwatch.Stop();
            ApplyLoadedScripts(loadOperation.Result);
            ReportLoadSuccess(context, stopwatch.ElapsedMilliseconds);
            return ValueTask.CompletedTask;
        }

        _ = CompleteBackgroundLoadAsync(context, loadOperation.AsTask(), stopwatch, loadGeneration, linkedCancellationTokenSource.Token);
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<PluginRibbonGroup> GetRibbonGroups()
    {
        lock (_stateSync)
        {
            return _ribbonGroups;
        }
    }

    public async ValueTask<PluginActionResult> ExecuteRibbonControlAsync(string controlId, PluginActionContext context, CancellationToken cancellationToken)
    {
        if (_context is null)
        {
            return PluginActionResult.Fail("Plugin has not been initialized.", "not_initialized");
        }

        if (!string.Equals(controlId, ControlId, StringComparison.Ordinal))
        {
            return PluginActionResult.Fail($"Unknown ribbon control '{controlId}'.", "unknown_control");
        }

        if (context.Arguments is null ||
            !context.Arguments.TryGetValue("menuItemId", out var menuItemId) ||
            string.IsNullOrWhiteSpace(menuItemId))
        {
            return PluginActionResult.Fail("No script menu item was selected.", "missing_menu_item");
        }

        if (string.Equals(menuItemId, LoadingMenuItemId, StringComparison.Ordinal))
        {
            return PluginActionResult.Fail("PowerShell script metadata is still loading. Try again in a moment.", "scripts_loading");
        }

        if (string.Equals(menuItemId, LoadFailedMenuItemId, StringComparison.Ordinal))
        {
            return PluginActionResult.Fail("PowerShell scripts could not be loaded. See the status log for details.", "scripts_load_failed");
        }

        if (string.Equals(menuItemId, NoScriptsMenuItemId, StringComparison.Ordinal))
        {
            return PluginActionResult.Fail("No PowerShell scripts are available.", "no_scripts");
        }

        PowerShellScriptCatalogEntry? script;
        lock (_stateSync)
        {
            _scriptsById.TryGetValue(menuItemId, out script);
        }

        if (script is null)
        {
            return PluginActionResult.Fail($"No PowerShell script is registered for '{menuItemId}'.", "unknown_script");
        }

        if (script.ExecutionMode == PowerShellScriptExecutionMode.Unsupported)
        {
            return PluginActionResult.Fail(
                script.ErrorMessage ?? $"Script '{script.DisplayName}' is not supported.",
                "unsupported_script");
        }

        var targetHostService = _context.Services.GetRequiredService<ITargetHostService>();
        var host = ResolveHost(context, targetHostService);
        if (host is null)
        {
            return PluginActionResult.Fail("Client is not connected. Click Connect first.", "no_host");
        }

        var parameterLiterals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ComputerName"] = PowerShellScriptLiteralBuilder.CreateStringLiteral(host)
        };

        if (script.ExecutionMode == PowerShellScriptExecutionMode.PromptForComputerNameParameters)
        {
            var dialog = new ScriptParameterDialogWindow(script.DisplayName, host, script.RequiredParameters);
            var dialogResult = dialog.ShowDialog();
            if (dialogResult != true)
            {
                return PluginActionResult.Fail($"Script '{script.DisplayName}' was canceled.", "cancelled");
            }

            foreach (var parameter in dialog.ParameterLiterals)
            {
                parameterLiterals[parameter.Key] = parameter.Value;
            }
        }

        return await _launcher.LaunchAsync(
            host,
            script,
            parameterLiterals,
            _context.Services.GetService<IPowerShellExecutor>(),
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        CancelScriptLoad();
        lock (_stateSync)
        {
            _scriptsById.Clear();
            _ribbonGroups = [];
        }

        _context = null;
        _hostStatusLogSink = null;
        _hostRibbonRefreshSink = null;
        _scriptDirectory = string.Empty;
        return ValueTask.CompletedTask;
    }

    private async Task CompleteBackgroundLoadAsync(
        IPluginContext context,
        Task<IReadOnlyList<PowerShellScriptCatalogEntry>> loadTask,
        Stopwatch stopwatch,
        int loadGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            var scripts = await loadTask;
            stopwatch.Stop();

            if (!IsCurrentLoad(loadGeneration, cancellationToken))
            {
                return;
            }

            ApplyLoadedScripts(scripts);
            ReportLoadSuccess(context, stopwatch.ElapsedMilliseconds);
            _hostRibbonRefreshSink?.RequestRibbonRefresh(Manifest.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (!IsCurrentLoad(loadGeneration, cancellationToken))
            {
                return;
            }

            SetRibbonState([], BuildStatusRibbonGroups("Script load failed. See status log.", LoadFailedMenuItemId));
            _hostStatusLogSink?.Append($"PowerShell script metadata load failed: {ex.Message}");
            context.Logger.LogError(ex, "PowerShell script metadata load failed for {ScriptDirectory}.", _scriptDirectory);
            _hostRibbonRefreshSink?.RequestRibbonRefresh(Manifest.Id);
        }
    }

    private void ApplyLoadedScripts(IReadOnlyList<PowerShellScriptCatalogEntry> scripts)
    {
        SetRibbonState(scripts, BuildRibbonGroups(scripts));
    }

    private void ReportLoadSuccess(IPluginContext context, long elapsedMilliseconds)
    {
        var scriptCount = GetLoadedScriptCount();
        context.Logger.LogInformation(
            "Loaded {ScriptCount} PowerShell script entries from {ScriptDirectory} in {ElapsedMilliseconds} ms.",
            scriptCount,
            _scriptDirectory,
            elapsedMilliseconds);
        _hostStatusLogSink?.Append(
            $"PowerShell scripts ready: {scriptCount} entries loaded from '{_scriptDirectory}' in {elapsedMilliseconds} ms.");
    }

    private int GetLoadedScriptCount()
    {
        lock (_stateSync)
        {
            return _scriptsById.Count;
        }
    }

    private void SetRibbonState(
        IReadOnlyList<PowerShellScriptCatalogEntry> scripts,
        IReadOnlyList<PluginRibbonGroup> ribbonGroups)
    {
        lock (_stateSync)
        {
            _scriptsById.Clear();
            foreach (var script in scripts)
            {
                _scriptsById[script.ItemId] = script;
            }

            _ribbonGroups = ribbonGroups;
        }
    }

    private bool IsCurrentLoad(int loadGeneration, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested &&
               loadGeneration == Volatile.Read(ref _loadGeneration) &&
               !string.IsNullOrWhiteSpace(_scriptDirectory);
    }

    private void CancelScriptLoad()
    {
        var current = _scriptLoadCancellationTokenSource;
        _scriptLoadCancellationTokenSource = null;
        current?.Cancel();
        current?.Dispose();
    }

    private static string? ResolveHost(PluginActionContext context, ITargetHostService targetHostService)
    {
        if (context.Arguments is not null &&
            context.Arguments.TryGetValue("host", out var hostFromArgs) &&
            !string.IsNullOrWhiteSpace(hostFromArgs))
        {
            return hostFromArgs.Trim();
        }

        return string.IsNullOrWhiteSpace(targetHostService.CurrentHost)
            ? null
            : targetHostService.CurrentHost.Trim();
    }

    private static IReadOnlyList<PluginRibbonGroup> BuildRibbonGroups(IReadOnlyList<PowerShellScriptCatalogEntry> scripts)
    {
        var menuItems = BuildMenuItems(scripts);
        return BuildRibbonGroups(menuItems);
    }

    private static IReadOnlyList<PluginRibbonGroup> BuildStatusRibbonGroups(string statusText, string itemId)
    {
        return BuildRibbonGroups(
        [
            new PluginRibbonMenuItem(itemId, statusText)
        ]);
    }

    private static IReadOnlyList<PluginRibbonGroup> BuildRibbonGroups(IReadOnlyList<PluginRibbonMenuItem> menuItems)
    {
        return
        [
            new PluginRibbonGroup(
                GroupId: "scripts",
                Title: "Scripts",
                Controls:
                [
                    new PluginRibbonControl(
                        ControlId: ControlId,
                        Kind: PluginRibbonControlKind.MenuButton,
                        Text: "Scripts",
                        MinWidth: 110,
                        Height: 26,
                        FontSize: 12,
                        MenuItems: menuItems,
                        RequiresConnectedHost: true)
                ],
                DefaultControlHeight: 26,
                DefaultControlFontSize: 12,
                DefaultControlHorizontalPadding: 5,
                DefaultControlVerticalPadding: 1)
        ];
    }

    private static IReadOnlyList<PluginRibbonMenuItem> BuildMenuItems(IReadOnlyList<PowerShellScriptCatalogEntry> scripts)
    {
        if (scripts.Count == 0)
        {
            return
            [
                new PluginRibbonMenuItem(NoScriptsMenuItemId, "No scripts available")
            ];
        }

        var root = new ScriptFolderNode(string.Empty, string.Empty, isFolder: true);
        foreach (var script in scripts)
        {
            var segments = script.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var currentPath = string.Join('/', segments.Take(i + 1));
                current = current.GetOrAddFolder(segments[i], currentPath);
            }

            current.Children.Add(new ScriptFolderNode(script.DisplayName, script.ItemId, isFolder: false));
        }

        return BuildMenuItems(root.Children);
    }

    private static IReadOnlyList<PluginRibbonMenuItem> BuildMenuItems(IReadOnlyList<ScriptFolderNode> nodes)
    {
        return nodes
            .OrderByDescending(static node => node.IsFolder)
            .ThenBy(static node => node.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static node => new PluginRibbonMenuItem(
                node.ItemId,
                node.Name,
                node.IsFolder ? BuildMenuItems(node.Children) : null))
            .ToArray();
    }

    private static string ResolveScriptDirectory(IPluginContext context)
    {
        if (context.Settings.TryGetValue("scriptDirectory", out var configuredDirectory) &&
            !string.IsNullOrWhiteSpace(configuredDirectory))
        {
            var resolvedConfiguredDirectory = ResolveRelativePath(configuredDirectory);
            if (Directory.Exists(resolvedConfiguredDirectory))
            {
                return resolvedConfiguredDirectory;
            }
        }

        if (context.Settings.TryGetValue("NativePluginDirectory", out var nativePluginDirectory) &&
            !string.IsNullOrWhiteSpace(nativePluginDirectory))
        {
            var stagedDirectory = Path.Combine(nativePluginDirectory, "PSScripts");
            if (Directory.Exists(stagedDirectory))
            {
                return stagedDirectory;
            }
        }

        var appBaseDirectoryFallback = ResolveRelativePath("PSScripts");
        if (Directory.Exists(appBaseDirectoryFallback))
        {
            return appBaseDirectoryFallback;
        }

        return ResolveRelativePath(DefaultRelativeScriptDirectory);
    }

    private static string ResolveRelativePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    private sealed class ScriptFolderNode(string name, string itemId, bool isFolder)
    {
        public string Name { get; } = name;

        public string ItemId { get; } = itemId;

        public bool IsFolder { get; } = isFolder;

        public List<ScriptFolderNode> Children { get; } = [];

        public ScriptFolderNode GetOrAddFolder(string folderName, string folderPath)
        {
            var existing = Children.FirstOrDefault(node =>
                node.IsFolder &&
                node.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            var created = new ScriptFolderNode(folderName, folderPath, isFolder: true);
            Children.Add(created);
            return created;
        }
    }
}
