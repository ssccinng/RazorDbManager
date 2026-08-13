using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using MySqlConnector;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;
using RazorDbManager.MySql.Infrastructure;
using RazorDbManager.MySql.Metadata;
using RazorDbManager.MySql.Sql;

namespace RazorDbManager.MySql.Data;

internal sealed class MySqlDataService(
    MySqlProviderOptions options,
    MySqlCredentialSource credentials,
    MySqlDatabaseGuard guard,
    MySqlMetadataService metadata)
{
    public async Task<RowPage> QueryAsync(RowQueryRequest request, CancellationToken cancellationToken)
    {
        guard.EnsureAllowed(request.Table.Schema);
        var table = await metadata.GetTableAsync(request.Table, false, cancellationToken).ConfigureAwait(false);
        var pageSize = Math.Min(request.Page.PageSize, options.MaximumPageSize);
        var effectiveSorts = BuildEffectiveSorts(request.Sorts, table);
        if (request.Page.After is not null && table.RowIdentityKey is null)
        {
            throw new ArgumentException("Keyset pagination requires a safe unique row identity.", nameof(request));
        }
        var filter = MySqlCoreFilterCompiler.Compile(request.Filter, table.Columns.Select(column => column.Name).ToArray());

        await using var dataSource = await credentials.CreateDataSourceAsync(MySqlCredentialSlot.Reader, cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var where = new List<string>();
        if (filter.Text.Length > 0)
        {
            where.Add(filter.Text);
            foreach (var parameter in filter.Parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        if (request.Page.After is not null)
        {
            CompiledSql compiledCursor = MySqlCursorPredicateCompiler.Compile(
                request.Page.After,
                effectiveSorts,
                table.Columns.Select(column => column.Name).ToArray());
            where.Add(compiledCursor.Text);
            foreach (var parameter in compiledCursor.Parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }
        }

        var columns = string.Join(", ", table.Columns.Select(column => MySqlIdentifier.Quote(column.Name)));
        var sql = new StringBuilder("SELECT ").Append(columns).Append(" FROM ")
            .Append(MySqlIdentifier.Qualify(request.Table.Schema, request.Table.Name));
        if (where.Count > 0) sql.Append(" WHERE ").Append(string.Join(" AND ", where.Select(value => $"({value})")));
        if (effectiveSorts.Count > 0)
        {
            sql.Append(" ORDER BY ").Append(string.Join(", ", effectiveSorts.Select(sort =>
                $"{MySqlIdentifier.Quote(sort.Column)} {(sort.Direction == DbSortDirection.Descending ? "DESC" : "ASC")}")));
        }

        sql.Append(" LIMIT @limit");
        command.Parameters.AddWithValue("@limit", pageSize + 1);
        if (request.Page.Offset is { } offset)
        {
            sql.Append(" OFFSET @offset");
            command.Parameters.AddWithValue("@offset", offset);
        }

        command.CommandText = sql.ToString();
        var commands = new List<DbCommandDiagnostic>(request.IncludeTotalCount ? 2 : 1);
        var rows = new List<DbRow>(pageSize + 1);
        var cursorSafety = new List<bool>(pageSize + 1);
        long responseSize = 0;
        var byteTruncated = false;
        var stopReading = false;
        Stopwatch commandStopwatch = Stopwatch.StartNew();
        await using (var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false))
        {
            while (rows.Count <= pageSize && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var values = new DbValue[table.Columns.Count];
                var truncatedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < values.Length; index++)
                {
                    var remainingBytes = options.MaximumResponseBytes - responseSize;
                    if (remainingBytes <= 0)
                    {
                        byteTruncated = stopReading = true;
                        break;
                    }

                    values[index] = MySqlValueConverter.Read(
                        reader,
                        index,
                        table.Columns[index],
                        Math.Min(options.MaximumCellPreviewBytes, remainingBytes),
                        out var cellTruncated);
                    if (cellTruncated) truncatedColumns.Add(table.Columns[index].Name);
                    byteTruncated |= cellTruncated;
                    responseSize += MySqlValueConverter.EstimateSize(values[index]);
                    if (responseSize > options.MaximumResponseBytes)
                    {
                        byteTruncated = stopReading = true;
                        break;
                    }
                }

                if (stopReading) break;
                // Optimistic updates compare the full original row. A truncated preview is therefore
                // browse-only even when its key itself fits in the response.
                bool identityTruncated = table.RowIdentityKey?.Columns.Any(truncatedColumns.Contains) == true;
                RowIdentity? binaryIdentity = identityTruncated ? null : CreateIdentity(table, values);
                rows.Add(new DbRow(
                    values,
                    truncatedColumns.Count == 0 ? binaryIdentity : null)
                {
                    BinaryIdentity = binaryIdentity,
                });
                cursorSafety.Add(effectiveSorts.All(sort => !truncatedColumns.Contains(sort.Column)));
            }
        }
        commandStopwatch.Stop();
        commands.Add(CreateCommandDiagnostic(command, commandStopwatch.Elapsed));

        var hasLookaheadRow = rows.Count > pageSize;
        if (hasLookaheadRow)
        {
            rows.RemoveAt(rows.Count - 1);
            cursorSafety.RemoveAt(cursorSafety.Count - 1);
        }
        if (stopReading && rows.Count == 0)
        {
            throw new RazorDbException(
                RazorDbErrorCode.LimitExceeded,
                "The first row exceeds the configured response-size limit.");
        }

        var hasMore = hasLookaheadRow || stopReading;
        long? total = null;
        if (request.IncludeTotalCount)
        {
            (long count, DbCommandDiagnostic diagnostic) = await CountAsync(
                connection,
                request.Table,
                filter,
                cancellationToken).ConfigureAwait(false);
            total = count;
            commands.Add(diagnostic);
        }
        var cursor = MySqlKeysetPagination.CreateNextCursor(
            table,
            effectiveSorts,
            rows,
            rows.Count > 0 && cursorSafety[^1]);
        long? nextOffset = hasMore && cursor is null
            ? checked((request.Page.Offset ?? 0L) + rows.Count)
            : null;
        return new RowPage(
            table.Columns,
            rows,
            total,
            cursor,
            hasMore,
            table.SchemaFingerprint,
            byteTruncated,
            nextOffset)
        {
            Commands = commands,
        };
    }

    public async Task<RowMutationResult> InsertAsync(InsertRowRequest request, CancellationToken cancellationToken)
    {
        var table = await ValidateMutationAsync(request.Table, request.ExpectedSchemaFingerprint, cancellationToken).ConfigureAwait(false);
        ValidateEditColumns(table, request.Values);
        var included = request.Values.Where(pair => pair.Value.Kind != EditValueKind.Omitted).ToArray();
        await using var dataSource = await credentials.CreateDataSourceAsync(MySqlCredentialSlot.Writer, cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (included.Length == 0)
        {
            command.CommandText = $"INSERT INTO {MySqlIdentifier.Qualify(request.Table.Schema, request.Table.Name)} () VALUES ()";
        }
        else
        {
            var names = string.Join(", ", included.Select(pair => MySqlIdentifier.Quote(pair.Key)));
            var values = string.Join(", ", included.Select(pair => EditSql(pair.Value, command)));
            command.CommandText = $"INSERT INTO {MySqlIdentifier.Qualify(request.Table.Schema, request.Table.Name)} ({names}) VALUES ({values})";
        }

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        RowIdentity? identity = null;
        if (affected == 1 && table.RowIdentityKey is { Columns.Count: 1 } key)
        {
            var keyColumn = key.Columns[0];
            if (request.Values.TryGetValue(keyColumn, out var edit) && edit.Kind == EditValueKind.Value)
            {
                identity = new RowIdentity(key.Name, new Dictionary<string, DbValue> { [keyColumn] = edit.Value! });
            }
            else if (table.Columns.First(column => column.Name.Equals(keyColumn, StringComparison.OrdinalIgnoreCase)).IsAutoIncrement)
            {
                identity = new RowIdentity(key.Name, new Dictionary<string, DbValue>
                {
                    [keyColumn] = DbValue.FromSignedInteger(command.LastInsertedId),
                });
            }
        }

        return new RowMutationResult(affected == 1 ? RowMutationStatus.Succeeded : RowMutationStatus.Conflict,
            affected, identity, table.SchemaFingerprint);
    }

    public async Task<RowMutationResult> UpdateAsync(UpdateRowRequest request, CancellationToken cancellationToken)
    {
        var table = await ValidateMutationAsync(request.Table, request.ExpectedSchemaFingerprint, cancellationToken).ConfigureAwait(false);
        if (table.RowIdentityKey is null)
        {
            throw new RazorDbException(RazorDbErrorCode.Unsupported, "The table has no safe row identity.");
        }
        try
        {
            ValidateIdentity(table, request.Identity);
        }
        catch (ArgumentException exception)
        {
            throw new RazorDbException(
                RazorDbErrorCode.Validation,
                "The row identity does not match current metadata.",
                exception);
        }
        ValidateEditColumns(table, request.Values);
        ValidateOriginalColumns(table, request.OriginalValues);
        var changes = request.Values.Where(pair => pair.Value.Kind != EditValueKind.Omitted).ToArray();
        if (changes.Length == 0)
        {
            return new RowMutationResult(RowMutationStatus.Succeeded, 0, request.Identity, table.SchemaFingerprint, "No values changed.");
        }

        await using var dataSource = await credentials.CreateDataSourceAsync(MySqlCredentialSlot.Writer, cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var set = string.Join(", ", changes.Select(pair => $"{MySqlIdentifier.Quote(pair.Key)} = {EditSql(pair.Value, command)}"));
        var where = ConcurrencyWhere(request.Identity, request.OriginalValues, command);
        command.CommandText = $"UPDATE {MySqlIdentifier.Qualify(request.Table.Schema, request.Table.Name)} SET {set} WHERE {where} LIMIT 1";
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 1)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RowMutationResult(RowMutationStatus.Succeeded, 1, request.Identity, table.SchemaFingerprint);
        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new RowMutationResult(RowMutationStatus.Conflict, affected, request.Identity, table.SchemaFingerprint,
            "The row changed or no longer exists.");
    }

    public async Task<RowMutationResult> DeleteAsync(DeleteRowRequest request, CancellationToken cancellationToken)
    {
        var table = await ValidateMutationAsync(request.Table, request.ExpectedSchemaFingerprint, cancellationToken).ConfigureAwait(false);
        ValidateIdentity(table, request.Identity);
        ValidateOriginalColumns(table, request.OriginalValues);
        await using var dataSource = await credentials.CreateDataSourceAsync(MySqlCredentialSlot.Writer, cancellationToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {MySqlIdentifier.Qualify(request.Table.Schema, request.Table.Name)} WHERE {ConcurrencyWhere(request.Identity, request.OriginalValues, command)} LIMIT 1";
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 1)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RowMutationResult(RowMutationStatus.Succeeded, 1, null, table.SchemaFingerprint);
        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new RowMutationResult(RowMutationStatus.Conflict, affected, request.Identity, table.SchemaFingerprint,
            "The row changed or no longer exists.");
    }

    public async Task<BatchRowMutationResult> DeleteManyAsync(
        DeleteRowsRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Rows.Count is < 1 or > RazorDbBatchLimits.MaximumRows)
        {
            throw new RazorDbException(
                RazorDbErrorCode.LimitExceeded,
                $"A batch delete requires between 1 and {RazorDbBatchLimits.MaximumRows} rows.");
        }

        DbTableMetadata table = await ValidateMutationAsync(
            request.Table,
            request.ExpectedSchemaFingerprint,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(table.Engine, "InnoDB", StringComparison.OrdinalIgnoreCase))
        {
            throw new RazorDbException(
                RazorDbErrorCode.Unsupported,
                "Atomic batch deletion requires an InnoDB table.");
        }

        foreach (DeleteRowTarget row in request.Rows)
        {
            ValidateIdentity(table, row.Identity);
            ValidateOriginalColumns(table, row.OriginalValues);
        }

        await using MySqlDataSource dataSource = await credentials.CreateDataSourceAsync(
            MySqlCredentialSlot.Writer,
            cancellationToken).ConfigureAwait(false);
        await using MySqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var index = 0; index < request.Rows.Count; index++)
            {
                DeleteRowTarget row = request.Rows[index];
                await using MySqlCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM {MySqlIdentifier.Qualify(request.Table.Schema, request.Table.Name)} WHERE {ConcurrencyWhere(row.Identity, row.OriginalValues, command)} LIMIT 1";
                int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (affected != 1)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return new BatchRowMutationResult(
                        RowMutationStatus.Conflict,
                        request.Rows.Count,
                        0,
                        table.SchemaFingerprint,
                        index,
                        "A selected row changed or no longer exists. No rows were deleted.");
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BatchRowMutationResult(
                RowMutationStatus.Succeeded,
                request.Rows.Count,
                request.Rows.Count,
                table.SchemaFingerprint);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { }
            throw;
        }
    }

    public async Task<IRazorDbBinaryReadSession> OpenBinaryAsync(
        BinaryCellRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        guard.EnsureAllowed(request.Table.Schema);
        DbTableMetadata table = await metadata.GetTableAsync(
            request.Table,
            refresh: true,
            cancellationToken).ConfigureAwait(false);
        if (table.Kind != DbObjectKind.Table)
        {
            throw new RazorDbException(RazorDbErrorCode.Unsupported, "Binary downloads require a table with a safe row identity.");
        }

        ValidateIdentity(table, request.Identity);
        DbColumnMetadata column = table.Columns.FirstOrDefault(item =>
                item.Name.Equals(request.Column, StringComparison.OrdinalIgnoreCase))
            ?? throw new RazorDbException(RazorDbErrorCode.Validation, "The binary column does not exist in current metadata.");
        if (column.Type.Kind is not (DbDataKind.Binary or DbDataKind.Geometry))
        {
            throw new RazorDbException(RazorDbErrorCode.Validation, "The requested column is not binary or geometry data.");
        }

        Dictionary<string, DbValue> identityValues;
        try
        {
            identityValues = request.Identity.Values.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException exception)
        {
            throw new RazorDbException(RazorDbErrorCode.Validation, "The row identity contains duplicate columns.", exception);
        }

        MySqlDataSource? dataSource = null;
        MySqlConnection? connection = null;
        MySqlCommand? command = null;
        MySqlDataReader? reader = null;
        Stream? stream = null;
        try
        {
            dataSource = await credentials.CreateDataSourceAsync(MySqlCredentialSlot.Reader, cancellationToken).ConfigureAwait(false);
            connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            command = connection.CreateCommand();
            List<string> predicates = [];
            foreach (string keyColumn in table.RowIdentityKey!.Columns)
            {
                string parameter = $"@key{command.Parameters.Count}";
                command.Parameters.AddWithValue(parameter, MySqlValueConverter.ToParameter(identityValues[keyColumn]));
                predicates.Add($"{MySqlIdentifier.Quote(keyColumn)} = {parameter}");
            }

            string quotedColumn = MySqlIdentifier.Quote(column.Name);
            command.CommandText = $"SELECT OCTET_LENGTH({quotedColumn}), {quotedColumn} FROM {MySqlIdentifier.Qualify(table.Name.Schema, table.Name.Name)} WHERE {string.Join(" AND ", predicates)} LIMIT 1";
            reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleResult | CommandBehavior.SingleRow,
                cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new RazorDbException(RazorDbErrorCode.NotFound, "The row no longer exists.");
            }
            if (reader.IsDBNull(0))
            {
                throw new RazorDbException(RazorDbErrorCode.Validation, "The binary value is NULL.");
            }

            long length = reader.GetInt64(0);
            if (length < 0 || length > options.MaximumBinaryDownloadBytes)
            {
                throw new RazorDbException(
                    RazorDbErrorCode.LimitExceeded,
                    $"The binary value exceeds the configured {options.MaximumBinaryDownloadBytes} byte download limit.");
            }

            stream = reader.GetStream(1);
            BinaryCellDescriptor descriptor = new(
                length,
                "application/octet-stream",
                SafeFileName(table.Name.Name, column.Name, column.Type.Kind),
                column.Type.Kind);
            return new MySqlBinaryReadSession(
                descriptor,
                options.MaximumBinaryDownloadBytes,
                dataSource,
                connection,
                command,
                reader,
                stream);
        }
        catch
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            if (reader is not null) await reader.DisposeAsync().ConfigureAwait(false);
            if (command is not null) await command.DisposeAsync().ConfigureAwait(false);
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            if (dataSource is not null) await dataSource.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<DbTableMetadata> ValidateMutationAsync(DbObjectName name, string fingerprint, CancellationToken cancellationToken)
    {
        guard.EnsureAllowed(name.Schema);
        var table = await metadata.GetTableAsync(name, true, cancellationToken).ConfigureAwait(false);
        if (table.Kind != DbObjectKind.Table) throw new InvalidOperationException("Views cannot be edited.");
        if (!string.Equals(table.SchemaFingerprint, fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The table schema changed. Reload it before editing rows.");
        return table;
    }

    private static IReadOnlyList<DbSort> BuildEffectiveSorts(IReadOnlyList<DbSort>? requested, DbTableMetadata table)
    {
        var allowed = table.Columns.Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sorts = new List<DbSort>();
        foreach (var sort in requested ?? [])
        {
            if (!allowed.Contains(sort.Column)) throw new ArgumentException($"Unknown sort column '{sort.Column}'.", nameof(requested));
            if (sorts.All(value => !value.Column.Equals(sort.Column, StringComparison.OrdinalIgnoreCase))) sorts.Add(sort);
        }

        if (table.RowIdentityKey is not null)
        {
            foreach (var column in table.RowIdentityKey.Columns)
            {
                if (sorts.All(value => !value.Column.Equals(column, StringComparison.OrdinalIgnoreCase))) sorts.Add(new DbSort(column));
            }
        }
        else if (sorts.Count == 0)
        {
            sorts.AddRange(table.Columns.Select(column => new DbSort(column.Name)));
        }

        return sorts;
    }

    private static RowIdentity? CreateIdentity(DbTableMetadata table, IReadOnlyList<DbValue> values)
    {
        if (table.RowIdentityKey is null) return null;
        var ordinals = table.Columns.Select((column, index) => (column.Name, index))
            .ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        return new RowIdentity(table.RowIdentityKey.Name, table.RowIdentityKey.Columns
            .ToDictionary(column => column, column => values[ordinals[column]], StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<(long Count, DbCommandDiagnostic Diagnostic)> CountAsync(
        MySqlConnection connection,
        DbObjectName table,
        CompiledSql filter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {MySqlIdentifier.Qualify(table.Schema, table.Name)}{(filter.Text.Length > 0 ? $" WHERE {filter.Text}" : string.Empty)}";
        foreach (var parameter in filter.Parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        Stopwatch stopwatch = Stopwatch.StartNew();
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return (
            Convert.ToInt64(result, CultureInfo.InvariantCulture),
            CreateCommandDiagnostic(command, stopwatch.Elapsed));
    }

    internal static DbCommandDiagnostic CreateCommandDiagnostic(MySqlCommand command, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(command);
        DbCommandParameterDiagnostic[] parameters = command.Parameters
            .Cast<MySqlParameter>()
            .Select(parameter => new DbCommandParameterDiagnostic(
                parameter.ParameterName,
                parameter.MySqlDbType.ToString(),
                FormatParameterValue(parameter.Value)))
            .ToArray();
        return new DbCommandDiagnostic(command.CommandText, parameters, elapsed);
    }

    private static string FormatParameterValue(object? value)
    {
        if (value is null or DBNull) return "NULL";
        if (value is byte[] bytes) return $"<binary {bytes.LongLength.ToString(CultureInfo.InvariantCulture)} bytes>";
        if (value is ReadOnlyMemory<byte> memory) return $"<binary {memory.Length.ToString(CultureInfo.InvariantCulture)} bytes>";

        string text = value switch
        {
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
        text = text
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        const int maximumPreviewLength = 256;
        return text.Length <= maximumPreviewLength
            ? text
            : text[..maximumPreviewLength] + $"... ({text.Length.ToString(CultureInfo.InvariantCulture)} chars)";
    }

    private static void ValidateEditColumns(DbTableMetadata table, IReadOnlyDictionary<string, EditValue> values)
    {
        var columns = table.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            if (!columns.TryGetValue(pair.Key, out var column)) throw new ArgumentException($"Unknown column '{pair.Key}'.", nameof(values));
            if (column.IsGenerated && pair.Value.Kind != EditValueKind.Omitted) throw new ArgumentException($"Generated column '{pair.Key}' cannot be edited.", nameof(values));
        }
    }

    private static void ValidateOriginalColumns(DbTableMetadata table, IReadOnlyDictionary<string, DbValue> values)
    {
        var columns = table.Columns.Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (values.Keys.Any(column => !columns.Contains(column))) throw new ArgumentException("Original values contain an unknown column.", nameof(values));
    }

    private static void ValidateIdentity(DbTableMetadata table, RowIdentity identity)
    {
        var key = table.RowIdentityKey ?? throw new InvalidOperationException("The table has no safe row identity.");
        if (!key.Name.Equals(identity.KeyName, StringComparison.OrdinalIgnoreCase)
            || key.Columns.Count != identity.Values.Count
            || key.Columns.Any(column => !identity.Values.ContainsKey(column)))
            throw new ArgumentException("The supplied row identity does not match current metadata.", nameof(identity));
    }

    private static string EditSql(EditValue edit, MySqlCommand command) => edit.Kind switch
    {
        EditValueKind.Null => "NULL",
        EditValueKind.Default => "DEFAULT",
        EditValueKind.Value => Add(command, edit.Value!),
        _ => throw new ArgumentException("Omitted values cannot be compiled at this stage.", nameof(edit)),
    };

    private static string ConcurrencyWhere(RowIdentity identity, IReadOnlyDictionary<string, DbValue> original, MySqlCommand command)
    {
        var predicates = new List<string>();
        foreach (var pair in identity.Values) predicates.Add(ValuePredicate(pair.Key, pair.Value, command));
        foreach (var pair in original.Where(pair => !identity.Values.ContainsKey(pair.Key))) predicates.Add(ValuePredicate(pair.Key, pair.Value, command));
        return string.Join(" AND ", predicates);
    }

    private static string ValuePredicate(string column, DbValue value, MySqlCommand command) => value.IsNull
        ? $"{MySqlIdentifier.Quote(column)} IS NULL"
        : $"{MySqlIdentifier.Quote(column)} <=> {Add(command, value)}";

    private static string Add(MySqlCommand command, DbValue value)
    {
        var name = $"@p{command.Parameters.Count}";
        command.Parameters.AddWithValue(name, MySqlValueConverter.ToParameter(value));
        return name;
    }

    private static string SafeFileName(string table, string column, DbDataKind kind)
    {
        string stem = string.Concat($"{table}-{column}".Take(120).Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_'));
        return $"{(stem.Length == 0 ? "binary" : stem)}{(kind == DbDataKind.Geometry ? ".wkb" : ".bin")}";
    }

    private sealed class MySqlBinaryReadSession(
        BinaryCellDescriptor descriptor,
        long maximumBytes,
        MySqlDataSource dataSource,
        MySqlConnection connection,
        MySqlCommand command,
        MySqlDataReader reader,
        Stream stream) : IRazorDbBinaryReadSession
    {
        private int _consumed;
        private int _disposed;

        public BinaryCellDescriptor Descriptor { get; } = descriptor;

        public async ValueTask CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (Interlocked.Exchange(ref _consumed, 1) != 0)
            {
                throw new InvalidOperationException("The binary download session can only be consumed once.");
            }

            byte[] buffer = new byte[64 * 1024];
            long copied = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                copied += read;
                if (copied > maximumBytes || copied > Descriptor.Length)
                {
                    throw new RazorDbException(RazorDbErrorCode.LimitExceeded, "The binary value exceeded its validated download limit.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (copied != Descriptor.Length)
            {
                throw new RazorDbException(RazorDbErrorCode.Conflict, "The binary value changed while it was being downloaded.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await stream.DisposeAsync().ConfigureAwait(false);
            await reader.DisposeAsync().ConfigureAwait(false);
            await command.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            await dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }
}
