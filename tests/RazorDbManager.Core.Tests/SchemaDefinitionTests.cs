using RazorDbManager.Core;

namespace RazorDbManager.Core.Tests;

public sealed class SchemaDefinitionTests
{
    [Fact]
    public void DecimalDefinition_AcceptsProviderScaleAndUnsignedFacet()
    {
        ColumnTypeDefinition type = new()
        {
            Type = SchemaDataType.Decimal,
            Precision = 65,
            Scale = 30,
            IsUnsigned = true,
        };

        Assert.Same(type, type.Validate());
    }

    [Fact]
    public void TypeDefinition_RejectsUnsupportedFacets()
    {
        ColumnTypeDefinition invalidLength = new()
        {
            Type = SchemaDataType.Date,
            Length = 10,
        };
        ColumnTypeDefinition invalidEnum = new()
        {
            Type = SchemaDataType.Enum,
        };

        Assert.Throws<InvalidOperationException>(invalidLength.Validate);
        Assert.Throws<InvalidOperationException>(invalidEnum.Validate);
    }

    [Fact]
    public void ColumnDefinition_RejectsRawLikeTimestampDefaultOnText()
    {
        ColumnDefinition column = new()
        {
            Name = "description",
            Type = new ColumnTypeDefinition { Type = SchemaDataType.Text },
            Default = new CurrentTimestampColumnDefault(),
        };

        Assert.Throws<InvalidOperationException>(column.Validate);
    }

    [Fact]
    public void TableDefinition_RejectsIndexReferencingUnknownColumn()
    {
        TableDefinition table = new()
        {
            Name = new DbObjectName("app", "items"),
            Columns =
            [
                new ColumnDefinition
                {
                    Name = "id",
                    Type = new ColumnTypeDefinition { Type = SchemaDataType.Int64 },
                    IsNullable = false,
                },
            ],
            Indexes =
            [
                new IndexDefinition
                {
                    Name = "ux_missing",
                    Columns = [new DbIndexColumn("missing")],
                    IsUnique = true,
                },
            ],
        };

        Assert.Throws<InvalidOperationException>(table.Validate);
    }

    [Fact]
    public void TableDefinition_AcceptsCompositePrimaryKeyAndForeignKey()
    {
        TableDefinition table = new()
        {
            Name = new DbObjectName("app", "line_items"),
            Columns =
            [
                IntegerColumn("order_id"),
                IntegerColumn("line_number"),
                IntegerColumn("product_id"),
            ],
            Indexes =
            [
                new IndexDefinition
                {
                    IsPrimary = true,
                    IsUnique = true,
                    Columns = [new DbIndexColumn("order_id"), new DbIndexColumn("line_number")],
                },
            ],
            ForeignKeys =
            [
                new ForeignKeyDefinition
                {
                    Name = "fk_product",
                    Columns = ["product_id"],
                    ReferencedTable = new DbObjectName("app", "products"),
                    ReferencedColumns = ["id"],
                    OnDelete = DbReferentialAction.Restrict,
                },
            ],
        };

        Assert.Same(table, table.Validate());
    }

    [Fact]
    public void DdlChanges_ClassifyDestructiveOperations()
    {
        DbObjectName table = new("app", "items");

        Assert.False(new RenameTableChange(table, "products").IsDestructive);
        Assert.False(new AddColumnChange(table, IntegerColumn("quantity")).IsDestructive);
        Assert.True(new DropTableChange(table).IsDestructive);
        Assert.True(new AlterColumnChange(table, "id", IntegerColumn("id")).IsDestructive);
        Assert.True(new DropColumnChange(table, "description").IsDestructive);
    }

    [Fact]
    public void Credential_ToStringNeverExposesConnectionString()
    {
        RazorDbCredential credential = new("Server=secret;Password=top-secret");

        Assert.DoesNotContain("secret", credential.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", credential.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static ColumnDefinition IntegerColumn(string name) => new()
    {
        Name = name,
        Type = new ColumnTypeDefinition { Type = SchemaDataType.Int64 },
        IsNullable = false,
    };
}
