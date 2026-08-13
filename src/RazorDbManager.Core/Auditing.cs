namespace RazorDbManager.Core;

/// <summary>Classifies the lifecycle event represented by an audit record.</summary>
public enum RazorDbAuditStatus
{
    /// <summary>An operation was accepted for execution.</summary>
    Started,
    /// <summary>An operation completed successfully.</summary>
    Completed,
    /// <summary>An operation failed.</summary>
    Failed,
    /// <summary>An operation was cancelled.</summary>
    Cancelled,
    /// <summary>An authorization or validation boundary denied an operation.</summary>
    Denied,
}

/// <summary>An append-only, non-sensitive record of a database operation lifecycle event.</summary>
public sealed record RazorDbAuditRecord
{
    /// <summary>Gets the unique record identifier.</summary>
    public required Guid Id { get; init; }
    /// <summary>Gets the identifier shared by all events for one attempted operation.</summary>
    public required Guid CorrelationId { get; init; }
    /// <summary>Gets the event time.</summary>
    public required DateTimeOffset Timestamp { get; init; }
    /// <summary>Gets the stable actor identifier.</summary>
    public required string ActorId { get; init; }
    /// <summary>Gets the database registration identifier.</summary>
    public required string DatabaseId { get; init; }
    /// <summary>Gets the operation classification.</summary>
    public required RazorDbOperation Operation { get; init; }
    /// <summary>Gets the operation lifecycle status.</summary>
    public required RazorDbAuditStatus Status { get; init; }
    /// <summary>Gets the optional resource target.</summary>
    public RazorDbResource? Resource { get; init; }
    /// <summary>Gets the SHA-256 of SQL or another sensitive payload, never the payload itself.</summary>
    public string? PayloadHash { get; init; }
    /// <summary>Gets a stable, non-sensitive statement classification.</summary>
    public string? SqlClassification { get; init; }
    /// <summary>Gets a stable, non-sensitive outcome or failure code.</summary>
    public string? ResultCode { get; init; }
    /// <summary>Gets elapsed time for a terminal event.</summary>
    public TimeSpan? Duration { get; init; }
    /// <summary>Gets additional non-sensitive scalar metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Persists append-only audit events.</summary>
public interface IRazorDbAuditSink
{
    /// <summary>Appends one event. Sensitive operations must fail closed if this operation fails.</summary>
    ValueTask AppendAsync(RazorDbAuditRecord record, CancellationToken cancellationToken = default);
}
