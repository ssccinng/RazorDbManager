using RazorDbManager.Components;
using RazorDbManager.Core;

namespace RazorDbManager.Tests;

public sealed class SchemaColumnDraftMapperTests
{
    [Fact]
    public void TryCreate_PreservesSupportedDecimalFacetsAndLiteralDefault()
    {
        DbColumnMetadata column = new(
            "amount",
            0,
            new DbTypeDescriptor("decimal(65,30) unsigned", DbDataKind.Decimal, true, Precision: 65, Scale: 30),
            false,
            "12345678901234567890.123456789012345678901234567890",
            Comment: "Exact total");

        bool mapped = SchemaColumnDraftMapper.TryCreate(column, out SchemaColumnDraftSeed? seed);

        Assert.True(mapped);
        Assert.NotNull(seed);
        Assert.Equal(SchemaDataType.Decimal, seed.DataType);
        Assert.Equal(65, seed.Precision);
        Assert.Equal(30, seed.Scale);
        Assert.True(seed.Unsigned);
        Assert.Equal(SchemaColumnSeedDefaultKind.Literal, seed.DefaultKind);
        Assert.Equal(column.DefaultSql, seed.DefaultValue);
        Assert.Equal("Exact total", seed.Comment);
    }

    [Theory]
    [InlineData("CURRENT_TIMESTAMP", null)]
    [InlineData("current_timestamp()", null)]
    [InlineData("CURRENT_TIMESTAMP(6)", 6)]
    public void TryCreate_PreservesCurrentTimestampDefault(string defaultSql, int? precision)
    {
        DbColumnMetadata column = new(
            "created_at",
            0,
            new DbTypeDescriptor("timestamp", DbDataKind.Timestamp),
            false,
            defaultSql);

        Assert.True(SchemaColumnDraftMapper.TryCreate(column, out SchemaColumnDraftSeed? seed));
        Assert.Equal(SchemaColumnSeedDefaultKind.CurrentTimestamp, seed!.DefaultKind);
        Assert.Equal(precision, seed.CurrentTimestampPrecision);
    }

    [Theory]
    [InlineData("mediumint", DbDataKind.SignedInteger)]
    [InlineData("longtext", DbDataKind.Text)]
    [InlineData("datetime(6)", DbDataKind.DateTime)]
    [InlineData("double(10,2)", DbDataKind.FloatingPoint)]
    [InlineData("point", DbDataKind.Geometry)]
    public void TryCreate_RejectsTypesThatStructuredDdlCannotPreserve(string providerType, DbDataKind kind)
    {
        DbColumnMetadata column = new("value", 0, new DbTypeDescriptor(providerType, kind), true);

        Assert.False(SchemaColumnDraftMapper.TryCreate(column, out _));
    }

    [Fact]
    public void TryCreate_RejectsGeneratedColumns()
    {
        DbColumnMetadata column = new(
            "computed",
            0,
            new DbTypeDescriptor("int", DbDataKind.SignedInteger),
            false,
            IsGenerated: true);

        Assert.False(SchemaColumnDraftMapper.TryCreate(column, out _));
    }

    [Fact]
    public void TryCreate_RejectsEnumValuesThatCommaEditorCannotRoundTrip()
    {
        DbColumnMetadata column = new(
            "status",
            0,
            new DbTypeDescriptor("enum('open','needs,review')", DbDataKind.Enum, AllowedValues: ["open", "needs,review"]),
            false,
            "open");

        Assert.False(SchemaColumnDraftMapper.TryCreate(column, out _));
    }
}
