namespace RazorDbManager.Core;

/// <summary>Requests arbitrary SQL execution through the separately authorized console.</summary>
/// <param name="DatabaseId">The registration identifier.</param>
/// <param name="Sql">The SQL script. Only this explicit high-risk contract accepts raw SQL.</param>
/// <param name="Timeout">An optional timeout no greater than the registration limit.</param>
/// <param name="MaximumRows">An optional row limit no greater than the registration limit.</param>
/// <param name="MaximumBytes">An optional result-size limit no greater than the registration limit.</param>
public sealed record SqlExecutionRequest(
    string DatabaseId,
    string Sql,
    TimeSpan? Timeout = null,
    int? MaximumRows = null,
    long? MaximumBytes = null);

/// <summary>Classifies one SQL statement result.</summary>
public enum SqlStatementResultKind
{
    /// <summary>A tabular result set.</summary>
    ResultSet,
    /// <summary>A non-query affected rows.</summary>
    AffectedRows,
    /// <summary>An informational provider message.</summary>
    Message,
}

/// <summary>Contains the bounded output from one SQL statement.</summary>
/// <param name="Kind">The output kind.</param>
/// <param name="Columns">Ordered result columns.</param>
/// <param name="Rows">Result rows without edit identities.</param>
/// <param name="AffectedRows">The affected row count for a non-query.</param>
/// <param name="Message">A safe provider message.</param>
/// <param name="Truncated">Whether a row or byte limit shortened this statement's result.</param>
public sealed record SqlStatementResult(
    SqlStatementResultKind Kind,
    IReadOnlyList<DbColumnMetadata> Columns,
    IReadOnlyList<IReadOnlyList<DbValue>> Rows,
    long? AffectedRows = null,
    string? Message = null,
    bool Truncated = false);

/// <summary>Contains ordered results from a SQL script.</summary>
/// <param name="Results">Statement and result-set outputs in provider order.</param>
/// <param name="Elapsed">Total elapsed execution time.</param>
/// <param name="Truncated">Whether any bounded output was truncated.</param>
/// <param name="SqlHash">The SHA-256 digest used for auditing without retaining SQL text.</param>
public sealed record SqlExecutionResult(
    IReadOnlyList<SqlStatementResult> Results,
    TimeSpan Elapsed,
    bool Truncated,
    string SqlHash);
