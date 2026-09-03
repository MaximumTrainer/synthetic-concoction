using System.Text;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Export;

public sealed class CsvExporter : IExporter, IStreamingExporter
{
    public string Name => "csv";

    // Streaming state — one active file per table
    private StreamWriter? _writer;
    private string[]? _headers;

    public async Task ExportAsync(IReadOnlyList<TableData> tables, string target, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(target);

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = Path.Combine(target, table.Table.Replace(".", "_", StringComparison.Ordinal) + ".csv");

            if (table.Rows.Count == 0)
            {
                await File.WriteAllTextAsync(filePath, string.Empty, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var headers = table.Rows[0].Keys.OrderBy(static h => h, StringComparer.Ordinal).ToArray();
            var lines = new List<string> { string.Join(',', headers.Select(Escape)) };

            foreach (var row in table.Rows)
            {
                var values = headers.Select(h => Escape(row.TryGetValue(h, out var value) ? value : null));
                lines.Add(string.Join(',', values));
            }

            await File.WriteAllLinesAsync(filePath, lines, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task BeginTableAsync(TableSchema table, string target, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(target);
        var filePath = Path.Combine(target, table.QualifiedName.Replace(".", "_", StringComparison.Ordinal) + ".csv");
        _writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
        _headers = table.Columns.Select(c => c.Name).OrderBy(static h => h, StringComparer.Ordinal).ToArray();
        await _writer.WriteLineAsync(string.Join(',', _headers.Select(Escape)));
    }

    public async Task WriteRowAsync(IReadOnlyDictionary<string, object?> row, CancellationToken cancellationToken = default)
    {
        if (_writer is null || _headers is null) throw new InvalidOperationException("BeginTableAsync must be called first.");
        var values = _headers.Select(h => Escape(row.TryGetValue(h, out var value) ? value : null));
        await _writer.WriteLineAsync(string.Join(',', values));
    }

    public async Task EndTableAsync(CancellationToken cancellationToken = default)
    {
        if (_writer is not null)
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }
        _headers = null;
    }

    private static string Escape(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        if (text.Contains(',', StringComparison.Ordinal) || text.Contains('"', StringComparison.Ordinal) ||
            text.Contains('\r', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal))
        {
            return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return text;
    }
}
