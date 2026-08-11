using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.Services.UsoStore;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.ViewModels;

public partial class WindowsUpdateAgentViewModel
{
    private const int UsoDiagnosticsSectionIndex = 4;

    private readonly SemaphoreSlim _usoDiagnosticsGate = new(1, 1);
    private readonly TimestampParser _usoTimestampParser = new();
    private readonly SqliteRepository _usoSqliteRepository = new();
    private readonly ExportService _usoExportService = new();
    private readonly DataTable _emptyRawTable = new("Empty");

    private readonly List<RebootTimelineEvent> _allRebootTimelineEvents = [];
    private readonly List<RebootHistoryRecord> _allRebootHistoryRecords = [];
    private readonly List<ProviderScanStatus> _allProviderScanStatuses = [];
    private readonly List<UpdateLifecycleRecord> _allUpdateLifecycleRecords = [];
    private readonly List<DowntimeEstimateRecord> _allDowntimeEstimateRecords = [];
    private readonly List<VariableExplanationRecord> _allVariableExplanationRecords = [];

    private ScanDiagnosticsService _scanDiagnosticsService = null!;
    private UpdateLifecycleService _updateLifecycleService = null!;
    private DowntimeAnalysisService _downtimeAnalysisService = null!;
    private RebootAnalysisService _rebootAnalysisService = null!;
    private readonly VariableExplanationService _variableExplanationService = new();

    private UsoDatabaseSnapshot? _usoSnapshot;
    private DataTable? _currentRawTable;
    private string? _usoLoadedHost;
    private string? _usoWorkingDatabasePath;
    private string? _usoWorkingDirectory;
    private bool _usoLoadedViaPowerShellFallback;

    public ObservableCollection<DashboardStatusCard> UsoDashboardCards { get; } = [];
    public ObservableCollection<RebootTimelineEvent> VisibleRebootTimelineEvents { get; } = [];
    public ObservableCollection<RebootHistoryRecord> VisibleRebootHistoryRecords { get; } = [];
    public ObservableCollection<ProviderScanStatus> VisibleProviderScanStatuses { get; } = [];
    public ObservableCollection<UpdateLifecycleRecord> VisibleUpdateLifecycleRecords { get; } = [];
    public ObservableCollection<UpdateLifecycleRecord> VisibleUpdatePropertiesRecords { get; } = [];
    public ObservableCollection<DowntimeEstimateRecord> VisibleDowntimeEstimateRecords { get; } = [];
    public ObservableCollection<VariableExplanationRecord> VisibleVariableExplanationRecords { get; } = [];
    public ObservableCollection<RawTableInfo> UsoRawTables { get; } = [];

    [ObservableProperty]
    private string _usoDatabasePath = string.Empty;

    [ObservableProperty]
    private string _usoDiagnosticsSourceText = "Current host source: not loaded.";

    [ObservableProperty]
    private string _usoDiagnosticsStatus = "Select a SQLite database to analyze.";

    [ObservableProperty]
    private string _usoDashboardAttentionSummary = "No database loaded.";

    [ObservableProperty]
    private string _usoRebootSummaryText = "No reboot analysis loaded.";

    [ObservableProperty]
    private bool _isUsoDiagnosticsBusy;

    [ObservableProperty]
    private int _selectedUsoDiagnosticsTabIndex;

    [ObservableProperty]
    private int _selectedRebootTimelineSubTabIndex;

    [ObservableProperty]
    private string _rebootTimelineSearchText = string.Empty;

    [ObservableProperty]
    private string _scanDiagnosticsSearchText = string.Empty;

    [ObservableProperty]
    private string _updateLifecycleSearchText = string.Empty;

    [ObservableProperty]
    private string _updatePropertiesSearchText = string.Empty;

    [ObservableProperty]
    private string _downtimeSearchText = string.Empty;

    [ObservableProperty]
    private string _variableDictionarySearchText = string.Empty;

    [ObservableProperty]
    private string _rawInspectorFilterText = string.Empty;

    [ObservableProperty]
    private RawTableInfo? _selectedUsoRawTable;

    [ObservableProperty]
    private DataView? _usoRawTableView;

    [ObservableProperty]
    private string _rawInspectorStatusText = "No raw table loaded.";

    [ObservableProperty]
    private RebootTimelineEvent? _selectedRebootTimelineEvent;

    [ObservableProperty]
    private RebootHistoryRecord? _selectedRebootHistoryRecord;

    [ObservableProperty]
    private ProviderScanStatus? _selectedProviderScanStatus;

    [ObservableProperty]
    private UpdateLifecycleRecord? _selectedUpdateLifecycleRecord;

    [ObservableProperty]
    private UpdateLifecycleRecord? _selectedUpdatePropertiesRecord;

    [ObservableProperty]
    private DowntimeEstimateRecord? _selectedDowntimeEstimateRecord;

    [ObservableProperty]
    private VariableExplanationRecord? _selectedVariableExplanationRecord;

    [ObservableProperty]
    private object? _selectedRawInspectorRow;

    private void InitializeUsoDiagnostics()
    {
        _scanDiagnosticsService = new ScanDiagnosticsService(_usoTimestampParser);
        _updateLifecycleService = new UpdateLifecycleService(_usoTimestampParser);
        _downtimeAnalysisService = new DowntimeAnalysisService(_usoTimestampParser);
        _rebootAnalysisService = new RebootAnalysisService(_usoTimestampParser);

        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        UsoDiagnosticsSourceText = string.IsNullOrWhiteSpace(host)
            ? "Current host source: not loaded."
            : $"Current host source: {host} (not loaded yet)";
        UsoDiagnosticsStatus = string.IsNullOrWhiteSpace(host)
            ? "Connect to a host to analyze USO diagnostics."
            : $"Ready to analyze USO diagnostics from '{host}'.";
    }

    [RelayCommand]
    private async Task BrowseUsoDatabaseAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open local Windows Update SQLite database",
            Filter = "SQLite database (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|All files (*.*)|*.*",
            FileName = Path.GetFileName(UsoDatabasePath),
            InitialDirectory = ResolveInitialDirectory(UsoDatabasePath)
        };

        if (dialog.ShowDialog() == true)
        {
            ResetUsoDiagnosticsState();
            UsoDatabasePath = dialog.FileName;
            UsoDiagnosticsSourceText = $"Local file override: {dialog.FileName}";
            UsoDiagnosticsStatus = $"Selected local database override: {dialog.FileName}";
            _usoWorkingDatabasePath = dialog.FileName;
            _usoLoadedHost = "__LOCAL_FILE__";
            await RefreshUsoDiagnosticsFromPreparedSourceAsync(dialog.FileName, CancellationToken.None);
        }
    }

    [RelayCommand]
    private async Task RefreshUsoDiagnosticsAsync()
    {
        if (_disposed)
        {
            return;
        }

        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        CurrentHost = host;
        if (string.IsNullOrWhiteSpace(host))
        {
            UsoDiagnosticsStatus = DisconnectedStatus;
            return;
        }

        await _usoDiagnosticsGate.WaitAsync();
        try
        {
            IsUsoDiagnosticsBusy = true;
            UsoDiagnosticsStatus = IsLocalHost(host)
                ? "Loading USO diagnostics from the local host..."
                : $"Loading USO diagnostics from '{host}' (SMB preferred, PowerShell fallback)...";

            var prepared = await PrepareUsoDatabaseSourceForHostAsync(host, CancellationToken.None);
            await RefreshUsoDiagnosticsFromPreparedSourceAsync(prepared.DatabasePath, CancellationToken.None, host, prepared.DisplaySource, prepared.CleanupDirectory, prepared.UsedRemoteExtraction);
        }
        catch (Exception ex)
        {
            var detailedMessage = FormatExceptionWithInnerMessages(ex);
            UsoDiagnosticsStatus = $"USO diagnostics failed: {detailedMessage}";
            _logger.LogWarning(ex, "USO diagnostics refresh failed for host {Host}", host);
        }
        finally
        {
            IsUsoDiagnosticsBusy = false;
            _usoDiagnosticsGate.Release();
        }
    }

    [RelayCommand]
    private async Task ExportCurrentUsoViewAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export current diagnostics view",
            Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = GetExportFileName()
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            switch (SelectedUsoDiagnosticsTabIndex)
            {
                case 0:
                    await _usoExportService.ExportAsync(dialog.FileName, UsoDashboardCards, CancellationToken.None);
                    break;
                case 1 when SelectedRebootTimelineSubTabIndex == 1:
                    await _usoExportService.ExportAsync(dialog.FileName, VisibleRebootHistoryRecords, CancellationToken.None);
                    break;
                case 1:
                    await _usoExportService.ExportAsync(dialog.FileName, VisibleRebootTimelineEvents, CancellationToken.None);
                    break;
                case 2:
                    await _usoExportService.ExportAsync(dialog.FileName, VisibleProviderScanStatuses, CancellationToken.None);
                    break;
                case 3:
                    await _usoExportService.ExportAsync(dialog.FileName, VisibleUpdateLifecycleRecords, CancellationToken.None);
                    break;
                case 4:
                    await _usoExportService.ExportAsync(dialog.FileName, VisibleDowntimeEstimateRecords, CancellationToken.None);
                    break;
                case 5:
                    await _usoExportService.ExportAsync(dialog.FileName, VisibleVariableExplanationRecords, CancellationToken.None);
                    break;
                case 6 when UsoRawTableView is not null:
                    await _usoExportService.ExportAsync(dialog.FileName, UsoRawTableView, CancellationToken.None);
                    break;
                case 7:
                    await _usoExportService.ExportAsync(dialog.FileName, VisibleUpdatePropertiesRecords, CancellationToken.None);
                    break;
                default:
                    UsoDiagnosticsStatus = "Nothing to export for the current view.";
                    return;
            }

            UsoDiagnosticsStatus = $"Exported current view to '{dialog.FileName}'.";
        }
        catch (Exception ex)
        {
            UsoDiagnosticsStatus = $"Export failed: {ex.Message}";
            _logger.LogWarning(ex, "USO diagnostics export failed.");
        }
    }

    [RelayCommand]
    private Task CopySelectedUsoRowAsync()
    {
        var text = BuildClipboardPayloadForCurrentUsoSelection();
        if (string.IsNullOrWhiteSpace(text))
        {
            UsoDiagnosticsStatus = "No selected row to copy.";
            return Task.CompletedTask;
        }

        Clipboard.SetText(text);
        UsoDiagnosticsStatus = "Copied selected row to clipboard.";
        return Task.CompletedTask;
    }

    private async Task LoadSelectedRawTableAsync(string tableName)
    {
        if (string.IsNullOrWhiteSpace(_usoWorkingDatabasePath) || string.IsNullOrWhiteSpace(tableName))
        {
            return;
        }

        try
        {
            EnsureSqliteInitialized();
            _currentRawTable = await _usoSqliteRepository.LoadRawTableAsync(_usoWorkingDatabasePath, tableName, CancellationToken.None);
            ApplyRawInspectorFilter();
        }
        catch (Exception ex)
        {
            RawInspectorStatusText = $"Raw table load failed: {ex.Message}";
            _logger.LogWarning(ex, "USO raw table load failed for table {TableName}", tableName);
        }
    }

    private void ApplyUsoFilters()
    {
        ReplaceFilteredCollection(VisibleRebootTimelineEvents, _allRebootTimelineEvents, RebootTimelineSearchText, item => item.SearchText);
        ReplaceFilteredCollection(VisibleRebootHistoryRecords, _allRebootHistoryRecords, RebootTimelineSearchText, item => item.SearchText);
        ReplaceFilteredCollection(VisibleProviderScanStatuses, _allProviderScanStatuses, ScanDiagnosticsSearchText, item => item.SearchText);
        ReplaceFilteredCollection(VisibleUpdateLifecycleRecords, _allUpdateLifecycleRecords, UpdateLifecycleSearchText, item => item.SearchText);
        ReplaceFilteredCollection(VisibleUpdatePropertiesRecords, _allUpdateLifecycleRecords, UpdatePropertiesSearchText, item => item.SearchText);
        ReplaceFilteredCollection(VisibleDowntimeEstimateRecords, _allDowntimeEstimateRecords, DowntimeSearchText, item => item.SearchText);
        ReplaceFilteredCollection(VisibleVariableExplanationRecords, _allVariableExplanationRecords, VariableDictionarySearchText, item => item.SearchText);
    }

    private void ApplyRawInspectorFilter()
    {
        if (_currentRawTable is null)
        {
            UsoRawTableView = _emptyRawTable.DefaultView;
            RawInspectorStatusText = "No raw table loaded.";
            return;
        }

        if (string.IsNullOrWhiteSpace(RawInspectorFilterText))
        {
            UsoRawTableView = _currentRawTable.DefaultView;
            RawInspectorStatusText = $"{_currentRawTable.Rows.Count.ToString(CultureInfo.InvariantCulture)} row(s) loaded from '{_currentRawTable.TableName}'.";
            return;
        }

        var filteredTable = _currentRawTable.Clone();
        foreach (DataRow row in _currentRawTable.Rows)
        {
            var rowText = string.Join(" ", _currentRawTable.Columns.Cast<DataColumn>().Select(column => row[column]?.ToString() ?? string.Empty));
            if (rowText.Contains(RawInspectorFilterText, StringComparison.OrdinalIgnoreCase))
            {
                filteredTable.ImportRow(row);
            }
        }

        UsoRawTableView = filteredTable.DefaultView;
        RawInspectorStatusText =
            $"{filteredTable.Rows.Count.ToString(CultureInfo.InvariantCulture)} / {_currentRawTable.Rows.Count.ToString(CultureInfo.InvariantCulture)} row(s) match the filter in '{_currentRawTable.TableName}'.";
    }

    private string BuildClipboardPayloadForCurrentUsoSelection()
    {
        object? selectedItem = SelectedUsoDiagnosticsTabIndex switch
        {
            1 when SelectedRebootTimelineSubTabIndex == 1 => SelectedRebootHistoryRecord,
            1 => SelectedRebootTimelineEvent,
            2 => SelectedProviderScanStatus,
            3 => SelectedUpdateLifecycleRecord,
            4 => SelectedDowntimeEstimateRecord,
            5 => SelectedVariableExplanationRecord,
            6 => SelectedRawInspectorRow,
            7 => SelectedUpdatePropertiesRecord,
            _ => null
        };

        if (selectedItem is DataRowView dataRowView)
        {
            return string.Join(
                Environment.NewLine,
                dataRowView.Row.Table.Columns.Cast<DataColumn>().Select(column => $"{column.ColumnName}: {dataRowView.Row[column]}"));
        }

        if (selectedItem is null)
        {
            return string.Empty;
        }

        var lines = selectedItem.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead)
            .Select(property => $"{property.Name}: {property.GetValue(selectedItem)?.ToString() ?? string.Empty}");
        return string.Join(Environment.NewLine, lines);
    }

    private static void ReplaceBackingList<T>(ICollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static void ReplaceFilteredCollection<T>(ObservableCollection<T> target, IEnumerable<T> source, string filter, Func<T, string> searchTextSelector)
    {
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? source
            : source.Where(item => searchTextSelector(item).Contains(filter, StringComparison.OrdinalIgnoreCase));
        ReplaceCollection(target, filtered);
    }

    private string GetExportFileName()
    {
        return SelectedUsoDiagnosticsTabIndex switch
        {
            0 => "uso-dashboard.csv",
            1 when SelectedRebootTimelineSubTabIndex == 1 => "uso-reboot-history.csv",
            1 => "uso-reboot-timeline.csv",
            2 => "uso-scan-diagnostics.csv",
            3 => "uso-update-lifecycle.csv",
            4 => "uso-downtime-estimates.csv",
            5 => "uso-variable-dictionary.csv",
            6 => $"{SelectedUsoRawTable?.Name ?? "uso-raw-data"}.csv",
            7 => "uso-updatesprop.csv",
            _ => "uso-export.csv"
        };
    }

    private static string ResolveInitialDirectory(string currentPath)
    {
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            var directory = Path.GetDirectoryName(currentPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                return directory;
            }
        }

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private async Task EnsureUsoDiagnosticsLoadedAsync()
    {
        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        if (_usoSnapshot is not null && string.Equals(_usoLoadedHost, host, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        await RefreshUsoDiagnosticsAsync();
    }

    private async Task PrefetchUsoDiagnosticsForHostAsync(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        if (_usoSnapshot is not null && string.Equals(_usoLoadedHost, host, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await RefreshUsoDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "USO diagnostics prefetch failed for host {Host}", host);
        }
    }

    private async Task RefreshUsoDiagnosticsFromPreparedSourceAsync(
        string databasePath,
        CancellationToken cancellationToken,
        string? loadedHost = null,
        string? sourceDisplayName = null,
        string? workingDirectory = null,
        bool usedRemoteExtraction = false)
    {
        EnsureSqliteInitialized();
        var snapshot = await _usoSqliteRepository.LoadSnapshotAsync(databasePath, cancellationToken);
        var scanStatuses = _scanDiagnosticsService.Build(snapshot.ProviderProperties);
        var lifecycleRecords = _updateLifecycleService.Build(snapshot.CompletedUpdates, snapshot.UpdateProperties, snapshot.ActionRecords);
        var downtimeRecords = _downtimeAnalysisService.Build(snapshot.DowntimeHistory);
        var rebootAnalysis = _rebootAnalysisService.Build(snapshot, lifecycleRecords, scanStatuses, downtimeRecords);
        var variableExplanations = _variableExplanationService.Build(snapshot.Variables, lifecycleRecords, downtimeRecords, rebootAnalysis.TimelineEvents);

        _usoSnapshot = snapshot;
        _usoLoadedHost = loadedHost ?? _usoLoadedHost;
        _usoWorkingDatabasePath = databasePath;

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            CleanupUsoWorkingDirectoryIfOwned();
            _usoWorkingDirectory = workingDirectory;
        }

        _usoLoadedViaPowerShellFallback = usedRemoteExtraction;
        UsoDatabasePath = sourceDisplayName ?? databasePath;
        UsoDiagnosticsSourceText = usedRemoteExtraction
            ? $"Current host source: PowerShell file transfer fallback ({UsoDatabasePath})"
            : $"Current host source: {UsoDatabasePath}";

        ReplaceCollection(UsoDashboardCards, rebootAnalysis.DashboardSummary.Cards);
        UsoDashboardAttentionSummary = rebootAnalysis.DashboardSummary.AttentionSummary;
        UsoRebootSummaryText =
            $"Reboot pending likely: {(rebootAnalysis.RebootSummary.RebootPendingLikely ? "Yes" : "No")} | " +
            $"Current update: {rebootAnalysis.RebootSummary.CurrentUpdateTitle ?? "Unknown"} | " +
            $"Confidence: {rebootAnalysis.RebootSummary.ConfidenceText}";

        ReplaceBackingList(_allRebootTimelineEvents, rebootAnalysis.TimelineEvents);
        ReplaceBackingList(_allRebootHistoryRecords, rebootAnalysis.RebootHistory);
        ReplaceBackingList(_allProviderScanStatuses, scanStatuses);
        ReplaceBackingList(_allUpdateLifecycleRecords, lifecycleRecords);
        ReplaceBackingList(_allDowntimeEstimateRecords, downtimeRecords);
        ReplaceBackingList(_allVariableExplanationRecords, variableExplanations);
        ReplaceCollection(UsoRawTables, snapshot.Tables);

        SelectedUsoRawTable ??= UsoRawTables.FirstOrDefault(table => table.RowCount > 0) ?? UsoRawTables.FirstOrDefault();
        ApplyUsoFilters();

        if (SelectedUsoRawTable is not null)
        {
            await LoadSelectedRawTableAsync(SelectedUsoRawTable.Name);
        }
        else
        {
            UsoRawTableView = _emptyRawTable.DefaultView;
            RawInspectorStatusText = "No raw tables available.";
        }

        UsoDiagnosticsStatus =
            $"Loaded {snapshot.Tables.Count.ToString(CultureInfo.InvariantCulture)} table(s), " +
            $"{snapshot.Variables.Count.ToString(CultureInfo.InvariantCulture)} variables, " +
            $"{lifecycleRecords.Count.ToString(CultureInfo.InvariantCulture)} lifecycle row(s)." +
            (usedRemoteExtraction ? " Source used PowerShell fallback." : string.Empty);
    }

    private async Task<UsoPreparedDatabaseSource> PrepareUsoDatabaseSourceForHostAsync(string host, CancellationToken cancellationToken)
    {
        var useLocalAccess = IsLocalHost(host);
        var sourcePath = ResolveUsoStorePath(host);

        try
        {
            var snapshotPath = await CreateStoreSnapshotAsync(sourcePath, cancellationToken);
            return new UsoPreparedDatabaseSource(
                DatabasePath: snapshotPath,
                CleanupDirectory: Path.GetDirectoryName(snapshotPath),
                DisplaySource: useLocalAccess ? sourcePath : $"{host} via SMB ({sourcePath})",
                UsedRemoteExtraction: false);
        }
        catch (Exception ex) when (!useLocalAccess)
        {
            _logger.LogDebug(ex, "SMB access to store.db failed for host {Host}. Falling back to PowerShell transfer.", host);
            return await ExtractUsoDatabaseViaPowerShellAsync(host, cancellationToken);
        }
    }

    private async Task<UsoPreparedDatabaseSource> ExtractUsoDatabaseViaPowerShellAsync(string host, CancellationToken cancellationToken)
    {
        var script = BuildPowerShellScriptForHost(host, useLocalAccess: false, BuildReadUsoStoreAsBase64ScriptBody());
        var execution = await RunPowershellAsync(script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(NormalizePowerShellError(string.IsNullOrWhiteSpace(execution.StdErr) ? execution.StdOut : execution.StdErr, execution.ExitCode));
        }

        var payload = JsonSerializer.Deserialize<UsoRemoteFileTransferPayload>(execution.StdOut, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("PowerShell transfer returned no JSON payload.");

        if (string.IsNullOrWhiteSpace(payload.DatabaseBase64))
        {
            throw new InvalidOperationException("PowerShell transfer returned no database content.");
        }

        var workingDirectory = Path.Combine(Path.GetTempPath(), "WindowsClientCenter", "UsoRemoteTransfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var dbPath = Path.Combine(workingDirectory, "store.db");

        await File.WriteAllBytesAsync(dbPath, Convert.FromBase64String(payload.DatabaseBase64), cancellationToken);
        if (!string.IsNullOrWhiteSpace(payload.WalBase64))
        {
            await File.WriteAllBytesAsync(dbPath + "-wal", Convert.FromBase64String(payload.WalBase64), cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(payload.ShmBase64))
        {
            await File.WriteAllBytesAsync(dbPath + "-shm", Convert.FromBase64String(payload.ShmBase64), cancellationToken);
        }

        return new UsoPreparedDatabaseSource(
            DatabasePath: dbPath,
            CleanupDirectory: workingDirectory,
            DisplaySource: $"{host} via PowerShell transfer",
            UsedRemoteExtraction: true);
    }

    private void ResetUsoDiagnosticsState()
    {
        _usoSnapshot = null;
        _usoLoadedHost = null;
        _usoWorkingDatabasePath = null;
        _usoLoadedViaPowerShellFallback = false;
        _currentRawTable = null;
        UsoDatabasePath = string.Empty;
        ReplaceCollection(UsoDashboardCards, []);
        ReplaceCollection(VisibleRebootTimelineEvents, []);
        ReplaceCollection(VisibleRebootHistoryRecords, []);
        ReplaceCollection(VisibleProviderScanStatuses, []);
        ReplaceCollection(VisibleUpdateLifecycleRecords, []);
        ReplaceCollection(VisibleUpdatePropertiesRecords, []);
        ReplaceCollection(VisibleDowntimeEstimateRecords, []);
        ReplaceCollection(VisibleVariableExplanationRecords, []);
        ReplaceCollection(UsoRawTables, []);
        SelectedUpdateLifecycleRecord = null;
        SelectedUpdatePropertiesRecord = null;
        UsoRawTableView = _emptyRawTable.DefaultView;
        RawInspectorStatusText = "No raw table loaded.";
        CleanupUsoWorkingDirectoryIfOwned();
    }

    private void CleanupUsoWorkingDirectoryIfOwned()
    {
        if (string.IsNullOrWhiteSpace(_usoWorkingDirectory))
        {
            return;
        }

        try
        {
            if (Directory.Exists(_usoWorkingDirectory))
            {
                Directory.Delete(_usoWorkingDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
        finally
        {
            _usoWorkingDirectory = null;
        }
    }

    private static string BuildReadUsoStoreAsBase64ScriptBody()
    {
        return
            "function Read-SharedFileBase64 {" +
            "  param([string]$Path);" +
            "  if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) { return '' };" +
            "  $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete;" +
            "  $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share);" +
            "  try {" +
            "    $memory = New-Object System.IO.MemoryStream;" +
            "    try { $stream.CopyTo($memory); return [System.Convert]::ToBase64String($memory.ToArray()) } finally { $memory.Dispose() };" +
            "  } finally { $stream.Dispose() };" +
            "};" +
            "$dbPath = Join-Path $env:ProgramData 'USOPrivate\\UpdateStore\\store.db';" +
            "if (-not (Test-Path -LiteralPath $dbPath)) { throw ('store.db was not found at ' + $dbPath) };" +
            "$payload = [PSCustomObject]@{" +
            "  DatabasePath = $dbPath;" +
            "  DatabaseBase64 = Read-SharedFileBase64 -Path $dbPath;" +
            "  WalBase64 = Read-SharedFileBase64 -Path ($dbPath + '-wal');" +
            "  ShmBase64 = Read-SharedFileBase64 -Path ($dbPath + '-shm')" +
            "};" +
            "$payload | ConvertTo-Json -Compress;";
    }

    partial void OnUsoDiagnosticsStatusChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ForwardStatusToHost(value);
    }

    partial void OnSelectedUsoRawTableChanged(RawTableInfo? value)
    {
        if (value is null)
        {
            UsoRawTableView = _emptyRawTable.DefaultView;
            return;
        }

        _ = LoadSelectedRawTableAsync(value.Name);
    }

    partial void OnRebootTimelineSearchTextChanged(string value)
    {
        ApplyUsoFilters();
    }

    partial void OnScanDiagnosticsSearchTextChanged(string value)
    {
        ApplyUsoFilters();
    }

    partial void OnUpdateLifecycleSearchTextChanged(string value)
    {
        ApplyUsoFilters();
    }

    partial void OnUpdatePropertiesSearchTextChanged(string value)
    {
        ApplyUsoFilters();
    }

    partial void OnDowntimeSearchTextChanged(string value)
    {
        ApplyUsoFilters();
    }

    partial void OnVariableDictionarySearchTextChanged(string value)
    {
        ApplyUsoFilters();
    }

    partial void OnRawInspectorFilterTextChanged(string value)
    {
        ApplyRawInspectorFilter();
    }

    private sealed record UsoPreparedDatabaseSource(string DatabasePath, string? CleanupDirectory, string DisplaySource, bool UsedRemoteExtraction);

    private sealed class UsoRemoteFileTransferPayload
    {
        public string? DatabasePath { get; init; }

        public string? DatabaseBase64 { get; init; }

        public string? WalBase64 { get; init; }

        public string? ShmBase64 { get; init; }
    }
}
