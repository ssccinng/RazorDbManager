using RazorDbManager.Core;
using RazorDbManager.MySql.Schema;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlDdlGeneratorTests
{
    private readonly MySqlDdlGenerator _generator = new();

    [Fact]
    public void Generate_CreateTable_UsesStructuredSafeSql()
    {
        var definition = new MySqlTableDefinition(
            "app",
            "order",
            [
                new MySqlColumnDefinition("id", MySqlColumnType.BigInt, Unsigned: true, Nullable: false, AutoIncrement: true),
                new MySqlColumnDefinition("amount", MySqlColumnType.Decimal, Length: 65, Scale: 30, Nullable: false),
                new MySqlColumnDefinition("status", MySqlColumnType.Enum, Nullable: false, AllowedValues: ["new", "paid's"]),
                new MySqlColumnDefinition("created_at", MySqlColumnType.Timestamp, Length: 6, Nullable: false, Default: MySqlColumnDefault.CurrentTimestamp),
            ],
            [new MySqlIndexDefinition("PRIMARY", [new RazorDbManager.Core.DbIndexColumn("id")], Unique: true, Primary: true)]);

        var result = _generator.Generate(new MySqlCreateTableChange(definition));

        Assert.Contains("CREATE TABLE `app`.`order`", result.Sql, StringComparison.Ordinal);
        Assert.Contains("`amount` DECIMAL(65,30) NOT NULL", result.Sql, StringComparison.Ordinal);
        Assert.Contains("ENUM('new','paid''s')", result.Sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (`id`)", result.Sql, StringComparison.Ordinal);
        Assert.False(result.IsDestructive);
        Assert.Equal(64, result.Fingerprint.Length);
    }

    [Fact]
    public void Generate_DropColumn_IsDestructive()
    {
        var result = _generator.Generate(new MySqlDropColumnChange("app", "users", "legacy"));

        Assert.Equal("ALTER TABLE `app`.`users` DROP COLUMN `legacy`", result.Sql);
        Assert.True(result.IsDestructive);
    }

    [Fact]
    public void Generate_RejectsRawEngineInjection()
    {
        var definition = new MySqlTableDefinition(
            "app",
            "users",
            [new MySqlColumnDefinition("id", MySqlColumnType.Int)],
            Engine: "InnoDB; DROP TABLE users");

        Assert.Throws<ArgumentException>(() => _generator.Generate(new MySqlCreateTableChange(definition)));
    }

    [Fact]
    public void Generate_IndexPreservesPrefixAndDirectionFacets()
    {
        var result = _generator.Generate(new MySqlAddIndexChange(
            "app",
            "users",
            new MySqlIndexDefinition(
                "ix_name",
                [new RazorDbManager.Core.DbIndexColumn("name", Descending: true, PrefixLength: 20)])));

        Assert.Contains("INDEX `ix_name` (`name`(20) DESC)", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_LiteralDefaultsPreserveNumericTypesAndEscapeBackslashes()
    {
        TableDefinition definition = new()
        {
            Name = new RazorDbManager.Core.DbObjectName("app", "defaults"),
            Columns =
            [
                new ColumnDefinition
                {
                    Name = "amount",
                    Type = new ColumnTypeDefinition { Type = SchemaDataType.Decimal, Precision = 10, Scale = 2 },
                    Default = new LiteralColumnDefault(RazorDbManager.Core.DbValue.FromDecimal("1.20")),
                },
                new ColumnDefinition
                {
                    Name = "label",
                    Type = new ColumnTypeDefinition { Type = SchemaDataType.VarChar, Length = 100 },
                    Default = new LiteralColumnDefault(RazorDbManager.Core.DbValue.FromString("path\\it's")),
                },
            ],
        };

        MySqlDdlPreview result = _generator.Generate(Assert.IsType<MySqlCreateTableChange>(
            MySqlCoreDdlAdapter.Convert(new CreateTableChange(definition))));

        Assert.Contains("DEFAULT 1.20", result.Sql, StringComparison.Ordinal);
        Assert.Contains("DEFAULT 'path\\\\it''s'", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_CommentCannotEscapeLiteralIntoDestructiveAlterClause()
    {
        var result = _generator.Generate(new MySqlAddColumnChange(
            "app",
            "users",
            new MySqlColumnDefinition(
                "nickname",
                MySqlColumnType.VarChar,
                Length: 100,
                Comment: "\\'; DROP COLUMN `email`; -- ")));

        Assert.Contains("COMMENT '\\\\''; DROP COLUMN `email`; -- '", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COMMENT '\\''; DROP", result.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "DEFAULT CURRENT_TIMESTAMP")]
    [InlineData(0, "DEFAULT CURRENT_TIMESTAMP(0)")]
    [InlineData(6, "DEFAULT CURRENT_TIMESTAMP(6)")]
    public void Convert_CurrentTimestampPreservesSupportedPrecision(int? precision, string expected)
    {
        var column = new ColumnDefinition
        {
            Name = "created_at",
            Type = new ColumnTypeDefinition { Type = SchemaDataType.Timestamp },
            Default = new CurrentTimestampColumnDefault(precision),
        };

        var converted = Assert.IsType<MySqlAddColumnChange>(MySqlCoreDdlAdapter.Convert(
            new AddColumnChange(new DbObjectName("app", "users"), column)));

        Assert.Contains(expected, _generator.Generate(converted).Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_CurrentTimestampRejectsProviderUnsupportedPrecision()
    {
        var column = new ColumnDefinition
        {
            Name = "created_at",
            Type = new ColumnTypeDefinition { Type = SchemaDataType.Timestamp },
            Default = new CurrentTimestampColumnDefault(7),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => MySqlCoreDdlAdapter.Convert(
            new AddColumnChange(new DbObjectName("app", "users"), column)));
    }

    [Fact]
    public void Convert_RejectsForgedNumericDefaultText()
    {
        ColumnDefinition column = new()
        {
            Name = "amount",
            Type = new ColumnTypeDefinition { Type = SchemaDataType.Decimal, Precision = 10, Scale = 2 },
            Default = new LiteralColumnDefault(RazorDbManager.Core.DbValue.FromText(
                RazorDbManager.Core.DbValueKind.Decimal,
                "1); DROP TABLE users; --")),
        };

        Assert.Throws<FormatException>(() => MySqlCoreDdlAdapter.Convert(
            new AddColumnChange(new RazorDbManager.Core.DbObjectName("app", "orders"), column)));
    }

    [Theory]
    [InlineData(66, 0)]
    [InlineData(10, 31)]
    [InlineData(10, 11)]
    public void Generate_RejectsInvalidDecimal(int precision, int scale)
    {
        var change = new MySqlAddColumnChange(
            "app",
            "numbers",
            new MySqlColumnDefinition("value", MySqlColumnType.Decimal, precision, scale));

        Assert.Throws<ArgumentOutOfRangeException>(() => _generator.Generate(change));
    }
}
