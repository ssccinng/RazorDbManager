using System.Collections.ObjectModel;

namespace RazorDbManager.MySql.Models;

internal sealed record MySqlDatabaseMetadata(
    string Schema,
    IReadOnlyList<MySqlTableMetadata> Tables,
    DateTimeOffset LoadedAt);

internal sealed record MySqlTableMetadata(
    string Schema,
    string Name,
    string TableType,
    string? Engine,
    long? EstimatedRows,
    string? Collation,
    IReadOnlyList<MySqlColumnMetadata> Columns,
    IReadOnlyList<MySqlIndexMetadata> Indexes,
    IReadOnlyList<MySqlForeignKeyMetadata> ForeignKeys)
{
    private IReadOnlyDictionary<string, MySqlColumnMetadata>? _columnMap;

    public IReadOnlyDictionary<string, MySqlColumnMetadata> ColumnMap =>
        _columnMap ??= new ReadOnlyDictionary<string, MySqlColumnMetadata>(
            Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase));
}

internal sealed record MySqlColumnMetadata(
    string Name,
    int Ordinal,
    string DataType,
    string ColumnType,
    bool IsNullable,
    string? DefaultValue,
    string? Extra,
    string? CharacterSet,
    string? Collation,
    ulong? CharacterMaximumLength,
    ulong? NumericPrecision,
    ulong? NumericScale,
    bool IsGenerated,
    string? GenerationExpression)
{
    public bool IsBinary => DataType.Equals("binary", StringComparison.OrdinalIgnoreCase)
        || DataType.Equals("varbinary", StringComparison.OrdinalIgnoreCase)
        || DataType.EndsWith("blob", StringComparison.OrdinalIgnoreCase)
        || DataType.Equals("geometry", StringComparison.OrdinalIgnoreCase);
}

internal sealed record MySqlIndexMetadata(
    string Name,
    bool IsUnique,
    bool IsPrimary,
    string IndexType,
    IReadOnlyList<MySqlIndexColumnMetadata> Columns);

internal sealed record MySqlIndexColumnMetadata(string Name, int Sequence, int? PrefixLength, bool Descending);

internal sealed record MySqlForeignKeyMetadata(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    string UpdateRule,
    string DeleteRule);

internal sealed record MySqlRowIdentity(IReadOnlyList<string> Columns, bool IsPrimary)
{
    public static MySqlRowIdentity? Select(MySqlTableMetadata table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var primary = table.Indexes.FirstOrDefault(index => index.IsPrimary);
        if (primary is not null && primary.Columns.Count > 0)
        {
            return new MySqlRowIdentity(
                primary.Columns.OrderBy(column => column.Sequence).Select(column => column.Name).ToArray(),
                true);
        }

        var safeUnique = table.Indexes
            .Where(index => index.IsUnique && index.Columns.Count > 0)
            .Where(index => index.Columns.All(indexColumn => indexColumn.PrefixLength is null &&
                table.ColumnMap.TryGetValue(indexColumn.Name, out var column) && !column.IsNullable))
            .OrderBy(index => index.Columns.Count)
            .ThenBy(index => index.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        return safeUnique is null
            ? null
            : new MySqlRowIdentity(
                safeUnique.Columns.OrderBy(column => column.Sequence).Select(column => column.Name).ToArray(),
                false);
    }
}
