namespace RazorDbManager.Core;

/// <summary>Lists supported import and export representations.</summary>
public enum TransferFormat
{
    /// <summary>Comma-separated values.</summary>
    Csv,
    /// <summary>A provider SQL dump or restore script.</summary>
    Sql,
}

/// <summary>Defines an optional structured row selection for a CSV export.</summary>
/// <param name="Filter">An optional metadata-validated filter.</param>
/// <param name="Sorts">Optional metadata-validated sorts. Providers append a stable identity sort.</param>
/// <param name="Columns">Optional projected column names. Null selects every exportable column.</param>
public sealed record RowExportQuery(
    FilterExpression? Filter = null,
    IReadOnlyList<DbSort>? Sorts = null,
    IReadOnlyList<string>? Columns = null);

/// <summary>Requests a streamed data import.</summary>
/// <param name="DatabaseId">The registration identifier.</param>
/// <param name="Format">The input representation.</param>
/// <param name="Table">The CSV target table. SQL restores may omit it.</param>
/// <param name="HasHeader">Whether the first CSV record contains column names.</param>
/// <param name="Delimiter">The CSV delimiter.</param>
/// <param name="NullToken">An optional exact CSV token mapped to database null.</param>
/// <param name="ContinueOnError">Whether valid records after a failed record should be attempted.</param>
/// <param name="DecodeProtectedValues">Whether to reverse RazorDbManager's exported CSV formula and NULL-token escaping.</param>
public sealed record ImportRequest(
    string DatabaseId,
    TransferFormat Format,
    DbObjectName? Table = null,
    bool HasHeader = true,
    char Delimiter = ',',
    string? NullToken = null,
    bool ContinueOnError = false,
    bool DecodeProtectedValues = false);

/// <summary>Requests a streamed data export.</summary>
/// <param name="DatabaseId">The registration identifier.</param>
/// <param name="Format">The output representation.</param>
/// <param name="Tables">Tables to export. An empty list denotes all allowed tables for SQL dumps.</param>
/// <param name="IncludeSchema">Whether SQL output includes object definitions.</param>
/// <param name="IncludeData">Whether row data is included.</param>
/// <param name="CompressWithGzip">Whether the provider should emit gzip-compressed output.</param>
/// <param name="MaximumRows">An optional limit no greater than the registration limit.</param>
/// <param name="MaximumBytes">An optional limit no greater than the registration limit.</param>
/// <param name="RowQuery">Optional structured CSV row selection. SQL dumps reject this value.</param>
public sealed record ExportRequest(
    string DatabaseId,
    TransferFormat Format,
    IReadOnlyList<DbObjectName> Tables,
    bool IncludeSchema = true,
    bool IncludeData = true,
    bool CompressWithGzip = false,
    long? MaximumRows = null,
    long? MaximumBytes = null,
    RowExportQuery? RowQuery = null);

/// <summary>Reports bounded progress for a transfer operation.</summary>
/// <param name="RowsProcessed">The number of records processed so far.</param>
/// <param name="BytesProcessed">The number of bytes processed so far.</param>
/// <param name="CurrentObject">The current object, when available.</param>
public sealed record TransferProgress(long RowsProcessed, long BytesProcessed, DbObjectName? CurrentObject = null);

/// <summary>Reports a sanitized failure tied to an input record or statement.</summary>
/// <param name="Position">The one-based record or statement position.</param>
/// <param name="Message">A sanitized, user-facing message.</param>
public sealed record TransferError(long Position, string Message);

/// <summary>Reports final transfer counts and partial failures.</summary>
/// <param name="RowsProcessed">Records successfully processed.</param>
/// <param name="BytesProcessed">Bytes read or written.</param>
/// <param name="Errors">Sanitized failures, capped by the provider.</param>
/// <param name="IsPartial">Whether some input was skipped or output was truncated.</param>
public sealed record TransferResult(
    long RowsProcessed,
    long BytesProcessed,
    IReadOnlyList<TransferError> Errors,
    bool IsPartial = false);

/// <summary>Isolates provider-specific SQL dump and restore implementations.</summary>
public interface ISqlDumpService
{
    /// <summary>Writes a provider-compatible SQL dump.</summary>
    /// <param name="request">The export request.</param>
    /// <param name="destination">The caller-owned output stream.</param>
    /// <param name="progress">Optional progress reporting.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The transfer result.</returns>
    ValueTask<TransferResult> ExportAsync(
        ExportRequest request,
        Stream destination,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Restores a provider-compatible SQL dump.</summary>
    /// <param name="request">The import request.</param>
    /// <param name="source">The caller-owned input stream.</param>
    /// <param name="progress">Optional progress reporting.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The transfer result.</returns>
    ValueTask<TransferResult> ImportAsync(
        ImportRequest request,
        Stream source,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
