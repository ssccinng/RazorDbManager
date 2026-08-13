using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;
using RazorDbManager.MySql.Infrastructure;

namespace RazorDbManager.MySql.Sql;

internal sealed class MySqlSqlService(
    MySqlProviderOptions options,
    MySqlCredentialSource credentials)
{
    public async Task<SqlExecutionResult> ExecuteAsync(SqlExecutionRequest request, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(request.Sql) > options.MaximumSqlTextBytes)
            throw new ArgumentException("SQL text exceeds the configured limit.", nameof(request));
        var timeout = request.Timeout ?? TimeSpan.FromSeconds(options.SqlCommandTimeoutSeconds);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(options.SqlCommandTimeoutSeconds))
            throw new ArgumentOutOfRangeException(nameof(request), "SQL timeout exceeds the configured limit.");
        if (request.MaximumRows is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "SQL row limit must be positive.");
        if (request.MaximumBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "SQL byte limit must be positive.");
        using var timeoutSource = CreateBatchTimeoutSource(timeout, cancellationToken);
        var batchToken = timeoutSource.Token;
        var maximumRows = Math.Min(request.MaximumRows ?? options.MaximumSqlRows, options.MaximumSqlRows);
        var maximumBytes = Math.Min(request.MaximumBytes ?? options.MaximumSqlResultBytes, options.MaximumSqlResultBytes);
        var statements = MySqlScriptTokenizer.Tokenize(request.Sql, options.MaximumSqlStatements);
        var results = new List<SqlStatementResult>();
        long totalBytes = 0;
        var totalRows = 0;
        var truncated = false;
        var stopwatch = Stopwatch.StartNew();
        await using var dataSource = await credentials.CreateDataSourceAsync(MySqlCredentialSlot.SqlConsole, batchToken).ConfigureAwait(false);
        await using var connection = await dataSource.OpenConnectionAsync(batchToken).ConfigureAwait(false);
        foreach (var statement in statements)
        {
            batchToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.CommandText = statement.Text;
            command.CommandTimeout = checked((int)Math.Ceiling(timeout.TotalSeconds));
            await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, batchToken).ConfigureAwait(false);
            do
            {
                if (reader.FieldCount == 0)
                {
                    results.Add(new SqlStatementResult(SqlStatementResultKind.AffectedRows, [], [], reader.RecordsAffected));
                    continue;
                }

                var columns = Enumerable.Range(0, reader.FieldCount).Select(index => new DbColumnMetadata(
                    reader.GetName(index), index,
                    new DbTypeDescriptor(reader.GetDataTypeName(index), GuessKind(reader.GetFieldType(index))),
                    true)).ToArray();
                var rows = new List<IReadOnlyList<DbValue>>();
                var resultTruncated = false;
                while (await reader.ReadAsync(batchToken).ConfigureAwait(false))
                {
                    if (totalRows >= maximumRows || totalBytes >= maximumBytes)
                    {
                        truncated = resultTruncated = true;
                        break;
                    }

                    var row = new DbValue[reader.FieldCount];
                    for (var index = 0; index < row.Length; index++)
                    {
                        var remainingBytes = maximumBytes - totalBytes;
                        if (remainingBytes <= 0)
                        {
                            truncated = resultTruncated = true;
                            break;
                        }

                        row[index] = MySqlValueConverter.Read(
                            reader,
                            index,
                            columns[index],
                            remainingBytes,
                            out var cellTruncated);
                        truncated |= cellTruncated;
                        resultTruncated |= cellTruncated;
                        totalBytes += MySqlValueConverter.EstimateSize(row[index]);
                        if (totalBytes > maximumBytes)
                        {
                            truncated = resultTruncated = true;
                            break;
                        }
                    }

                    if (truncated) break;
                    rows.Add(row);
                    totalRows++;
                }

                results.Add(new SqlStatementResult(SqlStatementResultKind.ResultSet, columns, rows, Truncated: resultTruncated));
            }
            while (!truncated && await reader.NextResultAsync(batchToken).ConfigureAwait(false));

            if (truncated) break;
        }

        stopwatch.Stop();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Sql)));
        return new SqlExecutionResult(results, stopwatch.Elapsed, truncated, hash);
    }

    internal static CancellationTokenSource CreateBatchTimeoutSource(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private static DbDataKind GuessKind(Type type) => type == typeof(byte[]) ? DbDataKind.Binary
        : type == typeof(bool) ? DbDataKind.Boolean
        : type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long) ? DbDataKind.SignedInteger
        : type == typeof(sbyte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong) ? DbDataKind.UnsignedInteger
        : type == typeof(decimal) ? DbDataKind.Decimal
        : type == typeof(float) || type == typeof(double) ? DbDataKind.FloatingPoint
        : type == typeof(DateTime) ? DbDataKind.DateTime
        : type == typeof(DateOnly) ? DbDataKind.Date
        : type == typeof(TimeOnly) || type == typeof(TimeSpan) ? DbDataKind.Time
        : DbDataKind.Text;
}
