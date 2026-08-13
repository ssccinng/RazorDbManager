using RazorDbManager.Core;

namespace RazorDbManager.Core.Tests;

public sealed class QueryValidationTests
{
    [Fact]
    public void OffsetPage_RejectsNegativeOffsetAndNonPositiveSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PageRequest.FromOffset(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageRequest.FromOffset(100, -1));
    }

    [Fact]
    public void CursorPage_CannotAlsoContainOffset()
    {
        RowCursor cursor = new([DbValue.FromSignedInteger(42)]);

        PageRequest page = PageRequest.FromCursor(100, cursor);

        Assert.Null(page.Offset);
        Assert.Same(cursor, page.After);
    }

    [Fact]
    public void FilterValidation_AcceptsKnownCaseInsensitiveColumns()
    {
        DbTableMetadata table = CreateTable();
        FilterExpression filter = new LogicalFilter(
            DbLogicalOperator.And,
            [
                new ComparisonFilter("ID", DbComparisonOperator.GreaterThan, DbValue.FromSignedInteger(10)),
                new NullFilter("name", false),
            ]);

        Assert.Same(filter, filter.ValidateAgainst(table));
    }

    [Fact]
    public void FilterValidation_RejectsUnknownColumnsAndNullComparison()
    {
        DbTableMetadata table = CreateTable();

        Assert.Throws<InvalidOperationException>(() =>
            new NullFilter("missing").ValidateAgainst(table));
        Assert.Throws<InvalidOperationException>(() =>
            new ComparisonFilter("id", DbComparisonOperator.Equal, DbValue.Null).ValidateAgainst(table));
    }

    [Fact]
    public void FilterValidation_RejectsEmptyInAndSingleLogicalTerm()
    {
        DbTableMetadata table = CreateTable();

        Assert.Throws<InvalidOperationException>(() =>
            new InFilter("id", Array.Empty<DbValue>()).ValidateAgainst(table));
        Assert.Throws<InvalidOperationException>(() =>
            new LogicalFilter(DbLogicalOperator.And, [new NullFilter("name")]).ValidateAgainst(table));
    }

    [Fact]
    public void FilterValidation_EnforcesDepthAndNodeLimits()
    {
        DbTableMetadata table = CreateTable();
        FilterExpression filter = new LogicalFilter(DbLogicalOperator.Or,
        [
            new NullFilter("name"),
            new LogicalFilter(DbLogicalOperator.And,
            [
                new NullFilter("name"),
                new NullFilter("name"),
            ]),
        ]);

        Assert.Throws<InvalidOperationException>(() => filter.ValidateAgainst(table, maximumDepth: 2));
        Assert.Throws<InvalidOperationException>(() => filter.ValidateAgainst(table, maximumTerms: 3));
    }

    [Fact]
    public void SortValidation_RejectsDuplicateAndUnknownColumns()
    {
        DbTableMetadata table = CreateTable();

        Assert.Throws<InvalidOperationException>(() =>
            new DbSort[] { new("id"), new("ID") }.ValidateAgainst(table));
        Assert.Throws<InvalidOperationException>(() =>
            new DbSort[] { new("missing") }.ValidateAgainst(table));
    }

    private static DbTableMetadata CreateTable()
    {
        DbKeyMetadata key = new("PRIMARY", DbKeyKind.Primary, ["id"], true);
        return new DbTableMetadata(
            new DbObjectName("app", "items"),
            DbObjectKind.Table,
            [
                new DbColumnMetadata("id", 0, new DbTypeDescriptor("bigint", DbDataKind.SignedInteger), false),
                new DbColumnMetadata("name", 1, new DbTypeDescriptor("varchar(100)", DbDataKind.Text), true),
            ],
            [key],
            [],
            [],
            key,
            "schema-v1");
    }
}
