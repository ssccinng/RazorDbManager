using RazorDbManager.Core;

namespace RazorDbManager.Components;

internal enum SchemaColumnSeedDefaultKind
{
    None,
    Literal,
    CurrentTimestamp,
}

internal sealed record SchemaColumnDraftSeed(
    string Name,
    SchemaDataType DataType,
    long? Length,
    int? Precision,
    int? Scale,
    bool Nullable,
    bool Unsigned,
    bool AutoIncrement,
    string AllowedValues,
    SchemaColumnSeedDefaultKind DefaultKind,
    string DefaultValue,
    int? CurrentTimestampPrecision,
    string Comment,
    string CharacterSet,
    string Collation);

internal static class SchemaColumnDraftMapper
{
    public static bool TryCreate(DbColumnMetadata column, out SchemaColumnDraftSeed? seed)
    {
        ArgumentNullException.ThrowIfNull(column);
        seed = null;

        if (column.IsGenerated || !TryMapType(column.Type, out SchemaDataType dataType))
        {
            return false;
        }

        IReadOnlyList<string> values = column.Type.AllowedValues ?? [];
        if (values.Any(value => string.IsNullOrEmpty(value) || value.Contains(',') || value != value.Trim())
            || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            return false;
        }

        if (!TryMapDefault(column.DefaultSql, dataType, out SchemaColumnSeedDefaultKind defaultKind,
                out string defaultValue, out int? currentTimestampPrecision))
        {
            return false;
        }

        seed = new SchemaColumnDraftSeed(
            column.Name,
            dataType,
            SupportsLength(dataType) ? column.Type.Length : null,
            dataType == SchemaDataType.Decimal ? column.Type.Precision : null,
            dataType == SchemaDataType.Decimal ? column.Type.Scale : null,
            column.IsNullable,
            SupportsUnsigned(dataType) && column.Type.IsUnsigned,
            SupportsAutoIncrement(dataType) && column.IsAutoIncrement,
            string.Join(", ", values),
            defaultKind,
            defaultValue,
            currentTimestampPrecision,
            column.Comment ?? string.Empty,
            column.CharacterSet ?? string.Empty,
            column.Collation ?? string.Empty);
        return true;
    }

    private static bool TryMapType(DbTypeDescriptor type, out SchemaDataType dataType)
    {
        string declaration = type.ProviderTypeName.Trim();
        int separator = declaration.IndexOfAny(['(', ' ']);
        string providerType = (separator < 0 ? declaration : declaration[..separator]).ToLowerInvariant();

        bool mapped = providerType switch
        {
            "bool" or "boolean" => Set(SchemaDataType.Boolean, out dataType),
            "tinyint" when type.Kind == DbDataKind.Boolean => Set(SchemaDataType.Boolean, out dataType),
            "tinyint" => Set(SchemaDataType.Int8, out dataType),
            "smallint" => Set(SchemaDataType.Int16, out dataType),
            "int" or "integer" => Set(SchemaDataType.Int32, out dataType),
            "bigint" => Set(SchemaDataType.Int64, out dataType),
            "decimal" or "numeric" => Set(SchemaDataType.Decimal, out dataType),
            "float" => Set(SchemaDataType.Float, out dataType),
            "double" => Set(SchemaDataType.Double, out dataType),
            "char" => Set(SchemaDataType.Char, out dataType),
            "varchar" => Set(SchemaDataType.VarChar, out dataType),
            "text" => Set(SchemaDataType.Text, out dataType),
            "binary" => Set(SchemaDataType.Binary, out dataType),
            "varbinary" => Set(SchemaDataType.VarBinary, out dataType),
            "blob" => Set(SchemaDataType.Blob, out dataType),
            "date" => Set(SchemaDataType.Date, out dataType),
            "time" => Set(SchemaDataType.Time, out dataType),
            "datetime" => Set(SchemaDataType.DateTime, out dataType),
            "timestamp" => Set(SchemaDataType.Timestamp, out dataType),
            "json" => Set(SchemaDataType.Json, out dataType),
            "enum" => Set(SchemaDataType.Enum, out dataType),
            "set" => Set(SchemaDataType.Set, out dataType),
            "bit" => Set(SchemaDataType.Bit, out dataType),
            "geometry" => Set(SchemaDataType.Geometry, out dataType),
            _ => Set(default, out dataType, false),
        };

        // The structured schema model cannot preserve integer/float display
        // facets, temporal precision, or MySQL ZEROFILL.
        if (mapped && (declaration.Contains("zerofill", StringComparison.OrdinalIgnoreCase)
            || type.IsUnsigned && dataType == SchemaDataType.Boolean
            || declaration.Contains('(')
            && dataType is SchemaDataType.Int8 or SchemaDataType.Int16 or SchemaDataType.Int32
                or SchemaDataType.Int64 or SchemaDataType.Time or SchemaDataType.DateTime
                or SchemaDataType.Timestamp or SchemaDataType.Float or SchemaDataType.Double
            && dataType != SchemaDataType.Boolean))
        {
            return false;
        }

        return mapped;
    }

    private static bool TryMapDefault(
        string? defaultSql,
        SchemaDataType dataType,
        out SchemaColumnSeedDefaultKind kind,
        out string value,
        out int? currentTimestampPrecision)
    {
        kind = SchemaColumnSeedDefaultKind.None;
        value = string.Empty;
        currentTimestampPrecision = null;
        if (defaultSql is null)
        {
            return true;
        }

        string candidate = defaultSql.Trim();
        if (candidate.StartsWith("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase))
        {
            if (dataType is not (SchemaDataType.DateTime or SchemaDataType.Timestamp))
            {
                return false;
            }

            string suffix = candidate["CURRENT_TIMESTAMP".Length..];
            if (suffix.Length > 0)
            {
                if (suffix == "()")
                {
                    kind = SchemaColumnSeedDefaultKind.CurrentTimestamp;
                    return true;
                }
                if (suffix.Length < 3 || suffix[0] != '(' || suffix[^1] != ')'
                    || !int.TryParse(suffix[1..^1], out int precision) || precision is < 0 or > 6)
                {
                    return false;
                }
                currentTimestampPrecision = precision;
            }

            kind = SchemaColumnSeedDefaultKind.CurrentTimestamp;
            return true;
        }

        if (!SupportsLiteralDefault(dataType) || candidate.StartsWith('(') || candidate.EndsWith("()", StringComparison.Ordinal))
        {
            return false;
        }

        kind = SchemaColumnSeedDefaultKind.Literal;
        value = defaultSql;
        return true;
    }

    private static bool Set(SchemaDataType value, out SchemaDataType target, bool result = true)
    {
        target = value;
        return result;
    }

    private static bool SupportsLength(SchemaDataType type) => type is SchemaDataType.Char or SchemaDataType.VarChar
        or SchemaDataType.Binary or SchemaDataType.VarBinary or SchemaDataType.Bit;
    private static bool SupportsUnsigned(SchemaDataType type) => type is SchemaDataType.Int8 or SchemaDataType.Int16
        or SchemaDataType.Int32 or SchemaDataType.Int64 or SchemaDataType.Decimal;
    private static bool SupportsAutoIncrement(SchemaDataType type) => type is SchemaDataType.Int8 or SchemaDataType.Int16
        or SchemaDataType.Int32 or SchemaDataType.Int64;
    private static bool SupportsLiteralDefault(SchemaDataType type) => type is not (SchemaDataType.Binary
        or SchemaDataType.VarBinary or SchemaDataType.Blob or SchemaDataType.Geometry or SchemaDataType.Json
        or SchemaDataType.Text or SchemaDataType.Bit);
}
