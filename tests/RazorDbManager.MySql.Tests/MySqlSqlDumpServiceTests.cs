using System.Text;
using RazorDbManager.Core;
using RazorDbManager.MySql.Transfer;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlSqlDumpServiceTests
{
    [Fact]
    public void DumpLiteral_UsesHexForTextAndCannotBeChangedByNoBackslashEscapes()
    {
        const string malicious = "x\\'; DROP TABLE users; --";

        var literal = MySqlSqlDumpService.DumpLiteral(DbValue.FromString(malicious));

        Assert.Equal(
            $"CONVERT(X'{Convert.ToHexString(Encoding.UTF8.GetBytes(malicious))}' USING utf8mb4)",
            literal);
        Assert.DoesNotContain(malicious, literal, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', literal);
        Assert.DoesNotContain(';', literal);
    }

    [Fact]
    public void ExportableColumns_ExcludesGeneratedAndBuildsExplicitColumnSql()
    {
        var table = Table();

        var columns = MySqlSqlDumpService.ExportableColumns(table);

        var column = Assert.Single(columns);
        Assert.Equal("id", column.Name);
        Assert.Equal("SELECT `id` FROM `app`.`items`", MySqlSqlDumpService.BuildSelectSql(table.Name, columns));
        Assert.Equal("INSERT INTO `app`.`items` (`id`) VALUES (", MySqlSqlDumpService.BuildInsertPrefix(table.Name, columns));
        Assert.DoesNotContain("computed", MySqlSqlDumpService.BuildInsertPrefix(table.Name, columns), StringComparison.Ordinal);
    }

    [Fact]
    public void ExportValueReader_RejectsGigabyteCellBeforeAllocation()
    {
        var exception = Assert.Throws<RazorDbException>(() =>
            MySqlExportValueReader.EnsureWithinLimit(1024L * 1024 * 1024, 16L * 1024 * 1024, "payload"));

        Assert.Equal(RazorDbErrorCode.LimitExceeded, exception.Code);
        Assert.Contains("payload", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DumpEnvelope_DisablesAndRestoresForeignKeysWithinImportSession()
    {
        Assert.Contains("session", MySqlSqlDumpService.ForeignKeyChecksComment, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@@FOREIGN_KEY_CHECKS", MySqlSqlDumpService.ForeignKeyChecksSave, StringComparison.Ordinal);
        Assert.Equal("SET FOREIGN_KEY_CHECKS=0;", MySqlSqlDumpService.ForeignKeyChecksDisable);
        Assert.Contains("@RAZORDB_OLD_FOREIGN_KEY_CHECKS", MySqlSqlDumpService.ForeignKeyChecksRestore, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CREATE TABLE `items` (\n  `id` int NOT NULL\n) ENGINE=InnoDB")]
    [InlineData("create table items (`id` int NOT NULL)")]
    [InlineData("CREATE TABLE `weird``name` (`id` int NOT NULL)")]
    public void QualifyCreateTable_RewritesShowCreateTargetToTheRequestedSchema(string showCreate)
    {
        string qualified = MySqlSqlDumpService.QualifyCreateTable(
            showCreate,
            new DbObjectName("tenant`one", "items"));

        Assert.StartsWith("CREATE TABLE `tenant``one`.`items`", qualified, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`id` int NOT NULL", qualified, StringComparison.Ordinal);
    }

    [Fact]
    public void QualifyCreateTable_RejectsUnexpectedStatements()
    {
        Assert.Throws<RazorDbException>(() => MySqlSqlDumpService.QualifyCreateTable(
            "CREATE VIEW `items` AS SELECT 1",
            new DbObjectName("app", "items")));
    }

    [Fact]
    public void BuildInsertStatement_CombinesRowsIntoOneValidMultiValueInsert()
    {
        string statement = MySqlSqlDumpService.BuildInsertStatement(
            "INSERT INTO `app`.`items` (`id`, `name`) VALUES (",
            ["1,CONVERT(X'41' USING utf8mb4)", "2,NULL"]);

        Assert.Equal(
            "INSERT INTO `app`.`items` (`id`, `name`) VALUES (1,CONVERT(X'41' USING utf8mb4)),(2,NULL);",
            statement);
    }

    [Fact]
    public void CanAppendInsertRow_StopsAtFiveHundredRows()
    {
        Assert.True(MySqlSqlDumpService.CanAppendInsertRow(499, 100, "1"));
        Assert.False(MySqlSqlDumpService.CanAppendInsertRow(500, 100, "1"));
    }

    [Fact]
    public void CanAppendInsertRow_UsesUtf8BytesAndAllowsExactSizeBoundary()
    {
        const string unicodeRow = "CONVERT(X'E4BDA0E5A5BD' USING utf8mb4)";
        int rowBytes = Encoding.UTF8.GetByteCount(unicodeRow);
        int exactLimit = 100 + 3 + rowBytes;

        Assert.True(MySqlSqlDumpService.CanAppendInsertRow(
            1, 100, unicodeRow, maximumRows: 500, maximumUtf8Bytes: exactLimit));
        Assert.False(MySqlSqlDumpService.CanAppendInsertRow(
            1, 100, unicodeRow, maximumRows: 500, maximumUtf8Bytes: exactLimit - 1));
    }

    [Fact]
    public void CanAppendInsertRow_AllowsOneOversizedRowAsItsOwnBatch()
    {
        string row = new('a', 1024);

        Assert.True(MySqlSqlDumpService.CanAppendInsertRow(
            0, 100, row, maximumRows: 500, maximumUtf8Bytes: 128));
        Assert.False(MySqlSqlDumpService.CanAppendInsertRow(
            1, 100, row, maximumRows: 500, maximumUtf8Bytes: 128));
    }

    [Fact]
    public async Task RunTransactionAsync_CommitsAfterSuccessfulOperation()
    {
        using CancellationTokenSource cancellation = new();
        List<string> calls = [];
        CancellationToken operationToken = default;
        CancellationToken commitToken = default;

        await MySqlSqlDumpService.RunTransactionAsync(
            token =>
            {
                calls.Add("operation");
                operationToken = token;
                return ValueTask.CompletedTask;
            },
            token =>
            {
                calls.Add("commit");
                commitToken = token;
                return ValueTask.CompletedTask;
            },
            _ =>
            {
                calls.Add("rollback");
                return ValueTask.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(["operation", "commit"], calls);
        Assert.Equal(cancellation.Token, operationToken);
        Assert.Equal(cancellation.Token, commitToken);
    }

    [Fact]
    public async Task RunTransactionAsync_RollsBackOperationFailureWithoutCallerCancellation()
    {
        using CancellationTokenSource cancellation = new();
        var expected = new InvalidOperationException("write failed");
        CancellationToken rollbackToken = new(canceled: true);
        var commitCalls = 0;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await MySqlSqlDumpService.RunTransactionAsync(
                _ => ValueTask.FromException(expected),
                _ =>
                {
                    commitCalls++;
                    return ValueTask.CompletedTask;
                },
                token =>
                {
                    rollbackToken = token;
                    return ValueTask.CompletedTask;
                },
                cancellation.Token));

        Assert.Same(expected, actual);
        Assert.Equal(0, commitCalls);
        Assert.Equal(CancellationToken.None, rollbackToken);
    }

    [Fact]
    public async Task RunTransactionAsync_RollsBackCancellationWithNonCancelableToken()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        CancellationToken rollbackToken = new(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await MySqlSqlDumpService.RunTransactionAsync(
                token => ValueTask.FromException(new OperationCanceledException(token)),
                _ => ValueTask.CompletedTask,
                token =>
                {
                    rollbackToken = token;
                    return ValueTask.CompletedTask;
                },
                cancellation.Token));

        Assert.Equal(CancellationToken.None, rollbackToken);
    }

    [Fact]
    public async Task RunTransactionAsync_RollsBackCommitFailureAndPreservesOriginalFailure()
    {
        var expected = new InvalidOperationException("commit failed");
        var rollbackCalls = 0;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await MySqlSqlDumpService.RunTransactionAsync(
                _ => ValueTask.CompletedTask,
                _ => ValueTask.FromException(expected),
                _ =>
                {
                    rollbackCalls++;
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal(1, rollbackCalls);
    }

    private static DbTableMetadata Table() => new(
        new DbObjectName("app", "items"),
        DbObjectKind.Table,
        [
            new DbColumnMetadata("id", 0, new DbTypeDescriptor("int", DbDataKind.SignedInteger), false),
            new DbColumnMetadata("computed", 1, new DbTypeDescriptor("int", DbDataKind.SignedInteger), false, IsGenerated: true),
        ],
        [], [], [], null, "fingerprint");
}
