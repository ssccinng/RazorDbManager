using System.Globalization;
using MySqlConnector;
using RazorDbManager.Core;

namespace RazorDbManager.MySql.Infrastructure;

internal static class MySqlValueConverter
{
    public static DbValue Read(
        MySqlDataReader reader,
        int ordinal,
        DbColumnMetadata column,
        long maximumBytes,
        out bool truncated)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        truncated = false;
        if (reader.IsDBNull(ordinal))
        {
            return DbValue.Null;
        }

        if (column.Type.Kind is DbDataKind.Binary or DbDataKind.Geometry)
        {
            var length = reader.GetBytes(ordinal, 0, null, 0, 0);
            var bytesToRead = checked((int)Math.Min(length, Math.Min(maximumBytes, int.MaxValue)));
            var buffer = new byte[bytesToRead];
            if (bytesToRead > 0)
            {
                _ = reader.GetBytes(ordinal, 0, buffer, 0, bytesToRead);
            }

            truncated = length > bytesToRead;
            return DbValue.FromBinary(
                buffer,
                column.Type.Kind == DbDataKind.Geometry ? DbValueKind.Geometry : DbValueKind.Binary);
        }

        if (reader.GetFieldType(ordinal) == typeof(string))
        {
            var length = reader.GetChars(ordinal, 0, null, 0, 0);
            // UTF-16 in-memory size is the bound that protects the process. It is intentionally
            // stricter than the possible UTF-8 output size for ASCII-heavy values.
            var maximumCharacters = Math.Min(maximumBytes / sizeof(char), int.MaxValue);
            var charactersToRead = checked((int)Math.Min(length, maximumCharacters));
            var buffer = new char[charactersToRead];
            if (charactersToRead > 0)
            {
                _ = reader.GetChars(ordinal, 0, buffer, 0, charactersToRead);
            }

            truncated = length > charactersToRead;
            return DbValue.FromText(ValueKind(column.Type.Kind), new string(buffer));
        }

        // MySqlDecimal preserves DECIMAL(65,30), which cannot fit in System.Decimal.
        // Other scalar values remain invariant text to preserve unsigned BIGINT, zero dates,
        // ENUM/SET and BIT without projecting unknown schemas into CLR entity types.
        var text = column.Type.Kind == DbDataKind.Decimal
            ? reader.GetMySqlDecimal(ordinal).ToString()
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
        var maximumCharactersForScalar = checked((int)Math.Min(maximumBytes / sizeof(char), int.MaxValue));
        if (text.Length > maximumCharactersForScalar)
        {
            text = text[..maximumCharactersForScalar];
            truncated = true;
        }
        var kind = ValueKind(column.Type.Kind);

        if (kind == DbValueKind.Boolean && text is "0" or "1")
        {
            text = text == "1" ? "true" : "false";
        }

        return DbValue.FromText(kind, text);
    }

    private static DbValueKind ValueKind(DbDataKind kind) => kind switch
    {
        DbDataKind.SignedInteger => DbValueKind.SignedInteger,
        DbDataKind.UnsignedInteger => DbValueKind.UnsignedInteger,
        DbDataKind.Decimal => DbValueKind.Decimal,
        DbDataKind.FloatingPoint => DbValueKind.FloatingPoint,
        DbDataKind.Boolean => DbValueKind.Boolean,
        DbDataKind.Date => DbValueKind.Date,
        DbDataKind.Time => DbValueKind.Time,
        DbDataKind.DateTime => DbValueKind.DateTime,
        DbDataKind.Timestamp => DbValueKind.Timestamp,
        DbDataKind.Json => DbValueKind.Json,
        DbDataKind.Enum => DbValueKind.Enum,
        DbDataKind.Set => DbValueKind.Set,
        DbDataKind.BitString => DbValueKind.BitString,
        DbDataKind.Guid => DbValueKind.Guid,
        DbDataKind.ProviderSpecific => DbValueKind.ProviderSpecific,
        _ => DbValueKind.String,
    };

    public static object ToParameter(DbValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            DbValueKind.Null => DBNull.Value,
            DbValueKind.Binary or DbValueKind.Geometry => value.Binary.ToArray(),
            DbValueKind.Boolean => value.Text == "true",
            _ => value.Text ?? string.Empty,
        };
    }

    public static long EstimateSize(DbValue value) => value.Kind switch
    {
        DbValueKind.Null => 4,
        DbValueKind.Binary or DbValueKind.Geometry => value.Binary.Length,
        _ => value.Text?.Length * sizeof(char) ?? 0,
    };
}
