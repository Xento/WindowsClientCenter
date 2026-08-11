using System.Data;
using System.IO;
using System.Reflection;
using System.Text;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Services.UsoStore;

public sealed class ExportService
{
    public async Task ExportAsync<T>(string path, IEnumerable<T> rows, CancellationToken cancellationToken)
    {
        var items = rows.ToArray();
        var properties = typeof(T)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead)
            .ToArray();

        await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync(string.Join(",", properties.Select(property => Escape(property.Name))));
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = properties.Select(property => Escape(property.GetValue(item)?.ToString() ?? string.Empty));
            await writer.WriteLineAsync(string.Join(",", values));
        }
    }

    public async Task ExportAsync(string path, DataView view, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var columns = view.Table?.Columns.Cast<DataColumn>().ToArray() ?? [];
        await writer.WriteLineAsync(string.Join(",", columns.Select(column => Escape(column.ColumnName))));

        foreach (DataRowView row in view)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = columns.Select(column => Escape(row.Row[column]?.ToString() ?? string.Empty));
            await writer.WriteLineAsync(string.Join(",", values));
        }
    }

    private static string Escape(string text)
    {
        if (text.Contains('"', StringComparison.Ordinal))
        {
            text = text.Replace("\"", "\"\"", StringComparison.Ordinal);
        }

        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? $"\"{text}\""
            : text;
    }
}
