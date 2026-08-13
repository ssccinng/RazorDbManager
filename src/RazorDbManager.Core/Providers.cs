namespace RazorDbManager.Core;

/// <summary>Resolves immutable registrations and provider instances for the hosting integration.</summary>
public interface IRazorDbProviderRegistry
{
    /// <summary>Gets all registered logical databases.</summary>
    IReadOnlyCollection<DatabaseRegistration> Registrations { get; }

    /// <summary>Gets a registration or throws when it is unknown.</summary>
    /// <param name="databaseId">The stable registration identifier.</param>
    /// <returns>The registration.</returns>
    DatabaseRegistration GetRequiredRegistration(string databaseId);

    /// <summary>Creates or resolves the provider for a registration without exposing credentials to callers.</summary>
    /// <param name="databaseId">The stable registration identifier.</param>
    /// <param name="cancellationToken">Cancels asynchronous resolution.</param>
    /// <returns>The provider aggregation.</returns>
    ValueTask<IRazorDbProvider> GetProviderAsync(
        string databaseId,
        CancellationToken cancellationToken = default);
}

/// <summary>Aggregates provider-neutral database services for one immutable registration.</summary>
public interface IRazorDbProvider
{
    /// <summary>Gets the stable provider identifier.</summary>
    string ProviderName { get; }
    /// <summary>Gets the immutable logical database registration.</summary>
    DatabaseRegistration Registration { get; }
    /// <summary>Gets metadata operations.</summary>
    IRazorDbMetadataProvider Metadata { get; }
    /// <summary>Gets row query and mutation operations.</summary>
    IRazorDbDataProvider Data { get; }
    /// <summary>Gets structured schema operations.</summary>
    IRazorDbSchemaProvider Schema { get; }
    /// <summary>Gets arbitrary SQL-console operations.</summary>
    IRazorDbSqlProvider Sql { get; }
    /// <summary>Gets streamed import and export operations.</summary>
    IRazorDbTransferProvider Transfer { get; }
}

/// <summary>Reads provider metadata and capability probes.</summary>
public interface IRazorDbMetadataProvider
{
    /// <summary>Reads the visible database object tree and effective capabilities.</summary>
    /// <param name="request">The metadata request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Current or cached metadata.</returns>
    ValueTask<DatabaseMetadata> GetDatabaseAsync(
        MetadataRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads detailed table or view metadata.</summary>
    /// <param name="table">The schema-qualified object.</param>
    /// <param name="refresh">Whether to bypass a metadata cache.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Detailed metadata.</returns>
    ValueTask<DbTableMetadata> GetTableAsync(
        DbObjectName table,
        bool refresh = false,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads and safely mutates table rows.</summary>
public interface IRazorDbDataProvider
{
    /// <summary>Reads a bounded row page.</summary>
    ValueTask<RowPage> QueryRowsAsync(RowQueryRequest request, CancellationToken cancellationToken = default);
    /// <summary>Inserts exactly one row.</summary>
    ValueTask<RowMutationResult> InsertRowAsync(InsertRowRequest request, CancellationToken cancellationToken = default);
    /// <summary>Optimistically updates exactly one row.</summary>
    ValueTask<RowMutationResult> UpdateRowAsync(UpdateRowRequest request, CancellationToken cancellationToken = default);
    /// <summary>Optimistically deletes exactly one row.</summary>
    ValueTask<RowMutationResult> DeleteRowAsync(DeleteRowRequest request, CancellationToken cancellationToken = default);
    /// <summary>Atomically and optimistically deletes a bounded set of safely identified rows.</summary>
    ValueTask<BatchRowMutationResult> DeleteRowsAsync(
        DeleteRowsRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<BatchRowMutationResult>(
            new RazorDbException(RazorDbErrorCode.Unsupported, "The provider does not support batch row deletion."));

    /// <summary>Opens a bounded, streamed binary or geometry cell download.</summary>
    ValueTask<IRazorDbBinaryReadSession> OpenBinaryAsync(
        BinaryCellRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IRazorDbBinaryReadSession>(
            new RazorDbException(RazorDbErrorCode.Unsupported, "The provider does not support binary downloads."));
}

/// <summary>Previews and executes structured schema changes.</summary>
public interface IRazorDbSchemaProvider
{
    /// <summary>Generates SQL and a binding fingerprint without changing the database.</summary>
    ValueTask<DdlPreview> PreviewAsync(SchemaChangeRequest request, CancellationToken cancellationToken = default);
    /// <summary>Executes a confirmed preview after revalidating its fingerprints.</summary>
    ValueTask<DdlExecutionResult> ExecuteAsync(DdlExecutionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Executes bounded arbitrary SQL through the explicit high-risk credential.</summary>
public interface IRazorDbSqlProvider
{
    /// <summary>Executes a script and returns its ordered, bounded results.</summary>
    ValueTask<SqlExecutionResult> ExecuteAsync(SqlExecutionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Imports and exports database content through caller-owned streams.</summary>
public interface IRazorDbTransferProvider
{
    /// <summary>Imports CSV data or a provider SQL dump.</summary>
    ValueTask<TransferResult> ImportAsync(
        ImportRequest request,
        Stream source,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Exports CSV data or a provider SQL dump.</summary>
    ValueTask<TransferResult> ExportAsync(
        ExportRequest request,
        Stream destination,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
