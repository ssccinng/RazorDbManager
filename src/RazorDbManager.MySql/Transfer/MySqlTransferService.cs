using System.Globalization;
using System.IO.Compression;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using MySqlConnector;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;
using RazorDbManager.MySql.Data;
using RazorDbManager.MySql.Infrastructure;
using RazorDbManager.MySql.Metadata;
using RazorDbManager.MySql.Sql;

namespace RazorDbManager.MySql.Transfer;

internal sealed class MySqlTransferService(
    MySqlProviderOptions options,
    MySqlCredentialSource credentials,
    MySqlDatabaseGuard guard,
    MySqlMetadataService metadata,
    ISqlDumpService dump) : IRazorDbTransferProvider
{
    private const string DefaultNullToken = "\\N";

    public ValueTask<TransferResult> ImportAsync(ImportRequest request, Stream source, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
        request.Format == TransferFormat.Sql
            ? options.EnableSqlRestore
                ? dump.ImportAsync(request, source, progress, cancellationToken)
                : ValueTask.FromException<TransferResult>(new RazorDbException(
                    RazorDbErrorCode.Unsupported,
                    "SQL restore is disabled for this database registration."))
            : ImportCsvAsync(request, source, progress, cancellationToken);

    public ValueTask<TransferResult> ExportAsync(ExportRequest request, Stream destination, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
        request.Format == TransferFormat.Sql
            ? dump.ExportAsync(request, destination, progress, cancellationToken)
            : ExportCsvAsync(request, destination, progress, cancellationToken);

    private async ValueTask<TransferResult> ImportCsvAsync(ImportRequest request, Stream source, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var tableName = request.Table ?? throw new ArgumentException("CSV import requires a target table.", nameof(request));
        ValidateCsvOptions(request);
        guard.EnsureAllowed(tableName.Schema);
        var table = await metadata.GetTableAsync(tableName, false, cancellationToken).ConfigureAwait(false);
        DbColumnMetadata[] writableColumns = table.Columns.Where(column => !column.IsGenerated).ToArray();
        if (writableColumns.Length == 0)
            throw new RazorDbException(RazorDbErrorCode.Unsupported, "The target table has no writable columns.");
        if (!request.ContinueOnError && !string.Equals(table.Engine, "InnoDB", StringComparison.OrdinalIgnoreCase))
        {
            throw new RazorDbException(
                RazorDbErrorCode.Unsupported,
                "Atomic CSV import requires an InnoDB table. Enable continue-on-error only when partial writes are acceptable.");
        }
        await using var counting = new CountingStream(source, options.MaximumUploadBytes);
        await using var recordLimited = new CsvRecordLimitStream(counting, options.MaximumCsvRecordBytes);
        using var textReader = new StreamReader(recordLimited, Encoding.UTF8, true, 64 * 1024, leaveOpen: true);
        using var csv = new CsvReader(textReader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = request.Delimiter.ToString(),
            HasHeaderRecord = request.HasHeader,
            BadDataFound = null,
            MissingFieldFound = null,
            DetectColumnCountChanges = true,
        });
        var hasRecord = await csv.ReadAsync();
        if (!hasRecord)
        {
            return new TransferResult(0, counting.BytesProcessed, [], false);
        }
        string[] columns;
        if (request.HasHeader)
        {
            csv.ReadHeader();
            columns = (csv.HeaderRecord
                ?? throw new RazorDbException(RazorDbErrorCode.Validation, "CSV header is missing."))
                .Select(column => request.DecodeProtectedValues
                    ? DecodeCsvText(column, request.NullToken ?? DefaultNullToken)
                    : column)
                .ToArray();
            hasRecord = await csv.ReadAsync();
        }
        else
        {
            columns = writableColumns.Select(column => column.Name).ToArray();
        }

        if (columns.Length is 0 || columns.Length > options.MaximumImportColumns) throw new RazorDbException(RazorDbErrorCode.LimitExceeded, "CSV column limit exceeded.");
        var metadataColumns = table.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        if (columns.Any(column => !metadataColumns.ContainsKey(column))) throw new RazorDbException(RazorDbErrorCode.Validation, "CSV contains an unknown column.");
        if (columns.Any(column => metadataColumns[column].IsGenerated))
            throw new RazorDbException(RazorDbErrorCode.Validation, "CSV cannot write generated columns.");
        var errors = new List<TransferError>();
        long rows = 0;
        await using var dataSource = await credentials.CreateDataSourceAsync(MySqlCredentialSlot.Writer, cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using MySqlTransaction? transaction = request.ContinueOnError
            ? null
            : await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (hasRecord)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    var parameters = new string[columns.Length];
                    for (var index = 0; index < columns.Length; index++)
                    {
                        parameters[index] = $"@p{index}";
                        var text = csv.GetField(index) ?? string.Empty;
                        var nullToken = request.NullToken ?? DefaultNullToken;
                        command.Parameters.AddWithValue(
                            parameters[index],
                            ParseCsvField(
                                metadataColumns[columns[index]],
                                text,
                                nullToken,
                                request.DecodeProtectedValues));
                    }

                    command.CommandText = $"INSERT INTO {MySqlIdentifier.Qualify(tableName.Schema, tableName.Name)} ({string.Join(", ", columns.Select(MySqlIdentifier.Quote))}) VALUES ({string.Join(", ", parameters)})";
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    rows++;
                    if (rows % 100 == 0) progress?.Report(new TransferProgress(rows, counting.BytesProcessed, tableName));
                }
                catch (Exception exception) when (exception is MySqlException or CsvHelperException or RazorDbException)
                {
                    if (errors.Count < 100)
                    {
                        errors.Add(new TransferError(
                            csv.Parser.Row,
                            exception is RazorDbException known
                                ? known.Message
                                : "The CSV record was rejected by the database."));
                    }
                    if (!request.ContinueOnError) break;
                }

                hasRecord = await csv.ReadAsync();
            }

            if (transaction is not null)
            {
                if (errors.Count == 0)
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                else
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    rows = 0;
                }
            }
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new TransferResult(rows, counting.BytesProcessed, errors, errors.Count > 0);
    }

    private async ValueTask<TransferResult> ExportCsvAsync(ExportRequest request, Stream destination, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        if (request.Tables.Count != 1) throw new ArgumentException("CSV export requires exactly one table.", nameof(request));
        if (request.MaximumRows is <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Export row limit must be positive.");
        if (request.MaximumBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Export byte limit must be positive.");
        var tableName = request.Tables[0];
        guard.EnsureAllowed(tableName.Schema);
        var table = await metadata.GetTableAsync(tableName, false, cancellationToken).ConfigureAwait(false);
        DbColumnMetadata[] exportColumns = SelectExportColumns(table, request.RowQuery?.Columns);
        if (exportColumns.Length == 0)
            throw new RazorDbException(RazorDbErrorCode.Unsupported, "The source table has no exportable columns.");
        request.RowQuery?.Filter?.ValidateAgainst(table);
        request.RowQuery?.Sorts?.ValidateAgainst(table);
        CompiledSql filter = MySqlCoreFilterCompiler.Compile(
            request.RowQuery?.Filter,
            table.Columns.Select(column => column.Name).ToArray());
        IReadOnlyList<DbSort> sorts = BuildExportSorts(request.RowQuery?.Sorts, table);
        var maximumRows = Math.Min(request.MaximumRows ?? options.MaximumExportRows, options.MaximumExportRows);
        var maximumBytes = Math.Min(request.MaximumBytes ?? options.MaximumExportBytes, options.MaximumExportBytes);
        await using var counting = new CountingStream(destination, maximumBytes);
        Stream output = counting;
        GZipStream? gzip = null;
        if (request.CompressWithGzip) { gzip = new GZipStream(output, CompressionLevel.Fastest, true); output = gzip; }
        long rows = 0;
        bool hasMoreRows = false;
        try
        {
            await using var dataSource = await credentials.CreateDataSourceAsync(MySqlCredentialSlot.Reader, cancellationToken).ConfigureAwait(false);
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = BuildCsvSelectSql(tableName, exportColumns, filter.Text, sorts);
            foreach ((string name, object value) in filter.Parameters)
                command.Parameters.AddWithValue(name, value);
            await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            await using var textWriter = new StreamWriter(output, new UTF8Encoding(false), 64 * 1024, true);
            await using var csv = new CsvWriter(textWriter, CultureInfo.InvariantCulture);
            foreach (var column in exportColumns)
                csv.WriteField(EncodeCsvField(DbValue.FromString(column.Name), DefaultNullToken));
            await csv.NextRecordAsync();
            while (rows < maximumRows && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                for (var index = 0; index < exportColumns.Length; index++)
                {
                    var value = MySqlExportValueReader.Read(
                        reader,
                        index,
                        exportColumns[index],
                        options.MaximumExportCellBytes);
                    csv.WriteField(EncodeCsvField(value, DefaultNullToken));
                }
                await csv.NextRecordAsync();
                rows++;
                if (rows % 100 == 0) progress?.Report(new TransferProgress(rows, counting.BytesProcessed, tableName));
            }
            if (rows == maximumRows)
                hasMoreRows = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            await textWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (gzip is not null) await gzip.DisposeAsync().ConfigureAwait(false);
        }

        return new TransferResult(rows, counting.BytesProcessed, [], hasMoreRows);
    }

    internal static DbColumnMetadata[] SelectExportColumns(
        DbTableMetadata table,
        IReadOnlyList<string>? requestedColumns)
    {
        DbColumnMetadata[] available = table.Columns.Where(column => !column.IsGenerated).ToArray();
        if (requestedColumns is null) return available;
        if (requestedColumns.Count == 0)
            throw new RazorDbException(RazorDbErrorCode.Validation, "CSV export must select at least one column.");
        if (requestedColumns.Count != requestedColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new RazorDbException(RazorDbErrorCode.Validation, "CSV export column selection contains duplicates.");

        Dictionary<string, DbColumnMetadata> byName = available.ToDictionary(
            column => column.Name,
            StringComparer.OrdinalIgnoreCase);
        try
        {
            return requestedColumns.Select(column => byName.TryGetValue(column, out DbColumnMetadata? metadataColumn)
                ? metadataColumn
                : throw new RazorDbException(
                    RazorDbErrorCode.Validation,
                    "CSV export column selection is not present or exportable in current metadata."))
                .ToArray();
        }
        catch (ArgumentException exception)
        {
            throw new RazorDbException(RazorDbErrorCode.Validation, "CSV export metadata contains duplicate column names.", exception);
        }
    }

    internal static IReadOnlyList<DbSort> BuildExportSorts(
        IReadOnlyList<DbSort>? requested,
        DbTableMetadata table)
    {
        List<DbSort> sorts = requested is null ? [] : [.. requested];
        if (table.RowIdentityKey is not null)
        {
            foreach (string column in table.RowIdentityKey.Columns)
            {
                if (sorts.All(sort => !sort.Column.Equals(column, StringComparison.OrdinalIgnoreCase)))
                    sorts.Add(new DbSort(column));
            }
        }
        else if (sorts.Count == 0)
        {
            sorts.AddRange(table.Columns.Select(column => new DbSort(column.Name)));
        }

        return sorts;
    }

    internal static string BuildCsvSelectSql(
        DbObjectName table,
        IReadOnlyList<DbColumnMetadata> columns,
        string filterSql,
        IReadOnlyList<DbSort> sorts)
    {
        StringBuilder sql = new("SELECT ");
        sql.Append(string.Join(", ", columns.Select(column => MySqlIdentifier.Quote(column.Name))))
            .Append(" FROM ").Append(MySqlIdentifier.Qualify(table.Schema, table.Name));
        if (filterSql.Length > 0) sql.Append(" WHERE ").Append(filterSql);
        if (sorts.Count > 0)
        {
            sql.Append(" ORDER BY ").Append(string.Join(", ", sorts.Select(sort =>
                $"{MySqlIdentifier.Quote(sort.Column)} {(sort.Direction == DbSortDirection.Descending ? "DESC" : "ASC")}")));
        }
        return sql.ToString();
    }

    internal static object ParseCsvField(
        DbColumnMetadata column,
        string text,
        string nullToken,
        bool decodeProtectedValues = false)
    {
        if (string.Equals(text, nullToken, StringComparison.Ordinal)) return DBNull.Value;
        if (decodeProtectedValues) text = DecodeCsvText(text, nullToken);
        if (column.Type.Kind is not (DbDataKind.Binary or DbDataKind.Geometry)) return text;

        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException exception)
        {
            throw new RazorDbException(
                RazorDbErrorCode.Validation,
                $"CSV field for binary column '{column.Name}' is not valid Base64.",
                exception);
        }
    }

    internal static string EncodeCsvField(DbValue value, string nullToken)
    {
        if (value.IsNull) return nullToken;

        string text = value.Kind is DbValueKind.Binary or DbValueKind.Geometry
            ? Convert.ToBase64String(value.Binary.Span)
            : value.Text ?? string.Empty;
        bool textLike = value.Kind is DbValueKind.String
            or DbValueKind.Json
            or DbValueKind.Enum
            or DbValueKind.Set
            or DbValueKind.ProviderSpecific
            or DbValueKind.Binary
            or DbValueKind.Geometry;
        if (!textLike) return text;

        if (text.StartsWith('\\') || string.Equals(text, nullToken, StringComparison.Ordinal))
            text = $"\\{text}";
        if (text.StartsWith('\''))
            return $"'{text}";
        return StartsSpreadsheetFormula(text) ? $"'{text}" : text;
    }

    private static string DecodeCsvText(string text, string nullToken)
    {
        if (text.StartsWith("''", StringComparison.Ordinal)
            || text.Length > 1 && text[0] == '\'' && StartsSpreadsheetFormula(text.AsSpan(1)))
            text = text[1..];
        if (text.StartsWith("\\\\", StringComparison.Ordinal)
            || text.Length > 0 && text[0] == '\\' && text.AsSpan(1).SequenceEqual(nullToken))
            text = text[1..];
        return text;
    }

    private static bool StartsSpreadsheetFormula(string text) =>
        StartsSpreadsheetFormula(text.AsSpan());

    private static bool StartsSpreadsheetFormula(ReadOnlySpan<char> text) =>
        StartsSpreadsheetFormulaAfterSpaces(text);

    private static bool StartsSpreadsheetFormulaAfterSpaces(ReadOnlySpan<char> text)
    {
        int index = 0;
        while (index < text.Length && text[index] == ' ') index++;
        return index < text.Length && text[index] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n';
    }

    private static void ValidateCsvOptions(ImportRequest request)
    {
        if (request.Delimiter is '\r' or '\n' or '"')
            throw new RazorDbException(RazorDbErrorCode.Validation, "The CSV delimiter is not supported.");
        if (request.NullToken is { Length: 0 } || request.NullToken?.Length > 64)
            throw new RazorDbException(RazorDbErrorCode.Validation, "The CSV NULL token is invalid.");
    }
}
