using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RazorDbManager.Core;

namespace RazorDbManager.Tests;

public sealed class WorkspaceFilteredExportProtectionTests
{
    [Fact]
    public async Task QueueExport_PersistsProtectedRowSelectionAndBindsItsPlaintextDigest()
    {
        const string sensitiveValue = "private-filter-value@example.test";
        string root = Path.Combine(Path.GetTempPath(), "RazorDbManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IHost host = CreateHost(root);
        try
        {
            using IServiceScope scope = host.Services.CreateScope();
            DatabaseWorkspace workspace = scope.ServiceProvider.GetRequiredService<DatabaseWorkspace>();
            RowExportQuery rowQuery = new(
                new ComparisonFilter("email", DbComparisonOperator.Equal, DbValue.FromString(sensitiveValue)),
                [new DbSort("email")],
                ["email"]);
            RazorDbJobRecord queued = await workspace.QueueExportAsync(
                "Main",
                TransferFormat.Csv,
                [new DbObjectName("app", "contacts")],
                includeSchema: false,
                includeData: true,
                compressWithGzip: false,
                rowQuery,
                CancellationToken.None);

            Assert.False(queued.Parameters.ContainsKey("rowQuery"));
            Assert.DoesNotContain(sensitiveValue, queued.Parameters[RowExportQueryProtector.PayloadParameter], StringComparison.Ordinal);
            string expectedPlaintextHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(RowExportQueryCodec.Serialize(rowQuery))));
            Assert.Equal(expectedPlaintextHash, queued.Parameters[RowExportQueryProtector.HashParameter]);
            Assert.Equal(ComputeAuthorizationHash(queued.Parameters), queued.Parameters["authorizationHash"]);

            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "state.db"),
                Mode = SqliteOpenMode.ReadOnly,
            }.ConnectionString);
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT parameters_json FROM jobs WHERE id=$id";
            command.Parameters.AddWithValue("$id", queued.Id.ToString("N"));
            string persistedJson = Assert.IsType<string>(await command.ExecuteScalarAsync());

            Assert.DoesNotContain(sensitiveValue, persistedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"rowQuery\"", persistedJson, StringComparison.Ordinal);
            Assert.Contains(RowExportQueryProtector.PayloadParameter, persistedJson, StringComparison.Ordinal);
            Assert.Contains(queued.Parameters[RowExportQueryProtector.HashParameter], persistedJson, StringComparison.Ordinal);
        }
        finally
        {
            host.Dispose();
            SqliteConnection.ClearAllPools();
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

    private static IHost CreateHost(string root)
    {
        DatabaseRegistration registration = new()
        {
            Id = "Main",
            ProviderName = "test",
            ConnectionStringName = "Unused",
            EnabledCapabilities = RazorDbCapability.BrowseMetadata | RazorDbCapability.Export,
            AllowedSchemas = ["app"],
        };
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddRazorComponents().AddInteractiveServerComponents();
                services.AddAuthorizationBuilder()
                    .AddPolicy(RazorDbManagerPolicies.Access, policy => policy.RequireAuthenticatedUser());
                services.AddSingleton<AuthenticationStateProvider>(new TestAuthenticationStateProvider("alice"));
                services.AddSingleton<IRazorDbProviderRegistry>(new TestProviderRegistry(registration));
                services.AddDataProtection()
                    .SetApplicationName("RazorDbManager.WorkspaceFilteredExportProtectionTests")
                    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")));
                services.AddRazorDbManager(options =>
                {
                    options.DefaultDatabaseId = registration.Id;
                    options.StoragePath = root;
                });
            })
            .Build();
    }

    private static string ComputeAuthorizationHash(IReadOnlyDictionary<string, string> parameters)
    {
        string canonical = string.Join("\n", parameters
            .Where(pair => !pair.Key.StartsWith("authorization", StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed class TestProviderRegistry(DatabaseRegistration registration) : IRazorDbProviderRegistry
    {
        public IReadOnlyCollection<DatabaseRegistration> Registrations => [registration];

        public DatabaseRegistration GetRequiredRegistration(string databaseId) =>
            string.Equals(databaseId, registration.Id, StringComparison.Ordinal)
                ? registration
                : throw new KeyNotFoundException(databaseId);

        public ValueTask<IRazorDbProvider> GetProviderAsync(
            string databaseId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IRazorDbProvider>(new NotSupportedException());
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
