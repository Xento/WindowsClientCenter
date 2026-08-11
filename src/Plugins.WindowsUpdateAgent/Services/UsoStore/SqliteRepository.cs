using System.Data;
using System.IO;
using Microsoft.Data.Sqlite;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Services.UsoStore;

public sealed class SqliteRepository
{
    public async Task<UsoDatabaseSnapshot> LoadSnapshotAsync(string databasePath, CancellationToken cancellationToken)
    {
        var snapshotPath = await CreateSnapshotAsync(databasePath, cancellationToken);
        var snapshotDirectory = Path.GetDirectoryName(snapshotPath) ?? string.Empty;

        try
        {
            await using var connection = await OpenReadOnlyConnectionAsync(snapshotPath, cancellationToken);
            var tables = await LoadTableInfosAsync(connection, cancellationToken);
            var tableLookup = tables.Select(table => table.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            return new UsoDatabaseSnapshot
            {
                DatabasePath = databasePath,
                Tables = tables,
                Variables = tableLookup.Contains("VARIABLES")
                    ? await LoadVariablesAsync(connection, cancellationToken)
                    : [],
                ProviderProperties = tableLookup.Contains("PROVIDERSPROP")
                    ? await LoadProviderPropertiesAsync(connection, cancellationToken)
                    : [],
                UpdateProperties = tableLookup.Contains("UPDATESPROP")
                    ? await LoadUpdatePropertiesAsync(connection, cancellationToken)
                    : [],
                CompletedUpdates = tableLookup.Contains("COMPLETEDUPDATES")
                    ? await LoadCompletedUpdatesAsync(connection, cancellationToken)
                    : [],
                ActionRecords = tableLookup.Contains("ACTIONRECORDS")
                    ? await LoadActionRecordsAsync(connection, cancellationToken)
                    : [],
                DowntimeHistory = tableLookup.Contains("DOWNTIMEHISTORY")
                    ? await LoadDowntimeHistoryAsync(connection, cancellationToken)
                    : []
            };
        }
        finally
        {
            TryDeleteDirectory(snapshotDirectory);
        }
    }

    public async Task<DataTable> LoadRawTableAsync(string databasePath, string tableName, CancellationToken cancellationToken)
    {
        var snapshotPath = await CreateSnapshotAsync(databasePath, cancellationToken);
        var snapshotDirectory = Path.GetDirectoryName(snapshotPath) ?? string.Empty;

        try
        {
            await using var connection = await OpenReadOnlyConnectionAsync(snapshotPath, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {QuoteIdentifier(tableName)};";

            var table = new DataTable(tableName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                table.Columns.Add(reader.GetName(i), typeof(string));
            }

            while (await reader.ReadAsync(cancellationToken))
            {
                var row = table.NewRow();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i) ? string.Empty : reader.GetValue(i)?.ToString() ?? string.Empty;
                }

                table.Rows.Add(row);
            }

            return table;
        }
        finally
        {
            TryDeleteDirectory(snapshotDirectory);
        }
    }

    private static async Task<SqliteConnection> OpenReadOnlyConnectionAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString());

        await connection.OpenAsync(cancellationToken);
        await using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 1000;";
        await pragmaCommand.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task<IReadOnlyList<RawTableInfo>> LoadTableInfosAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var tableNames = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tableNames.Add(reader.GetString(0));
            }
        }

        var results = new List<RawTableInfo>(tableNames.Count);
        foreach (var tableName in tableNames)
        {
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = $"SELECT COUNT(1) FROM {QuoteIdentifier(tableName)};";
            var scalar = await countCommand.ExecuteScalarAsync(cancellationToken);
            results.Add(new RawTableInfo
            {
                Name = tableName,
                RowCount = scalar is long count ? count : Convert.ToInt64(scalar ?? 0L)
            });
        }

        return results;
    }

    private static async Task<IReadOnlyList<VariableRecord>> LoadVariablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var parser = new TimestampParser();
        var results = new List<VariableRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT KEY, VALUE, TYPE FROM VARIABLES ORDER BY KEY;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(parser.CreateVariableRecord(
                reader["KEY"]?.ToString() ?? string.Empty,
                reader["VALUE"]?.ToString() ?? string.Empty,
                reader["TYPE"] is long type ? (int)type : 0));
        }

        return results;
    }

    private static async Task<IReadOnlyList<UsoProviderPropertyRecord>> LoadProviderPropertiesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var results = new List<UsoProviderPropertyRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PROVIDERID, VARIABLE, VALUE, TYPE FROM PROVIDERSPROP ORDER BY PROVIDERID, VARIABLE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new UsoProviderPropertyRecord
            {
                ProviderId = reader["PROVIDERID"]?.ToString() ?? string.Empty,
                Variable = reader["VARIABLE"]?.ToString() ?? string.Empty,
                Value = reader["VALUE"]?.ToString() ?? string.Empty,
                Type = reader["TYPE"] is long type ? (int)type : 0
            });
        }

        return results;
    }

    private static async Task<IReadOnlyList<UsoUpdatePropertyRecord>> LoadUpdatePropertiesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var results = new List<UsoUpdatePropertyRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PROVIDERID, UPDATEID, VARIABLE, VALUE, TYPE FROM UPDATESPROP ORDER BY PROVIDERID, UPDATEID, VARIABLE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new UsoUpdatePropertyRecord
            {
                ProviderId = reader["PROVIDERID"]?.ToString() ?? string.Empty,
                UpdateId = reader["UPDATEID"]?.ToString() ?? string.Empty,
                Variable = reader["VARIABLE"]?.ToString() ?? string.Empty,
                Value = reader["VALUE"]?.ToString() ?? string.Empty,
                Type = reader["TYPE"] is long type ? (int)type : 0
            });
        }

        return results;
    }

    private static async Task<IReadOnlyList<UsoCompletedUpdateRecord>> LoadCompletedUpdatesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var results = new List<UsoCompletedUpdateRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PROVIDERID, UPDATEID, TIME, TITLE, DESCRIPTION, MOREINFOURL, HISTORYCATEGORY, UNINSTALL, WASREBOOTREQUIRED, FOROS, METADATA FROM COMPLETEDUPDATES ORDER BY TIME DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new UsoCompletedUpdateRecord
            {
                ProviderId = reader["PROVIDERID"]?.ToString() ?? string.Empty,
                UpdateId = reader["UPDATEID"]?.ToString() ?? string.Empty,
                TimeRaw = reader["TIME"]?.ToString() ?? string.Empty,
                Title = reader["TITLE"]?.ToString() ?? string.Empty,
                Description = reader["DESCRIPTION"]?.ToString() ?? string.Empty,
                MoreInfoUrl = reader["MOREINFOURL"]?.ToString() ?? string.Empty,
                HistoryCategory = reader["HISTORYCATEGORY"]?.ToString() ?? string.Empty,
                Uninstall = reader["UNINSTALL"] as long? is long uninstall ? (int)uninstall : null,
                WasRebootRequired = ParseNullableBool(reader["WASREBOOTREQUIRED"]),
                ForOs = reader["FOROS"] as long? is long forOs ? (int)forOs : null,
                Metadata = reader["METADATA"]?.ToString() ?? string.Empty
            });
        }

        return results;
    }

    private static async Task<IReadOnlyList<UsoActionRecord>> LoadActionRecordsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var results = new List<UsoActionRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PROVIDERID, UPDATEID, TIME, ACTION, ACTIONCLASS, RESULT FROM ACTIONRECORDS ORDER BY TIME;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new UsoActionRecord
            {
                ProviderId = reader["PROVIDERID"]?.ToString() ?? string.Empty,
                UpdateId = reader["UPDATEID"]?.ToString() ?? string.Empty,
                TimeRaw = reader["TIME"]?.ToString() ?? string.Empty,
                Action = reader["ACTION"]?.ToString() ?? string.Empty,
                ActionClass = reader["ACTIONCLASS"]?.ToString() ?? string.Empty,
                Result = reader["RESULT"] as long? is long result ? (int)result : null
            });
        }

        return results;
    }

    private static async Task<IReadOnlyList<UsoDowntimeHistoryRecord>> LoadDowntimeHistoryAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var results = new List<UsoDowntimeHistoryRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT UNIQUEID, ESTIMATEDTIME, ESTIMATEDTIMEHIGH, TIMESTAMP, REALLABEL, REALLABELSECONDS, UPDATEMETADATA FROM DOWNTIMEHISTORY ORDER BY TIMESTAMP DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new UsoDowntimeHistoryRecord
            {
                UniqueId = reader["UNIQUEID"]?.ToString() ?? string.Empty,
                EstimatedTime = reader["ESTIMATEDTIME"] as long?,
                EstimatedTimeHigh = reader["ESTIMATEDTIMEHIGH"] as long?,
                TimestampRaw = reader["TIMESTAMP"]?.ToString() ?? string.Empty,
                RealLabel = reader["REALLABEL"] as long?,
                RealLabelSeconds = reader["REALLABELSECONDS"] as long?,
                UpdateMetadata = reader["UPDATEMETADATA"]?.ToString() ?? string.Empty
            });
        }

        return results;
    }

    private static bool? ParseNullableBool(object? value)
    {
        return value switch
        {
            long number => number != 0,
            int number => number != 0,
            string text when text == "1" => true,
            string text when text == "0" => false,
            _ => null
        };
    }

    private static async Task<string> CreateSnapshotAsync(string sourceDbPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourceDbPath))
        {
            throw new FileNotFoundException("SQLite database was not found.", sourceDbPath);
        }

        var snapshotDirectory = Path.Combine(Path.GetTempPath(), "WindowsClientCenter", "UsoDiagnostics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshotDirectory);

        var snapshotDbPath = Path.Combine(snapshotDirectory, Path.GetFileName(sourceDbPath));
        await CopyReadableAsync(sourceDbPath, snapshotDbPath, cancellationToken);
        await CopyIfExistsAsync(sourceDbPath + "-wal", snapshotDbPath + "-wal", cancellationToken);
        await CopyIfExistsAsync(sourceDbPath + "-shm", snapshotDbPath + "-shm", cancellationToken);
        return snapshotDbPath;
    }

    private static async Task CopyIfExistsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        await CopyReadableAsync(sourcePath, destinationPath, cancellationToken);
    }

    private static async Task CopyReadableAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
