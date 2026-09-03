using Fabricate.Domain.Enums;

namespace Fabricate.Application.Schema;

public static class SqlTypeMapper
{
    private static readonly Dictionary<string, DataKind> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bool"] = DataKind.Boolean,
        ["boolean"] = DataKind.Boolean,
        ["bit"] = DataKind.Boolean,
        ["smallint"] = DataKind.Integer,
        ["integer"] = DataKind.Integer,
        ["int"] = DataKind.Integer,
        ["bigint"] = DataKind.Long,
        ["numeric"] = DataKind.Decimal,
        ["decimal"] = DataKind.Decimal,
        ["float"] = DataKind.Double,
        ["double"] = DataKind.Double,
        ["real"] = DataKind.Double,
        ["text"] = DataKind.String,
        ["varchar"] = DataKind.String,
        ["character varying"] = DataKind.String,
        ["char"] = DataKind.String,
        ["uuid"] = DataKind.Guid,
        ["date"] = DataKind.Date,
        ["timestamp"] = DataKind.DateTime,
        ["timestamp without time zone"] = DataKind.DateTime,
        ["timestamp with time zone"] = DataKind.TimestampTz,
        ["timestamptz"] = DataKind.TimestampTz,
        ["json"] = DataKind.Json,
        ["jsonb"] = DataKind.Json,
        ["blob"] = DataKind.Binary,
        ["bytea"] = DataKind.Binary
    };

    private static readonly HashSet<string> UnknownTypes = new(StringComparer.OrdinalIgnoreCase);

    public static DataKind MapToDataKind(string sqlType)
    {
        var normalized = sqlType.Trim();
        if (Map.TryGetValue(normalized, out var dataKind))
        {
            return dataKind;
        }

        lock (UnknownTypes)
        {
            UnknownTypes.Add(normalized);
        }

        return DataKind.Unknown;
    }

    public static IReadOnlyList<string> GetUnknownTypeDiagnostics()
    {
        lock (UnknownTypes)
        {
            return UnknownTypes.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
