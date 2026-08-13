using RazorDbManager.MySql.Sql;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlIdentifierTests
{
    [Theory]
    [InlineData("users", "`users`")]
    [InlineData("select", "`select`")]
    [InlineData("we`ird", "`we``ird`")]
    [InlineData("表", "`表`")]
    public void Quote_QuotesAndEscapes(string input, string expected) =>
        Assert.Equal(expected, MySqlIdentifier.Quote(input));

    [Fact]
    public void Quote_RejectsNul() =>
        Assert.Throws<ArgumentException>(() => MySqlIdentifier.Quote("bad\0name"));

    [Fact]
    public void Qualify_QuotesBothParts() =>
        Assert.Equal("`my``db`.`order`", MySqlIdentifier.Qualify("my`db", "order"));
}
