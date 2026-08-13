using RazorDbManager.MySql.Filters;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlFilterCompilerTests
{
    private readonly MySqlFilterCompiler _compiler = new();

    [Fact]
    public void Compile_ProducesOnlyParametersForValues()
    {
        var filter = new MySqlAndFilter(
        [
            new MySqlComparisonFilter("name", MySqlComparisonOperator.Like, "%admin%"),
            new MySqlComparisonFilter("age", MySqlComparisonOperator.GreaterThanOrEqual, 18),
        ]);

        var result = _compiler.Compile(filter, ["name", "age"]);

        Assert.Equal("(`name` LIKE @p0 AND `age` >= @p1)", result.Sql);
        Assert.Collection(
            result.Parameters,
            parameter => Assert.Equal(("@p0", "%admin%"), (parameter.Name, parameter.Value)),
            parameter => Assert.Equal(("@p1", 18), (parameter.Name, parameter.Value)));
    }

    [Fact]
    public void Compile_RejectsUnknownColumn() =>
        Assert.Throws<ArgumentException>(() => _compiler.Compile(
            new MySqlComparisonFilter("password", MySqlComparisonOperator.Equal, "x"),
            ["name"]));

    [Fact]
    public void Compile_UsesNullSemantics()
    {
        var result = _compiler.Compile(
            new MySqlComparisonFilter("deleted", MySqlComparisonOperator.NotEqual, null),
            ["deleted"]);

        Assert.Equal("`deleted` IS NOT NULL", result.Sql);
        Assert.Empty(result.Parameters);
    }

    [Fact]
    public void Compile_InWithNull_PreservesSqlNullSemantics()
    {
        var result = _compiler.Compile(new MySqlInFilter("status", ["active", null]), ["status"]);

        Assert.Equal("(`status` IN (@p0) OR `status` IS NULL)", result.Sql);
        Assert.Single(result.Parameters);
    }

    [Fact]
    public void Compile_NotInWithNull_UsesAnd()
    {
        var result = _compiler.Compile(new MySqlInFilter("status", ["active", null], true), ["status"]);

        Assert.Equal("(`status` NOT IN (@p0) AND `status` IS NOT NULL)", result.Sql);
    }
}
