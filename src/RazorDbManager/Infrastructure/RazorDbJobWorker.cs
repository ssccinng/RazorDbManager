using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorDbManager.Core;

namespace RazorDbManager;

internal sealed class RazorDbJobWorker(
    IRazorDbJobStore jobStore,
    IRazorDbArtifactStore artifactStore,
    IRazorDbProviderRegistry registry,
    IRazorDbOperationTokenStore operationTokens,
    IRazorDbBackgroundAuthorizer backgroundAuthorizer,
    IRazorDbStoreMaintenance storeMaintenance,
    IRazorDbAuditSink auditSink,
    RowExportQueryProtector rowQueryProtector,
    IOptions<RazorDbManagerOptions> options,
    ILogger<RazorDbJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                await ProcessQueuedAsync(stoppingToken);
                await artifactStore.DeleteExpiredAsync(DateTimeOffset.UtcNow, stoppingToken);
                await storeMaintenance.CleanupAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(
                    "RazorDbManager background job polling failed with {ErrorType}.",
                    exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProcessQueuedAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RazorDbJobRecord> queued = await jobStore.ListAsync(new RazorDbJobQuery(Status: RazorDbJobStatus.Queued, Limit: 4), cancellationToken);
        foreach (RazorDbJobRecord candidate in queued)
        {
            RazorDbJobRecord? running = await jobStore.TryUpdateAsync(candidate.Id, candidate.Version, new RazorDbJobUpdate { Status = RazorDbJobStatus.Running }, cancellationToken);
            if (running is null) continue;
            await ExecuteJobAsync(running, cancellationToken);
        }
    }

    private async Task ExecuteJobAsync(RazorDbJobRecord job, CancellationToken cancellationToken)
    {
        RazorDbArtifactWriteSession? write = null;
        Guid correlationId = Guid.NewGuid();
        Stopwatch stopwatch = Stopwatch.StartNew();
        RazorDbOperation auditOperation = job.Kind is RazorDbJobKind.CsvExport or RazorDbJobKind.SqlDump ? RazorDbOperation.Export : RazorDbOperation.Import;
        RazorDbResource? auditResource = null;
        string? auditPayloadHash = null;
        string? auditSqlClassification = null;
        Dictionary<string, string> auditMetadata = new(StringComparer.Ordinal)
        {
            ["jobId"] = job.Id.ToString("N"),
        };
        try
        {
            bool isExport = job.Kind is RazorDbJobKind.CsvExport or RazorDbJobKind.SqlDump;
            TransferFormat format = job.Kind is RazorDbJobKind.CsvExport or RazorDbJobKind.CsvImport ? TransferFormat.Csv : TransferFormat.Sql;
            IReadOnlyList<DbObjectName> tables = isExport && job.Parameters.TryGetValue("tables", out string? serialized)
                ? DeserializeObjectNames(serialized) : [];
            DbObjectName? importTable = !isExport && job.Parameters.TryGetValue("table", out string? serializedTable)
                ? DeserializeObjectName(serializedTable) : null;
            bool hasHeader = BooleanParameter(job.Parameters, "hasHeader", defaultValue: true);
            char delimiter = CharacterParameter(job.Parameters, "delimiter", ',');
            string? nullToken = job.Parameters.TryGetValue("nullToken", out string? configuredNullToken)
                ? configuredNullToken : null;
            bool continueOnError = BooleanParameter(job.Parameters, "continueOnError");
            bool decodeProtectedValues = BooleanParameter(job.Parameters, "decodeProtectedValues");
            bool includeSchema = BooleanParameter(job.Parameters, "includeSchema", defaultValue: true);
            bool includeData = BooleanParameter(job.Parameters, "includeData", defaultValue: true);
            bool compressWithGzip = BooleanParameter(job.Parameters, "compressWithGzip");
            DatabaseRegistration registration = registry.GetRequiredRegistration(job.DatabaseId);
            RazorDbCapability required = isExport ? RazorDbCapability.Export : RazorDbCapability.Import;
            if (format == TransferFormat.Sql && !isExport) required |= RazorDbCapability.ExecuteSql;
            if (!registration.EnabledCapabilities.Includes(required))
                throw new RazorDbException(RazorDbErrorCode.Forbidden, "The required transfer capability was revoked before the job started.");
            if (string.IsNullOrWhiteSpace(job.ActorId))
                throw new RazorDbException(RazorDbErrorCode.Forbidden, "The queued job has no authenticated owner.");
            if (!job.Parameters.TryGetValue("authorizationToken", out string? envelope)
                || !job.Parameters.TryGetValue("authorizationHash", out string? expectedHash)
                || !string.Equals(expectedHash, HashJobParameters(job.Parameters), StringComparison.Ordinal))
                throw new RazorDbException(RazorDbErrorCode.Forbidden, "The queued job authorization envelope is missing or changed.");
            bool hasProtectedQuery = job.Parameters.TryGetValue(
                RowExportQueryProtector.PayloadParameter,
                out string? protectedQuery);
            bool hasProtectedQueryHash = job.Parameters.TryGetValue(
                RowExportQueryProtector.HashParameter,
                out string? protectedQueryHash);
            if (job.Parameters.ContainsKey(RowExportQueryProtector.LegacyPlaintextParameter)
                || hasProtectedQuery != hasProtectedQueryHash
                || (hasProtectedQuery && (!isExport || format != TransferFormat.Csv)))
            {
                throw new RazorDbException(
                    RazorDbErrorCode.Forbidden,
                    "The queued export row selection binding is invalid.");
            }
            RowExportQuery? rowQuery = hasProtectedQuery
                ? rowQueryProtector.Unprotect(protectedQuery, protectedQueryHash)
                : null;
            if (!isExport)
            {
                ValidateInputArtifactParameters(job, out string expectedArtifactId, out long expectedArtifactLength, out string expectedArtifactSha256);
                auditPayloadHash = expectedArtifactSha256;
                auditMetadata["inputArtifactId"] = expectedArtifactId;
                auditMetadata["inputArtifactLength"] = expectedArtifactLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (format == TransferFormat.Sql) auditSqlClassification = "RESTORE";
            }
            else if (format == TransferFormat.Sql)
            {
                auditSqlClassification = "DUMP";
            }
            RazorDbResource? jobResource = isExport ? tables.Count == 1 ? RazorDbResource.FromObject(tables[0]) : null : importTable is null ? null : RazorDbResource.FromObject(importTable.Value);
            auditResource = jobResource;
            RazorDbActor actor = new(job.ActorId);
            IReadOnlyList<RazorDbResource?> resources = isExport && tables.Count > 0
                ? tables.Select(table => (RazorDbResource?)RazorDbResource.FromObject(table)).ToArray()
                : [jobResource];
            foreach (RazorDbResource? resource in resources)
            {
                RazorDbAuthorizationResult authorizationResult = await backgroundAuthorizer.AuthorizeAsync(
                    new RazorDbAuthorizationContext(actor, registration, auditOperation, isExport ? RazorDbCapability.Export : RazorDbCapability.Import, resource),
                    highRisk: !isExport && format == TransferFormat.Sql,
                    cancellationToken);
                if (!authorizationResult.IsAllowed)
                    throw new RazorDbException(RazorDbErrorCode.Forbidden, "The queued job is no longer authorized.");
                if (!isExport && format == TransferFormat.Sql)
                {
                    RazorDbAuthorizationResult sql = await backgroundAuthorizer.AuthorizeAsync(
                        new RazorDbAuthorizationContext(actor, registration, RazorDbOperation.ExecuteSql, RazorDbCapability.ExecuteSql, resource),
                        highRisk: true,
                        cancellationToken);
                    if (!sql.IsAllowed)
                        throw new RazorDbException(RazorDbErrorCode.Forbidden, "SQL restore is no longer authorized.");
                }
            }
            RazorDbJobRecord? beforeStart = await jobStore.GetAsync(job.Id, cancellationToken);
            if (beforeStart?.CancellationRequested == true)
            {
                await UpdateCurrentAsync(job.Id, new RazorDbJobUpdate
                {
                    Status = RazorDbJobStatus.Cancelled,
                    RowsProcessed = beforeStart.RowsProcessed,
                    BytesProcessed = beforeStart.BytesProcessed,
                    ResultCode = "user-cancelled-before-start",
                    Parameters = TerminalParameters(job),
                }, cancellationToken);
                await AppendOutcomeAuditAsync(
                    job, auditOperation, RazorDbAuditStatus.Cancelled, correlationId, jobResource,
                    stopwatch.Elapsed, "user-cancelled-before-start", auditPayloadHash,
                    auditSqlClassification, auditMetadata, CancellationToken.None);
                return;
            }
            await AppendAuditAsync(
                job, auditOperation, RazorDbAuditStatus.Started, correlationId, jobResource, null, null,
                auditPayloadHash, auditSqlClassification, auditMetadata, cancellationToken);
            RazorDbOperationTokenResult authorized = await operationTokens.ConsumeAsync(envelope,
                new RazorDbOperationTokenContext(job.ActorId, job.DatabaseId, auditOperation, jobResource, string.Empty, expectedHash),
                DateTimeOffset.UtcNow, cancellationToken);
            if (!authorized.IsValid)
                throw new RazorDbException(RazorDbErrorCode.Forbidden, "The queued job authorization expired or was already consumed.");
            RazorDbResourceLimits limits = registration.ResourceLimits ?? options.Value.ResourceLimits;
            IRazorDbProvider provider = await registry.GetProviderAsync(job.DatabaseId, cancellationToken);
            using CancellationTokenSource operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operationTimeout.CancelAfter(limits.ExportTimeout);
            CancellationToken operationToken = operationTimeout.Token;
            TransferProgressState progressState = new();
            using CancellationTokenSource monitorStop = new();
            Task monitor = MonitorJobAsync(job.Id, operationTimeout, progressState, monitorStop.Token);
            TransferResult result;
            RazorDbArtifactDescriptor? completedOutput = null;
            try
            {
                if (isExport)
                {
                    string extension = $"{(format == TransferFormat.Csv ? "csv" : "sql")}{(compressWithGzip ? ".gz" : string.Empty)}";
                    string contentType = compressWithGzip ? "application/gzip" : format == TransferFormat.Csv ? "text/csv" : "application/sql";
                    write = await artifactStore.CreateWriteAsync(new RazorDbArtifactCreateRequest(
                        job.DatabaseId,
                        job.ActorId,
                        $"{job.DatabaseId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{extension}",
                        contentType,
                        DateTimeOffset.UtcNow.Add(options.Value.ArtifactLifetime),
                        tables.Select(RazorDbResource.FromObject).ToArray()), operationToken);
                    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using HashingWriteStream hashing = new(write.Content, hash);
                    result = await provider.Transfer.ExportAsync(new ExportRequest(
                        job.DatabaseId,
                        format,
                        tables,
                        includeSchema,
                        includeData,
                        compressWithGzip,
                        limits.MaximumExportRows,
                        limits.MaximumExportBytes,
                        rowQuery), hashing, progressState, operationToken);
                    await hashing.FlushAsync(operationToken); long length = hashing.BytesWritten; string digest = Convert.ToHexStringLower(hash.GetHashAndReset()); await hashing.DisposeAsync();
                    completedOutput = await artifactStore.CompleteWriteAsync(write.Descriptor.Id, length, digest, operationToken);
                }
                else
                {
                    if (job.InputArtifactId is null) throw new RazorDbException(RazorDbErrorCode.NotFound, "The import artifact is missing.");
                    RazorDbArtifactReadSession input = await OpenVerifiedInputArtifactAsync(job, operationToken);
                    await using Stream inputStream = input.Content;
                    result = await provider.Transfer.ImportAsync(new ImportRequest(
                        job.DatabaseId,
                        format,
                        importTable,
                        hasHeader,
                        delimiter,
                        nullToken,
                        continueOnError,
                        decodeProtectedValues), inputStream, progressState, operationToken);
                }
            }
            finally
            {
                monitorStop.Cancel();
                try { await monitor; }
                catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
            }

            RazorDbJobRecord? terminal = await UpdateCurrentAsync(job.Id, new RazorDbJobUpdate
            {
                Status = result.IsPartial ? RazorDbJobStatus.Failed : RazorDbJobStatus.Completed,
                RowsProcessed = result.RowsProcessed,
                BytesProcessed = result.BytesProcessed,
                OutputArtifactId = write?.Descriptor.Id,
                ResultCode = result.IsPartial ? "partial" : null,
                Parameters = TerminalParameters(job),
            }, cancellationToken);
            if (terminal?.Status != (result.IsPartial ? RazorDbJobStatus.Failed : RazorDbJobStatus.Completed))
            {
                if (write is not null) await TryDeleteArtifactAsync(write.Descriptor.Id, job.Id, "terminal-conflict");
                throw new RazorDbException(RazorDbErrorCode.Conflict, "The worker could not persist the transfer's terminal state.");
            }
            if (!isExport && !result.IsPartial && job.InputArtifactId is not null)
                await TryDeleteArtifactAsync(job.InputArtifactId, job.Id, "completed-import-input");
            if (completedOutput is not null)
            {
                auditPayloadHash = completedOutput.Sha256;
                auditMetadata["outputArtifactId"] = completedOutput.Id;
                auditMetadata["outputArtifactLength"] = completedOutput.Length!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            await AppendOutcomeAuditAsync(
                job, auditOperation, result.IsPartial ? RazorDbAuditStatus.Failed : RazorDbAuditStatus.Completed,
                correlationId, auditResource, stopwatch.Elapsed, result.IsPartial ? "partial" : null,
                auditPayloadHash, auditSqlClassification, auditMetadata, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            bool isImport = auditOperation == RazorDbOperation.Import;
            await UpdateCurrentAsync(job.Id, new RazorDbJobUpdate
            {
                Status = isImport ? RazorDbJobStatus.Failed : RazorDbJobStatus.Cancelled,
                ResultCode = isImport ? "host-stopping-outcome-unknown" : "host-stopping",
                Parameters = TerminalParameters(job),
            }, CancellationToken.None);
            if (write is not null) await TryDeleteArtifactAsync(write.Descriptor.Id, job.Id, "host-stopping-output");
            await AppendOutcomeAuditAsync(
                job, auditOperation, isImport ? RazorDbAuditStatus.Failed : RazorDbAuditStatus.Cancelled,
                correlationId, auditResource, stopwatch.Elapsed,
                isImport ? "host-stopping-outcome-unknown" : "host-stopping", auditPayloadHash,
                auditSqlClassification, auditMetadata, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            RazorDbJobRecord? current = await jobStore.GetAsync(job.Id, CancellationToken.None);
            bool userCancelled = current?.CancellationRequested == true;
            bool uncertainImport = userCancelled && auditOperation == RazorDbOperation.Import;
            RazorDbJobStatus status = userCancelled && !uncertainImport ? RazorDbJobStatus.Cancelled : RazorDbJobStatus.Failed;
            string resultCode = uncertainImport ? "cancellation-outcome-unknown" : userCancelled ? "user-cancelled" : "timeout";
            await UpdateCurrentAsync(job.Id, new RazorDbJobUpdate
            {
                Status = status,
                ResultCode = resultCode,
                Parameters = TerminalParameters(job),
            }, CancellationToken.None);
            if (write is not null) await TryDeleteArtifactAsync(write.Descriptor.Id, job.Id, "cancelled-or-timed-out-output");
            await AppendOutcomeAuditAsync(
                job, auditOperation, status == RazorDbJobStatus.Cancelled ? RazorDbAuditStatus.Cancelled : RazorDbAuditStatus.Failed,
                correlationId, auditResource, stopwatch.Elapsed, resultCode, auditPayloadHash,
                auditSqlClassification, auditMetadata, CancellationToken.None);
        }
        catch (Exception exception)
        {
            string resultCode = exception is RazorDbException known ? known.Code.ToString() : "worker-failure";
            logger.LogError("RazorDbManager job {JobId} failed with {ErrorType} ({ResultCode}).", job.Id, exception.GetType().Name, resultCode);
            await UpdateCurrentAsync(job.Id, new RazorDbJobUpdate
            {
                Status = RazorDbJobStatus.Failed,
                ResultCode = resultCode,
                Parameters = TerminalParameters(job),
            }, CancellationToken.None);
            if (write is not null) await TryDeleteArtifactAsync(write.Descriptor.Id, job.Id, "failed-output");
            await AppendOutcomeAuditAsync(job, auditOperation,
                exception is RazorDbException { Code: RazorDbErrorCode.Forbidden } ? RazorDbAuditStatus.Denied : RazorDbAuditStatus.Failed,
                correlationId, auditResource, stopwatch.Elapsed,
                resultCode, auditPayloadHash, auditSqlClassification, auditMetadata, CancellationToken.None);
        }
    }

    private async Task MonitorJobAsync(
        Guid jobId,
        CancellationTokenSource operationCancellation,
        TransferProgressState progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));
            long persistedRows = -1;
            long persistedBytes = -1;
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                RazorDbJobRecord? current = await jobStore.GetAsync(jobId, cancellationToken);
                if (current is null) { operationCancellation.Cancel(); return; }
                if (current.CancellationRequested) { operationCancellation.Cancel(); return; }
                if (current.Status != RazorDbJobStatus.Running) return;

                (long rows, long bytes) = progress.Snapshot();
                if (rows == persistedRows && bytes == persistedBytes) continue;
                RazorDbJobRecord? updated = await jobStore.TryUpdateAsync(jobId, current.Version, new RazorDbJobUpdate
                {
                    Status = RazorDbJobStatus.Running,
                    RowsProcessed = rows,
                    BytesProcessed = bytes,
                }, cancellationToken);
                if (updated is not null) { persistedRows = rows; persistedBytes = bytes; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            operationCancellation.Cancel();
            throw;
        }
    }

    private async ValueTask<RazorDbJobRecord?> UpdateCurrentAsync(
        Guid jobId,
        RazorDbJobUpdate update,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            RazorDbJobRecord? current = await jobStore.GetAsync(jobId, cancellationToken);
            if (current is null || IsTerminal(current.Status)) return current;
            RazorDbJobRecord? changed = await jobStore.TryUpdateAsync(jobId, current.Version, update, cancellationToken);
            if (changed is not null) return changed;
        }
        return await jobStore.GetAsync(jobId, cancellationToken);
    }

    private ValueTask AppendAuditAsync(
        RazorDbJobRecord job,
        RazorDbOperation operation,
        RazorDbAuditStatus status,
        Guid correlationId,
        RazorDbResource? resource,
        TimeSpan? duration,
        string? resultCode,
        string? payloadHash,
        string? sqlClassification,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken) =>
        auditSink.AppendAsync(new RazorDbAuditRecord
        {
            Id = Guid.NewGuid(), CorrelationId = correlationId, Timestamp = DateTimeOffset.UtcNow,
            ActorId = job.ActorId, DatabaseId = job.DatabaseId, Operation = operation, Status = status,
            Resource = resource, Duration = duration, ResultCode = resultCode, PayloadHash = payloadHash,
            SqlClassification = sqlClassification,
            Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
        }, cancellationToken);

    private async ValueTask AppendOutcomeAuditAsync(
        RazorDbJobRecord job,
        RazorDbOperation operation,
        RazorDbAuditStatus status,
        Guid correlationId,
        RazorDbResource? resource,
        TimeSpan? duration,
        string? resultCode,
        string? payloadHash,
        string? sqlClassification,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        try
        {
            await AppendAuditAsync(
                job, operation, status, correlationId, resource, duration, resultCode,
                payloadHash, sqlClassification, metadata, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                "RazorDbManager could not persist the {AuditStatus} audit outcome for job {JobId} and correlation {CorrelationId} because {ErrorType} occurred. The transfer may already have completed.",
                status, job.Id, correlationId, exception.GetType().Name);
        }
    }

    private static string HashJobParameters(IReadOnlyDictionary<string, string> parameters)
    {
        string canonical = string.Join("\n", parameters.Where(pair => !pair.Key.StartsWith("authorization", StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsTerminal(RazorDbJobStatus status) =>
        status is RazorDbJobStatus.Completed or RazorDbJobStatus.Failed or RazorDbJobStatus.Cancelled;

    private static IReadOnlyDictionary<string, string> TerminalParameters(RazorDbJobRecord job) =>
        RowExportQueryProtector.TerminalParameters(job.Parameters);

    private async ValueTask TryDeleteArtifactAsync(string artifactId, Guid jobId, string phase)
    {
        try
        {
            await artifactStore.DeleteAsync(artifactId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "RazorDbManager could not clean artifact for job {JobId} during {CleanupPhase} because {ErrorType} occurred. The persisted job outcome is unchanged.",
                jobId,
                phase,
                exception.GetType().Name);
        }
    }

    private static void ValidateInputArtifactParameters(
        RazorDbJobRecord job,
        out string artifactId,
        out long length,
        out string sha256)
    {
        if (job.InputArtifactId is null
            || !job.Parameters.TryGetValue("inputArtifactId", out artifactId!)
            || !string.Equals(job.InputArtifactId, artifactId, StringComparison.Ordinal)
            || !job.Parameters.TryGetValue("inputArtifactLength", out string? serializedLength)
            || !long.TryParse(
                serializedLength,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out length)
            || length < 0
            || !job.Parameters.TryGetValue("inputArtifactSha256", out sha256!)
            || !IsSha256(sha256))
        {
            throw new RazorDbException(
                RazorDbErrorCode.Forbidden,
                "The queued import artifact binding is missing or invalid.");
        }
    }

    private static bool InputArtifactMatches(
        RazorDbJobRecord job,
        RazorDbArtifactDescriptor descriptor)
    {
        ValidateInputArtifactParameters(job, out string expectedId, out long expectedLength, out string expectedSha256);
        return string.Equals(descriptor.Id, expectedId, StringComparison.Ordinal)
            && string.Equals(descriptor.DatabaseId, job.DatabaseId, StringComparison.Ordinal)
            && string.Equals(descriptor.ActorId, job.ActorId, StringComparison.Ordinal)
            && descriptor.Length == expectedLength
            && string.Equals(descriptor.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private async ValueTask<RazorDbArtifactReadSession> OpenVerifiedInputArtifactAsync(
        RazorDbJobRecord job,
        CancellationToken cancellationToken)
    {
        ValidateInputArtifactParameters(job, out _, out long expectedLength, out string expectedSha256);
        RazorDbArtifactReadSession input = await artifactStore.OpenReadAsync(job.InputArtifactId!, cancellationToken)
            ?? throw new RazorDbException(RazorDbErrorCode.NotFound, "The import artifact is missing.");

        try
        {
            if (!InputArtifactMatches(job, input.Descriptor))
                throw new RazorDbException(RazorDbErrorCode.Forbidden, "The import artifact no longer matches its authorization envelope.");
            if (!input.Content.CanSeek)
                throw new RazorDbException(
                    RazorDbErrorCode.Forbidden,
                    "The import artifact store returned a non-seekable stream that cannot be verified safely.");

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long length = 0;
            while (true)
            {
                int read = await input.Content.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                length += read;
                if (length > expectedLength)
                    throw new RazorDbException(RazorDbErrorCode.Forbidden, "The import artifact content length changed after authorization.");
                hash.AppendData(buffer, 0, read);
            }

            string actualSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (length != expectedLength
                || !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new RazorDbException(RazorDbErrorCode.Forbidden, "The import artifact content changed after authorization.");
            }

            input.Content.Position = 0;
            return input;
        }
        catch
        {
            await input.Content.DisposeAsync();
            throw;
        }
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64) return false;
        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character)) return false;
        }
        return true;
    }

    private static DbObjectName? DeserializeObjectName(string serialized)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(serialized);
            if (document.RootElement.ValueKind == JsonValueKind.Null) return null;
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(nameof(DbObjectName.Schema), out JsonElement schemaElement)
                || !document.RootElement.TryGetProperty(nameof(DbObjectName.Name), out JsonElement nameElement)
                || schemaElement.ValueKind != JsonValueKind.String
                || nameElement.ValueKind != JsonValueKind.String)
            {
                throw new JsonException();
            }

            string? schema = schemaElement.GetString();
            string? name = nameElement.GetString();
            return new DbObjectName(
                schema ?? throw new JsonException(),
                name ?? throw new JsonException());
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new RazorDbException(RazorDbErrorCode.Forbidden, "The queued import table binding is invalid.", exception);
        }
    }

    private static IReadOnlyList<DbObjectName> DeserializeObjectNames(string serialized)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(serialized);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
            List<DbObjectName> result = [];
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                DbObjectName? name = DeserializeObjectName(element.GetRawText());
                result.Add(name ?? throw new JsonException());
            }
            return result;
        }
        catch (RazorDbException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new RazorDbException(RazorDbErrorCode.Forbidden, "The queued export table binding is invalid.", exception);
        }
    }

    private static bool BooleanParameter(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        bool defaultValue = false) =>
        parameters.TryGetValue(name, out string? value)
            ? bool.TryParse(value, out bool parsed)
                ? parsed
                : throw new RazorDbException(RazorDbErrorCode.Validation, $"The queued {name} option is invalid.")
            : defaultValue;

    private static char CharacterParameter(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        char defaultValue)
    {
        if (!parameters.TryGetValue(name, out string? value)) return defaultValue;
        return value.Length == 1 && value[0] is not '\r' and not '\n' and not '"'
            ? value[0]
            : throw new RazorDbException(RazorDbErrorCode.Validation, $"The queued {name} option is invalid.");
    }

    private sealed class TransferProgressState : IProgress<TransferProgress>
    {
        private long _rows;
        private long _bytes;

        public void Report(TransferProgress value)
        {
            Interlocked.Exchange(ref _rows, value.RowsProcessed);
            Interlocked.Exchange(ref _bytes, value.BytesProcessed);
        }

        public (long Rows, long Bytes) Snapshot() =>
            (Interlocked.Read(ref _rows), Interlocked.Read(ref _bytes));
    }

    private sealed class HashingWriteStream(Stream inner, IncrementalHash hash) : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { hash.AppendData(buffer, offset, count); inner.Write(buffer, offset, count); BytesWritten += count; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { hash.AppendData(buffer.Span); await inner.WriteAsync(buffer, cancellationToken); BytesWritten += buffer.Length; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
    }
}

internal interface IRazorDbStoreMaintenance
{
    ValueTask CleanupAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
