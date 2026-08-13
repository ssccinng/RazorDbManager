using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RazorDbManager.Core;

namespace RazorDbManager.Tests;

public sealed class WorkspacePagingTests
{
    [Fact]
    public async Task QueryRows_PreservesCursorRelativeOffsetWhileClampingPageSize()
    {
        string root = Path.Combine(Path.GetTempPath(), "RazorDbManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var data = new RecordingDataProvider();
        using IHost host = CreateHost(root, data);
        try
        {
            using IServiceScope scope = host.Services.CreateScope();
            DatabaseWorkspace workspace = scope.ServiceProvider.GetRequiredService<DatabaseWorkspace>();
            RowCursor anchor = new([DbValue.FromString("anchor"), DbValue.FromSignedInteger(10)]);

            await workspace.QueryRowsAsync(
                new RowQueryRequest(
                    "Main",
                    new DbObjectName("app", "items"),
                    PageRequest.FromCursor(5_000, anchor, relativeOffset: 37)),
                CancellationToken.None);

            RowQueryRequest forwarded = Assert.IsType<RowQueryRequest>(data.Request);
            Assert.Equal(500, forwarded.Page.PageSize);
            Assert.Same(anchor, forwarded.Page.After);
            Assert.Equal(37, forwarded.Page.Offset);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static IHost CreateHost(string root, RecordingDataProvider data)
    {
        DatabaseRegistration registration = new()
        {
            Id = "Main",
            ProviderName = "test",
            ConnectionStringName = "Unused",
            EnabledCapabilities = RazorDbCapabilitySets.ReadOnly,
            AllowedSchemas = ["app"],
        };
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddRazorComponents().AddInteractiveServerComponents();
                services.AddAuthorizationBuilder()
                    .AddPolicy(RazorDbManagerPolicies.Access, policy => policy.RequireAuthenticatedUser());
                services.AddSingleton<AuthenticationStateProvider>(new TestAuthenticationStateProvider("alice"));
                services.AddSingleton<IRazorDbProviderRegistry>(new TestProviderRegistry(registration, data));
                services.AddRazorDbManager(options =>
                {
                    options.DefaultDatabaseId = registration.Id;
                    options.StoragePath = root;
                });
            })
            .Build();
    }

    private sealed class RecordingDataProvider : IRazorDbDataProvider
    {
        public RowQueryRequest? Request { get; private set; }

        public ValueTask<RowPage> QueryRowsAsync(
            RowQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return ValueTask.FromResult(new RowPage([], [], null, null, false, "fingerprint"));
        }

        public ValueTask<RowMutationResult> InsertRowAsync(InsertRowRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<RowMutationResult>(new NotSupportedException());

        public ValueTask<RowMutationResult> UpdateRowAsync(UpdateRowRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<RowMutationResult>(new NotSupportedException());

        public ValueTask<RowMutationResult> DeleteRowAsync(DeleteRowRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<RowMutationResult>(new NotSupportedException());
    }

    private sealed class TestProviderRegistry(
        DatabaseRegistration registration,
        IRazorDbDataProvider data) : IRazorDbProviderRegistry
    {
        public IReadOnlyCollection<DatabaseRegistration> Registrations => [registration];

        public DatabaseRegistration GetRequiredRegistration(string databaseId) =>
            string.Equals(databaseId, registration.Id, StringComparison.Ordinal)
                ? registration
                : throw new KeyNotFoundException(databaseId);

        public ValueTask<IRazorDbProvider> GetProviderAsync(
            string databaseId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IRazorDbProvider>(new TestProvider(GetRequiredRegistration(databaseId), data));
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
}
