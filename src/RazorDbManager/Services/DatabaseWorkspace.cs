using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorDbManager.Core;

namespace RazorDbManager;

internal sealed record WorkspaceDatabase(
    DatabaseMetadata Metadata,
    string DisplayName,
    RazorDbResourceLimits Limits,
    int? PreferredPageSize);

internal interface IRazorDbAuditReader
{
    ValueTask<IReadOnlyList<RazorDbAuditRecord>> ListAsync(string databaseId, string actorId, int limit, CancellationToken cancellationToken);
}

internal sealed class DatabaseWorkspace(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    IRazorDbProviderRegistry registry,
    IRazorDbManagerAuthorizer resourceAuthorizer,
    IRazorDbSessionValidator sessionValidator,
    IRazorDbAuditSink auditSink,
    IRazorDbJobStore jobStore,
    IRazorDbOperationTokenStore operationTokens,
    IRazorDbAuditReader auditReader,
    IRazorDbPreferenceStore preferenceStore,
    RazorDbTransferAdmissionCoordinator transferAdmission,
    RowExportQueryProtector rowExportQueryProtector,
    IOptions<RazorDbManagerOptions> options,
    ILogger<DatabaseWorkspace> logger)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SqlGates = new(StringComparer.Ordinal);
    private readonly RazorDbManagerOptions _options = options.Value;

    public async ValueTask<WorkspaceDatabase> LoadDatabaseAsync(string databaseId, bool refresh, CancellationToken cancellationToken)
    {
        DatabaseRegistration registration = registry.GetRequiredRegistration(databaseId);
        RazorDbActor actor = await AuthorizeAsync(registration, RazorDbOperation.BrowseMetadata, RazorDbCapability.BrowseMetadata, null, false, cancellationToken);
        IRazorDbProvider provider = await registry.GetProviderAsync(databaseId, cancellationToken);
        DatabaseMetadata metadata = await provider.Metadata.GetDatabaseAsync(new MetadataRequest(databaseId, refresh), cancellationToken);
        RazorDbResourceLimits limits = registration.ResourceLimits ?? _options.ResourceLimits;
        string? storedPageSize = await preferenceStore.GetAsync(actor.Id, registration.Id, "page-size", cancellationToken);
        int? preferredPageSize = int.TryParse(storedPageSize, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? Math.Clamp(parsed, 1, limits.MaximumPageSize)
            : null;
        return new WorkspaceDatabase(metadata, registration.DisplayName ?? registration.Id, limits, preferredPageSize);
    }

    public async ValueTask<DbTableMetadata> LoadTableAsync(string databaseId, DbObjectName table, bool refresh, CancellationToken cancellationToken)
    {
        DatabaseRegistration registration = registry.GetRequiredRegistration(databaseId);
        await AuthorizeAsync(registration, RazorDbOperation.BrowseMetadata, RazorDbCapability.BrowseMetadata, RazorDbResource.FromObject(table), false, cancellationToken);
        IRazorDbProvider provider = await registry.GetProviderAsync(databaseId, cancellationToken);
        return await provider.Metadata.GetTableAsync(table, refresh, cancellationToken);
    }

    public async ValueTask<RowPage> QueryRowsAsync(string databaseId, DbObjectName table, int pageSize, long offset, FilterExpression? filter, CancellationToken cancellationToken)
        => await QueryRowsAsync(
            new RowQueryRequest(databaseId, table, PageRequest.FromOffset(pageSize, offset), filter),
            cancellationToken);

    public async ValueTask<RowPage> QueryRowsAsync(RowQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DatabaseRegistration registration = registry.GetRequiredRegistration(request.DatabaseId);
        RazorDbResourceLimits limits = registration.ResourceLimits ?? _options.ResourceLimits;
        int pageSize = Math.Clamp(request.Page.PageSize, 1, limits.MaximumPageSize);
        PageRequest page = request.Page.After is not null
            ? request.Page.Offset is long relativeOffset
                ? PageRequest.FromCursor(pageSize, request.Page.After, relativeOffset)
                : PageRequest.FromCursor(pageSize, request.Page.After)
            : PageRequest.FromOffset(pageSize, request.Page.Offset ?? 0);
        await AuthorizeAsync(
            registration,
            RazorDbOperation.ReadRows,
            RazorDbCapability.ReadRows,
            RazorDbResource.FromObject(request.Table),
            false,
            cancellationToken);
        IRazorDbProvider provider = await registry.GetProviderAsync(request.DatabaseId, cancellationToken);
        return await provider.Data.QueryRowsAsync(request with { Page = page }, cancellationToken);
    }

    public ValueTask<RowMutationResult> InsertRowAsync(string databaseId, DbTableMetadata table, IReadOnlyDictionary<string, EditValue> values, CancellationToken cancellationToken) =>
        AuditedMutationAsync(databaseId, table.Name, RazorDbOperation.InsertRow, RazorDbCapability.InsertRows,
            (provider, token) => provider.Data.InsertRowAsync(new InsertRowRequest(databaseId, table.Name, values, table.SchemaFingerprint), token), cancellationToken);

    public ValueTask<RowMutationResult> UpdateRowAsync(string databaseId, DbTableMetadata table, DbRow row, IReadOnlyDictionary<string, EditValue> values, CancellationToken cancellationToken)
    {
        if (row.Identity is null) throw new RazorDbException(RazorDbErrorCode.Validation, "This row has no safe identity and cannot be updated.");
        IReadOnlyDictionary<string, DbValue> originals = OriginalValues(table, row);
        return AuditedMutationAsync(databaseId, table.Name, RazorDbOperation.UpdateRow, RazorDbCapability.UpdateRows,
            (provider, token) => provider.Data.UpdateRowAsync(new UpdateRowRequest(databaseId, table.Name, row.Identity, originals, values, table.SchemaFingerprint), token), cancellationToken);
    }

    public ValueTask<RowMutationResult> DeleteRowAsync(string databaseId, DbTableMetadata table, DbRow row, CancellationToken cancellationToken)
    {
        if (row.Identity is null) throw new RazorDbException(RazorDbErrorCode.Validation, "This row has no safe identity and cannot be deleted.");
        IReadOnlyDictionary<string, DbValue> originals = OriginalValues(table, row);
        return AuditedMutationAsync(databaseId, table.Name, RazorDbOperation.DeleteRow, RazorDbCapability.DeleteRows,
            (provider, token) => provider.Data.DeleteRowAsync(new DeleteRowRequest(databaseId, table.Name, row.Identity, originals, table.SchemaFingerprint), token), cancellationToken);
    }

    public async ValueTask<BatchRowMutationResult> DeleteRowsAsync(
        string databaseId,
        DbTableMetadata table,
        IReadOnlyList<DbRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count is < 1 or > RazorDbBatchLimits.MaximumRows)
        {
            throw new RazorDbException(
                RazorDbErrorCode.LimitExceeded,
                $"Select between 1 and {RazorDbBatchLimits.MaximumRows} rows for a batch delete.");
        }

        DeleteRowTarget[] targets = rows.Select(row =>
        {
            if (row.Identity is null)
            {
                throw new RazorDbException(
                    RazorDbErrorCode.Validation,
                    "Every selected row must have a safe identity.");
            }

            return new DeleteRowTarget(row.Identity, OriginalValues(table, row));
        }).ToArray();
        DatabaseRegistration registration = registry.GetRequiredRegistration(databaseId);
        RazorDbResource resource = RazorDbResource.FromObject(table.Name);
        RazorDbActor actor = await AuthorizeAsync(
            registration,
            RazorDbOperation.DeleteRows,
            RazorDbCapability.DeleteRows,
            resource,
            false,
            cancellationToken);
        Guid correlationId = Guid.NewGuid();
        Stopwatch stopwatch = Stopwatch.StartNew();
        await AppendAuditAsync(
            actor,
            databaseId,
            RazorDbOperation.DeleteRows,
            RazorDbAuditStatus.Started,
            correlationId,
            resource,
            null,
            null,
            cancellationToken,
            $"requested:{targets.Length}");
        try
        {
            IRazorDbProvider provider = await registry.GetProviderAsync(databaseId, cancellationToken);
            BatchRowMutationResult result = await provider.Data.DeleteRowsAsync(
                new DeleteRowsRequest(databaseId, table.Name, targets, table.SchemaFingerprint),
                cancellationToken);
            RazorDbAuditStatus status = result.Status == RowMutationStatus.Succeeded
                ? RazorDbAuditStatus.Completed
                : RazorDbAuditStatus.Failed;
            await AppendOutcomeAuditAsync(
                actor,
                databaseId,
                RazorDbOperation.DeleteRows,
                status,
                correlationId,
                resource,
                stopwatch.Elapsed,
                null,
                cancellationToken,
                $"{result.Status}:{result.AffectedRows}/{result.RequestedRows}");
            return result;
        }
        catch (OperationCanceledException)
        {
            await AppendOutcomeAuditAsync(actor, databaseId, RazorDbOperation.DeleteRows, RazorDbAuditStatus.Cancelled,
                correlationId, resource, stopwatch.Elapsed, null, CancellationToken.None);
            throw;
        }
        catch
        {
            await AppendOutcomeAuditAsync(actor, databaseId, RazorDbOperation.DeleteRows, RazorDbAuditStatus.Failed,
                correlationId, resource, stopwatch.Elapsed, null, CancellationToken.None);
            throw;
        }
    }

    public async ValueTask<SqlExecutionResult> ExecuteSqlAsync(string databaseId, string sql, CancellationToken cancellationToken)
    {
        DatabaseRegistration registration = registry.GetRequiredRegistration(databaseId);
        RazorDbResourceLimits limits = registration.ResourceLimits ?? _options.ResourceLimits;
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > limits.MaximumSqlCharacters)
            throw new RazorDbException(RazorDbErrorCode.LimitExceeded, "The SQL text is empty or exceeds the configured limit.");

        RazorDbActor actor = await AuthorizeAsync(registration, RazorDbOperation.ExecuteSql, RazorDbCapability.ExecuteSql, null, true, cancellationToken);
        string gateKey = $"{registration.Id}\n{actor.Id}";
        SemaphoreSlim gate = SqlGates.GetOrAdd(gateKey, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero, cancellationToken))
            throw new RazorDbException(RazorDbErrorCode.LimitExceeded, "Only one SQL command may run at a time for this user and database.");
        Guid correlationId = Guid.NewGuid();
        Stopwatch stopwatch = Stopwatch.StartNew();
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sql)));
        string classification = ClassifySql(sql);
        try
        {
            await AppendAuditAsync(actor, registration.Id, RazorDbOperation.ExecuteSql, RazorDbAuditStatus.Started, correlationId, null, null, hash, cancellationToken, sqlClassification: classification);
            IRazorDbProvider provider = await registry.GetProviderAsync(databaseId, cancellationToken);
            SqlExecutionResult result = await provider.Sql.ExecuteAsync(new SqlExecutionRequest(databaseId, sql, limits.SqlTimeout, limits.MaximumSqlRows, limits.MaximumSqlResultBytes), cancellationToken);
            await AppendOutcomeAuditAsync(actor, registration.Id, RazorDbOperation.ExecuteSql, RazorDbAuditStatus.Completed, correlationId, null, stopwatch.Elapsed, result.SqlHash, cancellationToken, sqlClassification: classification);
            return result;
        }
        catch (OperationCanceledException)
        {
            await AppendOutcomeAuditAsync(actor, registration.Id, RazorDbOperation.ExecuteSql, RazorDbAuditStatus.Cancelled, correlationId, null, stopwatch.Elapsed, hash, CancellationToken.None, sqlClassification: classification);
            throw;
        }
        catch
        {
            await AppendOutcomeAuditAsync(actor, registration.Id, RazorDbOperation.ExecuteSql, RazorDbAuditStatus.Failed, correlationId, null, stopwatch.Elapsed, hash, CancellationToken.None, sqlClassification: classification);
            throw;
        }
        finally { gate.Release(); }
    }

    public async ValueTask<WorkspaceDdlPreview> PreviewSchemaAsync(string databaseId, SchemaChange change, CancellationToken cancellationToken)
    {
        DatabaseRegistration registration = registry.GetRequiredRegistration(databaseId);
        IReadOnlyList<RazorDbResource> resources = SchemaResources(change);
        RazorDbResource resource = resources[0];
        RazorDbCapability required = RazorDbCapability.ModifySchema | (change.IsDestructive ? RazorDbCapability.DestructiveSchema : RazorDbCapability.None);
        RazorDbActor actor = await AuthorizeSchemaResourcesAsync(registration, RazorDbOperation.PreviewSchema, required, resources, false, cancellationToken);
        IRazorDbProvider provider = await registry.GetProviderAsync(databaseId, cancellationToken);
        DdlPreview preview = await provider.Schema.PreviewAsync(new SchemaChangeRequest(databaseId, change), cancellationToken);
        if (!registration.EnabledCapabilities.Includes(preview.RequiredCapability))
            throw new RazorDbException(RazorDbErrorCode.Forbidden, "The preview requires a capability that is not enabled.");
        string confirmationHash = SchemaConfirmationHash(preview.SqlHash, resources);
        RazorDbOperationTokenContext context = new(actor.Id, databaseId, RazorDbOperation.ExecuteSchema, resource, preview.SchemaFingerprint, confirmationHash);
        RazorDbOperationToken token = await operationTokens.IssueAsync(context, _options.OperationTokenLifetime, cancellationToken);
        return new WorkspaceDdlPreview(preview, token);
    }

    public async ValueTask<DdlExecutionResult> ExecuteSchemaAsync(string databaseId, SchemaChange change, WorkspaceDdlPreview confirmed, CancellationToken cancellationToken)
    {
        DatabaseRegistration registration = registry.GetRequiredRegistration(databaseId);
        IReadOnlyList<RazorDbResource> resources = SchemaResources(change);
        RazorDbResource resource = resources[0];
        RazorDbActor actor = await AuthorizeSchemaResourcesAsync(registration, RazorDbOperation.ExecuteSchema, confirmed.Preview.RequiredCapability, resources, true, cancellationToken);
        string confirmationHash = SchemaConfirmationHash(confirmed.Preview.SqlHash, resources);
        RazorDbOperationTokenContext context = new(actor.Id, databaseId, RazorDbOperation.ExecuteSchema, resource, confirmed.Preview.SchemaFingerprint, confirmationHash);
        RazorDbOperationTokenResult consumed = await operationTokens.ConsumeAsync(confirmed.Token.Value, context, DateTimeOffset.UtcNow, cancellationToken);
        if (!consumed.IsValid) throw new RazorDbException(RazorDbErrorCode.Forbidden, "The confirmation expired, was already used, or no longer matches the operation.");

        Guid correlationId = Guid.NewGuid();
        Stopwatch stopwatch = Stopwatch.StartNew();
        await AppendAuditAsync(actor, databaseId, RazorDbOperation.ExecuteSchema, RazorDbAuditStatus.Started, correlationId, resource, null, confirmed.Preview.SqlHash, cancellationToken);
        try
        {
            IRazorDbProvider provider = await registry.GetProviderAsync(databaseId, cancellationToken);
            DdlExecutionResult result = await provider.Schema.ExecuteAsync(new DdlExecutionRequest(databaseId, change, confirmed.Preview.SchemaFingerprint, confirmed.Preview.SqlHash, confirmed.Token.Value), cancellationToken);
            await AppendOutcomeAuditAsync(actor, databaseId, RazorDbOperation.ExecuteSchema, RazorDbAuditStatus.Completed, correlationId, resource, stopwatch.Elapsed, confirmed.Preview.SqlHash, cancellationToken);
            return result;
        }
        catch
        {
            await AppendOutcomeAuditAsync(actor, databaseId, RazorDbOperation.ExecuteSchema, RazorDbAuditStatus.Failed, correlationId, resource, stopwatch.Elapsed, confirmed.Preview.SqlHash, CancellationToken.None);
            throw;
        }
    }

    public async ValueTask ImportAsync(string databaseId, TransferFormat format, DbObjectName? table, string fileName, Stream stream, CancellationToken cancellationToken)
    {
        DatabaseRegistration registration = registry.GetRequiredRegistration(databaseId);
        RazorDbResource? resource = table is null ? null : RazorDbResource.FromObject(table.Value);
        RazorDbActor actor = await AuthorizeAsync(registration, RazorDbOperation.Import, RazorDbCapability.Import, resource, format == TransferFormat.Sql, cancellationToken);
        if (format == TransferFormat.Sql)
            _ = await AuthorizeAsync(registration, RazorDbOperation.ExecuteSql, RazorDbCapability.ExecuteSql, resource, true, cancellationToken);
        Guid correlationId = Guid.NewGuid();
        Stopwatch stopwatch = Stopwatch.StartNew();
        await AppendAuditAsync(actor, databaseId, RazorDbOperation.Import, RazorDbAuditStatus.Started, correlationId, resource, null, null, cancellationToken);
        try
        {
            IRazorDbProvider provider = await registry.GetProviderAsync(databaseId, cancellationToken);
            await provider.Transfer.ImportAsync(new ImportRequest(databaseId, format, table), stream, cancellationToken: cancellationToken);
            await AppendOutcomeAuditAsync(actor, databaseId, RazorDbOperation.Import, RazorDbAuditStatus.Completed, correlationId, resource, stopwatch.Elapsed, null, cancellationToken);
        }
        catch
        {
            await AppendOutcomeAuditAsync(actor, databaseId, RazorDbOperation.Import, RazorDbAuditStatus.Failed, correlationId, resource, stopwatch.Elapsed, null, CancellationToken.None);
            throw;
        }
    }

    public async ValueTask<RazorDbJobRecord> QueueExportAsync(
        string databaseId,
        TransferFormat format,
        IReadOnlyList<DbObjectName> tables,
        bool includeSchema,
        bool includeData,
        bool compressWithGzip,
        CancellationToken cancellationToken) =>
        await QueueExportAsync(databaseId, format, tables, includeSchema, includeData, compressWithGzip, null, cancellationToken);

    public async ValueTask<RazorDbJobRecord> QueueExportAsync(
        string databaseId,
        TransferFormat format,
        IReadOnlyList<DbObjectName> tables,
        bool includeSchema,
        bool includeData,
        bool compressWithGzip,
        RowExportQuery? rowQuery,
        CancellationToken cancellationToken)
    {
        DatabaseRegistration registration = registry.GetRequiredRegistration(databaseId);
        if (format == TransferFormat.Csv && tables.Count != 1)
            throw new RazorDbException(RazorDbErrorCode.Validation, "CSV export requires exactly one table.");
        if (!includeSchema && !includeData)
            throw new RazorDbException(RazorDbErrorCode.Validation, "An export must include schema, data, or both.");
        if (format == TransferFormat.Csv && !includeData)
            throw new RazorDbException(RazorDbErrorCode.Validation, "CSV export always contains table data.");
        if (format == TransferFormat.Sql && rowQuery is not null)
            throw new RazorDbException(RazorDbErrorCode.Validation, "Structured row selection is supported only for CSV exports.");
        IReadOnlyList<DbObjectName> authorizedTables = tables;
        RazorDbActor actor;
        if (authorizedTables.Count == 0)
        {
            actor = await AuthorizeAsync(registration, RazorDbOperation.BrowseMetadata, RazorDbCapability.BrowseMetadata, null, false, cancellationToken);
            IRazorDbProvider provider = await registry.GetProviderAsync(databaseId, cancellationToken);
            DatabaseMetadata metadata = await provider.Metadata.GetDatabaseAsync(new MetadataRequest(databaseId), cancellationToken);
            authorizedTables = metadata.Schemas.SelectMany(schema => schema.Objects)
                .Where(item => item.Kind == DbObjectKind.Table).Select(item => item.Name).ToArray();
            if (authorizedTables.Count == 0)
                actor = await AuthorizeAsync(registration, RazorDbOperation.Export, RazorDbCapability.Export, null, false, cancellationToken);
        }
        else
        {
            actor = await AuthorizeAsync(registration, RazorDbOperation.Export, RazorDbCapability.Export, RazorDbResource.FromObject(authorizedTables[0]), false, cancellationToken);
        }
        for (int index = authorizedTables.Count > 0 && tables.Count > 0 ? 1 : 0; index < authorizedTables.Count; index++)
            actor = await AuthorizeAsync(registration, RazorDbOperation.Export, RazorDbCapability.Export, RazorDbResource.FromObject(authorizedTables[index]), false, cancellationToken);
        using IDisposable admission = await transferAdmission.EnterAsync(databaseId, actor.Id, cancellationToken);
        Dictionary<string, string> parameters = new(StringComparer.Ordinal)
        {
            ["format"] = format.ToString(),
            ["tables"] = System.Text.Json.JsonSerializer.Serialize(authorizedTables),
            ["includeSchema"] = includeSchema.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["includeData"] = includeData.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["compressWithGzip"] = compressWithGzip.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (rowQuery is not null)
            rowExportQueryProtector.AddProtectedParameters(parameters, rowQuery);
        string payloadHash = HashJobParameters(parameters);
        RazorDbOperationToken envelope = await operationTokens.IssueAsync(
            new RazorDbOperationTokenContext(actor.Id, databaseId, RazorDbOperation.Export,
                authorizedTables.Count == 1 ? RazorDbResource.FromObject(authorizedTables[0]) : null, string.Empty, payloadHash),
            _options.JobAuthorizationLifetime, cancellationToken);
        parameters["authorizationToken"] = envelope.Value;
        parameters["authorizationHash"] = payloadHash;
        return await jobStore.CreateAsync(new RazorDbJobCreateRequest(databaseId, actor.Id, format == TransferFormat.Csv ? RazorDbJobKind.CsvExport : RazorDbJobKind.SqlDump, Parameters: parameters), cancellationToken);
    }

    public async ValueTask<(IReadOnlyList<RazorDbJobRecord> Jobs, IReadOnlyList<RazorDbAuditRecord> Audit)> GetActivityAsync(string databaseId, CancellationToken cancellationToken)
    {
        DatabaseRegistration registration = registry.GetRequiredRegistration(databaseId);
        RazorDbActor actor = await AuthorizeAsync(registration, RazorDbOperation.BrowseMetadata, RazorDbCapability.BrowseMetadata, null, false, cancellationToken);
        IReadOnlyList<RazorDbJobRecord> jobs = await jobStore.ListAsync(new RazorDbJobQuery(databaseId, actor.Id, Limit: 50), cancellationToken);
        IReadOnlyList<RazorDbAuditRecord> audit = await auditReader.ListAsync(databaseId, actor.Id, 50, cancellationToken);
        return (jobs, audit);
    }

    public async ValueTask SetPageSizeAsync(string databaseId, int pageSize, CancellationToken cancellationToken)
    {
        DatabaseRegistration registration = registry.GetRequiredRegistration(databaseId);
        RazorDbActor actor = await AuthorizeAsync(registration, RazorDbOperation.BrowseMetadata, RazorDbCapability.BrowseMetadata, null, false, cancellationToken);
        RazorDbResourceLimits limits = registration.ResourceLimits ?? _options.ResourceLimits;
        if (pageSize <= 0 || pageSize > limits.MaximumPageSize)
            throw new RazorDbException(RazorDbErrorCode.Validation, "The selected page size is outside the configured limit.");
        await preferenceStore.SetAsync(actor.Id, registration.Id, "page-size", pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
    }

    public async ValueTask<RazorDbJobRecord> CancelJobAsync(string databaseId, Guid jobId, CancellationToken cancellationToken)
    {
        DatabaseRegistration registration = registry.GetRequiredRegistration(databaseId);
        RazorDbResource resource = new(Subresource: jobId.ToString("N"));
        RazorDbActor actor = await AuthorizeAsync(
            registration, RazorDbOperation.CancelJob, RazorDbCapability.BrowseMetadata, resource, false, cancellationToken);
        RazorDbJobRecord? existing = await jobStore.GetAsync(jobId, cancellationToken);
        if (existing is null || !string.Equals(existing.DatabaseId, registration.Id, StringComparison.Ordinal)
            || !string.Equals(existing.ActorId, actor.Id, StringComparison.Ordinal))
        {
            await AppendAuditAsync(actor, registration.Id, RazorDbOperation.CancelJob, RazorDbAuditStatus.Denied,
                Guid.NewGuid(), resource, null, null, cancellationToken, "job-owner");
            throw new RazorDbException(RazorDbErrorCode.Forbidden, "The job cannot be cancelled.");
        }

        Guid correlationId = Guid.NewGuid();
        Stopwatch stopwatch = Stopwatch.StartNew();
        await AppendAuditAsync(actor, registration.Id, RazorDbOperation.CancelJob, RazorDbAuditStatus.Started,
            correlationId, resource, null, null, cancellationToken);
        RazorDbJobRecord? cancelled = await jobStore.RequestCancellationAsync(jobId, actor.Id, cancellationToken);
        if (cancelled is null)
        {
            await AppendOutcomeAuditAsync(actor, registration.Id, RazorDbOperation.CancelJob, RazorDbAuditStatus.Failed,
                correlationId, resource, stopwatch.Elapsed, null, cancellationToken, "job-not-active");
            throw new RazorDbException(RazorDbErrorCode.Validation, "The job is no longer active.");
        }

        await AppendOutcomeAuditAsync(actor, registration.Id, RazorDbOperation.CancelJob, RazorDbAuditStatus.Completed,
            correlationId, resource, stopwatch.Elapsed, null, cancellationToken);
        return cancelled;
    }

    public long GetMaximumUploadBytes(string databaseId) => (registry.GetRequiredRegistration(databaseId).ResourceLimits ?? _options.ResourceLimits).MaximumUploadBytes;

    private async ValueTask<RowMutationResult> AuditedMutationAsync(string databaseId, DbObjectName table, RazorDbOperation operation, RazorDbCapability capability, Func<IRazorDbProvider, CancellationToken, ValueTask<RowMutationResult>> action, CancellationToken cancellationToken)
    {
        DatabaseRegistration registration = registry.GetRequiredRegistration(databaseId);
        RazorDbResource resource = RazorDbResource.FromObject(table);
        RazorDbActor actor = await AuthorizeAsync(registration, operation, capability, resource, false, cancellationToken);
        Guid correlationId = Guid.NewGuid();
        Stopwatch stopwatch = Stopwatch.StartNew();
        await AppendAuditAsync(actor, databaseId, operation, RazorDbAuditStatus.Started, correlationId, resource, null, null, cancellationToken);
        try
        {
            IRazorDbProvider provider = await registry.GetProviderAsync(databaseId, cancellationToken);
            RowMutationResult result = await action(provider, cancellationToken);
            RazorDbAuditStatus status = result.Status == RowMutationStatus.Succeeded ? RazorDbAuditStatus.Completed : RazorDbAuditStatus.Failed;
            await AppendOutcomeAuditAsync(actor, databaseId, operation, status, correlationId, resource, stopwatch.Elapsed, null, cancellationToken, result.Status.ToString());
            return result;
        }
        catch (OperationCanceledException)
        {
            await AppendOutcomeAuditAsync(actor, databaseId, operation, RazorDbAuditStatus.Cancelled, correlationId, resource, stopwatch.Elapsed, null, CancellationToken.None);
            throw;
        }
        catch
        {
            await AppendOutcomeAuditAsync(actor, databaseId, operation, RazorDbAuditStatus.Failed, correlationId, resource, stopwatch.Elapsed, null, CancellationToken.None);
            throw;
        }
    }

    private async ValueTask<RazorDbActor> AuthorizeAsync(DatabaseRegistration registration, RazorDbOperation operation, RazorDbCapability capability, RazorDbResource? resource, bool highRisk, CancellationToken cancellationToken)
    {
        AuthenticationState state = await authenticationStateProvider.GetAuthenticationStateAsync();
        ClaimsPrincipal user = state.User;
        RazorDbActor actor = CreateActor(user);
        bool access = (await authorizationService.AuthorizeAsync(user, RazorDbManagerPolicies.Access)).Succeeded;
        bool allowedSchema = resource?.Schema is null || registration.AllowedSchemas.Count == 0 || registration.AllowedSchemas.Contains(resource.Schema, StringComparer.OrdinalIgnoreCase);
        RazorDbAuthorizationResult resourceResult = access && registration.EnabledCapabilities.Includes(capability) && allowedSchema
            ? await resourceAuthorizer.AuthorizeAsync(new RazorDbAuthorizationContext(actor, registration, operation, capability, resource), cancellationToken)
            : RazorDbAuthorizationResult.Denied(!access ? "access-policy" : !allowedSchema ? "schema-not-allowed" : "capability-disabled");

        if (!resourceResult.IsAllowed)
        {
            await AppendAuditAsync(actor, registration.Id, operation, RazorDbAuditStatus.Denied, Guid.NewGuid(), resource, null, null, cancellationToken, resourceResult.ReasonCode);
            throw new RazorDbException(RazorDbErrorCode.Forbidden, "The operation is not authorized.");
        }

        if (highRisk)
        {
            bool policy = (await authorizationService.AuthorizeAsync(user, RazorDbManagerPolicies.HighRisk)).Succeeded;
            RazorDbSessionValidationResult session = policy
                ? await sessionValidator.ValidateAsync(new RazorDbSessionValidationContext(actor, registration, operation, resource), cancellationToken)
                : new RazorDbSessionValidationResult(false, ReasonCode: "high-risk-policy");
            if (!session.IsValid)
            {
                await AppendAuditAsync(actor, registration.Id, operation, RazorDbAuditStatus.Denied, Guid.NewGuid(), resource, null, null, cancellationToken, session.ReasonCode);
                throw new RazorDbException(RazorDbErrorCode.Forbidden, "Recent authentication is required for this operation.");
            }
        }

        return actor;
    }

    private async ValueTask<RazorDbActor> AuthorizeSchemaResourcesAsync(
        DatabaseRegistration registration,
        RazorDbOperation operation,
        RazorDbCapability capability,
        IReadOnlyList<RazorDbResource> resources,
        bool highRisk,
        CancellationToken cancellationToken)
    {
        RazorDbActor? actor = null;
        for (int index = 0; index < resources.Count; index++)
        {
            // Revalidate the high-risk session once, while every resource still receives policy and authorizer checks.
            actor = await AuthorizeAsync(registration, operation, capability, resources[index], highRisk && index == 0, cancellationToken);
        }

        return actor ?? throw new RazorDbException(RazorDbErrorCode.Validation, "The schema change has no authorization resource.");
    }

    private ValueTask AppendAuditAsync(RazorDbActor actor, string databaseId, RazorDbOperation operation, RazorDbAuditStatus status, Guid correlationId, RazorDbResource? resource, TimeSpan? duration, string? hash, CancellationToken cancellationToken, string? resultCode = null, string? sqlClassification = null) =>
        auditSink.AppendAsync(new RazorDbAuditRecord { Id = Guid.NewGuid(), CorrelationId = correlationId, Timestamp = DateTimeOffset.UtcNow, ActorId = actor.Id, DatabaseId = databaseId, Operation = operation, Status = status, Resource = resource, Duration = duration, PayloadHash = hash, SqlClassification = sqlClassification, ResultCode = resultCode }, cancellationToken);

    private async ValueTask AppendOutcomeAuditAsync(RazorDbActor actor, string databaseId, RazorDbOperation operation, RazorDbAuditStatus status, Guid correlationId, RazorDbResource? resource, TimeSpan? duration, string? hash, CancellationToken cancellationToken, string? resultCode = null, string? sqlClassification = null)
    {
        try
        {
            await AppendAuditAsync(actor, databaseId, operation, status, correlationId, resource, duration, hash, cancellationToken, resultCode, sqlClassification);
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                "RazorDbManager could not persist the {AuditStatus} audit outcome for correlation {CorrelationId} because {ErrorType} occurred. The database operation may already have completed.",
                status, correlationId, exception.GetType().Name);
        }
    }

    private static RazorDbActor CreateActor(ClaimsPrincipal user)
    {
        string? id = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? user.Identity?.Name;
        if (user.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(id)) throw new RazorDbException(RazorDbErrorCode.Forbidden, "Authentication is required.");
        return new RazorDbActor(id, user.Identity.Name);
    }

    private static IReadOnlyDictionary<string, DbValue> OriginalValues(DbTableMetadata table, DbRow row)
    {
        if (table.Columns.Count != row.Values.Count) throw new RazorDbException(RazorDbErrorCode.Validation, "The row no longer matches the table schema.");
        return table.Columns.Select((column, index) => new KeyValuePair<string, DbValue>(column.Name, row.Values[index])).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    }

    internal static IReadOnlyList<RazorDbResource> SchemaResources(SchemaChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        List<RazorDbResource> resources = [];
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);

        void Add(RazorDbResource resource)
        {
            string key = $"{resource.Schema}\0{resource.Object}\0{resource.Subresource}";
            if (keys.Add(key)) resources.Add(resource);
        }

        static RazorDbResource Child(DbObjectName table, string child) => new(table.Schema, table.Name, child);

        void AddForeignKey(DbObjectName localTable, ForeignKeyDefinition foreignKey)
        {
            Add(Child(localTable, foreignKey.Name));
            foreach (string column in foreignKey.Columns) Add(Child(localTable, column));
            Add(RazorDbResource.FromObject(foreignKey.ReferencedTable));
            foreach (string column in foreignKey.ReferencedColumns) Add(Child(foreignKey.ReferencedTable, column));
        }

        switch (change)
        {
            case CreateTableChange item:
                Add(RazorDbResource.FromObject(item.Table.Name));
                foreach (ColumnDefinition column in item.Table.Columns) Add(Child(item.Table.Name, column.Name));
                foreach (IndexDefinition index in item.Table.Indexes)
                {
                    Add(Child(item.Table.Name, index.Name));
                    foreach (DbIndexColumn column in index.Columns) Add(Child(item.Table.Name, column.Name));
                }
                foreach (ForeignKeyDefinition foreignKey in item.Table.ForeignKeys) AddForeignKey(item.Table.Name, foreignKey);
                break;
            case RenameTableChange item:
                Add(RazorDbResource.FromObject(item.Table));
                Add(RazorDbResource.FromObject(new DbObjectName(item.Table.Schema, item.NewName)));
                break;
            case DropTableChange item:
                Add(RazorDbResource.FromObject(item.Table));
                break;
            case AddColumnChange item:
                Add(Child(item.Table, item.Column.Name));
                if (!string.IsNullOrWhiteSpace(item.AfterColumn)) Add(Child(item.Table, item.AfterColumn));
                break;
            case AlterColumnChange item:
                Add(Child(item.Table, item.ExistingColumn));
                Add(Child(item.Table, item.Column.Name));
                break;
            case DropColumnChange item:
                Add(Child(item.Table, item.Column));
                break;
            case AddIndexChange item:
                Add(Child(item.Table, item.Index.Name));
                foreach (DbIndexColumn column in item.Index.Columns) Add(Child(item.Table, column.Name));
                break;
            case DropIndexChange item:
                Add(Child(item.Table, item.Index));
                break;
            case AddForeignKeyChange item:
                AddForeignKey(item.Table, item.ForeignKey);
                break;
            case DropForeignKeyChange item:
                Add(Child(item.Table, item.ForeignKey));
                break;
            default:
                throw new NotSupportedException($"Schema change '{change.GetType().Name}' is not supported.");
        }

        return resources;
    }

    internal static string SchemaConfirmationHash(string sqlHash, IReadOnlyList<RazorDbResource> resources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlHash);
        ArgumentNullException.ThrowIfNull(resources);
        string canonical = string.Join("\n", resources.Select(resource =>
            $"{resource.Schema ?? string.Empty}\0{resource.Object ?? string.Empty}\0{resource.Subresource ?? string.Empty}"));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{sqlHash}\n{canonical}")));
    }

    internal static string ClassifySql(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "INSERT", "UPDATE", "DELETE", "REPLACE", "CREATE", "ALTER", "DROP", "TRUNCATE",
            "RENAME", "GRANT", "REVOKE", "SHOW", "EXPLAIN", "DESCRIBE", "WITH", "CALL", "SET", "USE",
            "ANALYZE", "CHECK", "OPTIMIZE", "REPAIR", "BEGIN", "START", "COMMIT", "ROLLBACK",
        };
        List<string> classifications = [];
        int index = 0;
        while (index < sql.Length && classifications.Count < 8)
        {
            SkipSqlTrivia(sql, ref index);
            while (index < sql.Length && sql[index] == ';')
            {
                index++;
                SkipSqlTrivia(sql, ref index);
            }
            if (index >= sql.Length) break;

            int start = index;
            while (index < sql.Length && (char.IsAsciiLetter(sql[index]) || sql[index] == '_')) index++;
            string keyword = index > start ? sql[start..index].ToUpperInvariant() : "OTHER";
            classifications.Add(allowed.Contains(keyword) ? keyword : "OTHER");
            SkipSqlStatement(sql, ref index);
        }

        return classifications.Count switch
        {
            0 => "EMPTY",
            _ when index < sql.Length => string.Join(',', classifications) + ",MORE",
            _ => string.Join(',', classifications),
        };
    }

    private static void SkipSqlTrivia(string sql, ref int index)
    {
        while (index < sql.Length)
        {
            if (char.IsWhiteSpace(sql[index]) || sql[index] == '\ufeff') { index++; continue; }
            if (sql[index] == '#' || (sql[index] == '-' && index + 1 < sql.Length && sql[index + 1] == '-'))
            {
                index = sql.IndexOf('\n', index) is int next && next >= 0 ? next + 1 : sql.Length;
                continue;
            }
            if (sql[index] == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                int end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? sql.Length : end + 2;
                continue;
            }
            break;
        }
    }

    private static void SkipSqlStatement(string sql, ref int index)
    {
        char quote = '\0';
        while (index < sql.Length)
        {
            char current = sql[index++];
            if (quote != '\0')
            {
                if (current == '\\' && quote is '\'' or '"' && index < sql.Length) { index++; continue; }
                if (current == quote)
                {
                    if (index < sql.Length && sql[index] == quote) { index++; continue; }
                    quote = '\0';
                }
                continue;
            }

            if (current is '\'' or '"' or '`') { quote = current; continue; }
            if (current == '#') { index = sql.IndexOf('\n', index) is int next && next >= 0 ? next + 1 : sql.Length; continue; }
            if (current == '-' && index < sql.Length && sql[index] == '-') { index = sql.IndexOf('\n', index + 1) is int next && next >= 0 ? next + 1 : sql.Length; continue; }
            if (current == '/' && index < sql.Length && sql[index] == '*')
            {
                int end = sql.IndexOf("*/", index + 1, StringComparison.Ordinal);
                index = end < 0 ? sql.Length : end + 2;
                continue;
            }
            if (current == ';') return;
        }
    }

    private static string HashJobParameters(IReadOnlyDictionary<string, string> parameters)
    {
        string canonical = string.Join("\n", parameters.Where(pair => !pair.Key.StartsWith("authorization", StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    public static string SafeMessage(Exception exception) => exception switch
    {
        RazorDbException safe => safe.Message,
        OperationCanceledException => "The operation was cancelled.",
        _ => "The database operation failed. Check the server logs for details.",
    };
}

/// <summary>Pairs a provider-generated DDL preview with its actor-bound confirmation token.</summary>
/// <param name="Preview">The provider-generated SQL preview.</param>
/// <param name="Token">The short-lived, single-use confirmation token.</param>
public sealed record WorkspaceDdlPreview(DdlPreview Preview, RazorDbOperationToken Token);

internal sealed class AllowAllRazorDbAuthorizer : IRazorDbManagerAuthorizer
{
    public ValueTask<RazorDbAuthorizationResult> AuthorizeAsync(RazorDbAuthorizationContext context, CancellationToken cancellationToken = default) => ValueTask.FromResult(RazorDbAuthorizationResult.Allowed);
}

internal sealed class DenyHighRiskSessionValidator : IRazorDbSessionValidator
{
    public ValueTask<RazorDbSessionValidationResult> ValidateAsync(RazorDbSessionValidationContext context, CancellationToken cancellationToken = default) => ValueTask.FromResult(new RazorDbSessionValidationResult(false, ReasonCode: "session-validator-not-configured"));
}
