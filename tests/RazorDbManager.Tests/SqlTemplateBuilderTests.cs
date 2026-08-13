using RazorDbManager.Components;
using RazorDbManager.Core;

namespace RazorDbManager.Tests;

public sealed class SqlTemplateBuilderTests
{
    private static readonly DbTableMetadata Table = new(
        new DbObjectName("app`db", "user`records"),
        DbObjectKind.Table,
        [new DbColumnMetadata("display`name", 0, new DbTypeDescriptor("varchar(100)", DbDataKind.Text), true)],
        [], [], [], null, "fingerprint");

    [Theory]
    [InlineData(SqlTemplateKind.Select, "SELECT *")]
    [InlineData(SqlTemplateKind.Where, "WHERE 1 = 1")]
    [InlineData(SqlTemplateKind.Insert, "INSERT INTO")]
    [InlineData(SqlTemplateKind.Update, "WHERE 1 = 0")]
    [InlineData(SqlTemplateKind.Delete, "WHERE 1 = 0")]
    internal void Build_QuotesIdentifiersAndCreatesExpectedGuard(SqlTemplateKind kind, string expected)
    {
        string sql = SqlTemplateBuilder.Build(Table, kind);

        Assert.Contains("`app``db`.`user``records`", sql, StringComparison.Ordinal);
        Assert.Contains(expected, sql, StringComparison.Ordinal);
    }
}
