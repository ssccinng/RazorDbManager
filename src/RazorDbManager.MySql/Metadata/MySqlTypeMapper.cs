using System.Globalization;
using RazorDbManager.Core;

namespace RazorDbManager.MySql.Metadata;

internal static class MySqlTypeMapper
{
    private static readonly HashSet<string> IntegerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint",
    };

    public static DbTypeDescriptor Map(
        string dataType,
        string columnType,
        long? length,
        int? precision,
        int? scale)
    {
        var unsigned = columnType.Contains("unsigned", StringComparison.OrdinalIgnoreCase);
        var kind = dataType.ToLowerInvariant() switch
        {
            var value when IntegerTypes.Contains(value) =>
                dataType.Equals("tinyint", StringComparison.OrdinalIgnoreCase) && columnType.StartsWith("tinyint(1)", StringComparison.OrdinalIgnoreCase)
                    ? DbDataKind.Boolean
                    : unsigned ? DbDataKind.UnsignedInteger : DbDataKind.SignedInteger,
            "decimal" or "numeric" => DbDataKind.Decimal,
            "float" or "double" or "real" => DbDataKind.FloatingPoint,
            "bit" => DbDataKind.BitString,
            "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" => DbDataKind.Binary,
            "date" or "year" => DbDataKind.Date,
            "time" => DbDataKind.Time,
            "datetime" => DbDataKind.DateTime,
            "timestamp" => DbDataKind.Timestamp,
            "json" => DbDataKind.Json,
            "enum" => DbDataKind.Enum,
            "set" => DbDataKind.Set,
            "geometry" or "point" or "linestring" or "polygon" or "multipoint"
                or "multilinestring" or "multipolygon" or "geometrycollection" => DbDataKind.Geometry,
            "char" when columnType.StartsWith("char(36)", StringComparison.OrdinalIgnoreCase) => DbDataKind.Guid,
            "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext" => DbDataKind.Text,
            _ => DbDataKind.ProviderSpecific,
        };

        var values = kind is DbDataKind.Enum or DbDataKind.Set ? ParseAllowedValues(columnType) : null;
        return new DbTypeDescriptor(columnType, kind, unsigned, length, precision, scale, values);
    }

    internal static IReadOnlyList<string> ParseAllowedValues(string columnType)
    {
        var open = columnType.IndexOf('(');
        var close = columnType.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return [];
        }

        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;
        for (var index = open + 1; index < close; index++)
        {
            var character = columnType[index];
            if (!inQuote)
            {
                if (character == '\'') inQuote = true;
                continue;
            }

            if (character == '\\' && index + 1 < close)
            {
                current.Append(columnType[++index]);
            }
            else if (character == '\'' && index + 1 < close && columnType[index + 1] == '\'')
            {
                current.Append('\'');
                index++;
            }
            else if (character == '\'')
            {
                values.Add(current.ToString());
                current.Clear();
                inQuote = false;
            }
            else
            {
                current.Append(character);
            }
        }

        return values;
    }

    public static int? Int32OrNull(object value) => value is DBNull
        ? null
        : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    public static long? Int64OrNull(object value) => value is DBNull
        ? null
        : Convert.ToInt64(value, CultureInfo.InvariantCulture);
}
