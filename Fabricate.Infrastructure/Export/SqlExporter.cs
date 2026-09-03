using System.Text;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Export;

/// <summary>Exports table data as SQL INSERT statements.</summary>
public sealed class SqlExporter : IExporter
{
    public string Name => "sql";

    public async Task ExportAsync(IReadOnlyList<TableData> tables, string target, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(target);

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = Path.Combine(target, table.Table.Replace(".", "_", StringComparison.Ordinal) + ".sql");

            if (table.Rows.Count == 0)
            {
                await File.WriteAllTextAsync(filePath, string.Empty, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var sb = new StringBuilder();
            var headers = table.Rows[0].Keys.OrderBy(static h => h, StringComparer.Ordinal).ToArray();
            var columnList = string.Join(", ", headers.Select(h => $"\"{h}\""));

            foreach (var row in table.Rows)
            {
                var values = string.Join(", ", headers.Select(h => FormatValue(row.TryGetValue(h, out var v) ? v : null)));
                sb.AppendLine($"INSERT INTO \"{table.Table}\" ({columnList}) VALUES ({values});");
            }

            await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "NULL",
            bool b => b ? "TRUE" : "FALSE",
            int or long or short or byte => value.ToString()!,
            float or double or decimal => value.ToString()!,
            string s => $"'{s.Replace("'", "''", StringComparison.Ordinal)}'",
            Guid g => $"'{g}'",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
            DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss zzz}'",
            DateOnly d => $"'{d:yyyy-MM-dd}'",
            _ => $"'{value}'"
        };
    }
}
