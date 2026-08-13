using RazorDbManager.Core;
using RazorDbManager.MySql.Data;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlCursorPredicateCompilerTests
{
    [Fact]
    public void AscendingNullCursorAdvancesWithinNullsAndThenToNonNulls()
    {
        CompiledSql result = MySqlCursorPredicateCompiler.Compile(
            new RowCursor([DbValue.Null, DbValue.FromSignedInteger(10)]),
            [new DbSort("rank"), new DbSort("id")],
            ["rank", "id"]);

        Assert.Equal(
            "((`rank` IS NOT NULL) OR (`rank` IS NULL AND `id` > @cursor0))",
            result.Text);
        Assert.Collection(result.Parameters,
            parameter =>
            {
                Assert.Equal("@cursor0", parameter.Name);
                Assert.Equal("10", parameter.Value);
            });
    }

    [Fact]
    public void DescendingNonNullCursorIncludesTrailingNullGroup()
    {
        CompiledSql result = MySqlCursorPredicateCompiler.Compile(
            new RowCursor([DbValue.FromSignedInteger(5), DbValue.FromSignedInteger(10)]),
            [new DbSort("rank", DbSortDirection.Descending), new DbSort("id")],
            ["rank", "id"]);

        Assert.Equal(
            "(((`rank` < @cursor0 OR `rank` IS NULL)) OR (`rank` = @cursor0 AND `id` > @cursor1))",
            result.Text);
        Assert.Equal(["@cursor0", "@cursor1"], result.Parameters.Select(parameter => parameter.Name));
    }

    [Fact]
    public void DescendingNullCursorOnlyAdvancesUsingTieBreaker()
    {
        CompiledSql result = MySqlCursorPredicateCompiler.Compile(
            new RowCursor([DbValue.Null, DbValue.FromSignedInteger(10)]),
            [new DbSort("rank", DbSortDirection.Descending), new DbSort("id")],
            ["rank", "id"]);

        Assert.Equal(
            "((1 = 0) OR (`rank` IS NULL AND `id` > @cursor0))",
            result.Text);
    }

    [Fact]
    public void CompileRejectsMismatchedOrUnknownSorts()
    {
        Assert.Throws<ArgumentException>(() => MySqlCursorPredicateCompiler.Compile(
            new RowCursor([DbValue.FromSignedInteger(1)]),
            [new DbSort("id"), new DbSort("tie")],
            ["id", "tie"]));
        Assert.Throws<ArgumentException>(() => MySqlCursorPredicateCompiler.Compile(
            new RowCursor([DbValue.FromSignedInteger(1)]),
            [new DbSort("missing")],
            ["id"]));
    }
}
