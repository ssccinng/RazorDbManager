using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using RazorDbManager.Core;

namespace RazorDbManager;

/// <summary>Registers RazorDbManager services in a Blazor Web App host.</summary>
public static class RazorDbManagerServiceCollectionExtensions
{
    /// <summary>Adds RazorDbManager with default single-instance SQLite and artifact stores.</summary>
    public static RazorDbManagerBuilder AddRazorDbManager(this IServiceCollection services, Action<RazorDbManagerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddLocalization();
        services.AddDataProtection();
        services.AddOptions<RazorDbManagerOptions>().Configure(configure).Validate(
            value => !string.IsNullOrWhiteSpace(value.DefaultDatabaseId),
            "RazorDbManagerOptions.DefaultDatabaseId is required.").ValidateOnStart();
        services.AddOptions<RazorDbRuntimeOptions>().Configure<IOptions<RazorDbManagerOptions>>((runtime, host) =>
        {
            runtime.DefaultDatabaseId = host.Value.DefaultDatabaseId;
            runtime.ResourceLimits = host.Value.ResourceLimits;
            runtime.MetadataCacheDuration = host.Value.MetadataCacheDuration;
        });

        services.TryAddSingleton<IRazorDbCredentialProvider, ConfigurationCredentialProvider>();
        services.TryAddSingleton<IRazorDbManagerAuthorizer, AllowAllRazorDbAuthorizer>();
        services.TryAddSingleton<IRazorDbSessionValidator, DenyHighRiskSessionValidator>();
        services.TryAddSingleton<IRazorDbBackgroundAuthorizer, DefaultRazorDbBackgroundAuthorizer>();
        services.TryAddSingleton<LocalStorePath>();
        services.TryAddSingleton<SqliteRazorDbStore>();
        services.TryAddSingleton<IRazorDbAuditSink>(provider => provider.GetRequiredService<SqliteRazorDbStore>());
        services.TryAddSingleton<IRazorDbAuditReader>(provider => provider.GetRequiredService<SqliteRazorDbStore>());
        services.TryAddSingleton<IRazorDbJobStore>(provider => provider.GetRequiredService<SqliteRazorDbStore>());
        services.TryAddSingleton<IRazorDbOperationTokenStore>(provider => provider.GetRequiredService<SqliteRazorDbStore>());
        services.TryAddSingleton<IRazorDbStoreMaintenance>(provider => provider.GetRequiredService<SqliteRazorDbStore>());
        services.TryAddSingleton<IRazorDbPreferenceStore>(provider => provider.GetRequiredService<SqliteRazorDbStore>());
        services.TryAddSingleton<IRazorDbArtifactStore, LocalArtifactStore>();
        services.TryAddSingleton<RazorDbTransferAdmissionCoordinator>();
        services.TryAddSingleton<RowExportQueryProtector>();
        services.TryAddSingleton<RazorDbComponentScopeProtector>();
        services.TryAddScoped<DatabaseWorkspace>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, RazorDbConfigurationValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, RazorDbStoreInitializer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, RazorDbJobWorker>());
        return new RazorDbManagerBuilder(services);
    }

}
