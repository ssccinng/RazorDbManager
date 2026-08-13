using RazorDbManager.Core;
using RazorDbManager.MySql.Schema;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlStructuredDdlValidatorTests
{
    [Fact]
    public void Convert_CreateTableCallsTableDefinitionValidate()
    {
        var table = Table(columns: [Column("id"), Column("ID")]);

        Assert.Throws<InvalidOperationException>(() =>
            MySqlCoreDdlAdapter.Convert(new CreateTableChange(table)));
    }

    [Fact]
    public void Convert_CreateTableCallsNestedIndexValidate()
    {
        var table = Table(indexes:
        [
            new IndexDefinition
            {
                Name = string.Empty,
                Columns = [new DbIndexColumn("id")],
            },
        ]);

        Assert.Throws<InvalidOperationException>(() =>
            MySqlCoreDdlAdapter.Convert(new CreateTableChange(table)));
    }

    [Fact]
    public void Convert_CreateTableCallsNestedForeignKeyValidate()
    {
        var table = Table(foreignKeys:
        [
            new ForeignKeyDefinition
            {
                Name = " ",
                Columns = ["id"],
                ReferencedTable = new DbObjectName("app", "parents"),
                ReferencedColumns = ["id"],
            },
        ]);

        Assert.Throws<InvalidOperationException>(() =>
            MySqlCoreDdlAdapter.Convert(new CreateTableChange(table)));
    }

    [Fact]
    public void Convert_CreateTableRejectsDuplicateIndexAndForeignKeyNames()
    {
        var duplicateIndexes = Table(indexes:
        [
            new IndexDefinition { Name = "ix_id", Columns = [new DbIndexColumn("id")] },
            new IndexDefinition { Name = "IX_ID", Columns = [new DbIndexColumn("id")] },
        ]);
        var key = new ForeignKeyDefinition
        {
            Name = "fk_parent",
            Columns = ["id"],
            ReferencedTable = new DbObjectName("app", "parents"),
            ReferencedColumns = ["id"],
        };
        var duplicateForeignKeys = Table(foreignKeys: [key, key with { Name = "FK_PARENT" }]);

        Assert.Throws<InvalidOperationException>(() =>
            MySqlCoreDdlAdapter.Convert(new CreateTableChange(duplicateIndexes)));
        Assert.Throws<InvalidOperationException>(() =>
            MySqlCoreDdlAdapter.Convert(new CreateTableChange(duplicateForeignKeys)));
    }

    [Fact]
    public void Convert_AddIndexAndForeignKeyCallCoreValidate()
    {
        Assert.Throws<InvalidOperationException>(() => MySqlCoreDdlAdapter.Convert(
            new AddIndexChange(new DbObjectName("app", "users"), new IndexDefinition
            {
                Name = "ix_empty",
                Columns = [],
            })));
        Assert.Throws<InvalidOperationException>(() => MySqlCoreDdlAdapter.Convert(
            new AddForeignKeyChange(new DbObjectName("app", "users"), new ForeignKeyDefinition
            {
                Name = "fk_bad",
                Columns = ["parent_id"],
                ReferencedTable = new DbObjectName("app", "parents"),
                ReferencedColumns = [],
            })));
    }

    [Fact]
    public void ValidateAgainstTable_RejectsUnknownColumnsAndDuplicateNamesDuringPreview()
    {
        var table = Metadata();

        Assert.Throws<InvalidOperationException>(() => MySqlStructuredDdlValidator.ValidateAgainstTable(
            new AddIndexChange(table.Name, new IndexDefinition
            {
                Name = "ix_missing",
                Columns = [new DbIndexColumn("missing")],
            }),
            table));
        Assert.Throws<InvalidOperationException>(() => MySqlStructuredDdlValidator.ValidateAgainstTable(
            new AddIndexChange(table.Name, new IndexDefinition
            {
                Name = "ix_name",
                Columns = [new DbIndexColumn("id")],
            }),
            table));
        Assert.Throws<InvalidOperationException>(() => MySqlStructuredDdlValidator.ValidateAgainstTable(
            new AddForeignKeyChange(table.Name, new ForeignKeyDefinition
            {
                Name = "fk_name",
                Columns = ["id"],
                ReferencedTable = new DbObjectName("app", "parents"),
                ReferencedColumns = ["id"],
            }),
            table));
    }

    [Fact]
    public void ValidateReferencedColumns_RejectsUnknownTargetColumn()
    {
        var foreignKey = new ForeignKeyDefinition
        {
            Name = "fk_parent",
            Columns = ["parent_id"],
            ReferencedTable = new DbObjectName("app", "parents"),
            ReferencedColumns = ["missing"],
        };

        Assert.Throws<InvalidOperationException>(() =>
            MySqlStructuredDdlValidator.ValidateReferencedColumns(foreignKey, ["id"]));
    }

    [Fact]
    public void Convert_RejectsBlankNamesBeforeMetadataLookup()
    {
        Assert.Throws<InvalidOperationException>(() => MySqlCoreDdlAdapter.Convert(
            new AddColumnChange(new DbObjectName("app", "users"), Column(" "))));
        Assert.Throws<InvalidOperationException>(() => MySqlCoreDdlAdapter.Convert(
            new RenameTableChange(new DbObjectName("app", "users"), " ")));
    }

    private static TableDefinition Table(
        IReadOnlyList<ColumnDefinition>? columns = null,
        IReadOnlyList<IndexDefinition>? indexes = null,
        IReadOnlyList<ForeignKeyDefinition>? foreignKeys = null) => new()
    {
        Name = new DbObjectName("app", "users"),
        Columns = columns ?? [Column("id")],
        Indexes = indexes ?? [],
        ForeignKeys = foreignKeys ?? [],
    };

    private static ColumnDefinition Column(string name) => new()
    {
        Name = name,
        Type = new ColumnTypeDefinition { Type = SchemaDataType.Int32 },
    };

    private static DbTableMetadata Metadata() => new(
        new DbObjectName("app", "users"),
        DbObjectKind.Table,
        [new DbColumnMetadata("id", 0, new DbTypeDescriptor("int", DbDataKind.SignedInteger), false)],
        [],
        [new DbIndexMetadata("ix_name", [new DbIndexColumn("id")], false)],
        [new DbForeignKeyMetadata(
            "fk_name",
            ["id"],
            new DbObjectName("app", "parents"),
            ["id"],
            DbReferentialAction.Restrict,
            DbReferentialAction.Restrict)],
        null,
        "fingerprint");
}
