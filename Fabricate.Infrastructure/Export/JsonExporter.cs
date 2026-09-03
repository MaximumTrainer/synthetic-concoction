using System.Text.Json;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Export;

public sealed class JsonExporter : IExporter, IStreamingExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    // Streaming state
    private Stream? _stream;
    private Utf8JsonWriter? _jsonWriter;

    public string Name => "json";

    public async Task ExportAsync(IReadOnlyList<TableData> tables, string target, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(target);
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = Path.Combine(target, table.Table.Replace(".", "_", StringComparison.Ordinal) + ".json");
            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, table.Rows, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task BeginTableAsync(TableSchema table, string target, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(target);
        var filePath = Path.Combine(target, table.QualifiedName.Replace(".", "_", StringComparison.Ordinal) + ".json");
        _stream = File.Create(filePath);
        _jsonWriter = new Utf8JsonWriter(_stream, new JsonWriterOptions { Indented = true });
        _jsonWriter.WriteStartArray();
        return Task.CompletedTask;
    }

    public async Task WriteRowAsync(IReadOnlyDictionary<string, object?> row, CancellationToken cancellationToken = default)
    {
        if (_jsonWriter is null) throw new InvalidOperationException("BeginTableAsync must be called first.");
        _jsonWriter.WriteStartObject();
        foreach (var (key, value) in row)
        {
            _jsonWriter.WritePropertyName(key);
            JsonSerializer.Serialize(_jsonWriter, value, JsonOptions);
        }
        _jsonWriter.WriteEndObject();
        await _jsonWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EndTableAsync(CancellationToken cancellationToken = default)
    {
        if (_jsonWriter is not null)
        {
            _jsonWriter.WriteEndArray();
            await _jsonWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _jsonWriter.DisposeAsync().ConfigureAwait(false);
            _jsonWriter = null;
        }
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }
}
