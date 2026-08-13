using RazorDbManager.Core;

namespace RazorDbManager.MySql.Schema;

internal static class MySqlStructuredDdlValidator
{
    public static void ValidateStructure(SchemaChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ValidateObjectName(Target(change), "The target table name is required.");
        switch (change)
        {
            case CreateTableChange create:
                create.Table.Validate();
                foreach (var foreignKey in create.Table.ForeignKeys)
                    ValidateObjectName(foreignKey.ReferencedTable, "The referenced table name is required.");
                ValidateSelfReferencingForeignKeys(create.Table);
                break;
            case RenameTableChange rename:
                RequiredName(rename.NewName, "A new table name is required.");
                break;
            case AddColumnChange add:
                add.Column.Validate();
                OptionalName(add.AfterColumn, "The predecessor column name cannot be blank.");
                break;
            case AlterColumnChange alter:
                RequiredName(alter.ExistingColumn, "The existing column name is required.");
                alter.Column.Validate();
                break;
            case DropColumnChange drop:
                RequiredName(drop.Column, "The column name is required.");
                break;
            case AddIndexChange add:
                add.Index.Validate();
                break;
            case DropIndexChange drop when !drop.IsPrimary:
                RequiredName(drop.Index, "The index name is required.");
                break;
            case AddForeignKeyChange add:
                add.ForeignKey.Validate();
                ValidateObjectName(add.ForeignKey.ReferencedTable, "The referenced table name is required.");
                break;
            case DropForeignKeyChange drop:
                RequiredName(drop.ForeignKey, "The foreign-key name is required.");
                break;
        }
    }

    public static void ValidateAgainstTable(SchemaChange change, DbTableMetadata table)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(table);
        var columns = new HashSet<string>(table.Columns.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);
        switch (change)
        {
            case AddColumnChange add:
                if (columns.Contains(add.Column.Name))
                    throw new InvalidOperationException($"Column '{add.Column.Name}' already exists.");
                if (add.AfterColumn is not null && !columns.Contains(add.AfterColumn))
                    throw new InvalidOperationException($"Predecessor column '{add.AfterColumn}' does not exist.");
                break;
            case AlterColumnChange alter:
                RequireColumn(columns, alter.ExistingColumn);
                if (!alter.Column.Name.Equals(alter.ExistingColumn, StringComparison.OrdinalIgnoreCase)
                    && columns.Contains(alter.Column.Name))
                    throw new InvalidOperationException($"Column '{alter.Column.Name}' already exists.");
                break;
            case DropColumnChange drop:
                RequireColumn(columns, drop.Column);
                break;
            case AddIndexChange add:
                foreach (var column in add.Index.Columns) RequireColumn(columns, column.Name);
                if (add.Index.IsPrimary
                    ? table.Indexes.Any(index => index.IsPrimary)
                    : table.Indexes.Any(index => index.Name.Equals(add.Index.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(add.Index.IsPrimary
                        ? "The table already has a primary key."
                        : $"Index '{add.Index.Name}' already exists.");
                break;
            case DropIndexChange drop:
                if (drop.IsPrimary
                    ? !table.Indexes.Any(index => index.IsPrimary)
                    : !table.Indexes.Any(index => index.Name.Equals(drop.Index, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(drop.IsPrimary
                        ? "The table does not have a primary key."
                        : $"Index '{drop.Index}' does not exist.");
                break;
            case AddForeignKeyChange add:
                foreach (var column in add.ForeignKey.Columns) RequireColumn(columns, column);
                if (table.ForeignKeys.Any(key => key.Name.Equals(add.ForeignKey.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Foreign key '{add.ForeignKey.Name}' already exists.");
                break;
            case DropForeignKeyChange drop when !table.ForeignKeys.Any(
                key => key.Name.Equals(drop.ForeignKey, StringComparison.OrdinalIgnoreCase)):
                throw new InvalidOperationException($"Foreign key '{drop.ForeignKey}' does not exist.");
        }
    }

    public static void ValidateReferencedColumns(
        ForeignKeyDefinition foreignKey,
        IReadOnlyCollection<string> referencedColumns)
    {
        foreignKey.Validate();
        var columns = new HashSet<string>(referencedColumns, StringComparer.OrdinalIgnoreCase);
        var unknown = foreignKey.ReferencedColumns.FirstOrDefault(column => !columns.Contains(column));
        if (unknown is not null)
            throw new InvalidOperationException($"Referenced column '{unknown}' does not exist.");
    }

    private static void ValidateSelfReferencingForeignKeys(TableDefinition table)
    {
        var columns = table.Columns.Select(column => column.Name).ToArray();
        foreach (var foreignKey in table.ForeignKeys.Where(key => SameObject(key.ReferencedTable, table.Name)))
        {
            ValidateReferencedColumns(foreignKey, columns);
        }
    }

    private static void RequireColumn(IReadOnlySet<string> columns, string column)
    {
        if (!columns.Contains(column))
            throw new InvalidOperationException($"Column '{column}' does not exist.");
    }

    private static void RequiredName(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(message);
    }

    private static void OptionalName(string? value, string message)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(message);
    }

    internal static bool SameObject(DbObjectName left, DbObjectName right) =>
        string.Equals(left.Schema, right.Schema, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

    private static DbObjectName Target(SchemaChange change) => change switch
    {
        CreateTableChange create => create.Table.Name,
        RenameTableChange rename => rename.Table,
        DropTableChange drop => drop.Table,
        AddColumnChange add => add.Table,
        AlterColumnChange alter => alter.Table,
        DropColumnChange drop => drop.Table,
        AddIndexChange add => add.Table,
        DropIndexChange drop => drop.Table,
        AddForeignKeyChange add => add.Table,
        DropForeignKeyChange drop => drop.Table,
        _ => throw new NotSupportedException($"Schema change '{change.GetType().Name}' is not supported by MySQL."),
    };

    private static void ValidateObjectName(DbObjectName value, string message)
    {
        if (string.IsNullOrWhiteSpace(value.Schema) || string.IsNullOrWhiteSpace(value.Name))
            throw new InvalidOperationException(message);
    }
}
