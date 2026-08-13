using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RazorDbManager.MySql.Sql;

namespace RazorDbManager.MySql.Schema;

internal sealed class MySqlDdlGenerator
{
    private static readonly HashSet<string> AllowedEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        "InnoDB",
        "MyISAM",
        "MEMORY",
        "ARCHIVE",
    };

    public MySqlDdlPreview Generate(MySqlSchemaChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var sql = change switch
        {
            MySqlCreateTableChange create => CreateTable(create.Definition),
            MySqlDropTableChange drop => $"DROP TABLE {MySqlIdentifier.Qualify(drop.Schema, drop.Table)}",
            MySqlRenameTableChange rename =>
                $"RENAME TABLE {MySqlIdentifier.Qualify(rename.Schema, rename.Table)} TO {MySqlIdentifier.Qualify(rename.Schema, rename.NewName)}",
            MySqlAddColumnChange add =>
                $"ALTER TABLE {MySqlIdentifier.Qualify(add.Schema, add.Table)} ADD COLUMN {Column(add.Column)}{(add.AfterColumn is null ? string.Empty : $" AFTER {MySqlIdentifier.Quote(add.AfterColumn)}")}",
            MySqlAlterColumnChange alter =>
                $"ALTER TABLE {MySqlIdentifier.Qualify(alter.Schema, alter.Table)} CHANGE COLUMN {MySqlIdentifier.Quote(alter.ExistingColumn)} {Column(alter.Column)}",
            MySqlDropColumnChange dropColumn =>
                $"ALTER TABLE {MySqlIdentifier.Qualify(dropColumn.Schema, dropColumn.Table)} DROP COLUMN {MySqlIdentifier.Quote(dropColumn.Column)}",
            MySqlAddIndexChange addIndex =>
                $"ALTER TABLE {MySqlIdentifier.Qualify(addIndex.Schema, addIndex.Table)} ADD {Index(addIndex.Index)}",
            MySqlDropIndexChange dropIndex => dropIndex.Primary
                ? $"ALTER TABLE {MySqlIdentifier.Qualify(dropIndex.Schema, dropIndex.Table)} DROP PRIMARY KEY"
                : $"ALTER TABLE {MySqlIdentifier.Qualify(dropIndex.Schema, dropIndex.Table)} DROP INDEX {MySqlIdentifier.Quote(dropIndex.Index)}",
            MySqlAddForeignKeyChange addForeignKey =>
                $"ALTER TABLE {MySqlIdentifier.Qualify(addForeignKey.Schema, addForeignKey.Table)} ADD {ForeignKey(addForeignKey.ForeignKey)}",
            MySqlDropForeignKeyChange dropForeignKey =>
                $"ALTER TABLE {MySqlIdentifier.Qualify(dropForeignKey.Schema, dropForeignKey.Table)} DROP FOREIGN KEY {MySqlIdentifier.Quote(dropForeignKey.ForeignKey)}",
            _ => throw new NotSupportedException($"Unsupported schema change '{change.GetType().Name}'."),
        };

        var destructive = change is MySqlDropTableChange or MySqlDropColumnChange or MySqlDropIndexChange
            or MySqlDropForeignKeyChange or MySqlAlterColumnChange;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        return new MySqlDdlPreview(sql, destructive, hash);
    }

    private static string CreateTable(MySqlTableDefinition definition)
    {
        ValidateTable(definition);

        var clauses = definition.Columns.Select(Column).ToList();
        clauses.AddRange((definition.Indexes ?? []).Select(Index));
        clauses.AddRange((definition.ForeignKeys ?? []).Select(ForeignKey));

        var builder = new StringBuilder()
            .Append("CREATE TABLE ")
            .Append(MySqlIdentifier.Qualify(definition.Schema, definition.Name))
            .Append(" (\n  ")
            .Append(string.Join(",\n  ", clauses))
            .Append("\n) ENGINE=")
            .Append(definition.Engine)
            .Append(" DEFAULT CHARACTER SET=")
            .Append(ValidateSimpleName(definition.CharacterSet, nameof(definition.CharacterSet)));

        if (definition.Collation is not null)
        {
            builder.Append(" COLLATE=").Append(ValidateSimpleName(definition.Collation, nameof(definition.Collation)));
        }

        if (definition.Comment is not null)
        {
            builder.Append(" COMMENT=").Append(Literal(definition.Comment));
        }

        return builder.ToString();
    }

    private static string Column(MySqlColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var builder = new StringBuilder(MySqlIdentifier.Quote(column.Name)).Append(' ').Append(Type(column));

        if (column.Unsigned)
        {
            if (column.Type is not (MySqlColumnType.TinyInt or MySqlColumnType.SmallInt or MySqlColumnType.MediumInt
                or MySqlColumnType.Int or MySqlColumnType.BigInt or MySqlColumnType.Decimal
                or MySqlColumnType.Float or MySqlColumnType.Double))
            {
                throw new ArgumentException("UNSIGNED is only valid for numeric columns.", nameof(column));
            }

            builder.Append(" UNSIGNED");
        }

        if (column.CharacterSet is not null)
        {
            builder.Append(" CHARACTER SET ").Append(ValidateSimpleName(column.CharacterSet, nameof(column.CharacterSet)));
        }

        if (column.Collation is not null)
        {
            builder.Append(" COLLATE ").Append(ValidateSimpleName(column.Collation, nameof(column.Collation)));
        }

        builder.Append(column.Nullable ? " NULL" : " NOT NULL");
        var defaultValue = column.Default ?? MySqlColumnDefault.None;
        builder.Append(defaultValue.Kind switch
        {
            MySqlDefaultKind.None => string.Empty,
            MySqlDefaultKind.Null when column.Nullable => " DEFAULT NULL",
            MySqlDefaultKind.Null => throw new ArgumentException("A NOT NULL column cannot default to NULL.", nameof(column)),
            MySqlDefaultKind.Literal => $" DEFAULT {Literal(defaultValue.Value)}",
            MySqlDefaultKind.CurrentTimestamp when column.Type is MySqlColumnType.Timestamp or MySqlColumnType.DateTime
                => defaultValue.Value is int precision
                    ? $" DEFAULT CURRENT_TIMESTAMP({FractionalSeconds(precision)})"
                    : " DEFAULT CURRENT_TIMESTAMP",
            MySqlDefaultKind.CurrentTimestamp => throw new ArgumentException("CURRENT_TIMESTAMP is only valid for temporal columns.", nameof(column)),
            _ => throw new ArgumentOutOfRangeException(nameof(column)),
        });

        if (column.AutoIncrement)
        {
            if (column.Type is not (MySqlColumnType.TinyInt or MySqlColumnType.SmallInt or MySqlColumnType.MediumInt
                or MySqlColumnType.Int or MySqlColumnType.BigInt))
            {
                throw new ArgumentException("AUTO_INCREMENT is only valid for integer columns.", nameof(column));
            }

            builder.Append(" AUTO_INCREMENT");
        }

        if (column.Comment is not null)
        {
            builder.Append(" COMMENT ").Append(Literal(column.Comment));
        }

        return builder.ToString();
    }

    private static string Type(MySqlColumnDefinition column)
    {
        static string RequireLength(MySqlColumnDefinition value, int maximum = 65_535)
        {
            if (value.Length is null or <= 0 || value.Length > maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"A length between 1 and {maximum} is required.");
            }

            return value.Length.Value.ToString(CultureInfo.InvariantCulture);
        }

        static string DecimalType(MySqlColumnDefinition value)
        {
            if (value.Length is null or < 1 or > 65 || value.Scale is null or < 0 or > 30 || value.Scale > value.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "DECIMAL precision must be 1..65 and scale 0..30 not exceeding precision.");
            }

            return $"DECIMAL({value.Length.Value.ToString(CultureInfo.InvariantCulture)},{value.Scale.Value.ToString(CultureInfo.InvariantCulture)})";
        }

        static string Values(MySqlColumnDefinition value)
        {
            if (value.AllowedValues is not { Count: > 0 })
            {
                throw new ArgumentException("ENUM and SET require at least one allowed value.", nameof(value));
            }

            return string.Join(',', value.AllowedValues.Select(Literal));
        }

        return column.Type switch
        {
            MySqlColumnType.TinyInt => "TINYINT",
            MySqlColumnType.SmallInt => "SMALLINT",
            MySqlColumnType.MediumInt => "MEDIUMINT",
            MySqlColumnType.Int => "INT",
            MySqlColumnType.BigInt => "BIGINT",
            MySqlColumnType.Decimal => DecimalType(column),
            MySqlColumnType.Float => "FLOAT",
            MySqlColumnType.Double => "DOUBLE",
            MySqlColumnType.Bit => $"BIT({RequireLength(column, 64)})",
            MySqlColumnType.Boolean => "BOOLEAN",
            MySqlColumnType.Char => $"CHAR({RequireLength(column, 255)})",
            MySqlColumnType.VarChar => $"VARCHAR({RequireLength(column)})",
            MySqlColumnType.Binary => $"BINARY({RequireLength(column, 255)})",
            MySqlColumnType.VarBinary => $"VARBINARY({RequireLength(column)})",
            MySqlColumnType.TinyText => "TINYTEXT",
            MySqlColumnType.Text => "TEXT",
            MySqlColumnType.MediumText => "MEDIUMTEXT",
            MySqlColumnType.LongText => "LONGTEXT",
            MySqlColumnType.TinyBlob => "TINYBLOB",
            MySqlColumnType.Blob => "BLOB",
            MySqlColumnType.MediumBlob => "MEDIUMBLOB",
            MySqlColumnType.LongBlob => "LONGBLOB",
            MySqlColumnType.Date => "DATE",
            MySqlColumnType.DateTime => column.Length is null ? "DATETIME" : $"DATETIME({FractionalSeconds(column.Length.Value)})",
            MySqlColumnType.Timestamp => column.Length is null ? "TIMESTAMP" : $"TIMESTAMP({FractionalSeconds(column.Length.Value)})",
            MySqlColumnType.Time => column.Length is null ? "TIME" : $"TIME({FractionalSeconds(column.Length.Value)})",
            MySqlColumnType.Year => "YEAR",
            MySqlColumnType.Json => "JSON",
            MySqlColumnType.Enum => $"ENUM({Values(column)})",
            MySqlColumnType.Set => $"SET({Values(column)})",
            MySqlColumnType.Geometry => "GEOMETRY",
            _ => throw new ArgumentOutOfRangeException(nameof(column)),
        };
    }

    private static int FractionalSeconds(int precision) => precision is >= 0 and <= 6
        ? precision
        : throw new ArgumentOutOfRangeException(nameof(precision), "Fractional seconds precision must be 0..6.");

    private static string Index(MySqlIndexDefinition index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index.Columns.Count == 0)
        {
            throw new ArgumentException("An index must contain at least one column.", nameof(index));
        }

        var columns = string.Join(", ", index.Columns.Select(column =>
            $"{MySqlIdentifier.Quote(column.Name)}{(column.PrefixLength is null ? string.Empty : $"({column.PrefixLength.Value.ToString(CultureInfo.InvariantCulture)})")}{(column.Descending ? " DESC" : string.Empty)}"));
        if (index.Primary)
        {
            return $"PRIMARY KEY ({columns})";
        }

        return $"{(index.Unique ? "UNIQUE " : string.Empty)}INDEX {MySqlIdentifier.Quote(index.Name)} ({columns})";
    }

    private static string ForeignKey(MySqlForeignKeyDefinition foreignKey)
    {
        ArgumentNullException.ThrowIfNull(foreignKey);
        if (foreignKey.Columns.Count == 0 || foreignKey.Columns.Count != foreignKey.ReferencedColumns.Count)
        {
            throw new ArgumentException("Foreign key column lists must be non-empty and have equal lengths.", nameof(foreignKey));
        }

        var columns = string.Join(", ", foreignKey.Columns.Select(MySqlIdentifier.Quote));
        var referenced = string.Join(", ", foreignKey.ReferencedColumns.Select(MySqlIdentifier.Quote));
        return $"CONSTRAINT {MySqlIdentifier.Quote(foreignKey.Name)} FOREIGN KEY ({columns}) "
            + $"REFERENCES {MySqlIdentifier.Qualify(foreignKey.ReferencedSchema, foreignKey.ReferencedTable)} ({referenced}) "
            + $"ON UPDATE {Action(foreignKey.OnUpdate)} ON DELETE {Action(foreignKey.OnDelete)}";
    }

    private static string Action(MySqlReferentialAction action) => action switch
    {
        MySqlReferentialAction.Restrict => "RESTRICT",
        MySqlReferentialAction.Cascade => "CASCADE",
        MySqlReferentialAction.SetNull => "SET NULL",
        MySqlReferentialAction.NoAction => "NO ACTION",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static string Literal(object? value) => value switch
    {
        null => "NULL",
        bool boolean => boolean ? "1" : "0",
        MySqlNumericLiteral numeric => numeric.Value,
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
            => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        DateTime dateTime => $"'{dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}'",
        DateOnly date => $"'{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'",
        TimeOnly time => $"'{time.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}'",
        string text => $"'{text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal)}'",
        _ => throw new ArgumentException($"Unsupported DDL literal type '{value.GetType().Name}'.", nameof(value)),
    };

    private static void ValidateTable(MySqlTableDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Columns.Count == 0)
        {
            throw new ArgumentException("A table must contain at least one column.", nameof(definition));
        }

        if (!AllowedEngines.Contains(definition.Engine))
        {
            throw new ArgumentException($"Engine '{definition.Engine}' is not allowed.", nameof(definition));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in definition.Columns)
        {
            if (!names.Add(column.Name))
            {
                throw new ArgumentException($"Duplicate column '{column.Name}'.", nameof(definition));
            }
        }

        foreach (var index in definition.Indexes ?? [])
        {
            if (index.Columns.Any(column => !names.Contains(column.Name)))
            {
                throw new ArgumentException($"Index '{index.Name}' references an unknown column.", nameof(definition));
            }
        }
    }

    private static string ValidateSimpleName(string value, string parameterName)
    {
        if (value.Length is 0 or > 128 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException("Only ASCII letters, digits, and underscores are allowed.", parameterName);
        }

        return value;
    }
}
