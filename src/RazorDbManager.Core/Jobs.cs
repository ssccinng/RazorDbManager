namespace RazorDbManager.Core;

/// <summary>Classifies a long-running database job.</summary>
public enum RazorDbJobKind
{
    /// <summary>A CSV import.</summary>
    CsvImport,
    /// <summary>A CSV export.</summary>
    CsvExport,
    /// <summary>A SQL restore.</summary>
    SqlRestore,
    /// <summary>A SQL dump.</summary>
    SqlDump,
}

/// <summary>Classifies persisted job state.</summary>
public enum RazorDbJobStatus
{
    /// <summary>Waiting for a worker.</summary>
    Queued,
    /// <summary>Currently executing.</summary>
    Running,
    /// <summary>Completed successfully.</summary>
    Completed,
    /// <summary>Failed with a sanitized error.</summary>
    Failed,
    /// <summary>Cancelled by an actor or host shutdown.</summary>
    Cancelled,
}

/// <summary>Requests creation of a queued long-running job.</summary>
/// <param name="DatabaseId">The target registration.</param>
/// <param name="ActorId">The actor that submitted the job.</param>
/// <param name="Kind">The job kind.</param>
/// <param name="InputArtifactId">An optional input artifact.</param>
/// <param name="Parameters">Serialized parameters. Sensitive values must be protected before persistence and removed at terminal transition.</param>
public sealed record RazorDbJobCreateRequest(
    string DatabaseId,
    string ActorId,
    RazorDbJobKind Kind,
    string? InputArtifactId = null,
    IReadOnlyDictionary<string, string>? Parameters = null);

/// <summary>Represents one durable long-running job.</summary>
public sealed record RazorDbJobRecord
{
    /// <summary>Gets the job identifier.</summary>
    public required Guid Id { get; init; }
    /// <summary>Gets the target registration.</summary>
    public required string DatabaseId { get; init; }
    /// <summary>Gets the submitting actor identifier.</summary>
    public required string ActorId { get; init; }
    /// <summary>Gets the job kind.</summary>
    public required RazorDbJobKind Kind { get; init; }
    /// <summary>Gets current persisted state.</summary>
    public required RazorDbJobStatus Status { get; init; }
    /// <summary>Gets whether the owner requested cancellation while the worker still owns the terminal transition.</summary>
    public bool CancellationRequested { get; init; }
    /// <summary>Gets the creation time.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>Gets the last update time.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Gets processed row count.</summary>
    public long RowsProcessed { get; init; }
    /// <summary>Gets processed byte count.</summary>
    public long BytesProcessed { get; init; }
    /// <summary>Gets an optional input artifact identifier.</summary>
    public string? InputArtifactId { get; init; }
    /// <summary>Gets an optional output artifact identifier.</summary>
    public string? OutputArtifactId { get; init; }
    /// <summary>Gets an optional stable, sanitized result code.</summary>
    public string? ResultCode { get; init; }
    /// <summary>Gets serialized parameters. Sensitive values are stored only in protected form while the job is active.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    /// <summary>Gets the optimistic-concurrency version.</summary>
    public long Version { get; init; }
}

/// <summary>Contains values a worker may atomically update.</summary>
public sealed record RazorDbJobUpdate
{
    /// <summary>Gets the new state.</summary>
    public required RazorDbJobStatus Status { get; init; }
    /// <summary>Gets processed row count.</summary>
    public long RowsProcessed { get; init; }
    /// <summary>Gets processed byte count.</summary>
    public long BytesProcessed { get; init; }
    /// <summary>Gets an optional output artifact identifier.</summary>
    public string? OutputArtifactId { get; init; }
    /// <summary>Gets an optional stable, sanitized result code.</summary>
    public string? ResultCode { get; init; }
    /// <summary>Gets an optional cancellation-request update. Null preserves the current value.</summary>
    public bool? CancellationRequested { get; init; }
    /// <summary>Gets optional replacement parameters. Null preserves the persisted parameters.</summary>
    /// <remarks>Workers use this to remove protected, no-longer-needed inputs in the same optimistic update that writes a terminal state.</remarks>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}

/// <summary>Filters a bounded job listing.</summary>
/// <param name="DatabaseId">An optional registration identifier.</param>
/// <param name="ActorId">An optional submitting actor identifier.</param>
/// <param name="Status">An optional status.</param>
/// <param name="Limit">The maximum number of newest records.</param>
public sealed record RazorDbJobQuery(
    string? DatabaseId = null,
    string? ActorId = null,
    RazorDbJobStatus? Status = null,
    int Limit = 100);

/// <summary>Persists long-running jobs with optimistic concurrency.</summary>
public interface IRazorDbJobStore
{
    /// <summary>Creates a queued job.</summary>
    ValueTask<RazorDbJobRecord> CreateAsync(
        RazorDbJobCreateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a job or null when it does not exist.</summary>
    ValueTask<RazorDbJobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lists the newest jobs matching a bounded query.</summary>
    ValueTask<IReadOnlyList<RazorDbJobRecord>> ListAsync(
        RazorDbJobQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically updates a non-terminal job if its version matches.</summary>
    /// <remarks>Completed, failed, and cancelled records are immutable.</remarks>
    ValueTask<RazorDbJobRecord?> TryUpdateAsync(
        Guid id,
        long expectedVersion,
        RazorDbJobUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically records cancellation of an owned queued or running job without writing a terminal state.</summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="actorId">The stable owner identifier.</param>
    /// <param name="cancellationToken">Cancels the store operation.</param>
    /// <returns>The updated record, or null when the job is missing, not owned, terminal, or already requested.</returns>
    ValueTask<RazorDbJobRecord?> RequestCancellationAsync(
        Guid id,
        string actorId,
        CancellationToken cancellationToken = default);
}
