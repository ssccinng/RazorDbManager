using RazorDbManager.MySql.Sql;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlScriptTokenizerTests
{
    [Fact]
    public void Tokenize_DoesNotSplitStringOrComments()
    {
        const string script = "INSERT INTO t VALUES ('a;b'); -- ; ignored\nSELECT \"x;y\"; # ; ignored\nSELECT `semi;colon` FROM t;";

        var result = MySqlScriptTokenizer.Tokenize(script);

        Assert.Equal(3, result.Count);
        Assert.Contains("'a;b'", result[0].Text, StringComparison.Ordinal);
        Assert.Contains("\"x;y\"", result[1].Text, StringComparison.Ordinal);
        Assert.Contains("`semi;colon`", result[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Tokenize_SupportsDelimiterForRoutineBodies()
    {
        const string script = """
            DELIMITER $$
            CREATE PROCEDURE p()
            BEGIN
              SELECT 'inside;body';
              SELECT 2;
            END$$
            DELIMITER ;
            SELECT 3;
            """;

        var result = MySqlScriptTokenizer.Tokenize(script);

        Assert.Equal(2, result.Count);
        Assert.StartsWith("CREATE PROCEDURE", result[0].Text, StringComparison.Ordinal);
        Assert.Contains("SELECT 2;", result[0].Text, StringComparison.Ordinal);
        Assert.Equal("SELECT 3", result[1].Text);
    }

    [Fact]
    public void Tokenize_HandlesEscapedQuotes()
    {
        const string script = "SELECT 'it\\'s;ok', 'it''s;also'; SELECT 2;";

        var result = MySqlScriptTokenizer.Tokenize(script);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Tokenize_RejectsUnterminatedString() =>
        Assert.Throws<FormatException>(() => MySqlScriptTokenizer.Tokenize("SELECT 'oops;"));

    [Fact]
    public void Tokenize_EnforcesStatementLimit() =>
        Assert.Throws<InvalidOperationException>(() => MySqlScriptTokenizer.Tokenize("SELECT 1; SELECT 2;", 1));

    [Fact]
    public async Task TokenizeAsync_StreamsDelimiterScriptsWithoutLosingSqlAfterComments()
    {
        const string script = """
            -- leading comment
            CREATE TABLE `semi;colon` (`value` varchar(20));
            DELIMITER $$
            CREATE PROCEDURE p()
            BEGIN
              SELECT 'inside;body';
              SELECT 2;
            END$$
            DELIMITER ;
            # another comment
            SELECT 3;
            """;
        using var reader = new StringReader(script);
        var statements = new List<MySqlScriptStatement>();

        await foreach (var statement in MySqlScriptTokenizer.TokenizeAsync(reader, 4_096))
        {
            statements.Add(statement);
        }

        Assert.Equal(3, statements.Count);
        Assert.Contains("CREATE TABLE", statements[0].Text, StringComparison.Ordinal);
        Assert.Contains("SELECT 2;", statements[1].Text, StringComparison.Ordinal);
        Assert.Contains("SELECT 3", statements[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenizeAsync_EnforcesPerStatementLimit()
    {
        using var reader = new StringReader("SELECT 'this statement is too large';");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in MySqlScriptTokenizer.TokenizeAsync(reader, 16))
            {
            }
        });
    }
}
