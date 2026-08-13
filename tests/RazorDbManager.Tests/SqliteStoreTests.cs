using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RazorDbManager.Core;
using RazorDbManager.Tests.Infrastructure;

namespace RazorDbManager.Tests;

public sealed class SqliteStoreTests
{
    [Fact]
    public async Task OperationToken_IsActorBoundSingleUseAndExpires()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost();
        IRazorDbOperationTokenStore store = host.Services.GetRequiredService<IRazorDbOperationTokenStore>();
        RazorDbOperationTokenContext alice = TokenContext("alice");
        RazorDbOperationTokenContext bob = TokenContext("bob");

        RazorDbOperationToken token = await store.IssueAsync(alice, TimeSpan.FromMinutes(2));

        RazorDbOperationTokenResult wrongActor = await store.ConsumeAsync(token.Value, bob, DateTimeOffset.UtcNow);
        RazorDbOperationTokenResult accepted = await store.ConsumeAsync(token.Value, alice, DateTimeOffset.UtcNow);
        RazorDbOperationTokenResult replay = await store.ConsumeAsync(token.Value, alice, DateTimeOffset.UtcNow);
        RazorDbOperationToken expired = await store.IssueAsync(alice, TimeSpan.FromMilliseconds(1));
        RazorDbOperationTokenResult expiredResult = await store.ConsumeAsync(expired.Value, alice, expired.ExpiresAt.AddTicks(1));

        Assert.False(wrongActor.IsValid);
        Assert.True(accepted.IsValid);
        Assert.False(replay.IsValid);
        Assert.False(expiredResult.IsValid);
        Assert.Equal("invalid-expired-or-consumed", replay.ReasonCode);
    }

    [Fact]
    public async Task JobStore_EnforcesQuotasAndOptimisticConcurrency()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost();
        IRazorDbJobStore store = host.Services.GetRequiredService<IRazorDbJobStore>();

        RazorDbJobRecord first = await store.CreateAsync(Job("alice"));
        RazorDbException sameActor = await Assert.ThrowsAsync<RazorDbException>(async () =>
            await store.CreateAsync(Job("alice")));
        RazorDbJobRecord second = await store.CreateAsync(Job("bob"));
        RazorDbException databaseQuota = await Assert.ThrowsAsync<RazorDbException>(async () =>
            await store.CreateAsync(Job("carol")));

        RazorDbJobRecord? updated = await store.TryUpdateAsync(first.Id, first.Version, new RazorDbJobUpdate
        {
            Status = RazorDbJobStatus.Running,
            RowsProcessed = 12,
            BytesProcessed = 345,
        });
        RazorDbJobRecord? stale = await store.TryUpdateAsync(first.Id, first.Version, new RazorDbJobUpdate
        {
            Status = RazorDbJobStatus.Completed,
        });

        Assert.Equal(RazorDbErrorCode.LimitExceeded, sameActor.Code);
        Assert.Equal(RazorDbErrorCode.LimitExceeded, databaseQuota.Code);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotNull(updated);
        Assert.Equal(first.Version + 1, updated.Version);
        Assert.Equal(12, updated.RowsProcessed);
        Assert.Null(stale);
    }

    [Theory]
    [InlineData(RazorDbJobStatus.Completed)]
    [InlineData(RazorDbJobStatus.Failed)]
    [InlineData(RazorDbJobStatus.Cancelled)]
    public async Task JobStore_TerminalStatesAreImmutable(RazorDbJobStatus terminalStatus)
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost();
        IRazorDbJobStore store = host.Services.GetRequiredService<IRazorDbJobStore>();
        RazorDbJobRecord queued = await store.CreateAsync(Job("alice"));
        RazorDbJobRecord terminal = Assert.IsType<RazorDbJobRecord>(await store.TryUpdateAsync(
            queued.Id,
            queued.Version,
            new RazorDbJobUpdate { Status = terminalStatus, ResultCode = "original" }));

        RazorDbJobRecord? overwritten = await store.TryUpdateAsync(
            terminal.Id,
            terminal.Version,
            new RazorDbJobUpdate { Status = RazorDbJobStatus.Failed, ResultCode = "overwritten" });
        RazorDbJobRecord persisted = Assert.IsType<RazorDbJobRecord>(await store.GetAsync(terminal.Id));

        Assert.Null(overwritten);
        Assert.Equal(terminalStatus, persisted.Status);
        Assert.Equal("original", persisted.ResultCode);
        Assert.Equal(terminal.Version, persisted.Version);
    }

    [Fact]
    public async Task StoreInitializer_MarksInterruptedImportOutcomeUnknownPreservesInputAndAudits()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost();
        IRazorDbJobStore jobs = host.Services.GetRequiredService<IRazorDbJobStore>();
        IRazorDbArtifactStore artifacts = host.Services.GetRequiredService<IRazorDbArtifactStore>();
        IRazorDbAuditSink auditSink = host.Services.GetRequiredService<IRazorDbAuditSink>();
        IRazorDbAuditReader auditReader = host.Services.GetRequiredService<IRazorDbAuditReader>();
        byte[] content = "id,name\r\n1,Alice\r\n"u8.ToArray();
        RazorDbArtifactWriteSession write = await artifacts.CreateWriteAsync(new RazorDbArtifactCreateRequest(
            "Main", "alice", "import.csv", "text/csv", DateTimeOffset.UtcNow.AddHours(1)));
        await write.Content.WriteAsync(content);
        await write.Content.DisposeAsync();
        string digest = Convert.ToHexStringLower(SHA256.HashData(content));
        RazorDbArtifactDescriptor input = await artifacts.CompleteWriteAsync(write.Descriptor.Id, content.Length, digest);
        RazorDbJobRecord queued = await jobs.CreateAsync(new RazorDbJobCreateRequest(
            "Main",
            "alice",
            RazorDbJobKind.CsvImport,
            input.Id,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["inputArtifactId"] = input.Id,
                ["inputArtifactLength"] = content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["inputArtifactSha256"] = digest,
                ["authorizationToken"] = "sensitive-one-time-token",
            }));
        RazorDbJobRecord running = Assert.IsType<RazorDbJobRecord>(await jobs.TryUpdateAsync(
            queued.Id,
            queued.Version,
            new RazorDbJobUpdate { Status = RazorDbJobStatus.Running }));
        RazorDbStoreInitializer initializer = new(
            jobs,
            artifacts,
            auditSink,
            NullLogger<RazorDbStoreInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        RazorDbJobRecord recovered = Assert.IsType<RazorDbJobRecord>(await jobs.GetAsync(running.Id));
        Assert.Equal(RazorDbJobStatus.Failed, recovered.Status);
        Assert.Equal("host-interrupted-outcome-unknown", recovered.ResultCode);
        Assert.False(recovered.Parameters.ContainsKey("authorizationToken"));
        RazorDbArtifactReadSession preserved = Assert.IsType<RazorDbArtifactReadSession>(
            await artifacts.OpenReadAsync(input.Id));
        await preserved.Content.DisposeAsync();
        RazorDbAuditRecord audit = Assert.Single(await auditReader.ListAsync("Main", "alice", 10, CancellationToken.None));
        Assert.Equal(RazorDbOperation.Import, audit.Operation);
        Assert.Equal(RazorDbAuditStatus.Failed, audit.Status);
        Assert.Equal("host-interrupted-outcome-unknown", audit.ResultCode);
        Assert.Equal(digest, audit.PayloadHash);
        Assert.Equal(running.Id.ToString("N"), audit.Metadata["jobId"]);
        Assert.Equal("startup", audit.Metadata["recovery"]);
    }

    [Fact]
    public async Task JobStore_CancellationIsOwnerBoundAtomicAndLeavesTerminalTransitionToWorker()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost();
        IRazorDbJobStore store = host.Services.GetRequiredService<IRazorDbJobStore>();
        RazorDbJobRecord queued = await store.CreateAsync(Job("alice"));

        RazorDbJobRecord? wrongOwner = await store.RequestCancellationAsync(queued.Id, "bob");
        RazorDbJobRecord? requested = await store.RequestCancellationAsync(queued.Id, "alice");
        RazorDbJobRecord? replay = await store.RequestCancellationAsync(queued.Id, "alice");

        Assert.Null(wrongOwner);
        Assert.NotNull(requested);
        Assert.Equal(RazorDbJobStatus.Queued, requested.Status);
        Assert.True(requested.CancellationRequested);
        Assert.Equal("cancellation-requested", requested.ResultCode);
        Assert.Equal(queued.Version + 1, requested.Version);
        Assert.Null(replay);
    }

    [Fact]
    public async Task JobStore_TerminalUpdateAtomicallyReplacesProtectedParameters()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost();
        IRazorDbJobStore store = host.Services.GetRequiredService<IRazorDbJobStore>();
        RazorDbJobRecord queued = await store.CreateAsync(new RazorDbJobCreateRequest(
            "Main",
            "alice",
            RazorDbJobKind.CsvExport,
            Parameters: new Dictionary<string, string>
            {
                [RowExportQueryProtector.PayloadParameter] = "protected-secret-payload",
                [RowExportQueryProtector.LegacyPlaintextParameter] = "legacy-plaintext-secret",
                [RowExportQueryProtector.HashParameter] = new string('a', 64),
                ["authorizationToken"] = "one-time-token",
            }));
        IReadOnlyDictionary<string, string> sanitized = RowExportQueryProtector.TerminalParameters(queued.Parameters);

        RazorDbJobRecord? completed = await store.TryUpdateAsync(queued.Id, queued.Version, new RazorDbJobUpdate
        {
            Status = RazorDbJobStatus.Completed,
            Parameters = sanitized,
        });

        Assert.NotNull(completed);
        Assert.False(completed.Parameters.ContainsKey(RowExportQueryProtector.PayloadParameter));
        Assert.False(completed.Parameters.ContainsKey("authorizationToken"));
        Assert.Equal("cleared", completed.Parameters[RowExportQueryProtector.ClearedParameter]);
        Assert.Equal(new string('a', 64), completed.Parameters[RowExportQueryProtector.HashParameter]);

        SqliteConnectionStringBuilder connectionString = new()
        {
            DataSource = Path.Combine(host.StoragePath, "state.db"),
            Mode = SqliteOpenMode.ReadOnly,
        };
        await using SqliteConnection connection = new(connectionString.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT parameters_json FROM jobs WHERE id=$id";
        command.Parameters.AddWithValue("$id", queued.Id.ToString("N"));
        string json = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.DoesNotContain("protected-secret-payload", json, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-plaintext-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("one-time-token", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtifactStore_SanitizesNamesRequiresCompletionAndCleansExpiredFiles()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost();
        IRazorDbArtifactStore store = host.Services.GetRequiredService<IRazorDbArtifactStore>();
        byte[] content = "id,name\n1,Alice\n"u8.ToArray();
        RazorDbArtifactWriteSession write = await store.CreateWriteAsync(new RazorDbArtifactCreateRequest(
            "Main",
            "alice",
            "..\\..\\customer.csv",
            "text/csv",
            DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.Equal("customer.csv", write.Descriptor.FileName);

        RazorDbArtifactWriteSession unixPathWrite = await store.CreateWriteAsync(new RazorDbArtifactCreateRequest(
            "Main",
            "alice",
            "../../report?.csv",
            "text/csv",
            DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.Equal("report.csv", unixPathWrite.Descriptor.FileName);
        await unixPathWrite.Content.DisposeAsync();
        await store.DeleteAsync(unixPathWrite.Descriptor.Id);

        Assert.Null(await store.OpenReadAsync(write.Descriptor.Id));
        await write.Content.WriteAsync(content);
        await write.Content.DisposeAsync();
        string digest = Convert.ToHexStringLower(SHA256.HashData(content));
        RazorDbArtifactDescriptor completed = await store.CompleteWriteAsync(write.Descriptor.Id, content.Length, digest);

        RazorDbArtifactReadSession read = Assert.IsType<RazorDbArtifactReadSession>(
            await store.OpenReadAsync(completed.Id));
        await using Stream readContent = read.Content;
        using MemoryStream copy = new();
        await readContent.CopyToAsync(copy);

        Assert.Equal(content, copy.ToArray());
        Assert.Equal(digest, read.Descriptor.Sha256);

        RazorDbArtifactWriteSession expired = await store.CreateWriteAsync(new RazorDbArtifactCreateRequest(
            "Main", "alice", "old.csv", "text/csv", DateTimeOffset.UtcNow.AddMinutes(-1)));
        await expired.Content.DisposeAsync();
        _ = await store.CompleteWriteAsync(expired.Descriptor.Id, 0, Convert.ToHexStringLower(SHA256.HashData([])));

        Assert.Equal(1, await store.DeleteExpiredAsync(DateTimeOffset.UtcNow));
        Assert.Null(await store.OpenReadAsync(expired.Descriptor.Id));
    }

    [Fact]
    public async Task AuditSink_AppendsRecordsWithoutPersistingPayloadText()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost();
        IRazorDbAuditSink sink = host.Services.GetRequiredService<IRazorDbAuditSink>();
        const string rawSql = "DROP TABLE customers";
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawSql)));
        RazorDbAuditRecord record = new()
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            ActorId = "alice",
            DatabaseId = "Main",
            Operation = RazorDbOperation.ExecuteSql,
            Status = RazorDbAuditStatus.Completed,
            Resource = new RazorDbResource("app", "customers"),
            PayloadHash = hash,
            SqlClassification = "DDL",
            ResultCode = "ok",
        };

        await sink.AppendAsync(record);

        SqliteConnectionStringBuilder connectionString = new()
        {
            DataSource = Path.Combine(host.StoragePath, "state.db"),
            Mode = SqliteOpenMode.ReadOnly,
        };
        await using SqliteConnection connection = new(connectionString.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT actor_id, payload_hash, sql_classification, metadata_json FROM audit_events WHERE id=$id";
        command.Parameters.AddWithValue("$id", record.Id.ToString("N"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("alice", reader.GetString(0));
        Assert.Equal(hash, reader.GetString(1));
        Assert.Equal("DDL", reader.GetString(2));
        Assert.DoesNotContain(rawSql, reader.GetString(3), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreferenceStore_IsScopedByActorAndDatabaseAndUpserts()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost();
        IRazorDbPreferenceStore store = host.Services.GetRequiredService<IRazorDbPreferenceStore>();

        await store.SetAsync("alice", "Main", "page-size", "100");
        await store.SetAsync("alice", "Main", "page-size", "250");

        Assert.Equal("250", await store.GetAsync("alice", "Main", "page-size"));
        Assert.Null(await store.GetAsync("bob", "Main", "page-size"));
        Assert.Null(await store.GetAsync("alice", "Other", "page-size"));
    }

    [Fact]
    public async Task Cleanup_RemovesOnlyExpiredTokensAndOldTerminalJobs()
    {
        await using RazorDbTestHost host = RazorDbTestHost.CreateStoreHost(
            configure: options => options.TerminalJobRetention = TimeSpan.FromHours(1));
        IRazorDbJobStore jobs = host.Services.GetRequiredService<IRazorDbJobStore>();
        IRazorDbStoreMaintenance maintenance = host.Services.GetRequiredService<IRazorDbStoreMaintenance>();

        RazorDbJobRecord queued = await jobs.CreateAsync(Job("alice"));
        RazorDbJobRecord completed = Assert.IsType<RazorDbJobRecord>(await jobs.TryUpdateAsync(
            queued.Id,
            queued.Version,
            new RazorDbJobUpdate { Status = RazorDbJobStatus.Completed }));

        await maintenance.CleanupAsync(completed.UpdatedAt.AddMinutes(59), CancellationToken.None);
        Assert.NotNull(await jobs.GetAsync(completed.Id));

        await maintenance.CleanupAsync(completed.UpdatedAt.AddHours(1).AddTicks(1), CancellationToken.None);
        Assert.Null(await jobs.GetAsync(completed.Id));
    }

    private static RazorDbOperationTokenContext TokenContext(string actor) => new(
        actor,
        "Main",
        RazorDbOperation.ExecuteSchema,
        new RazorDbResource("app", "customers"),
        "schema-fingerprint",
        "payload-hash");

    private static RazorDbJobCreateRequest Job(string actor) => new("Main", actor, RazorDbJobKind.CsvExport);
}
