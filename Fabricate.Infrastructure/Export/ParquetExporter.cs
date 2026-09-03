using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Parquet;
using Parquet.Schema;

namespace Fabricate.Infrastructure.Export;

/// <summary>
/// Exports generated data to Parquet format. Produces one .parquet file per table.
/// All columns are nullable. Uses Parquet.Net v6 low-level API.
/// </summary>
public sealed class ParquetExporter : IExporter
{
    public string Name => "parquet";

    public async Task ExportAsync(IReadOnlyList<TableData> tables, string target, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(target);

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (table.Rows.Count == 0)
                continue;

            var filePath = Path.Combine(target, table.Table.Replace(".", "_", StringComparison.Ordinal) + ".parquet");
            await WriteTableAsync(table, filePath, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteTableAsync(TableData table, string filePath, CancellationToken cancellationToken)
    {
        var headers = table.Rows[0].Keys.OrderBy(h => h, StringComparer.Ordinal).ToList();

        // Infer CLR types by sampling values
        var colTypes = headers.Select(h => InferColumnType(h, table.Rows)).ToList();

        var fields = headers.Select((h, i) => MakeField(h, colTypes[i])).ToArray();
        var schema = new ParquetSchema(fields);

        await using var fs = File.Create(filePath);
        await using var writer = await ParquetWriter.CreateAsync(schema, fs, cancellationToken: cancellationToken).ConfigureAwait(false);

        using var rg = writer.CreateRowGroup();

        for (var i = 0; i < headers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteColumnAsync(rg, schema.DataFields[i], headers[i], colTypes[i], table.Rows).ConfigureAwait(false);
        }
    }

    private static Type InferColumnType(string header, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var sample = rows.Select(r => r.TryGetValue(header, out var v) ? v : null)
            .FirstOrDefault(v => v is not null);

        return sample switch
        {
            int => typeof(int),
            long => typeof(long),
            float => typeof(float),
            double => typeof(double),
            decimal => typeof(decimal),
            bool => typeof(bool),
            DateOnly => typeof(DateTime),
            DateTime => typeof(DateTime),
            DateTimeOffset => typeof(DateTime),
            byte[] => typeof(byte[]),
            _ => typeof(string)
        };
    }

    private static DataField MakeField(string name, Type clrType)
    {
        if (clrType == typeof(string) || clrType == typeof(byte[]))
            return new DataField(name, clrType);

        // Value types: use nullable version so null rows are valid
        var nullableType = typeof(Nullable<>).MakeGenericType(clrType);
        return new DataField(name, nullableType);
    }

    private static Task WriteColumnAsync(
        ParquetRowGroupWriter rg,
        DataField field,
        string header,
        Type clrType,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (clrType == typeof(int))
            return rg.WriteAsync<int>(field, Extract<int?>(rows, header, v => v is null ? null : Convert.ToInt32(v)));
        if (clrType == typeof(long))
            return rg.WriteAsync<long>(field, Extract<long?>(rows, header, v => v is null ? null : Convert.ToInt64(v)));
        if (clrType == typeof(float))
            return rg.WriteAsync<float>(field, Extract<float?>(rows, header, v => v is null ? null : Convert.ToSingle(v)));
        if (clrType == typeof(double))
            return rg.WriteAsync<double>(field, Extract<double?>(rows, header, v => v is null ? null : Convert.ToDouble(v)));
        if (clrType == typeof(decimal))
            return rg.WriteAsync<decimal>(field, Extract<decimal?>(rows, header, v => v is null ? null : Convert.ToDecimal(v)));
        if (clrType == typeof(bool))
            return rg.WriteAsync<bool>(field, Extract<bool?>(rows, header, v => v is null ? null : Convert.ToBoolean(v)));
        if (clrType == typeof(DateTime))
            return rg.WriteAsync<DateTime>(field, Extract<DateTime?>(rows, header, ToDateTime));
        if (clrType == typeof(byte[]))
            return rg.WriteAsync(field, (IReadOnlyCollection<byte[]?>)Extract<byte[]?>(rows, header, ToBytes));

        return rg.WriteAsync(field, (IReadOnlyCollection<string?>)Extract<string?>(rows, header, v => v?.ToString()));
    }

    private static T[] Extract<T>(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string col,
        Func<object?, T> convert)
    {
        var arr = new T[rows.Count];
        for (var i = 0; i < rows.Count; i++)
            arr[i] = convert(rows[i].TryGetValue(col, out var v) ? v : null);
        return arr;
    }

    private static DateTime? ToDateTime(object? v) => v switch
    {
        null => null,
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => DateTime.TryParse(v.ToString(), out var p) ? p : null
    };

    private static byte[]? ToBytes(object? v) => v switch
    {
        null => null,
        byte[] b => b,
        _ => System.Text.Encoding.UTF8.GetBytes(v.ToString() ?? string.Empty)
    };
}
