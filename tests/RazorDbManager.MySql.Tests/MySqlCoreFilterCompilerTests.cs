using RazorDbManager.Core;
using RazorDbManager.MySql.Data;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlCoreFilterCompilerTests
{
    [Fact]
    public void Compile_ContainsEscapesWildcards()
    {
        var result = MySqlCoreFilterCompiler.Compile(
            new ComparisonFilter("name", DbComparisonOperator.Contains, DbValue.FromString("a%b_c\\d")),
            ["name"]);

        Assert.Equal("`name` LIKE @p0 ESCAPE '\\\\'", result.Text);
        Assert.Equal("%a\\%b\\_c\\\\d%", result.Parameters[0].Value);
    }

    [Fact]
    public void Compile_RejectsSingleTermLogicalFilter()
    {
        var filter = new LogicalFilter(DbLogicalOperator.And,
            [new NullFilter("deleted_at")]);

        Assert.Throws<ArgumentException>(() => MySqlCoreFilterCompiler.Compile(filter, ["deleted_at"]));
    }

    [Fact]
    public void Compile_UsesParametersForEveryValue()
    {
        var filter = new InFilter("id", [DbValue.FromSignedInteger(1), DbValue.FromSignedInteger(2)]);

        var result = MySqlCoreFilterCompiler.Compile(filter, ["id"]);

        Assert.Equal("`id` IN (@p0, @p1)", result.Text);
        Assert.Equal(2, result.Parameters.Count);
    }
}
