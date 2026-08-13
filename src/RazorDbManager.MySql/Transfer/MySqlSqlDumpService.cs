using System.Globalization;
using System.IO.Compression;
using System.Text;
using MySqlConnector;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;
using RazorDbManager.MySql.Infrastructure;
using RazorDbManager.MySql.Metadata;
using RazorDbManager.MySql.Sql;

namespace RazorDbManager.MySql.Transfer;

internal sealed class MySqlSqlDumpService(
    MySqlProviderOptions options,
    MySqlCredentialSource credentials,
    MySqlDatabaseGuard guard,
    MySqlMetadataService metadata,
    IReadOnlyCollection<string> allowedSchemas) : ISqlDumpService
{
    internal const int MaximumInsertBatchRows = 500;
    internal const int MaximumInsertBatchUtf8Bytes = 1024 * 1024;
    internal const string ForeignKeyChecksComment =
        "-- FOREIGN_KEY_CHECKS is changed only for the session that imports this dump.";
    internal const string ForeignKeyChecksSave =
        "SET @RAZORDB_OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS;";
    internal const string ForeignKeyChecksDisable = "SET FOREIGN_KEY_CHECKS=0;";
    internal const string ForeignKeyChecksRestore =
        "SET FOREIGN_KEY_CHECKS=@RAZORDB_OLD_FOREIGN_KEY_CHECKS;";

    public async ValueTask<TransferResult> ExportAsync(
        ExportRequest request,
        Stream destination,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateExportLimits(request);
        if (request.RowQuery is not null)
            throw new RazorDbException(RazorDbErrorCode.Validation, "Structured row selection is supported only for CSV exports.");
        var maximumRows = Math.Min(request.MaximumRows ?? options.MaximumExportRows, options.MaximumExportRows);
        var maximumBytes = Math.Min(request.MaximumBytes ?? options.MaximumExportBytes, options.MaximumExportBytes);
        await using var counting = new CountingStream(destination, maximumBytes);
        long rows = 0;
        var partial = false;
        await using var dataSource = await credentials.CreateDataSourceAsync(
            MySqlCredentialSlot.Reader,
            cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead,
            isReadOnly: true,
            cancellationToken).ConfigureAwait(false);
        await RunTransactionAsync(
            async transactionToken =>
            {
                Stream output = counting;
                GZipStream? gzip = null;
                if (request.CompressWithGzip)
                {
                    gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
                    output = gzip;
                }

                try
                {
                    await using var writer = new StreamWriter(
                        output,
                        new UTF8Encoding(false),
                        64 * 1024,
                        leaveOpen: true);
                    await writer.WriteLineAsync(ForeignKeyChecksComment).ConfigureAwait(false);
                    await writer.WriteLineAsync(ForeignKeyChecksSave).ConfigureAwait(false);
                    await writer.WriteLineAsync(ForeignKeyChecksDisable).ConfigureAwait(false);
                    var tables = await ResolveTablesAsync(
                        connection,
                        transaction,
                        request,
                        transactionToken).ConfigureAwait(false);
                    foreach (var table in tables)
                    {
                        guard.EnsureAllowed(table.Schema);
                        if (request.IncludeSchema)
                        {
                            await using var show = connection.CreateCommand();
                            show.Transaction = transaction;
                            show.CommandText = $"SHOW CREATE TABLE {MySqlIdentifier.Qualify(table.Schema, table.Name)}";
                            await using var reader = await show.ExecuteReaderAsync(transactionToken).ConfigureAwait(false);
                            if (await reader.ReadAsync(transactionToken).ConfigureAwait(false))
                            {
                                await writer.WriteLineAsync($"DROP TABLE IF EXISTS {MySqlIdentifier.Qualify(table.Schema, table.Name)};").ConfigureAwait(false);
                                await writer.WriteAsync(QualifyCreateTable(reader.GetString(1), table)).ConfigureAwait(false);
                                await writer.WriteLineAsync(";").ConfigureAwait(false);
                            }
                        }

                        if (!request.IncludeData) continue;
                        var exportColumns = await ReadExportableColumnsAsync(
                            connection,
                            transaction,
                            table,
                            transactionToken).ConfigureAwait(false);
                        string insertPrefix = BuildInsertPrefix(table, exportColumns);
                        int emptyBatchUtf8Bytes = Encoding.UTF8.GetByteCount(insertPrefix) + 2;
                        int batchUtf8Bytes = emptyBatchUtf8Bytes;
                        List<string> batchRows = new(MaximumInsertBatchRows);

                        async ValueTask FlushBatchAsync()
                        {
                            if (batchRows.Count == 0) return;
                            await writer.WriteLineAsync(BuildInsertStatement(insertPrefix, batchRows)).ConfigureAwait(false);
                            batchRows.Clear();
                            batchUtf8Bytes = emptyBatchUtf8Bytes;
                        }

                        await using var select = connection.CreateCommand();
                        select.Transaction = transaction;
                        select.CommandText = BuildSelectSql(table, exportColumns);
                        await using var readerData = await select.ExecuteReaderAsync(
                            System.Data.CommandBehavior.SequentialAccess,
                            transactionToken).ConfigureAwait(false);
                        while (await readerData.ReadAsync(transactionToken).ConfigureAwait(false))
                        {
                            if (rows >= maximumRows) { partial = true; break; }
                            transactionToken.ThrowIfCancellationRequested();
                            string rowTuple = BuildRowTuple(readerData, exportColumns, options.MaximumExportCellBytes);
                            if (!CanAppendInsertRow(batchRows.Count, batchUtf8Bytes, rowTuple))
                                await FlushBatchAsync().ConfigureAwait(false);
                            if (batchRows.Count > 0) batchUtf8Bytes += 3;
                            batchRows.Add(rowTuple);
                            batchUtf8Bytes += Encoding.UTF8.GetByteCount(rowTuple);
                            rows++;
                            if (rows % 100 == 0)
                                progress?.Report(new TransferProgress(rows, counting.BytesProcessed, table));
                        }

                        await FlushBatchAsync().ConfigureAwait(false);
                        if (partial) break;
                    }

                    await writer.WriteLineAsync(ForeignKeyChecksRestore).ConfigureAwait(false);
                    await writer.FlushAsync(transactionToken).ConfigureAwait(false);
                }
                finally
                {
                    if (gzip is not null) await gzip.DisposeAsync().ConfigureAwait(false);
                }

            },
            async transactionToken => await transaction.CommitAsync(transactionToken).ConfigureAwait(false),
            async transactionToken => await transaction.RollbackAsync(transactionToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new TransferProgress(rows, counting.BytesProcessed));
        return new TransferResult(rows, counting.BytesProcessed, [], partial);
    }

    public async ValueTask<TransferResult> ImportAsync(
        ImportRequest request,
        Stream source,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var counting = new CountingStream(source, options.MaximumUploadBytes);
        using var reader = new StreamReader(counting, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 64 * 1024, leaveOpen: true);
        var errors = new List<TransferError>();
        long processed = 0;
        await using var dataSource = await credentials.CreateDataSourceAsync(MySqlCredentialSlot.SqlConsole, cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        long position = 0;
        await foreach (var statement in MySqlScriptTokenizer.TokenizeAsync(
            reader,
            options.MaximumSqlTextBytes,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            position++;
            try
            {
                MySqlDumpStatementGuard.EnsureAllowed(statement.Text, allowedSchemas);
                await using var command = connection.CreateCommand();
                command.CommandText = statement.Text;
                command.CommandTimeout = options.SqlCommandTimeoutSeconds;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                processed++;
                progress?.Report(new TransferProgress(processed, counting.BytesProcessed));
            }
            catch (MySqlException exception)
            {
                if (errors.Count < 100) errors.Add(new TransferError(position, Sanitize(exception)));
                if (!request.ContinueOnError) break;
            }
        }

        return new TransferResult(processed, counting.BytesProcessed, errors, errors.Count > 0);
    }

    private async Task<IReadOnlyList<DbObjectName>> ResolveTablesAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        ExportRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Tables.Count > 0) return request.Tables;
        string[] schemas = allowedSchemas.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (schemas.Length == 0)
            throw new RazorDbException(RazorDbErrorCode.Forbidden, "SQL dump requires at least one allowed schema.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        string[] parameters = new string[schemas.Length];
        for (var index = 0; index < schemas.Length; index++)
        {
            parameters[index] = $"@schema{index}";
            command.Parameters.AddWithValue(parameters[index], schemas[index]);
        }
        command.CommandText = $"""
            SELECT TABLE_SCHEMA, TABLE_NAME
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA IN ({string.Join(",", parameters)})
              AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """;
        List<DbObjectName> tables = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            tables.Add(new DbObjectName(reader.GetString(0), reader.GetString(1)));
        return tables;
    }

    private static async Task<DbColumnMetadata[]> ReadExportableColumnsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        DbObjectName table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COLUMN_NAME, ORDINAL_POSITION, DATA_TYPE, COLUMN_TYPE, IS_NULLABLE,
                   EXTRA, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE,
                   GENERATION_EXPRESSION
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION
            """;
        command.Parameters.AddWithValue("@schema", table.Schema);
        command.Parameters.AddWithValue("@table", table.Name);
        List<DbColumnMetadata> columns = [];
        long visibleColumnCount = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                visibleColumnCount++;
                string extra = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                bool generated = extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase)
                    || !reader.IsDBNull(9) && reader.GetString(9).Length > 0;
                if (generated) continue;
                columns.Add(new DbColumnMetadata(
                    reader.GetString(0),
                    reader.GetInt32(1) - 1,
                    MySqlTypeMapper.Map(
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        reader.IsDBNull(8) ? null : reader.GetInt32(8)),
                    reader.GetString(4).Equals("YES", StringComparison.OrdinalIgnoreCase)));
            }
        }

        if (visibleColumnCount == 0)
            throw new KeyNotFoundException($"Table or view '{table}' has no visible columns or does not exist.");
        return columns.ToArray();
    }

    private static void ValidateExportLimits(ExportRequest request)
    {
        if (request.MaximumRows is <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Export row limit must be positive.");
        if (request.MaximumBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Export byte limit must be positive.");
    }

    internal static async ValueTask RunTransactionAsync(
        Func<CancellationToken, ValueTask> operation,
        Func<CancellationToken, ValueTask> commit,
        Func<CancellationToken, ValueTask> rollback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(rollback);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
            await commit(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try
            {
                await rollback(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                exception.Data["RazorDbManager.RollbackFailure"] = rollbackException.GetType().Name;
            }
            throw;
        }
    }

    internal static string DumpLiteral(DbValue value) => value.Kind switch
    {
        DbValueKind.Null => "NULL",
        DbValueKind.Binary or DbValueKind.Geometry => $"X'{Convert.ToHexString(value.Binary.Span)}'",
        DbValueKind.SignedInteger or DbValueKind.UnsignedInteger or DbValueKind.Decimal or DbValueKind.FloatingPoint => value.Text!,
        DbValueKind.Boolean => value.Text == "true" ? "1" : "0",
        _ => $"CONVERT(X'{Convert.ToHexString(Encoding.UTF8.GetBytes(value.Text!))}' USING utf8mb4)",
    };

    internal static DbColumnMetadata[] ExportableColumns(DbTableMetadata table) =>
        table.Columns.Where(column => !column.IsGenerated).ToArray();

    internal static string BuildSelectSql(DbObjectName table, IReadOnlyList<DbColumnMetadata> columns) =>
        $"SELECT {(columns.Count == 0 ? "1" : string.Join(", ", columns.Select(column => MySqlIdentifier.Quote(column.Name))))} FROM {MySqlIdentifier.Qualify(table.Schema, table.Name)}";

    internal static string BuildInsertPrefix(DbObjectName table, IReadOnlyList<DbColumnMetadata> columns) =>
        $"INSERT INTO {MySqlIdentifier.Qualify(table.Schema, table.Name)} ({string.Join(", ", columns.Select(column => MySqlIdentifier.Quote(column.Name)))}) VALUES (";

    internal static bool CanAppendInsertRow(
        int currentRowCount,
        int currentUtf8Bytes,
        string rowTuple,
        int maximumRows = MaximumInsertBatchRows,
        int maximumUtf8Bytes = MaximumInsertBatchUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(rowTuple);
        if (currentRowCount < 0) throw new ArgumentOutOfRangeException(nameof(currentRowCount));
        if (currentUtf8Bytes < 0) throw new ArgumentOutOfRangeException(nameof(currentUtf8Bytes));
        if (maximumRows <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        if (maximumUtf8Bytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
        if (currentRowCount == 0) return true;
        return currentRowCount < maximumRows
            && currentUtf8Bytes + 3 + Encoding.UTF8.GetByteCount(rowTuple) <= maximumUtf8Bytes;
    }

    internal static string BuildInsertStatement(string insertPrefix, IReadOnlyList<string> rowTuples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(insertPrefix);
        ArgumentNullException.ThrowIfNull(rowTuples);
        if (rowTuples.Count == 0) throw new ArgumentException("An INSERT batch requires at least one row.", nameof(rowTuples));
        return $"{insertPrefix}{string.Join("),(", rowTuples)});";
    }

    private static string BuildRowTuple(
        MySqlDataReader reader,
        IReadOnlyList<DbColumnMetadata> columns,
        long maximumCellBytes)
    {
        StringBuilder tuple = new();
        for (var index = 0; index < columns.Count; index++)
        {
            DbValue value = MySqlExportValueReader.Read(reader, index, columns[index], maximumCellBytes);
            if (index > 0) tuple.Append(',');
            tuple.Append(DumpLiteral(value));
        }
        return tuple.ToString();
    }

    internal static string QualifyCreateTable(string showCreate, DbObjectName table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(showCreate);
        const string prefix = "CREATE TABLE ";
        if (!showCreate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new RazorDbException(RazorDbErrorCode.Validation, "SHOW CREATE TABLE returned an unexpected statement.");

        int objectStart = prefix.Length;
        int objectEnd = FindIdentifierEnd(showCreate, objectStart);
        return string.Concat(
            showCreate.AsSpan(0, objectStart),
            MySqlIdentifier.Qualify(table.Schema, table.Name),
            showCreate.AsSpan(objectEnd));
    }

    private static int FindIdentifierEnd(string sql, int start)
    {
        int index = start;
        while (index < sql.Length && char.IsWhiteSpace(sql[index])) index++;
        if (index >= sql.Length) throw new RazorDbException(RazorDbErrorCode.Validation, "SHOW CREATE TABLE omitted its target.");

        if (sql[index] == '`')
        {
            index++;
            while (index < sql.Length)
            {
                if (sql[index] != '`') { index++; continue; }
                if (index + 1 < sql.Length && sql[index + 1] == '`') { index += 2; continue; }
                return index + 1;
            }
            throw new RazorDbException(RazorDbErrorCode.Validation, "SHOW CREATE TABLE returned an unterminated identifier.");
        }

        while (index < sql.Length && !char.IsWhiteSpace(sql[index]) && sql[index] != '(') index++;
        return index;
    }

    private static string Sanitize(MySqlException exception) => exception.Number switch
    {
        1044 or 1045 => "The database rejected the configured credential.",
        1064 => "The SQL script contains invalid syntax.",
        1146 => "A referenced table does not exist.",
        1451 or 1452 => "A foreign key constraint rejected the statement.",
        _ => $"The database rejected statement ({exception.Number}).",
    };
}
