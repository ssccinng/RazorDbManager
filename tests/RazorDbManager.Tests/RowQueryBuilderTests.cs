using RazorDbManager.Components;
using RazorDbManager.Core;

namespace RazorDbManager.Tests;

public sealed class RowQueryBuilderTests
{
    private static readonly DbTableMetadata Table = new(
        new DbObjectName("app", "orders"),
        DbObjectKind.Table,
        [
            new DbColumnMetadata("id", 0, new DbTypeDescriptor("bigint", DbDataKind.SignedInteger), false),
            new DbColumnMetadata("name", 1, new DbTypeDescriptor("varchar(100)", DbDataKind.Text), true),
        ],
        [], [], [], null, "fingerprint");

    [Fact]
    public void BuildFilter_CombinesTypedTerms()
    {
        FilterExpression? result = RowQueryBuilder.BuildFilter(
            Table,
            [
                new FilterDraft { Column = "id", Operator = FilterDraftOperator.GreaterThan, Value = "42" },
                new FilterDraft { Column = "name", Operator = FilterDraftOperator.IsNotNull },
            ],
            DbLogicalOperator.And);

        LogicalFilter logical = Assert.IsType<LogicalFilter>(result);
        ComparisonFilter comparison = Assert.IsType<ComparisonFilter>(logical.Terms[0]);
        Assert.Equal(DbValueKind.SignedInteger, comparison.Value.Kind);
        Assert.Equal("42", comparison.Value.Text);
        Assert.False(Assert.IsType<NullFilter>(logical.Terms[1]).IsNull);
    }

    [Fact]
    public void BuildFilter_RejectsTextOperatorForNumericColumn()
    {
        RazorDbException exception = Assert.Throws<RazorDbException>(() => RowQueryBuilder.BuildFilter(
            Table,
            [new FilterDraft { Column = "id", Operator = FilterDraftOperator.Contains, Value = "4" }],
            DbLogicalOperator.And));

        Assert.Equal(RazorDbErrorCode.Validation, exception.Code);
    }

    [Fact]
    public void BuildFilter_SkipsIncompleteDrafts()
    {
        Assert.Null(RowQueryBuilder.BuildFilter(
            Table,
            [new FilterDraft { Column = "name", Operator = FilterDraftOperator.Equal }],
            DbLogicalOperator.And));
    }

    [Fact]
    public void BuildQuickFilter_SearchesAllTextColumnsOrParsesSelectedColumn()
    {
        ComparisonFilter allText = Assert.IsType<ComparisonFilter>(
            RowQueryBuilder.BuildQuickFilter(Table, null, "alice"));
        Assert.Equal("name", allText.Column);
        Assert.Equal(DbComparisonOperator.Contains, allText.Operator);

        ComparisonFilter selected = Assert.IsType<ComparisonFilter>(
            RowQueryBuilder.BuildQuickFilter(Table, "id", "42"));
        Assert.Equal(DbComparisonOperator.Equal, selected.Operator);
        Assert.Equal(DbValueKind.SignedInteger, selected.Value.Kind);
    }

    [Fact]
    public void BuildIdentityFilter_UsesInForSingleColumnKeys()
    {
        DbRow first = new([], new RowIdentity("PRIMARY", new Dictionary<string, DbValue>
        {
            ["id"] = DbValue.FromSignedInteger(1),
        }));
        DbRow second = new([], new RowIdentity("PRIMARY", new Dictionary<string, DbValue>
        {
            ["id"] = DbValue.FromSignedInteger(2),
        }));

        InFilter filter = Assert.IsType<InFilter>(RowQueryBuilder.BuildIdentityFilter([first, second]));
        Assert.Equal("id", filter.Column);
        Assert.Equal(2, filter.Values.Count);
    }

    [Fact]
    public void BuildIdentityFilter_RejectsUnsafeRows()
    {
        RazorDbException exception = Assert.Throws<RazorDbException>(() =>
            RowQueryBuilder.BuildIdentityFilter([new DbRow([], null)]));

        Assert.Equal(RazorDbErrorCode.Validation, exception.Code);
    }
}
