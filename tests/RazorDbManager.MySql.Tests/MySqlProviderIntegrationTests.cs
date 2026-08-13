using System.Text;
using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using RazorDbManager.Core;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlProviderIntegrationTests
{
    private const string DatabaseId = "Integration";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Provider_ExecutesMetadataCrudPagingConcurrencyAndCsvRoundTrip()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("RAZORDB_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            // Integration is opt-in locally. The CI integration job always supplies this value.
            return;
        }

        var connectionBuilder = new MySqlConnectionStringBuilder(configuredConnection)
        {
            AllowLoadLocalInfile = false,
            PersistSecurityInfo = false,
            AllowZeroDateTime = true,
            ConvertZeroDateTime = false,
        };
        var schema = connectionBuilder.Database;
        Assert.False(string.IsNullOrWhiteSpace(schema));

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var mainTable = $"rdm_main_{suffix}";
        var noKeyTable = $"rdm_nokey_{suffix}";
        var nullableUniqueTable = $"rdm_nullable_{suffix}";
        var csvTable = $"rdm_csv_{suffix}";
        var csvCopyTable = $"rdm_csv_copy_{suffix}";
        var budgetKeyTable = $"rdm_budget_key_{suffix}";
        var budgetNoKeyTable = $"rdm_budget_nokey_{suffix}";
        var budgetSortTable = $"rdm_budget_sort_{suffix}";
        var budgetOversizedTable = $"rdm_budget_oversized_{suffix}";

        await using var setupConnection = new MySqlConnection(connectionBuilder.ConnectionString);
        await setupConnection.OpenAsync();
        try
        {
            await ExecuteAsync(setupConnection, $"""
                CREATE TABLE `{mainTable}` (
                    `tenant_id` INT NOT NULL,
                    `id` BIGINT UNSIGNED NOT NULL,
                    `amount` DECIMAL(65,30) NOT NULL,
                    `note` VARCHAR(200) NULL,
                    `payload` BLOB NULL,
                    `generated_note_length` INT GENERATED ALWAYS AS (CHAR_LENGTH(`note`)) STORED,
                    PRIMARY KEY (`tenant_id`, `id`)
                ) ENGINE=InnoDB
                """);
            await ExecuteAsync(setupConnection, $"""
                CREATE TABLE `{noKeyTable}` (
                    `label` VARCHAR(50) NULL,
                    `value` INT NULL
                ) ENGINE=InnoDB
                """);
            await ExecuteAsync(setupConnection, $"""
                CREATE TABLE `{nullableUniqueTable}` (
                    `email` VARCHAR(100) NULL,
                    UNIQUE KEY `uq_email` (`email`)
                ) ENGINE=InnoDB
                """);
            await ExecuteAsync(setupConnection, $"""
                CREATE TABLE `{csvTable}` (
                    `id` INT NOT NULL,
                    `text_value` VARCHAR(200) NULL,
                    `empty_value` VARCHAR(20) NULL,
                    `binary_value` VARBINARY(16) NULL,
                    `generated_text_length` INT GENERATED ALWAYS AS (CHAR_LENGTH(`text_value`)) STORED,
                    PRIMARY KEY (`id`)
                ) ENGINE=InnoDB
                """);
            await ExecuteAsync(setupConnection, $"CREATE TABLE `{csvCopyTable}` LIKE `{csvTable}`");
            await ExecuteAsync(setupConnection, $"CREATE TABLE `{budgetKeyTable}` (`id` INT NOT NULL PRIMARY KEY, `body` VARCHAR(1000) NOT NULL) ENGINE=InnoDB");
            await ExecuteAsync(setupConnection, $"CREATE TABLE `{budgetNoKeyTable}` (`seq` INT NOT NULL, `body` VARCHAR(1000) NOT NULL) ENGINE=InnoDB");
            await ExecuteAsync(setupConnection, $"CREATE TABLE `{budgetSortTable}` (`id` INT NOT NULL PRIMARY KEY, `sort_value` VARCHAR(1000) NOT NULL) ENGINE=InnoDB");
            await ExecuteAsync(setupConnection, $"CREATE TABLE `{budgetOversizedTable}` (`body` VARCHAR(1000) NOT NULL, `id` INT NOT NULL PRIMARY KEY) ENGINE=InnoDB");
            await ExecuteAsync(setupConnection, $"""
                INSERT INTO `{mainTable}`
                    (`tenant_id`, `id`, `amount`, `note`, `payload`)
                VALUES
                    (1, 1, 99999999999999999999999999999999999.123456789012345678901234567890, NULL, X'0001FF'),
                    (1, 2, 1.000000000000000000000000000001, '', X''),
                    (2, 1, 2.500000000000000000000000000000, 'third', X'CAFE')
                """);
            await ExecuteAsync(setupConnection, $"INSERT INTO `{noKeyTable}` VALUES ('duplicate', 1), ('duplicate', 1)");
            await ExecuteAsync(setupConnection, $"INSERT INTO `{nullableUniqueTable}` VALUES (NULL), (NULL)");
            await ExecuteAsync(setupConnection, $"INSERT INTO `{budgetKeyTable}` VALUES (1, REPEAT('a', 700)), (2, REPEAT('b', 700)), (3, REPEAT('c', 700)), (4, REPEAT('d', 700))");
            await ExecuteAsync(setupConnection, $"INSERT INTO `{budgetNoKeyTable}` VALUES (1, REPEAT('a', 700)), (2, REPEAT('b', 700)), (3, REPEAT('c', 700)), (4, REPEAT('d', 700))");
            await ExecuteAsync(setupConnection, $"INSERT INTO `{budgetSortTable}` VALUES (1, 'a'), (2, 'b'), (3, CONCAT('c', REPEAT('x', 700))), (4, CONCAT('d', REPEAT('x', 700))), (5, CONCAT('e', REPEAT('x', 700)))");
            await ExecuteAsync(setupConnection, $"INSERT INTO `{budgetOversizedTable}` VALUES (REPEAT('x', 700), 1)");

            using var host = BuildHost(connectionBuilder.ConnectionString, schema);
            var registry = host.Services.GetRequiredService<IRazorDbProviderRegistry>();
            var provider = await registry.GetProviderAsync(DatabaseId);
            var mainName = new DbObjectName(schema, mainTable);

            var database = await provider.Metadata.GetDatabaseAsync(new MetadataRequest(DatabaseId, true));
            Assert.Contains(database.Schemas.SelectMany(item => item.Objects), item => item.Name == mainName);
            Assert.Contains(database.ProductName, new[] { "MySQL", "MariaDB" });
            var expectedEngine = Environment.GetEnvironmentVariable("RAZORDB_TEST_ENGINE");
            if (expectedEngine?.Equals("mysql", StringComparison.OrdinalIgnoreCase) == true)
                Assert.Equal("MySQL", database.ProductName);
            if (expectedEngine?.Equals("mariadb", StringComparison.OrdinalIgnoreCase) == true)
                Assert.Equal("MariaDB", database.ProductName);

            var mainMetadata = await provider.Metadata.GetTableAsync(mainName, true);
            Assert.Equal(["tenant_id", "id"], mainMetadata.RowIdentityKey?.Columns);
            Assert.Equal(DbDataKind.Decimal, Column(mainMetadata, "amount").Type.Kind);
            Assert.Equal(65, Column(mainMetadata, "amount").Type.Precision);
            Assert.Equal(30, Column(mainMetadata, "amount").Type.Scale);
            Assert.True(Column(mainMetadata, "generated_note_length").IsGenerated);
            const string hostileComment = "\\'; DROP COLUMN `payload`; -- ";
            DdlPreview safeLiteralPreview = await provider.Schema.PreviewAsync(new SchemaChangeRequest(
                DatabaseId,
                new AddColumnChange(mainName, new ColumnDefinition
                {
                    Name = "safe_comment",
                    Type = new ColumnTypeDefinition { Type = SchemaDataType.VarChar, Length = 100 },
                    Comment = hostileComment,
                })));
            Assert.Equal(2, safeLiteralPreview.Statements.Count);
            Assert.Contains("NO_BACKSLASH_ESCAPES", safeLiteralPreview.Statements[0], StringComparison.Ordinal);
            await ExecuteAsync(setupConnection,
                "SET SESSION sql_mode = CONCAT_WS(',', @@SESSION.sql_mode, 'NO_BACKSLASH_ESCAPES')");
            foreach (string statement in safeLiteralPreview.Statements)
                await ExecuteAsync(setupConnection, statement);
            DbTableMetadata safeLiteralMetadata = await provider.Metadata.GetTableAsync(mainName, true);
            Assert.Equal(hostileComment, Column(safeLiteralMetadata, "safe_comment").Comment);
            Assert.Contains(safeLiteralMetadata.Columns, column => column.Name == "payload");
            await ExecuteAsync(setupConnection, $"ALTER TABLE `{mainTable}` DROP COLUMN `safe_comment`");

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await provider.Schema.PreviewAsync(new SchemaChangeRequest(
                    DatabaseId,
                    new AddIndexChange(mainName, new IndexDefinition
                    {
                        Name = "ix_missing_column",
                        Columns = [new DbIndexColumn("missing")],
                    }))));
            var noKeyName = new DbObjectName(schema, noKeyTable);
            var noKeyMetadata = await provider.Metadata.GetTableAsync(noKeyName, true);
            Assert.Null(noKeyMetadata.RowIdentityKey);
            Assert.Null((await provider.Metadata.GetTableAsync(new DbObjectName(schema, nullableUniqueTable), true)).RowIdentityKey);
            var noKeyInsert = await provider.Data.InsertRowAsync(new InsertRowRequest(
                DatabaseId,
                noKeyName,
                new Dictionary<string, EditValue>
                {
                    ["label"] = EditValue.FromValue(DbValue.FromString("inserted-without-key")),
                    ["value"] = EditValue.FromValue(DbValue.FromSignedInteger(2)),
                },
                noKeyMetadata.SchemaFingerprint));
            Assert.Equal(RowMutationStatus.Succeeded, noKeyInsert.Status);
            var noKeyPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                noKeyName,
                PageRequest.FromOffset(10)));
            Assert.All(noKeyPage.Rows, row => Assert.Null(row.Identity));
            Assert.Null(noKeyPage.NextCursor);
            RazorDbException unsafeMutation = await Assert.ThrowsAsync<RazorDbException>(async () =>
                await provider.Data.UpdateRowAsync(new UpdateRowRequest(
                    DatabaseId,
                    noKeyName,
                    new RowIdentity("forged", new Dictionary<string, DbValue>
                    {
                        ["label"] = DbValue.FromString("duplicate"),
                    }),
                    new Dictionary<string, DbValue>(),
                    new Dictionary<string, EditValue>
                    {
                        ["label"] = EditValue.FromValue(DbValue.FromString("unsafe")),
                    },
                    noKeyMetadata.SchemaFingerprint)));
            Assert.Equal(RazorDbErrorCode.Unsupported, unsafeMutation.Code);

            var firstPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                mainName,
                PageRequest.FromOffset(2),
                Sorts: [new DbSort("tenant_id"), new DbSort("id")],
                IncludeTotalCount: true));
            Assert.Equal(3, firstPage.TotalCount);
            Assert.Equal(2, firstPage.Rows.Count);
            Assert.True(firstPage.HasMore);
            Assert.NotNull(firstPage.NextCursor);
            Assert.Equal("99999999999999999999999999999999999.123456789012345678901234567890", Value(firstPage, firstPage.Rows[0], "amount").Text);
            Assert.True(Value(firstPage, firstPage.Rows[0], "note").IsNull);
            Assert.Equal(new byte[] { 0, 1, 255 }, Value(firstPage, firstPage.Rows[0], "payload").Binary.ToArray());
            await using (IRazorDbBinaryReadSession binary = await provider.Data.OpenBinaryAsync(new BinaryCellRequest(
                DatabaseId,
                mainName,
                "payload",
                firstPage.Rows[0].EffectiveBinaryIdentity!)))
            {
                Assert.Equal(3, binary.Descriptor.Length);
                Assert.Equal(DbDataKind.Binary, binary.Descriptor.Kind);
                await using MemoryStream binaryOutput = new();
                await binary.CopyToAsync(binaryOutput);
                Assert.Equal(new byte[] { 0, 1, 255 }, binaryOutput.ToArray());
            }
            await Assert.ThrowsAsync<RazorDbException>(async () =>
                await provider.Data.OpenBinaryAsync(new BinaryCellRequest(
                    DatabaseId,
                    mainName,
                    "note",
                    firstPage.Rows[0].EffectiveBinaryIdentity!)));
            Assert.Equal(string.Empty, Value(firstPage, firstPage.Rows[1], "note").Text);
            Assert.Empty(Value(firstPage, firstPage.Rows[1], "payload").Binary.ToArray());

            var secondPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                mainName,
                PageRequest.FromCursor(2, firstPage.NextCursor),
                Sorts: [new DbSort("tenant_id"), new DbSort("id")]));
            Assert.Single(secondPage.Rows);
            Assert.Equal("2", Value(secondPage, secondPage.Rows[0], "tenant_id").Text);

            RowPage ascendingNullPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                mainName,
                PageRequest.FromOffset(1),
                Sorts: [new DbSort("note")]));
            Assert.True(Value(ascendingNullPage, ascendingNullPage.Rows[0], "note").IsNull);
            Assert.NotNull(ascendingNullPage.NextCursor);
            RowPage ascendingEmptyPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                mainName,
                PageRequest.FromCursor(1, ascendingNullPage.NextCursor),
                Sorts: [new DbSort("note")]));
            Assert.Equal(string.Empty, Value(ascendingEmptyPage, ascendingEmptyPage.Rows[0], "note").Text);

            RowPage descendingTextPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                mainName,
                PageRequest.FromOffset(1),
                Sorts: [new DbSort("note", DbSortDirection.Descending)]));
            Assert.Equal("third", Value(descendingTextPage, descendingTextPage.Rows[0], "note").Text);
            Assert.NotNull(descendingTextPage.NextCursor);
            RowPage descendingEmptyPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                mainName,
                PageRequest.FromCursor(1, descendingTextPage.NextCursor),
                Sorts: [new DbSort("note", DbSortDirection.Descending)]));
            Assert.Equal(string.Empty, Value(descendingEmptyPage, descendingEmptyPage.Rows[0], "note").Text);
            Assert.NotNull(descendingEmptyPage.NextCursor);
            RowPage descendingNullPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                mainName,
                PageRequest.FromCursor(1, descendingEmptyPage.NextCursor),
                Sorts: [new DbSort("note", DbSortDirection.Descending)]));
            Assert.True(Value(descendingNullPage, descendingNullPage.Rows[0], "note").IsNull);

            using (IHost budgetHost = BuildHost(
                connectionBuilder.ConnectionString,
                schema,
                maximumResponseBytes: 1024))
            {
                IRazorDbProvider budgetProvider = await budgetHost.Services
                    .GetRequiredService<IRazorDbProviderRegistry>()
                    .GetProviderAsync(DatabaseId);

                IReadOnlyList<int> keyIds = await TraverseIdsAsync(
                    budgetProvider,
                    new DbObjectName(schema, budgetKeyTable),
                    "id",
                    pageSize: 10);
                Assert.Equal([1, 2, 3, 4], keyIds);

                IReadOnlyList<int> noKeyIds = await TraverseIdsAsync(
                    budgetProvider,
                    new DbObjectName(schema, budgetNoKeyTable),
                    "seq",
                    pageSize: 10,
                    sorts: [new DbSort("seq")]);
                Assert.Equal([1, 2, 3, 4], noKeyIds);

                IReadOnlyList<int> truncatedSortIds = await TraverseIdsAsync(
                    budgetProvider,
                    new DbObjectName(schema, budgetSortTable),
                    "id",
                    pageSize: 2,
                    sorts: [new DbSort("sort_value")]);
                Assert.Equal([1, 2, 3, 4, 5], truncatedSortIds);

                RazorDbException oversized = await Assert.ThrowsAsync<RazorDbException>(async () =>
                    await budgetProvider.Data.QueryRowsAsync(new RowQueryRequest(
                        DatabaseId,
                        new DbObjectName(schema, budgetOversizedTable),
                        PageRequest.FromOffset(10))));
                Assert.Equal(RazorDbErrorCode.LimitExceeded, oversized.Code);
            }

            var inserted = await provider.Data.InsertRowAsync(new InsertRowRequest(
                DatabaseId,
                mainName,
                new Dictionary<string, EditValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["tenant_id"] = EditValue.FromValue(DbValue.FromSignedInteger(7)),
                    ["id"] = EditValue.FromValue(DbValue.FromUnsignedInteger(9)),
                    ["amount"] = EditValue.FromValue(DbValue.FromDecimal("123.456789012345678901234567890")),
                    ["note"] = EditValue.FromValue(DbValue.FromString("inserted")),
                    ["payload"] = EditValue.FromValue(DbValue.FromBinary(new byte[] { 16, 32 })),
                    ["generated_note_length"] = EditValue.Omitted,
                },
                mainMetadata.SchemaFingerprint));
            Assert.Equal(RowMutationStatus.Succeeded, inserted.Status);

            var insertedPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                mainName,
                PageRequest.FromOffset(1),
                new LogicalFilter(DbLogicalOperator.And,
                [
                    new ComparisonFilter("tenant_id", DbComparisonOperator.Equal, DbValue.FromSignedInteger(7)),
                    new ComparisonFilter("id", DbComparisonOperator.Equal, DbValue.FromUnsignedInteger(9)),
                ])));
            var insertedRow = Assert.Single(insertedPage.Rows);
            Assert.NotNull(insertedRow.Identity);
            var originals = OriginalValues(insertedPage, insertedRow);

            var updated = await provider.Data.UpdateRowAsync(new UpdateRowRequest(
                DatabaseId,
                mainName,
                insertedRow.Identity!,
                originals,
                new Dictionary<string, EditValue> { ["note"] = EditValue.FromValue(DbValue.FromString("updated")) },
                insertedPage.SchemaFingerprint));
            Assert.Equal(RowMutationStatus.Succeeded, updated.Status);

            var refreshedPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                mainName,
                PageRequest.FromOffset(1),
                new ComparisonFilter("tenant_id", DbComparisonOperator.Equal, DbValue.FromSignedInteger(7))));
            var refreshedRow = Assert.Single(refreshedPage.Rows);
            Assert.Equal("updated", Value(refreshedPage, refreshedRow, "note").Text);
            await ExecuteAsync(setupConnection, $"UPDATE `{mainTable}` SET `note` = 'outside' WHERE `tenant_id` = 7 AND `id` = 9");
            var conflict = await provider.Data.UpdateRowAsync(new UpdateRowRequest(
                DatabaseId,
                mainName,
                refreshedRow.Identity!,
                OriginalValues(refreshedPage, refreshedRow),
                new Dictionary<string, EditValue> { ["note"] = EditValue.FromValue(DbValue.FromString("stale")) },
                refreshedPage.SchemaFingerprint));
            Assert.Equal(RowMutationStatus.Conflict, conflict.Status);

            await ExecuteAsync(setupConnection, $"""
                INSERT INTO `{mainTable}` (`tenant_id`, `id`, `amount`, `note`)
                VALUES (8, 1, 1, 'batch-one'), (8, 2, 2, 'batch-two')
                """);
            RowPage batchPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                mainName,
                PageRequest.FromOffset(10),
                new ComparisonFilter("tenant_id", DbComparisonOperator.Equal, DbValue.FromSignedInteger(8))));
            BatchRowMutationResult batchDeleted = await provider.Data.DeleteRowsAsync(new DeleteRowsRequest(
                DatabaseId,
                mainName,
                batchPage.Rows.Select(row => new DeleteRowTarget(row.Identity!, OriginalValues(batchPage, row))).ToArray(),
                batchPage.SchemaFingerprint));
            Assert.Equal(RowMutationStatus.Succeeded, batchDeleted.Status);
            Assert.Equal(2, batchDeleted.AffectedRows);

            await ExecuteAsync(setupConnection, $"""
                INSERT INTO `{mainTable}` (`tenant_id`, `id`, `amount`, `note`)
                VALUES (9, 1, 1, 'batch-stale-one'), (9, 2, 2, 'batch-stale-two')
                """);
            RowPage staleBatchPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                mainName,
                PageRequest.FromOffset(10),
                new ComparisonFilter("tenant_id", DbComparisonOperator.Equal, DbValue.FromSignedInteger(9))));
            await ExecuteAsync(setupConnection, $"UPDATE `{mainTable}` SET `note` = 'changed-outside' WHERE `tenant_id` = 9 AND `id` = 2");
            BatchRowMutationResult staleBatch = await provider.Data.DeleteRowsAsync(new DeleteRowsRequest(
                DatabaseId,
                mainName,
                staleBatchPage.Rows.Select(row => new DeleteRowTarget(row.Identity!, OriginalValues(staleBatchPage, row))).ToArray(),
                staleBatchPage.SchemaFingerprint));
            Assert.Equal(RowMutationStatus.Conflict, staleBatch.Status);
            Assert.Equal(0, staleBatch.AffectedRows);
            await using (MySqlCommand batchCount = setupConnection.CreateCommand())
            {
                batchCount.CommandText = $"SELECT COUNT(*) FROM `{mainTable}` WHERE `tenant_id` = 9";
                Assert.Equal(2L, Convert.ToInt64(await batchCount.ExecuteScalarAsync()));
            }
            await ExecuteAsync(setupConnection, $"DELETE FROM `{mainTable}` WHERE `tenant_id` = 9");

            var currentPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                mainName,
                PageRequest.FromOffset(1),
                new ComparisonFilter("tenant_id", DbComparisonOperator.Equal, DbValue.FromSignedInteger(7))));
            var currentRow = Assert.Single(currentPage.Rows);
            var deleted = await provider.Data.DeleteRowAsync(new DeleteRowRequest(
                DatabaseId,
                mainName,
                currentRow.Identity!,
                OriginalValues(currentPage, currentRow),
                currentPage.SchemaFingerprint));
            Assert.Equal(RowMutationStatus.Succeeded, deleted.Status);

            var csvBytes = Encoding.UTF8.GetBytes("id,text_value,empty_value,binary_value\n1,\"line one\nline two\",,AAH/\n2,\\N,not-empty,\\N\n");
            await using var csvInput = new MemoryStream(csvBytes);
            var import = await provider.Transfer.ImportAsync(
                new ImportRequest(DatabaseId, TransferFormat.Csv, new DbObjectName(schema, csvTable), NullToken: "\\N"),
                csvInput);
            Assert.Equal(2, import.RowsProcessed);
            Assert.Empty(import.Errors);

            await using var csvOutput = new MemoryStream();
            var export = await provider.Transfer.ExportAsync(
                new ExportRequest(DatabaseId, TransferFormat.Csv, [new DbObjectName(schema, csvTable)], IncludeSchema: false),
                csvOutput);
            Assert.Equal(2, export.RowsProcessed);
            var exportedText = Encoding.UTF8.GetString(csvOutput.ToArray());
            Assert.Contains("line one\nline two", exportedText, StringComparison.Ordinal);
            Assert.Contains("\\N", exportedText, StringComparison.Ordinal);
            Assert.DoesNotContain("generated_text_length", exportedText, StringComparison.Ordinal);

            csvOutput.Position = 0;
            var roundTripImport = await provider.Transfer.ImportAsync(
                new ImportRequest(DatabaseId, TransferFormat.Csv, new DbObjectName(schema, csvCopyTable), NullToken: "\\N"),
                csvOutput);
            Assert.Equal(2, roundTripImport.RowsProcessed);
            Assert.Empty(roundTripImport.Errors);

            var csvPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                new DbObjectName(schema, csvTable),
                PageRequest.FromOffset(10),
                Sorts: [new DbSort("id")]));
            Assert.True(Value(csvPage, csvPage.Rows[0], "empty_value").Text == string.Empty);
            Assert.Equal(new byte[] { 0, 1, 255 }, Value(csvPage, csvPage.Rows[0], "binary_value").Binary.ToArray());
            Assert.True(Value(csvPage, csvPage.Rows[1], "text_value").IsNull);
            Assert.True(Value(csvPage, csvPage.Rows[1], "binary_value").IsNull);

            var copyPage = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                new DbObjectName(schema, csvCopyTable),
                PageRequest.FromOffset(10),
                Sorts: [new DbSort("id")]));
            Assert.Equal("line one\nline two", Value(copyPage, copyPage.Rows[0], "text_value").Text);
            Assert.Equal(string.Empty, Value(copyPage, copyPage.Rows[0], "empty_value").Text);
            Assert.Equal(new byte[] { 0, 1, 255 }, Value(copyPage, copyPage.Rows[0], "binary_value").Binary.ToArray());
            Assert.True(Value(copyPage, copyPage.Rows[1], "text_value").IsNull);
            Assert.Equal("not-empty", Value(copyPage, copyPage.Rows[1], "empty_value").Text);
            Assert.True(Value(copyPage, copyPage.Rows[1], "binary_value").IsNull);

            await using var dumpOutput = new MemoryStream();
            var dump = await provider.Transfer.ExportAsync(
                new ExportRequest(DatabaseId, TransferFormat.Sql, [mainName]),
                dumpOutput);
            Assert.False(dump.IsPartial);
            var dumpText = Encoding.UTF8.GetString(dumpOutput.ToArray());
            Assert.Contains("SET FOREIGN_KEY_CHECKS=0;", dumpText, StringComparison.Ordinal);
            Assert.Contains("SET FOREIGN_KEY_CHECKS=@RAZORDB_OLD_FOREIGN_KEY_CHECKS;", dumpText, StringComparison.Ordinal);
            string insertPrefix = $"INSERT INTO `{schema}`.`{mainTable}` (`tenant_id`, `id`, `amount`, `note`, `payload`) VALUES (";
            Assert.Equal(1, dumpText.Split(insertPrefix, StringSplitOptions.None).Length - 1);
            Assert.Contains("),(", dumpText, StringComparison.Ordinal);
            Assert.Contains("`generated_note_length`", dumpText, StringComparison.Ordinal);
            string insertStatement = Assert.Single(
                dumpText.Split('\n'),
                line => line.StartsWith(insertPrefix, StringComparison.Ordinal));
            Assert.DoesNotContain("generated_note_length", insertStatement, StringComparison.Ordinal);

            dumpOutput.Position = 0;
            var restore = await provider.Transfer.ImportAsync(
                new ImportRequest(DatabaseId, TransferFormat.Sql),
                dumpOutput);
            Assert.Empty(restore.Errors);
            Assert.False(restore.IsPartial);

            await using var restoredRows = setupConnection.CreateCommand();
            restoredRows.CommandText = $"SELECT COUNT(*) FROM `{mainTable}`";
            Assert.Equal(3L, Convert.ToInt64(await restoredRows.ExecuteScalarAsync()));

            await using var partialDumpOutput = new MemoryStream();
            var partialDump = await provider.Transfer.ExportAsync(
                new ExportRequest(
                    DatabaseId,
                    TransferFormat.Sql,
                    [mainName],
                    IncludeSchema: false,
                    CompressWithGzip: true,
                    MaximumRows: 2),
                partialDumpOutput);
            Assert.Equal(2, partialDump.RowsProcessed);
            Assert.True(partialDump.IsPartial);
            partialDumpOutput.Position = 0;
            await using var decompressor = new GZipStream(partialDumpOutput, CompressionMode.Decompress, leaveOpen: true);
            using var partialReader = new StreamReader(decompressor, Encoding.UTF8, leaveOpen: true);
            string partialDumpText = await partialReader.ReadToEndAsync();
            Assert.Contains("SET FOREIGN_KEY_CHECKS=0;", partialDumpText, StringComparison.Ordinal);
            Assert.Contains("SET FOREIGN_KEY_CHECKS=@RAZORDB_OLD_FOREIGN_KEY_CHECKS;", partialDumpText, StringComparison.Ordinal);
        }
        finally
        {
            await DropTableAsync(setupConnection, mainTable);
            await DropTableAsync(setupConnection, noKeyTable);
            await DropTableAsync(setupConnection, nullableUniqueTable);
            await DropTableAsync(setupConnection, csvTable);
            await DropTableAsync(setupConnection, csvCopyTable);
            await DropTableAsync(setupConnection, budgetKeyTable);
            await DropTableAsync(setupConnection, budgetNoKeyTable);
            await DropTableAsync(setupConnection, budgetSortTable);
            await DropTableAsync(setupConnection, budgetOversizedTable);
        }
    }

    private static IHost BuildHost(
        string connectionString,
        string schema,
        int? maximumResponseBytes = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:IntegrationDatabase"] = connectionString,
        };
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(MySqlProviderIntegrationTests).Assembly.GetName().Name,
        });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(global::RazorDbManager.RazorDbManagerPolicies.Access,
                policy => policy.RequireAssertion(_ => true))
            .AddPolicy(global::RazorDbManager.RazorDbManagerPolicies.HighRisk,
                policy => policy.RequireAssertion(_ => true));
        builder.Services.AddRazorDbManager(options =>
        {
            options.DefaultDatabaseId = DatabaseId;
            options.StoragePath = Path.Combine(Path.GetTempPath(), "RazorDbManager.Tests", Guid.NewGuid().ToString("N"));
        }).AddMySql(DatabaseId, options =>
        {
            options.ConnectionStringName = "IntegrationDatabase";
            options.WriterConnectionStringName = "IntegrationDatabase";
            options.EnabledCapabilities = RazorDbCapabilitySets.DataEditor
                | RazorDbCapability.Import
                | RazorDbCapability.Export
                | RazorDbCapability.ModifySchema
                | RazorDbCapability.ExecuteSql
                | RazorDbCapability.DownloadBinary;
            options.SchemaConnectionStringName = "IntegrationDatabase";
            options.SqlConsoleConnectionStringName = "IntegrationDatabase";
            options.EnableSqlRestore = true;
            options.AllowedSchemas.Add(schema);
            options.AllowInsecureDevelopmentConnection = true;
            if (maximumResponseBytes is int responseBytes)
            {
                options.MaximumResponseBytes = responseBytes;
            }
        });
        return builder.Build();
    }

    private static async Task<IReadOnlyList<int>> TraverseIdsAsync(
        IRazorDbProvider provider,
        DbObjectName table,
        string idColumn,
        int pageSize,
        IReadOnlyList<DbSort>? sorts = null)
    {
        List<int> ids = [];
        PageRequest request = PageRequest.FromOffset(pageSize);
        for (int pageNumber = 0; pageNumber < 20; pageNumber++)
        {
            RowPage page = await provider.Data.QueryRowsAsync(new RowQueryRequest(
                DatabaseId,
                table,
                request,
                Sorts: sorts));
            int ordinal = page.Columns.ToList().FindIndex(column =>
                column.Name.Equals(idColumn, StringComparison.OrdinalIgnoreCase));
            Assert.True(ordinal >= 0);
            ids.AddRange(page.Rows.Select(row => int.Parse(
                row.Values[ordinal].Text!,
                System.Globalization.CultureInfo.InvariantCulture)));

            if (!page.HasMore)
            {
                Assert.Equal(ids.Count, ids.Distinct().Count());
                return ids;
            }

            if (page.NextCursor is not null)
            {
                request = PageRequest.FromCursor(pageSize, page.NextCursor);
            }
            else
            {
                long nextOffset = Assert.IsType<long>(page.NextOffset);
                request = request.After is not null
                    ? PageRequest.FromCursor(pageSize, request.After, nextOffset)
                    : PageRequest.FromOffset(pageSize, nextOffset);
            }
        }

        throw new Xunit.Sdk.XunitException("Response-budget pagination did not terminate within the test bound.");
    }

    private static DbColumnMetadata Column(DbTableMetadata metadata, string name) =>
        metadata.Columns.Single(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static DbValue Value(RowPage page, DbRow row, string column) =>
        row.Values[page.Columns.ToList().FindIndex(item => item.Name.Equals(column, StringComparison.OrdinalIgnoreCase))];

    private static IReadOnlyDictionary<string, DbValue> OriginalValues(RowPage page, DbRow row) =>
        page.Columns.Select((column, index) => new KeyValuePair<string, DbValue>(column.Name, row.Values[index]))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static async Task ExecuteAsync(MySqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropTableAsync(MySqlConnection connection, string table)
    {
        try
        {
            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{table}`");
        }
        catch (MySqlException) when (connection.State != System.Data.ConnectionState.Open)
        {
            // Preserve the test failure when the database itself became unavailable.
        }
    }
}
