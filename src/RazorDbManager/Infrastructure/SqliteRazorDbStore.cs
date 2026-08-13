using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RazorDbManager.Core;

namespace RazorDbManager;

internal sealed class SqliteRazorDbStore(
    LocalStorePath paths,
    IOptions<RazorDbManagerOptions> options) :
    IRazorDbAuditSink, IRazorDbAuditReader, IRazorDbJobStore, IRazorDbOperationTokenStore, IRazorDbStoreMaintenance,
    IRazorDbPreferenceStore
{
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public async ValueTask AppendAsync(RazorDbAuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events
                (id, correlation_id, timestamp, actor_id, database_id, operation, status,
                 schema_name, object_name, subresource, payload_hash, sql_classification,
                 result_code, duration_ms, metadata_json)
            VALUES
                ($id, $correlation, $timestamp, $actor, $database, $operation, $status,
                 $schema, $object, $subresource, $hash, $classification, $result,
                 $duration, $metadata)
            """;
        Add(command, "$id", record.Id.ToString("N"));
        Add(command, "$correlation", record.CorrelationId.ToString("N"));
        Add(command, "$timestamp", record.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$actor", record.ActorId);
        Add(command, "$database", record.DatabaseId);
        Add(command, "$operation", record.Operation.ToString());
        Add(command, "$status", record.Status.ToString());
        Add(command, "$schema", record.Resource?.Schema);
        Add(command, "$object", record.Resource?.Object);
        Add(command, "$subresource", record.Resource?.Subresource);
        Add(command, "$hash", record.PayloadHash);
        Add(command, "$classification", record.SqlClassification);
        Add(command, "$result", record.ResultCode);
        Add(command, "$duration", record.Duration?.TotalMilliseconds);
        Add(command, "$metadata", JsonSerializer.Serialize(record.Metadata));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<RazorDbAuditRecord>> ListAsync(string databaseId, string actorId, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, correlation_id, timestamp, actor_id, database_id, operation, status,
                   schema_name, object_name, subresource, payload_hash, sql_classification,
                   result_code, duration_ms, metadata_json
            FROM audit_events WHERE database_id = $database AND actor_id = $actor ORDER BY sequence DESC LIMIT $limit
            """;
        Add(command, "$database", databaseId);
        Add(command, "$actor", actorId);
        Add(command, "$limit", limit);
        List<RazorDbAuditRecord> result = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadAudit(reader));
        return result;
    }

    public async ValueTask<RazorDbJobRecord> CreateAsync(RazorDbJobCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RazorDbJobRecord record = new()
        {
            Id = Guid.NewGuid(), DatabaseId = request.DatabaseId, ActorId = request.ActorId,
            Kind = request.Kind, Status = RazorDbJobStatus.Queued, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow, InputArtifactId = request.InputArtifactId,
            Parameters = request.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal), Version = 1,
        };
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        await using (SqliteCommand quota = connection.CreateCommand())
        {
            quota.Transaction = transaction;
            quota.CommandText = """
                SELECT
                  SUM(CASE WHEN actor_id=$actor THEN 1 ELSE 0 END),
                  COUNT(*)
                FROM jobs WHERE database_id=$database AND status IN ('Queued','Running')
                """;
            Add(quota, "$actor", request.ActorId); Add(quota, "$database", request.DatabaseId);
            await using SqliteDataReader quotaReader = await quota.ExecuteReaderAsync(cancellationToken);
            _ = await quotaReader.ReadAsync(cancellationToken);
            long actorActive = quotaReader.IsDBNull(0) ? 0 : quotaReader.GetInt64(0);
            long databaseActive = quotaReader.IsDBNull(1) ? 0 : quotaReader.GetInt64(1);
            if (actorActive >= 1 || databaseActive >= 2)
                throw new RazorDbException(RazorDbErrorCode.LimitExceeded, actorActive >= 1 ? "Only one active transfer job is allowed per user." : "Only two active transfer jobs are allowed for this database.");
        }
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO jobs (id, database_id, actor_id, kind, status, created_at, updated_at,
                rows_processed, bytes_processed, input_artifact_id, output_artifact_id,
                result_code, parameters_json, version)
            VALUES ($id,$database,$actor,$kind,$status,$created,$updated,0,0,$input,NULL,NULL,$parameters,1)
            """;
        BindJob(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async ValueTask<RazorDbJobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {JobColumns} FROM jobs WHERE id = $id";
        Add(command, "$id", id.ToString("N"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    public async ValueTask<IReadOnlyList<RazorDbJobRecord>> ListAsync(RazorDbJobQuery query, CancellationToken cancellationToken = default)
    {
        int limit = Math.Clamp(query.Limit, 1, 500);
        List<string> predicates = ["1=1"];
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        if (query.DatabaseId is not null) { predicates.Add("database_id = $database"); Add(command, "$database", query.DatabaseId); }
        if (query.ActorId is not null) { predicates.Add("actor_id = $actor"); Add(command, "$actor", query.ActorId); }
        if (query.Status is not null) { predicates.Add("status = $status"); Add(command, "$status", query.Status.Value.ToString()); }
        command.CommandText = $"SELECT {JobColumns} FROM jobs WHERE {string.Join(" AND ", predicates)} ORDER BY created_at DESC LIMIT $limit";
        Add(command, "$limit", limit);
        List<RazorDbJobRecord> result = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadJob(reader));
        return result;
    }

    public async ValueTask<RazorDbJobRecord?> TryUpdateAsync(Guid id, long expectedVersion, RazorDbJobUpdate update, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs SET status=$status, updated_at=$updated, rows_processed=$rows,
                bytes_processed=$bytes, output_artifact_id=$output, result_code=$result,
                cancellation_requested=COALESCE($cancellation_requested,cancellation_requested),
                parameters_json=COALESCE($parameters,parameters_json),
                version=version+1 WHERE id=$id AND version=$version
                  AND status IN ('Queued','Running')
            """;
        Add(command, "$status", update.Status.ToString()); Add(command, "$updated", DateTimeOffset.UtcNow.ToString("O"));
        Add(command, "$rows", update.RowsProcessed); Add(command, "$bytes", update.BytesProcessed);
        Add(command, "$output", update.OutputArtifactId); Add(command, "$result", update.ResultCode);
        Add(command, "$cancellation_requested", update.CancellationRequested is null ? null : update.CancellationRequested.Value ? 1 : 0);
        Add(command, "$parameters", update.Parameters is null ? null : JsonSerializer.Serialize(update.Parameters));
        Add(command, "$id", id.ToString("N")); Add(command, "$version", expectedVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? await GetAsync(id, cancellationToken) : null;
    }

    public async ValueTask<RazorDbJobRecord?> RequestCancellationAsync(
        Guid id,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE jobs SET cancellation_requested=1, updated_at=$updated,
                result_code='cancellation-requested', version=version+1
            WHERE id=$id AND actor_id=$actor AND status IN ('Queued','Running')
              AND cancellation_requested=0
            RETURNING {JobColumns}
            """;
        Add(command, "$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$id", id.ToString("N"));
        Add(command, "$actor", actorId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    public async ValueTask<RazorDbOperationToken> IssueAsync(RazorDbOperationTokenContext context, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO operation_tokens (token_hash, context_hash, expires_at) VALUES ($token,$context,$expires)";
        Add(command, "$token", Sha256(token)); Add(command, "$context", ContextHash(context)); Add(command, "$expires", expiresAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new RazorDbOperationToken(token, expiresAt);
    }

    public async ValueTask<RazorDbOperationTokenResult> ConsumeAsync(string token, RazorDbOperationTokenContext expectedContext, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM operation_tokens WHERE token_hash=$token AND context_hash=$context AND expires_at>$now RETURNING token_hash";
        Add(command, "$token", Sha256(token)); Add(command, "$context", ContextHash(expectedContext)); Add(command, "$now", now.ToUniversalTime().ToString("O"));
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return value is null ? new RazorDbOperationTokenResult(false, "invalid-expired-or-consumed") : new RazorDbOperationTokenResult(true);
    }

    public async ValueTask CleanupAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM operation_tokens WHERE expires_at <= $now;
            DELETE FROM jobs
            WHERE status IN ('Completed','Failed','Cancelled') AND updated_at <= $job_cutoff;
            """;
        Add(command, "$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$job_cutoff", now.Subtract(options.Value.TerminalJobRetention)
            .ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<string?> GetAsync(
        string actorId,
        string databaseId,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM preferences WHERE actor_id=$actor AND database_id=$database AND key=$key";
        Add(command, "$actor", actorId); Add(command, "$database", databaseId); Add(command, "$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async ValueTask SetAsync(
        string actorId,
        string databaseId,
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO preferences (actor_id,database_id,key,value,updated_at)
            VALUES ($actor,$database,$key,$value,$updated)
            ON CONFLICT(actor_id,database_id,key) DO UPDATE SET value=excluded.value,updated_at=excluded.updated_at
            """;
        Add(command, "$actor", actorId); Add(command, "$database", databaseId); Add(command, "$key", key);
        Add(command, "$value", value); Add(command, "$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        SqliteConnectionStringBuilder builder = new() { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = true };
        SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(paths.Root);
            Directory.CreateDirectory(paths.ArtifactRoot);
            string probe = Path.Combine(paths.Root, $".{Guid.NewGuid():N}.probe");
            await File.WriteAllBytesAsync(probe, [], cancellationToken);
            File.Delete(probe);
            SqliteConnectionStringBuilder builder = new() { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = true };
            await using SqliteConnection connection = new(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureJobCancellationColumnAsync(connection, cancellationToken);
            _initialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string ContextHash(RazorDbOperationTokenContext context) => Sha256(JsonSerializer.Serialize(context));

    private static RazorDbAuditRecord ReadAudit(SqliteDataReader reader) => new()
    {
        Id = Guid.ParseExact(reader.GetString(0), "N"), CorrelationId = Guid.ParseExact(reader.GetString(1), "N"),
        Timestamp = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture), ActorId = reader.GetString(3),
        DatabaseId = reader.GetString(4), Operation = Enum.Parse<RazorDbOperation>(reader.GetString(5)),
        Status = Enum.Parse<RazorDbAuditStatus>(reader.GetString(6)),
        Resource = reader.IsDBNull(7) && reader.IsDBNull(8) && reader.IsDBNull(9) ? null : new RazorDbResource(GetString(reader, 7), GetString(reader, 8), GetString(reader, 9)),
        PayloadHash = GetString(reader, 10), SqlClassification = GetString(reader, 11), ResultCode = GetString(reader, 12),
        Duration = reader.IsDBNull(13) ? null : TimeSpan.FromMilliseconds(reader.GetDouble(13)),
        Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(14)) ?? new Dictionary<string, string>(),
    };

    private const string JobColumns = "id,database_id,actor_id,kind,status,cancellation_requested,created_at,updated_at,rows_processed,bytes_processed,input_artifact_id,output_artifact_id,result_code,parameters_json,version";
    private static RazorDbJobRecord ReadJob(SqliteDataReader reader) => new()
    {
        Id = Guid.ParseExact(reader.GetString(0), "N"), DatabaseId = reader.GetString(1), ActorId = reader.GetString(2),
        Kind = Enum.Parse<RazorDbJobKind>(reader.GetString(3)), Status = Enum.Parse<RazorDbJobStatus>(reader.GetString(4)),
        CancellationRequested = reader.GetInt32(5) != 0,
        CreatedAt = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture), UpdatedAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
        RowsProcessed = reader.GetInt64(8), BytesProcessed = reader.GetInt64(9), InputArtifactId = GetString(reader, 10),
        OutputArtifactId = GetString(reader, 11), ResultCode = GetString(reader, 12),
        Parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(13)) ?? new Dictionary<string, string>(), Version = reader.GetInt64(14),
    };

    private static string? GetString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static void BindJob(SqliteCommand command, RazorDbJobRecord record)
    {
        Add(command, "$id", record.Id.ToString("N")); Add(command, "$database", record.DatabaseId); Add(command, "$actor", record.ActorId);
        Add(command, "$kind", record.Kind.ToString()); Add(command, "$status", record.Status.ToString()); Add(command, "$created", record.CreatedAt.ToString("O"));
        Add(command, "$updated", record.UpdatedAt.ToString("O")); Add(command, "$input", record.InputArtifactId); Add(command, "$parameters", JsonSerializer.Serialize(record.Parameters));
    }

    private static async Task EnsureJobCancellationColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(jobs)";
        bool exists = false;
        await using (SqliteDataReader reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "cancellation_requested", StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (exists) return;

        await using SqliteCommand migrate = connection.CreateCommand();
        migrate.CommandText = "ALTER TABLE jobs ADD COLUMN cancellation_requested INTEGER NOT NULL DEFAULT 0";
        await migrate.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SchemaSql = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=FULL;
        CREATE TABLE IF NOT EXISTS audit_events (
          sequence INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE, correlation_id TEXT NOT NULL,
          timestamp TEXT NOT NULL, actor_id TEXT NOT NULL, database_id TEXT NOT NULL,
          operation TEXT NOT NULL, status TEXT NOT NULL, schema_name TEXT, object_name TEXT,
          subresource TEXT, payload_hash TEXT, sql_classification TEXT, result_code TEXT,
          duration_ms REAL, metadata_json TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_audit_database_time ON audit_events(database_id, sequence DESC);
        CREATE TABLE IF NOT EXISTS jobs (
          id TEXT PRIMARY KEY, database_id TEXT NOT NULL, actor_id TEXT NOT NULL, kind TEXT NOT NULL,
          status TEXT NOT NULL, cancellation_requested INTEGER NOT NULL DEFAULT 0,
          created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
          rows_processed INTEGER NOT NULL, bytes_processed INTEGER NOT NULL, input_artifact_id TEXT,
          output_artifact_id TEXT, result_code TEXT, parameters_json TEXT NOT NULL, version INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_jobs_queue ON jobs(status, created_at);
        CREATE INDEX IF NOT EXISTS ix_jobs_database_actor ON jobs(database_id, actor_id, created_at DESC);
        CREATE TABLE IF NOT EXISTS operation_tokens (
          token_hash TEXT PRIMARY KEY, context_hash TEXT NOT NULL, expires_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_tokens_expires ON operation_tokens(expires_at);
        CREATE TABLE IF NOT EXISTS preferences (
          actor_id TEXT NOT NULL, database_id TEXT NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL,
          updated_at TEXT NOT NULL, PRIMARY KEY(actor_id,database_id,key));
        """;
}
