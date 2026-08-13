using System.Globalization;
using RazorDbManager.Core;

namespace RazorDbManager.MySql.Schema;

internal static class MySqlCoreDdlAdapter
{
    public static MySqlSchemaChange Convert(SchemaChange change)
    {
        MySqlStructuredDdlValidator.ValidateStructure(change);
        return change switch
        {
            CreateTableChange create => new MySqlCreateTableChange(Table(create.Table)),
            RenameTableChange rename => new MySqlRenameTableChange(rename.Table.Schema, rename.Table.Name, rename.NewName),
            DropTableChange drop => new MySqlDropTableChange(drop.Table.Schema, drop.Table.Name),
            AddColumnChange add => new MySqlAddColumnChange(add.Table.Schema, add.Table.Name, Column(add.Column), add.AfterColumn),
            AlterColumnChange alter => new MySqlAlterColumnChange(alter.Table.Schema, alter.Table.Name, alter.ExistingColumn, Column(alter.Column)),
            DropColumnChange dropColumn => new MySqlDropColumnChange(dropColumn.Table.Schema, dropColumn.Table.Name, dropColumn.Column),
            AddIndexChange addIndex => new MySqlAddIndexChange(addIndex.Table.Schema, addIndex.Table.Name, Index(addIndex.Index)),
            DropIndexChange dropIndex => new MySqlDropIndexChange(dropIndex.Table.Schema, dropIndex.Table.Name, dropIndex.Index, dropIndex.IsPrimary),
            AddForeignKeyChange addForeignKey => new MySqlAddForeignKeyChange(addForeignKey.Table.Schema, addForeignKey.Table.Name, ForeignKey(addForeignKey.ForeignKey)),
            DropForeignKeyChange dropForeignKey => new MySqlDropForeignKeyChange(dropForeignKey.Table.Schema, dropForeignKey.Table.Name, dropForeignKey.ForeignKey),
            _ => throw new NotSupportedException($"Schema change '{change.GetType().Name}' is not supported by MySQL."),
        };
    }

    public static DbObjectName Target(SchemaChange change) => change switch
    {
        CreateTableChange create => create.Table.Name,
        RenameTableChange rename => rename.Table,
        DropTableChange drop => drop.Table,
        AddColumnChange add => add.Table,
        AlterColumnChange alter => alter.Table,
        DropColumnChange dropColumn => dropColumn.Table,
        AddIndexChange addIndex => addIndex.Table,
        DropIndexChange dropIndex => dropIndex.Table,
        AddForeignKeyChange addForeignKey => addForeignKey.Table,
        DropForeignKeyChange dropForeignKey => dropForeignKey.Table,
        _ => throw new NotSupportedException(),
    };

    private static MySqlTableDefinition Table(TableDefinition table) => new(
        table.Name.Schema,
        table.Name.Name,
        table.Columns.Select(Column).ToArray(),
        table.Indexes.Select(Index).ToArray(),
        table.ForeignKeys.Select(ForeignKey).ToArray(),
        table.Engine ?? "InnoDB",
        table.CharacterSet ?? "utf8mb4",
        table.Collation,
        table.Comment);

    private static MySqlColumnDefinition Column(ColumnDefinition column)
    {
        column.Validate();
        var length = column.Type.Type == SchemaDataType.Decimal
            ? column.Type.Precision
            : column.Type.Type == SchemaDataType.Guid ? 36 : column.Type.Length;
        return new MySqlColumnDefinition(
            column.Name,
            Type(column.Type.Type),
            length is null ? null : checked((int)length.Value),
            column.Type.Scale,
            column.Type.IsUnsigned,
            column.IsNullable,
            Default(column.Default),
            column.IsAutoIncrement,
            column.CharacterSet,
            column.Collation,
            column.Type.AllowedValues,
            column.Comment);
    }

    private static MySqlColumnType Type(SchemaDataType type) => type switch
    {
        SchemaDataType.Boolean => MySqlColumnType.Boolean,
        SchemaDataType.Int8 => MySqlColumnType.TinyInt,
        SchemaDataType.Int16 => MySqlColumnType.SmallInt,
        SchemaDataType.Int32 => MySqlColumnType.Int,
        SchemaDataType.Int64 => MySqlColumnType.BigInt,
        SchemaDataType.Decimal => MySqlColumnType.Decimal,
        SchemaDataType.Float => MySqlColumnType.Float,
        SchemaDataType.Double => MySqlColumnType.Double,
        SchemaDataType.Char => MySqlColumnType.Char,
        SchemaDataType.VarChar => MySqlColumnType.VarChar,
        SchemaDataType.Text => MySqlColumnType.Text,
        SchemaDataType.Binary => MySqlColumnType.Binary,
        SchemaDataType.VarBinary => MySqlColumnType.VarBinary,
        SchemaDataType.Blob => MySqlColumnType.Blob,
        SchemaDataType.Date => MySqlColumnType.Date,
        SchemaDataType.Time => MySqlColumnType.Time,
        SchemaDataType.DateTime => MySqlColumnType.DateTime,
        SchemaDataType.Timestamp => MySqlColumnType.Timestamp,
        SchemaDataType.Json => MySqlColumnType.Json,
        SchemaDataType.Enum => MySqlColumnType.Enum,
        SchemaDataType.Set => MySqlColumnType.Set,
        SchemaDataType.Bit => MySqlColumnType.Bit,
        SchemaDataType.Geometry => MySqlColumnType.Geometry,
        SchemaDataType.Guid => MySqlColumnType.Char,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static MySqlColumnDefault Default(ColumnDefault? value) => value switch
    {
        null => MySqlColumnDefault.None,
        NullColumnDefault => MySqlColumnDefault.Null,
        LiteralColumnDefault literal => MySqlColumnDefault.Literal(ToLiteral(literal.Value)),
        CurrentTimestampColumnDefault { FractionalSecondsPrecision: null } => MySqlColumnDefault.CurrentTimestamp,
        CurrentTimestampColumnDefault current when current.FractionalSecondsPrecision is >= 0 and <= 6 =>
            MySqlColumnDefault.CurrentTimestampWithPrecision(current.FractionalSecondsPrecision.Value),
        CurrentTimestampColumnDefault => throw new ArgumentOutOfRangeException(
            nameof(value),
            "MySQL fractional seconds precision must be between 0 and 6."),
        _ => throw new NotSupportedException($"Column default '{value.GetType().Name}' is not supported."),
    };

    private static object ToLiteral(DbValue value) => value.Kind switch
    {
        DbValueKind.Null => throw new ArgumentException("Use NullColumnDefault for null."),
        DbValueKind.Binary or DbValueKind.Geometry => throw new ArgumentException("Binary DDL literals are not supported."),
        DbValueKind.Boolean => value.Text == "true",
        DbValueKind.SignedInteger => new MySqlNumericLiteral(
            long.Parse(value.Text!, NumberStyles.Integer, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture)),
        DbValueKind.UnsignedInteger => new MySqlNumericLiteral(
            ulong.Parse(value.Text!, NumberStyles.Integer, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture)),
        DbValueKind.Decimal => new MySqlNumericLiteral(ValidateDecimal(value.Text!)),
        DbValueKind.FloatingPoint => new MySqlNumericLiteral(ValidateFloatingPoint(value.Text!)),
        _ => value.Text ?? string.Empty,
    };

    private static string ValidateDecimal(string value)
    {
        if (value.Length is 0 or > 68) throw new FormatException("The decimal DDL literal is invalid.");
        int index = value[0] == '-' ? 1 : 0;
        bool hasDigit = false;
        bool hasPoint = false;
        for (; index < value.Length; index++)
        {
            char character = value[index];
            if (character is >= '0' and <= '9')
            {
                hasDigit = true;
                continue;
            }

            if (character == '.' && !hasPoint)
            {
                hasPoint = true;
                continue;
            }

            throw new FormatException("The decimal DDL literal is invalid.");
        }

        if (!hasDigit) throw new FormatException("The decimal DDL literal is invalid.");
        return value;
    }

    private static string ValidateFloatingPoint(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || !double.IsFinite(parsed))
            throw new FormatException("The floating-point DDL literal is invalid.");
        return parsed.ToString("R", CultureInfo.InvariantCulture);
    }

    private static MySqlIndexDefinition Index(IndexDefinition index) => new(
        index.Name,
        index.Columns,
        index.IsUnique,
        index.IsPrimary);

    private static MySqlForeignKeyDefinition ForeignKey(ForeignKeyDefinition foreignKey) => new(
        foreignKey.Name,
        foreignKey.Columns,
        foreignKey.ReferencedTable.Schema,
        foreignKey.ReferencedTable.Name,
        foreignKey.ReferencedColumns,
        Action(foreignKey.OnUpdate),
        Action(foreignKey.OnDelete));

    private static MySqlReferentialAction Action(DbReferentialAction action) => action switch
    {
        DbReferentialAction.Restrict => MySqlReferentialAction.Restrict,
        DbReferentialAction.Cascade => MySqlReferentialAction.Cascade,
        DbReferentialAction.SetNull => MySqlReferentialAction.SetNull,
        DbReferentialAction.NoAction => MySqlReferentialAction.NoAction,
        DbReferentialAction.SetDefault => throw new NotSupportedException("MySQL does not implement SET DEFAULT for foreign keys."),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
