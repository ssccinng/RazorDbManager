using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;
using RazorDbManager.MySql.Infrastructure;

namespace RazorDbManager.MySql;

public static class MySqlServiceCollectionExtensions
{
    /// <summary>Adds one MySQL or MariaDB database registration to RazorDbManager.</summary>
    /// <param name="builder">The manager builder returned by AddRazorDbManager.</param>
    /// <param name="databaseId">A stable, application-level registration id.</param>
    /// <param name="configure">Configures credentials, capabilities, schemas, and provider limits.</param>
    /// <returns>The same builder for additional registrations.</returns>
    public static global::RazorDbManager.RazorDbManagerBuilder AddMySql(
        this global::RazorDbManager.RazorDbManagerBuilder builder,
        string databaseId,
        Action<MySqlProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new MySqlProviderOptions();
        configure(options);
        var registration = new DatabaseRegistration
        {
            Id = databaseId,
            ProviderName = MySqlRazorDbProvider.ProviderId,
            ConnectionStringName = options.ConnectionStringName,
            WriterConnectionStringName = options.WriterConnectionStringName,
            SchemaConnectionStringName = options.SchemaConnectionStringName,
            SqlConsoleConnectionStringName = options.SqlConsoleConnectionStringName,
            EnabledCapabilities = options.EnabledCapabilities,
            AllowedSchemas = options.AllowedSchemas.ToArray(),
            AllowSharedHighRiskCredential = options.AllowSharedHighRiskCredential,
            ResourceLimits = new RazorDbResourceLimits
            {
                DefaultPageSize = options.DefaultPageSize,
                MaximumPageSize = options.MaximumPageSize,
                MaximumResponseBytes = options.MaximumResponseBytes,
                MaximumCellPreviewBytes = options.MaximumCellPreviewBytes,
                MaximumBinaryDownloadBytes = options.MaximumBinaryDownloadBytes,
                MaximumSqlCharacters = options.MaximumSqlTextBytes,
                SqlTimeout = TimeSpan.FromSeconds(options.SqlCommandTimeoutSeconds),
                MaximumSqlRows = options.MaximumSqlRows,
                MaximumSqlResultBytes = options.MaximumSqlResultBytes,
                MaximumUploadBytes = options.MaximumUploadBytes,
                MaximumCsvRecordBytes = options.MaximumCsvRecordBytes,
                MaximumCsvColumns = options.MaximumImportColumns,
                MaximumExportRows = options.MaximumExportRows,
                MaximumExportBytes = options.MaximumExportBytes,
            },
        }.Validate();
        builder.Services.AddSingleton(new MySqlRegistrationDescriptor(registration, options));
        builder.Services.TryAddSingleton<IRazorDbCredentialProvider, ConfigurationRazorDbCredentialProvider>();
        builder.Services.TryAddSingleton<IRazorDbProviderRegistry, MySqlProviderRegistry>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, MySqlStartupValidator>());
        return builder;
    }
}
