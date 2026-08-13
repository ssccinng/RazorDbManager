using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorDbManager.Core;

namespace RazorDbManager.Tests.Infrastructure;

internal sealed class RazorDbTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    private RazorDbTestHost(IHost host, string storagePath, HttpClient? client = null)
    {
        _host = host;
        StoragePath = storagePath;
        Client = client;
    }

    public IServiceProvider Services => _host.Services;

    public string StoragePath { get; }

    public HttpClient? Client { get; }

    public void SetCapabilities(RazorDbCapability capabilities) =>
        Services.GetRequiredService<StubProviderRegistry>().SetCapabilities(capabilities);

    public void SetAllowedSchemas(params string[] schemas) =>
        Services.GetRequiredService<StubProviderRegistry>().SetAllowedSchemas(schemas);

    public static RazorDbTestHost CreateStoreHost(
        RazorDbCapability capabilities = RazorDbCapabilitySets.DataEditor,
        Action<RazorDbManagerOptions>? configure = null,
        bool addAccessPolicy = true,
        bool addHighRiskPolicy = false,
        IRazorDbSessionValidator? sessionValidator = null,
        IRazorDbBackgroundAuthorizer? backgroundAuthorizer = null)
    {
        string root = CreateRoot();
        IHost host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseContentRoot(root);
                webBuilder.UseEnvironment(Environments.Development);
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services => ConfigureServices(
                    services,
                    root,
                    capabilities,
                    configure,
                    addAccessPolicy,
                    addHighRiskPolicy,
                    sessionValidator,
                    backgroundAuthorizer));
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(_ => { });
                });
            })
            .Build();

        return new RazorDbTestHost(host, root);
    }

    public static async Task<RazorDbTestHost> CreateEndpointHostAsync(
        IRazorDbManagerAuthorizer? authorizer = null,
        IRazorDbAuditSink? auditSink = null,
        IRazorDbDataProvider? dataProvider = null,
        RazorDbCapability capabilities = RazorDbCapabilitySets.DataEditor
            | RazorDbCapability.Export
            | RazorDbCapability.DownloadBinary,
        IRazorDbTransferProvider? transferProvider = null,
        IRazorDbSessionValidator? sessionValidator = null,
        bool addHighRiskPolicy = false,
        IRazorDbBackgroundAuthorizer? backgroundAuthorizer = null,
        IRazorDbProviderHealthProbe? healthProbe = null,
        IRazorDbArtifactStore? artifactStore = null)
    {
        string root = CreateRoot();
        IHost host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseContentRoot(root);
                webBuilder.UseEnvironment(Environments.Development);
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddAuthentication(HeaderAuthenticationHandler.AuthenticationSchemeName)
                        .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                            HeaderAuthenticationHandler.AuthenticationSchemeName,
                            _ => { });
                    ConfigureServices(
                        services,
                        root,
                        capabilities,
                        null,
                        addAccessPolicy: true,
                        addHighRiskPolicy,
                        sessionValidator,
                        backgroundAuthorizer: backgroundAuthorizer ?? new AllowBackgroundAuthorizer(),
                        authorizer,
                        auditSink,
                        dataProvider,
                        transferProvider,
                        healthProbe,
                        artifactStore);
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/_test/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
                        {
                            AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
                            return Results.Json(new { token = tokens.RequestToken, field = tokens.FormFieldName });
                        });
                        endpoints.MapRazorDbManagerEndpoints();
                    });
                });
            })
            .Build();

        await host.StartAsync();
        return new RazorDbTestHost(host, root, host.GetTestClient());
    }

    public static HttpRequestMessage Request(HttpMethod method, string uri, string? actorId = null)
    {
        HttpRequestMessage request = new(method, uri);
        if (actorId is not null)
        {
            request.Headers.Add(HeaderAuthenticationHandler.ActorHeader, actorId);
        }

        return request;
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _host.StartAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        try
        {
            await _host.StopAsync();
        }
        catch (InvalidOperationException)
        {
            // A configuration-validation test may dispose a host that never started.
        }

        _host.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(StoragePath, recursive: true);
        }
        catch (IOException)
        {
            // A failed test should retain its original assertion rather than fail in cleanup.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ConfigureServices(
        IServiceCollection services,
        string root,
        RazorDbCapability capabilities,
        Action<RazorDbManagerOptions>? configure,
        bool addAccessPolicy,
        bool addHighRiskPolicy,
        IRazorDbSessionValidator? sessionValidator,
        IRazorDbBackgroundAuthorizer? backgroundAuthorizer,
        IRazorDbManagerAuthorizer? authorizer = null,
        IRazorDbAuditSink? auditSink = null,
        IRazorDbDataProvider? dataProvider = null,
        IRazorDbTransferProvider? transferProvider = null,
        IRazorDbProviderHealthProbe? healthProbe = null,
        IRazorDbArtifactStore? artifactStore = null)
    {
        services.AddRouting();
        services.AddAuthentication();
        services.AddRazorComponents().AddInteractiveServerComponents();
        Microsoft.AspNetCore.Authorization.AuthorizationBuilder authorization = services.AddAuthorizationBuilder();
        if (addAccessPolicy)
        {
            authorization.AddPolicy(RazorDbManagerPolicies.Access, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => !string.Equals(
                    context.User.Identity?.Name,
                    "blocked",
                    StringComparison.Ordinal)));
        }

        if (addHighRiskPolicy)
        {
            authorization.AddPolicy(RazorDbManagerPolicies.HighRisk, policy => policy.RequireAuthenticatedUser());
        }

        StubProviderRegistry registry = new(capabilities, dataProvider, transferProvider, healthProbe);
        services.AddSingleton(registry);
        services.AddSingleton<IRazorDbProviderRegistry>(registry);
        if (sessionValidator is not null)
        {
            services.AddSingleton(sessionValidator);
            services.AddSingleton<IRazorDbSessionValidator>(sessionValidator);
        }
        if (backgroundAuthorizer is not null)
        {
            services.AddSingleton(backgroundAuthorizer);
            services.AddSingleton<IRazorDbBackgroundAuthorizer>(backgroundAuthorizer);
        }
        if (authorizer is not null) services.AddSingleton(authorizer);
        if (auditSink is not null) services.AddSingleton(auditSink);
        if (artifactStore is not null) services.AddSingleton(artifactStore);

        services.AddRazorDbManager(options =>
        {
            options.DefaultDatabaseId = "Main";
            options.StoragePath = root;
            configure?.Invoke(options);
        });
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "RazorDbManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class StubProviderRegistry : IRazorDbProviderRegistry
    {
        private DatabaseRegistration _registration;
        private readonly IRazorDbDataProvider? _dataProvider;
        private readonly IRazorDbTransferProvider? _transferProvider;
        private readonly IRazorDbProviderHealthProbe? _healthProbe;

        public StubProviderRegistry(
            RazorDbCapability capabilities,
            IRazorDbDataProvider? dataProvider = null,
            IRazorDbTransferProvider? transferProvider = null,
            IRazorDbProviderHealthProbe? healthProbe = null)
        {
            _dataProvider = dataProvider;
            _transferProvider = transferProvider;
            _healthProbe = healthProbe;
            _registration = new DatabaseRegistration
            {
                Id = "Main",
                ProviderName = "test",
                ConnectionStringName = "Unused",
                EnabledCapabilities = capabilities,
                AllowSharedHighRiskCredential = true,
                AllowedSchemas = ["app"],
            };
        }

        public IReadOnlyCollection<DatabaseRegistration> Registrations => [Volatile.Read(ref _registration)];

        public DatabaseRegistration GetRequiredRegistration(string databaseId)
        {
            DatabaseRegistration registration = Volatile.Read(ref _registration);
            return string.Equals(databaseId, registration.Id, StringComparison.Ordinal)
                ? registration
                : throw new KeyNotFoundException(databaseId);
        }

        public void SetCapabilities(RazorDbCapability capabilities)
        {
            DatabaseRegistration current = Volatile.Read(ref _registration);
            Volatile.Write(ref _registration, current with { EnabledCapabilities = capabilities });
        }

        public void SetAllowedSchemas(IReadOnlyList<string> schemas)
        {
            DatabaseRegistration current = Volatile.Read(ref _registration);
            Volatile.Write(ref _registration, current with { AllowedSchemas = schemas.ToArray() });
        }

        public ValueTask<IRazorDbProvider> GetProviderAsync(
            string databaseId,
            CancellationToken cancellationToken = default)
        {
            DatabaseRegistration registration = GetRequiredRegistration(databaseId);
            return _dataProvider is null && _transferProvider is null && _healthProbe is null
                ? ValueTask.FromException<IRazorDbProvider>(new NotSupportedException("The test registry has no provider."))
                : ValueTask.FromResult<IRazorDbProvider>(new StubProvider(
                    registration,
                    _dataProvider,
                    _transferProvider,
                    _healthProbe));
        }

        private sealed class StubProvider(
            DatabaseRegistration registration,
            IRazorDbDataProvider? data,
            IRazorDbTransferProvider? transfer,
            IRazorDbProviderHealthProbe? health) : IRazorDbProvider, IRazorDbProviderHealthProbe
        {
            public string ProviderName => "test";
            public DatabaseRegistration Registration => registration;
            public IRazorDbMetadataProvider Metadata => throw new NotSupportedException();
            public IRazorDbDataProvider Data => data ?? throw new NotSupportedException();
            public IRazorDbSchemaProvider Schema => throw new NotSupportedException();
            public IRazorDbSqlProvider Sql => throw new NotSupportedException();
            public IRazorDbTransferProvider Transfer => transfer ?? throw new NotSupportedException();
            public ValueTask<RazorDbProviderHealthReport> CheckHealthAsync(
                CancellationToken cancellationToken = default) =>
                health?.CheckHealthAsync(cancellationToken)
                ?? ValueTask.FromException<RazorDbProviderHealthReport>(new NotSupportedException());
        }
    }
}

internal sealed class HeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationSchemeName = "HeaderTest";
    public const string ActorHeader = "X-Test-Actor";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string actorId = Request.Headers[ActorHeader].ToString();
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, actorId),
            new(ClaimTypes.Name, actorId),
        ];
        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, AuthenticationSchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, AuthenticationSchemeName)));
    }
}

internal sealed class AllowSessionValidator : IRazorDbSessionValidator
{
    public ValueTask<RazorDbSessionValidationResult> ValidateAsync(
        RazorDbSessionValidationContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new RazorDbSessionValidationResult(true, DateTimeOffset.UtcNow.AddMinutes(5)));
}

internal sealed class RecordingSessionValidator(
    Func<RazorDbSessionValidationContext, RazorDbSessionValidationResult> validate) : IRazorDbSessionValidator
{
    public ConcurrentQueue<RazorDbSessionValidationContext> Contexts { get; } = new();

    public ValueTask<RazorDbSessionValidationResult> ValidateAsync(
        RazorDbSessionValidationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Contexts.Enqueue(context);
        return ValueTask.FromResult(validate(context));
    }
}

internal sealed class AllowBackgroundAuthorizer : IRazorDbBackgroundAuthorizer
{
    public ValueTask<RazorDbAuthorizationResult> AuthorizeAsync(
        RazorDbAuthorizationContext context,
        bool highRisk,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(RazorDbAuthorizationResult.Allowed);
}

internal sealed class RecordingAuthorizer(
    Func<RazorDbAuthorizationContext, RazorDbAuthorizationResult> authorize) : IRazorDbManagerAuthorizer
{
    public ConcurrentQueue<RazorDbAuthorizationContext> Contexts { get; } = new();

    public ValueTask<RazorDbAuthorizationResult> AuthorizeAsync(
        RazorDbAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Contexts.Enqueue(context);
        return ValueTask.FromResult(authorize(context));
    }
}

internal sealed class RecordingAuditSink(
    Func<RazorDbAuditRecord, Exception?>? failure = null) : IRazorDbAuditSink
{
    public ConcurrentQueue<RazorDbAuditRecord> Records { get; } = new();

    public ValueTask AppendAsync(
        RazorDbAuditRecord record,
        CancellationToken cancellationToken = default)
    {
        Records.Enqueue(record);
        Exception? exception = failure?.Invoke(record);
        return exception is null ? ValueTask.CompletedTask : ValueTask.FromException(exception);
    }
}
