namespace RazorDbManager.Core;

/// <summary>Base type for the structured, parameterized filter expression tree.</summary>
public abstract record FilterExpression;

/// <summary>Specifies a scalar comparison.</summary>
public enum DbComparisonOperator
{
    /// <summary>Equal.</summary>
    Equal,
    /// <summary>Not equal.</summary>
    NotEqual,
    /// <summary>Less than.</summary>
    LessThan,
    /// <summary>Less than or equal.</summary>
    LessThanOrEqual,
    /// <summary>Greater than.</summary>
    GreaterThan,
    /// <summary>Greater than or equal.</summary>
    GreaterThanOrEqual,
    /// <summary>Contains text.</summary>
    Contains,
    /// <summary>Starts with text.</summary>
    StartsWith,
    /// <summary>Ends with text.</summary>
    EndsWith,
}

/// <summary>Compares a metadata-validated column with a parameter value.</summary>
/// <param name="Column">The column name.</param>
/// <param name="Operator">The comparison operator.</param>
/// <param name="Value">The parameter value.</param>
public sealed record ComparisonFilter(string Column, DbComparisonOperator Operator, DbValue Value) : FilterExpression;

/// <summary>Tests a metadata-validated column for null or non-null.</summary>
/// <param name="Column">The column name.</param>
/// <param name="IsNull">Whether to test for null rather than non-null.</param>
public sealed record NullFilter(string Column, bool IsNull = true) : FilterExpression;

/// <summary>Tests membership in a parameterized value list.</summary>
/// <param name="Column">The column name.</param>
/// <param name="Values">The values, which must not be empty.</param>
/// <param name="Negated">Whether to negate the test.</param>
public sealed record InFilter(string Column, IReadOnlyList<DbValue> Values, bool Negated = false) : FilterExpression;

/// <summary>Specifies how child filter expressions are combined.</summary>
public enum DbLogicalOperator
{
    /// <summary>Every term must match.</summary>
    And,
    /// <summary>At least one term must match.</summary>
    Or,
}

/// <summary>Combines multiple filters without accepting raw SQL.</summary>
/// <param name="Operator">The logical operator.</param>
/// <param name="Terms">Two or more child expressions.</param>
public sealed record LogicalFilter(DbLogicalOperator Operator, IReadOnlyList<FilterExpression> Terms) : FilterExpression;

/// <summary>Specifies a sort direction.</summary>
public enum DbSortDirection
{
    /// <summary>Ascending.</summary>
    Ascending,
    /// <summary>Descending.</summary>
    Descending,
}

/// <summary>Defines a metadata-validated sort column.</summary>
/// <param name="Column">The column name.</param>
/// <param name="Direction">The direction.</param>
public sealed record DbSort(string Column, DbSortDirection Direction = DbSortDirection.Ascending);

/// <summary>Contains ordered values used to resume stable keyset pagination.</summary>
/// <param name="Values">Values corresponding to the effective sort columns.</param>
public sealed record RowCursor(IReadOnlyList<DbValue> Values);

/// <summary>Requests offset, keyset, or keyset-relative-offset pagination.</summary>
public sealed record PageRequest
{
    private PageRequest(int pageSize, long? offset, RowCursor? after)
    {
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (offset is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        PageSize = pageSize;
        Offset = offset;
        After = after;
    }

    /// <summary>Gets the requested maximum row count.</summary>
    public int PageSize { get; }
    /// <summary>
    /// Gets the zero-based offset. When <see cref="After"/> is present, the offset is relative to
    /// the rows strictly after that cursor; otherwise it is absolute within the filtered result.
    /// </summary>
    public long? Offset { get; }
    /// <summary>Gets the exclusive cursor when keyset pagination is selected.</summary>
    public RowCursor? After { get; }

    /// <summary>Creates an offset-based page request.</summary>
    /// <param name="pageSize">The maximum row count.</param>
    /// <param name="offset">The zero-based offset.</param>
    /// <returns>A page request.</returns>
    public static PageRequest FromOffset(int pageSize, long offset = 0) => new(pageSize, offset, null);

    /// <summary>Creates a keyset page request.</summary>
    /// <param name="pageSize">The maximum row count.</param>
    /// <param name="after">The exclusive cursor.</param>
    /// <returns>A page request.</returns>
    public static PageRequest FromCursor(int pageSize, RowCursor after)
    {
        ArgumentNullException.ThrowIfNull(after);
        return new PageRequest(pageSize, null, after);
    }

    /// <summary>Creates a page request at an exact offset relative to a keyset cursor.</summary>
    /// <param name="pageSize">The maximum row count.</param>
    /// <param name="after">The exclusive cursor that anchors the offset.</param>
    /// <param name="relativeOffset">The zero-based offset among rows strictly after <paramref name="after"/>.</param>
    /// <returns>A page request.</returns>
    public static PageRequest FromCursor(int pageSize, RowCursor after, long relativeOffset)
    {
        ArgumentNullException.ThrowIfNull(after);
        return new PageRequest(pageSize, relativeOffset, after);
    }
}

/// <summary>Requests a filtered, ordered page from one table or view.</summary>
/// <param name="DatabaseId">The registration identifier.</param>
/// <param name="Table">The source object.</param>
/// <param name="Page">The page specification.</param>
/// <param name="Filter">An optional structured filter.</param>
/// <param name="Sorts">Caller-requested sorts. Providers append a stable identity sort when necessary.</param>
/// <param name="IncludeTotalCount">Whether to compute an exact filtered row count.</param>
public sealed record RowQueryRequest(
    string DatabaseId,
    DbObjectName Table,
    PageRequest Page,
    FilterExpression? Filter = null,
    IReadOnlyList<DbSort>? Sorts = null,
    bool IncludeTotalCount = false);

/// <summary>Identifies exactly one mutable row using a primary or safe unique key.</summary>
/// <param name="KeyName">The metadata key name.</param>
/// <param name="Values">Key values by column name.</param>
public sealed record RowIdentity(string KeyName, IReadOnlyDictionary<string, DbValue> Values);

/// <summary>Contains values returned for one row and its optional safe identity.</summary>
/// <param name="Values">Values in the same order as the page columns.</param>
/// <param name="Identity">The safe row identity, or null when updates and deletes are forbidden.</param>
public sealed record DbRow(IReadOnlyList<DbValue> Values, RowIdentity? Identity)
{
    /// <summary>
    /// Gets the safe identity available for binary downloads when the editable row snapshot is incomplete.
    /// Null means <see cref="Identity"/> is used.
    /// </summary>
    public RowIdentity? BinaryIdentity { get; init; }

    /// <summary>Gets the identity that may safely locate a binary cell.</summary>
    public RowIdentity? EffectiveBinaryIdentity => BinaryIdentity ?? Identity;
}

/// <summary>Describes one parameter from a database command that was actually executed.</summary>
/// <param name="Name">The provider parameter name.</param>
/// <param name="DatabaseType">The provider database type selected for execution.</param>
/// <param name="ValuePreview">A bounded display value. Binary values are never included verbatim.</param>
public sealed record DbCommandParameterDiagnostic(
    string Name,
    string DatabaseType,
    string ValuePreview);

/// <summary>Describes a parameterized database command after it was executed.</summary>
/// <param name="CommandText">The exact parameterized command text sent through the provider.</param>
/// <param name="Parameters">The parameters attached to the command at execution time.</param>
/// <param name="Elapsed">Elapsed provider execution and result-consumption time.</param>
public sealed record DbCommandDiagnostic(
    string CommandText,
    IReadOnlyList<DbCommandParameterDiagnostic> Parameters,
    TimeSpan Elapsed);

/// <summary>Contains a bounded page of rows.</summary>
/// <param name="Columns">Ordered result columns.</param>
/// <param name="Rows">Returned rows.</param>
/// <param name="TotalCount">The exact filtered count when requested and available.</param>
/// <param name="NextCursor">The keyset cursor for the next page, when available.</param>
/// <param name="NextOffset">
/// The exact offset for the next page when keyset continuation is unavailable. It is relative to
/// the current request's cursor anchor, or absolute when that request has no cursor.
/// </param>
/// <param name="HasMore">Whether another page is known to exist.</param>
/// <param name="SchemaFingerprint">The schema fingerprint used to construct the result.</param>
/// <param name="Truncated">Whether a byte or row limit shortened the result.</param>
public sealed record RowPage(
    IReadOnlyList<DbColumnMetadata> Columns,
    IReadOnlyList<DbRow> Rows,
    long? TotalCount,
    RowCursor? NextCursor,
    bool HasMore,
    string SchemaFingerprint,
    bool Truncated = false,
    long? NextOffset = null)
{
    /// <summary>
    /// Gets the parameterized commands that the provider actually executed to produce this page.
    /// Parameter values are bounded for display and must not be persisted as audit metadata.
    /// </summary>
    public IReadOnlyList<DbCommandDiagnostic> Commands { get; init; } = [];
}
