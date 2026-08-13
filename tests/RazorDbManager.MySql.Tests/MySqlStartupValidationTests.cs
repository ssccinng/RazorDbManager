using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;
using RazorDbManager.MySql.Infrastructure;

namespace RazorDbManager.MySql.Tests;

public sealed class MySqlStartupValidationTests
{
    [Fact]
    public async Task HostStartAsync_RejectsUnsafeTlsFromConfigurationCredentialProvider()
    {
        using var host = BuildHost(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:MainDatabase"] =
                    "Server=localhost;Database=app;User ID=user;Password=x;SslMode=Required;PersistSecurityInfo=false;AllowLoadLocalInfile=false",
            });

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());

        Assert.Contains("SslMode must be VerifyFull", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostStartAsync_RejectsMissingDefaultSchemaFromConfigurationCredentialProvider()
    {
        using var host = BuildHost(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:MainDatabase"] = MySqlProviderOptionsValidatorTests.SecureConnection(database: null),
            });

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());

        Assert.Contains("default Database or explicit AllowedSchemas", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostStartAsync_UsesCustomCredentialProviderWithoutConfigurationPlaceholder()
    {
        var credentials = new VaultCredentialProvider(_ =>
            MySqlProviderOptionsValidatorTests.SecureConnection("app").Replace(
                "Server=localhost",
                "Server=unreachable.invalid",
                StringComparison.Ordinal));
        using var host = BuildHost(
            configuration: new Dictionary<string, string?>(),
            credentialProvider: credentials,
            configure: options => options.ConnectionStringName = "key-vault-reference");

        await host.StartAsync();
        await host.StopAsync();

        Assert.Contains(RazorDbCredentialPurpose.Reader, credentials.RequestedPurposes);
    }

    [Fact]
    public async Task HostStartAsync_AcceptsSafeConfigurationCredentialWithoutConnecting()
    {
        using var host = BuildHost(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:MainDatabase"] = MySqlProviderOptionsValidatorTests.SecureConnection("app").Replace(
                    "Server=localhost",
                    "Server=unreachable.invalid",
                    StringComparison.Ordinal),
            });

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task HostStartAsync_ValidatesActualWriterCredentialReturnedByCustomProvider()
    {
        var credentials = new VaultCredentialProvider(purpose => purpose switch
        {
            RazorDbCredentialPurpose.Reader => MySqlProviderOptionsValidatorTests.SecureConnection("app"),
            RazorDbCredentialPurpose.Writer =>
                "Server=localhost;Database=app;User ID=user;Password=x;SslMode=Required;PersistSecurityInfo=false;AllowLoadLocalInfile=false",
            _ => throw new InvalidOperationException("Unexpected credential purpose."),
        });
        using var host = BuildHost(
            configuration: new Dictionary<string, string?>(),
            credentialProvider: credentials,
            configure: options =>
            {
                options.ConnectionStringName = "key-vault-reader";
                options.WriterConnectionStringName = "key-vault-writer";
                options.EnabledCapabilities = RazorDbCapabilitySets.DataEditor;
            });

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());

        Assert.Contains("writer credential", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SslMode must be VerifyFull", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains(RazorDbCredentialPurpose.Writer, credentials.RequestedPurposes);
    }

    [Fact]
    public async Task CreateDataSourceAsync_ValidatesCustomCredentialBeforeBuildingDataSource()
    {
        var options = new MySqlProviderOptions { ConnectionStringName = "key-vault-reader" };
        var registration = Registration(options);
        var credentials = new VaultCredentialProvider(_ =>
            "Server=unreachable.invalid;Database=app;User ID=user;Password=x;SslMode=Required;PersistSecurityInfo=false;AllowLoadLocalInfile=false");
        var validator = new MySqlCredentialValidator(registration, options, new TestEnvironment(Environments.Production));
        var source = new MySqlCredentialSource(registration, credentials, validator);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await source.CreateDataSourceAsync(MySqlCredentialSlot.Reader));

        Assert.Contains("SslMode must be VerifyFull", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateDataSourceAsync_EnablesUserVariablesOnlyForSqlConsole()
    {
        var options = new MySqlProviderOptions { ConnectionStringName = "key-vault-reader" };
        var registration = Registration(options);
        var credentials = new VaultCredentialProvider(_ =>
            MySqlProviderOptionsValidatorTests.SecureConnection("app"));
        var validator = new MySqlCredentialValidator(
            registration,
            options,
            new TestEnvironment(Environments.Production));
        var source = new MySqlCredentialSource(registration, credentials, validator);

        (MySqlCredentialSlot Slot, bool Expected)[] cases =
        [
            (MySqlCredentialSlot.Reader, false),
            (MySqlCredentialSlot.Writer, false),
            (MySqlCredentialSlot.Schema, false),
            (MySqlCredentialSlot.SqlConsole, true),
        ];
        foreach (var item in cases)
        {
            await using MySqlDataSource dataSource = await source.CreateDataSourceAsync(item.Slot);
            var actual = new MySqlConnectionStringBuilder(dataSource.ConnectionString);
            Assert.Equal(item.Expected, actual.AllowUserVariables);
        }
    }

    [Fact]
    public async Task CredentialSource_DoesNotWidenInferredAllowlistAfterSecretRotation()
    {
        var options = new MySqlProviderOptions { ConnectionStringName = "key-vault-reader" };
        var registration = Registration(options);
        var credentials = new RotatingCredentialProvider(
            MySqlProviderOptionsValidatorTests.SecureConnection("app"),
            MySqlProviderOptionsValidatorTests.SecureConnection("other"));
        var validator = new MySqlCredentialValidator(registration, options, new TestEnvironment(Environments.Production));
        var source = new MySqlCredentialSource(registration, credentials, validator);

        _ = await source.GetConnectionStringAsync(MySqlCredentialSlot.Reader);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await source.CreateDataSourceAsync(MySqlCredentialSlot.Reader));

        Assert.Contains("Database must be included in AllowedSchemas", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredSlots_CoversEveryEnabledCredentialPurpose()
    {
        var options = new MySqlProviderOptions
        {
            ConnectionStringName = "reader",
            EnabledCapabilities = RazorDbCapabilitySets.All,
        };

        var slots = MySqlStartupValidator.RequiredSlots(options);

        Assert.Equal(4, slots.Count);
        Assert.Contains(MySqlCredentialSlot.Reader, slots);
        Assert.Contains(MySqlCredentialSlot.Writer, slots);
        Assert.Contains(MySqlCredentialSlot.Schema, slots);
        Assert.Contains(MySqlCredentialSlot.SqlConsole, slots);
    }

    private static IHost BuildHost(
        IReadOnlyDictionary<string, string?> configuration,
        IRazorDbCredentialProvider? credentialProvider = null,
        Action<MySqlProviderOptions>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
        });
        builder.Configuration.AddInMemoryCollection(configuration);
        if (credentialProvider is not null)
        {
            builder.Services.AddSingleton<IRazorDbCredentialProvider>(credentialProvider);
        }

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(global::RazorDbManager.RazorDbManagerPolicies.Access,
                policy => policy.RequireAssertion(_ => true));
        builder.Services.AddRazorDbManager(options =>
        {
            options.DefaultDatabaseId = "Main";
            options.StoragePath = Path.Combine(
                Path.GetTempPath(),
                "RazorDbManager.MySql.Tests",
                Guid.NewGuid().ToString("N"));
        }).AddMySql("Main", options =>
        {
            options.ConnectionStringName = "MainDatabase";
            configure?.Invoke(options);
        });
        return builder.Build();
    }

    private static DatabaseRegistration Registration(MySqlProviderOptions options) => new()
    {
        Id = "Main",
        ProviderName = "mysql",
        ConnectionStringName = options.ConnectionStringName,
        EnabledCapabilities = options.EnabledCapabilities,
    };

    private sealed class VaultCredentialProvider(Func<RazorDbCredentialPurpose, string> resolve) : IRazorDbCredentialProvider
    {
        private readonly List<RazorDbCredentialPurpose> _requestedPurposes = [];

        public IReadOnlyCollection<RazorDbCredentialPurpose> RequestedPurposes => _requestedPurposes;

        public ValueTask<RazorDbCredential> GetCredentialAsync(
            DatabaseRegistration registration,
            RazorDbCredentialPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requestedPurposes.Add(purpose);
            return ValueTask.FromResult(new RazorDbCredential(resolve(purpose)));
        }
    }

    private sealed class RotatingCredentialProvider(params string[] credentials) : IRazorDbCredentialProvider
    {
        private int _index;

        public ValueTask<RazorDbCredential> GetCredentialAsync(
            DatabaseRegistration registration,
            RazorDbCredentialPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, credentials.Length - 1);
            return ValueTask.FromResult(new RazorDbCredential(credentials[index]));
        }
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
