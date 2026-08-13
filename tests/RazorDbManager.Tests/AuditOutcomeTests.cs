using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RazorDbManager.Core;

namespace RazorDbManager.Tests;

public sealed class AuditOutcomeTests
{
    [Fact]
    public async Task InsertRow_WhenStartedAuditFails_DoesNotInvokeProvider()
    {
        RecordingDataProvider data = new();
        RecordingAuditSink audit = new(record =>
            record.Status == RazorDbAuditStatus.Started
                ? new IOException("Audit store unavailable.")
                : null);
        RecordingLoggerProvider logs = new();

        await using WorkspaceFixture fixture = CreateFixture(data, audit, logs);

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => fixture.InsertRowAsync().AsTask());

        Assert.Equal("Audit store unavailable.", error.Message);
        Assert.Equal(0, data.InsertCalls);
        Assert.Equal([RazorDbAuditStatus.Started], audit.Records.Select(record => record.Status));
    }

    [Fact]
    public async Task InsertRow_WhenCompletedAuditFails_ReturnsSuccessfulProviderResultAndLogsCritical()
    {
        RecordingDataProvider data = new();
        RecordingAuditSink audit = new(record =>
            record.Status == RazorDbAuditStatus.Completed
                ? new IOException("Audit store unavailable.")
                : null);
        RecordingLoggerProvider logs = new();

        await using WorkspaceFixture fixture = CreateFixture(data, audit, logs);

        RowMutationResult result = await fixture.InsertRowAsync();

        Assert.Equal(RowMutationStatus.Succeeded, result.Status);
        Assert.Equal(1, result.AffectedRows);
        Assert.Equal(1, data.InsertCalls);
        Assert.Equal(
            [RazorDbAuditStatus.Started, RazorDbAuditStatus.Completed],
            audit.Records.Select(record => record.Status));
        Assert.Contains(logs.Entries, entry =>
            entry.Level == LogLevel.Critical
            && entry.Exception is null
            && entry.Message.Contains(nameof(IOException), StringComparison.Ordinal)
            && entry.Message.Contains("could not persist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeleteRows_AuthorizesAuditsAndInvokesBatchProviderOnce()
    {
        RecordingDataProvider data = new();
        RecordingAuditSink audit = new(_ => null);
        RecordingLoggerProvider logs = new();
        await using WorkspaceFixture fixture = CreateFixture(data, audit, logs);

        BatchRowMutationResult result = await fixture.DeleteRowsAsync();

        Assert.Equal(RowMutationStatus.Succeeded, result.Status);
        Assert.Equal(2, result.AffectedRows);
        Assert.Equal(1, data.BatchDeleteCalls);
        Assert.Equal(
            [RazorDbAuditStatus.Started, RazorDbAuditStatus.Completed],
            audit.Records.Select(record => record.Status));
        Assert.All(audit.Records, record => Assert.Equal(RazorDbOperation.DeleteRows, record.Operation));
    }

    private static WorkspaceFixture CreateFixture(
        RecordingDataProvider data,
        RecordingAuditSink audit,
        RecordingLoggerProvider logs)
    {
        string storagePath = Path.Combine(
            Path.GetTempPath(),
            "RazorDbManager.Tests",
            Guid.NewGuid().ToString("N"));
        DatabaseRegistration registration = new()
        {
            Id = "Main",
            ProviderName = "test",
            ConnectionStringName = "Unused",
            EnabledCapabilities = RazorDbCapabilitySets.DataEditor,
            AllowedSchemas = ["app"],
        };
        TestProvider provider = new(registration, data);

        IHost host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddRazorComponents().AddInteractiveServerComponents();
                services.AddAuthorizationBuilder()
                    .AddPolicy(RazorDbManagerPolicies.Access, policy => policy.RequireAuthenticatedUser());
                services.AddSingleton<AuthenticationStateProvider>(new TestAuthenticationStateProvider("alice"));
                services.AddSingleton<IRazorDbProviderRegistry>(new TestProviderRegistry(registration, provider));
                services.AddSingleton<IRazorDbAuditSink>(audit);
                services.AddLogging(builder => builder.AddProvider(logs));
                services.AddRazorDbManager(options =>
                {
                    options.DefaultDatabaseId = registration.Id;
                    options.StoragePath = storagePath;
                });
            })
            .Build();

        return new WorkspaceFixture(host, storagePath);
    }

    private sealed class WorkspaceFixture(IHost host, string storagePath) : IAsyncDisposable
    {
        private readonly IServiceScope _scope = host.Services.CreateScope();

        public ValueTask<RowMutationResult> InsertRowAsync()
        {
            Type workspaceType = typeof(RazorDbManagerRouting).Assembly.GetType(
                "RazorDbManager.DatabaseWorkspace",
                throwOnError: true)!;
            object workspace = _scope.ServiceProvider.GetRequiredService(workspaceType);
            MethodInfo method = workspaceType.GetMethod(
                "InsertRowAsync",
                BindingFlags.Public | BindingFlags.Instance)!;
            DbTableMetadata table = new(
                new DbObjectName("app", "users"),
                DbObjectKind.Table,
                [],
                [],
                [],
                [],
                null,
                "schema-fingerprint");
            object? invocation = method.Invoke(
                workspace,
                ["Main", table, new Dictionary<string, EditValue>(StringComparer.Ordinal), CancellationToken.None]);
            return (ValueTask<RowMutationResult>)invocation!;
        }

        public ValueTask<BatchRowMutationResult> DeleteRowsAsync()
        {
            Type workspaceType = typeof(RazorDbManagerRouting).Assembly.GetType(
                "RazorDbManager.DatabaseWorkspace",
                throwOnError: true)!;
            object workspace = _scope.ServiceProvider.GetRequiredService(workspaceType);
            MethodInfo method = workspaceType.GetMethod(
                "DeleteRowsAsync",
                BindingFlags.Public | BindingFlags.Instance)!;
            DbTableMetadata table = Table();
            DbRow Row(long id) => new(
                [DbValue.FromSignedInteger(id), DbValue.FromString($"name-{id}")],
                new RowIdentity("PRIMARY", new Dictionary<string, DbValue>
                {
                    ["id"] = DbValue.FromSignedInteger(id),
                }));
            object? invocation = method.Invoke(
                workspace,
                ["Main", table, new[] { Row(1), Row(2) }, CancellationToken.None]);
            return (ValueTask<BatchRowMutationResult>)invocation!;
        }

        private static DbTableMetadata Table() => new(
            new DbObjectName("app", "users"),
            DbObjectKind.Table,
            [
                new DbColumnMetadata("id", 0, new DbTypeDescriptor("bigint", DbDataKind.SignedInteger), false),
                new DbColumnMetadata("name", 1, new DbTypeDescriptor("varchar(100)", DbDataKind.Text), true),
            ],
            [new DbKeyMetadata("PRIMARY", DbKeyKind.Primary, ["id"], true)],
            [],
            [],
            new DbKeyMetadata("PRIMARY", DbKeyKind.Primary, ["id"], true),
            "schema-fingerprint",
            "InnoDB");

        public ValueTask DisposeAsync()
        {
            _scope.Dispose();
            host.Dispose();
            try
            {
                Directory.Delete(storagePath, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAuditSink(Func<RazorDbAuditRecord, Exception?> failure) : IRazorDbAuditSink
    {
        public List<RazorDbAuditRecord> Records { get; } = [];

        public ValueTask AppendAsync(
            RazorDbAuditRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            Exception? error = failure(record);
            return error is null ? ValueTask.CompletedTask : ValueTask.FromException(error);
        }
    }

    private sealed class RecordingDataProvider : IRazorDbDataProvider
    {
        public int InsertCalls { get; private set; }
        public int BatchDeleteCalls { get; private set; }

        public ValueTask<RowMutationResult> InsertRowAsync(
            InsertRowRequest request,
            CancellationToken cancellationToken = default)
        {
            InsertCalls++;
            return ValueTask.FromResult(new RowMutationResult(
                RowMutationStatus.Succeeded,
                1,
                null,
                request.ExpectedSchemaFingerprint));
        }

        public ValueTask<RowPage> QueryRowsAsync(
            RowQueryRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<RowPage>(new NotSupportedException());

        public ValueTask<RowMutationResult> UpdateRowAsync(
            UpdateRowRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<RowMutationResult>(new NotSupportedException());

        public ValueTask<RowMutationResult> DeleteRowAsync(
            DeleteRowRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<RowMutationResult>(new NotSupportedException());

        public ValueTask<BatchRowMutationResult> DeleteRowsAsync(
            DeleteRowsRequest request,
            CancellationToken cancellationToken = default)
        {
            BatchDeleteCalls++;
            return ValueTask.FromResult(new BatchRowMutationResult(
                RowMutationStatus.Succeeded,
                request.Rows.Count,
                request.Rows.Count,
                request.ExpectedSchemaFingerprint));
        }
    }

    private sealed class TestProvider(
        DatabaseRegistration registration,
        IRazorDbDataProvider data) : IRazorDbProvider
    {
        public string ProviderName => "test";
        public DatabaseRegistration Registration => registration;
        public IRazorDbMetadataProvider Metadata => throw new NotSupportedException();
        public IRazorDbDataProvider Data => data;
        public IRazorDbSchemaProvider Schema => throw new NotSupportedException();
        public IRazorDbSqlProvider Sql => throw new NotSupportedException();
        public IRazorDbTransferProvider Transfer => throw new NotSupportedException();
    }

    private sealed class TestProviderRegistry(
        DatabaseRegistration registration,
        IRazorDbProvider provider) : IRazorDbProviderRegistry
    {
        public IReadOnlyCollection<DatabaseRegistration> Registrations => [registration];

        public DatabaseRegistration GetRequiredRegistration(string databaseId) =>
            string.Equals(databaseId, registration.Id, StringComparison.Ordinal)
                ? registration
                : throw new KeyNotFoundException(databaseId);

        public ValueTask<IRazorDbProvider> GetProviderAsync(
            string databaseId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                string.Equals(databaseId, registration.Id, StringComparison.Ordinal)
                    ? provider
                    : throw new KeyNotFoundException(databaseId));
    }

    private sealed class TestAuthenticationStateProvider(string actorId) : AuthenticationStateProvider
    {
        private readonly AuthenticationState _state = new(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, actorId),
                new Claim(ClaimTypes.Name, actorId),
            ],
            authenticationType: "Test")));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(_state);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
