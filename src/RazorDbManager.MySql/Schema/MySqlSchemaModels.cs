using RazorDbManager.Core;

namespace RazorDbManager.MySql.Schema;

internal enum MySqlColumnType
{
    TinyInt,
    SmallInt,
    MediumInt,
    Int,
    BigInt,
    Decimal,
    Float,
    Double,
    Bit,
    Boolean,
    Char,
    VarChar,
    Binary,
    VarBinary,
    TinyText,
    Text,
    MediumText,
    LongText,
    TinyBlob,
    Blob,
    MediumBlob,
    LongBlob,
    Date,
    DateTime,
    Timestamp,
    Time,
    Year,
    Json,
    Enum,
    Set,
    Geometry,
}

internal enum MySqlDefaultKind
{
    None,
    Null,
    Literal,
    CurrentTimestamp,
}

internal sealed record MySqlColumnDefault(MySqlDefaultKind Kind, object? Value = null)
{
    public static MySqlColumnDefault None { get; } = new(MySqlDefaultKind.None);
    public static MySqlColumnDefault Null { get; } = new(MySqlDefaultKind.Null);
    public static MySqlColumnDefault CurrentTimestamp { get; } = new(MySqlDefaultKind.CurrentTimestamp);
    public static MySqlColumnDefault CurrentTimestampWithPrecision(int precision) =>
        new(MySqlDefaultKind.CurrentTimestamp, precision);
    public static MySqlColumnDefault Literal(object value) => new(MySqlDefaultKind.Literal, value);
}

internal readonly record struct MySqlNumericLiteral(string Value);

internal sealed record MySqlColumnDefinition(
    string Name,
    MySqlColumnType Type,
    int? Length = null,
    int? Scale = null,
    bool Unsigned = false,
    bool Nullable = true,
    MySqlColumnDefault? Default = null,
    bool AutoIncrement = false,
    string? CharacterSet = null,
    string? Collation = null,
    IReadOnlyList<string>? AllowedValues = null,
    string? Comment = null);

internal sealed record MySqlIndexDefinition(
    string Name,
    IReadOnlyList<DbIndexColumn> Columns,
    bool Unique = false,
    bool Primary = false);

internal sealed record MySqlForeignKeyDefinition(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    MySqlReferentialAction OnUpdate = MySqlReferentialAction.Restrict,
    MySqlReferentialAction OnDelete = MySqlReferentialAction.Restrict);

internal enum MySqlReferentialAction
{
    Restrict,
    Cascade,
    SetNull,
    NoAction,
}

internal sealed record MySqlTableDefinition(
    string Schema,
    string Name,
    IReadOnlyList<MySqlColumnDefinition> Columns,
    IReadOnlyList<MySqlIndexDefinition>? Indexes = null,
    IReadOnlyList<MySqlForeignKeyDefinition>? ForeignKeys = null,
    string Engine = "InnoDB",
    string CharacterSet = "utf8mb4",
    string? Collation = null,
    string? Comment = null);

internal abstract record MySqlSchemaChange(string Schema, string Table);

internal sealed record MySqlCreateTableChange(MySqlTableDefinition Definition)
    : MySqlSchemaChange(Definition.Schema, Definition.Name);

internal sealed record MySqlDropTableChange(string SchemaName, string TableName)
    : MySqlSchemaChange(SchemaName, TableName);

internal sealed record MySqlRenameTableChange(string SchemaName, string TableName, string NewName)
    : MySqlSchemaChange(SchemaName, TableName);

internal sealed record MySqlAddColumnChange(string SchemaName, string TableName, MySqlColumnDefinition Column, string? AfterColumn = null)
    : MySqlSchemaChange(SchemaName, TableName);

internal sealed record MySqlAlterColumnChange(
    string SchemaName,
    string TableName,
    string ExistingColumn,
    MySqlColumnDefinition Column)
    : MySqlSchemaChange(SchemaName, TableName);

internal sealed record MySqlDropColumnChange(string SchemaName, string TableName, string Column)
    : MySqlSchemaChange(SchemaName, TableName);

internal sealed record MySqlAddIndexChange(string SchemaName, string TableName, MySqlIndexDefinition Index)
    : MySqlSchemaChange(SchemaName, TableName);

internal sealed record MySqlDropIndexChange(string SchemaName, string TableName, string Index, bool Primary = false)
    : MySqlSchemaChange(SchemaName, TableName);

internal sealed record MySqlAddForeignKeyChange(string SchemaName, string TableName, MySqlForeignKeyDefinition ForeignKey)
    : MySqlSchemaChange(SchemaName, TableName);

internal sealed record MySqlDropForeignKeyChange(string SchemaName, string TableName, string ForeignKey)
    : MySqlSchemaChange(SchemaName, TableName);

internal sealed record MySqlDdlPreview(string Sql, bool IsDestructive, string Fingerprint);
