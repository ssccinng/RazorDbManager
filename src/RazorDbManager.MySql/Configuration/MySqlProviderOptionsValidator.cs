using Microsoft.Extensions.Options;
using RazorDbManager.Core;

namespace RazorDbManager.MySql.Configuration;

internal sealed class MySqlProviderOptionsValidator
{
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema",
        "mysql",
        "performance_schema",
        "sys",
    };

    public void Validate(string databaseId, MySqlProviderOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionStringName))
        {
            throw new OptionsValidationException(databaseId, typeof(MySqlProviderOptions),
                ["ConnectionStringName is required."]);
        }

        ValidateLimits(databaseId, options);

        var writerName = options.WriterConnectionStringName ?? options.ConnectionStringName;

        if ((options.EnabledCapabilities & (RazorDbCapability.Schema | RazorDbCapability.DestructiveSchema)) != 0)
        {
            var schemaName = options.SchemaConnectionStringName;
            if (string.IsNullOrWhiteSpace(schemaName))
            {
                if (!options.AllowSharedHighRiskCredential)
                {
                    throw new OptionsValidationException(databaseId, typeof(MySqlProviderOptions),
                        ["SchemaConnectionStringName is required when schema capabilities are enabled unless AllowSharedHighRiskCredential is true."]);
                }

                schemaName = writerName;
            }
        }

        if ((options.EnabledCapabilities & RazorDbCapability.ExecuteSql) != 0)
        {
            var sqlName = options.SqlConsoleConnectionStringName;
            if (string.IsNullOrWhiteSpace(sqlName))
            {
                if (!options.AllowSharedHighRiskCredential)
                {
                    throw new OptionsValidationException(databaseId, typeof(MySqlProviderOptions),
                        ["SqlConsoleConnectionStringName is required when SQL execution is enabled unless AllowSharedHighRiskCredential is true."]);
                }

                sqlName = writerName;
            }
        }

        if (options.EnableSqlRestore
            && !options.EnabledCapabilities.Includes(RazorDbCapability.Import | RazorDbCapability.ExecuteSql))
        {
            throw new OptionsValidationException(databaseId, typeof(MySqlProviderOptions),
                ["EnableSqlRestore requires both Import and ExecuteSql capabilities."]);
        }

        var forbidden = options.AllowedSchemas.FirstOrDefault(schema => SystemSchemas.Contains(schema));
        if (forbidden is not null)
        {
            throw new OptionsValidationException(databaseId, typeof(MySqlProviderOptions),
                [$"System schema '{forbidden}' cannot be managed."]);
        }
        if (options.EnableSqlRestore)
        {
            _ = options.SqlConsoleConnectionStringName
                ?? throw new OptionsValidationException(databaseId, typeof(MySqlProviderOptions),
                    ["SQL restore requires an explicit SqlConsoleConnectionStringName; shared credential fallback is not allowed."]);
        }
    }

    private static void ValidateLimits(string databaseId, MySqlProviderOptions options)
    {
        var failures = new List<string>();
        if (options.MetadataCacheSeconds is < 0 or > 3600) failures.Add("MetadataCacheSeconds must be between 0 and 3600.");
        if (options.DefaultPageSize is < 1) failures.Add("DefaultPageSize must be positive.");
        if (options.MaximumPageSize is < 1 or > 10_000) failures.Add("MaximumPageSize must be between 1 and 10000.");
        if (options.DefaultPageSize > options.MaximumPageSize) failures.Add("DefaultPageSize cannot exceed MaximumPageSize.");
        if (options.MaximumResponseBytes < 1024) failures.Add("MaximumResponseBytes must be at least 1024.");
        if (options.MaximumCellPreviewBytes < 256) failures.Add("MaximumCellPreviewBytes must be at least 256.");
        if (options.MaximumBinaryDownloadBytes is < 1 or > 1024L * 1024 * 1024)
            failures.Add("MaximumBinaryDownloadBytes must be between 1 byte and 1 GiB.");
        if (options.SqlCommandTimeoutSeconds is < 1 or > 3600) failures.Add("SqlCommandTimeoutSeconds must be between 1 and 3600.");
        if (options.MaximumSqlTextBytes is < 1 or > 16 * 1024 * 1024) failures.Add("MaximumSqlTextBytes must be between 1 and 16 MiB.");
        if (options.MaximumSqlStatements is < 1 or > 10_000) failures.Add("MaximumSqlStatements must be between 1 and 10000.");
        if (options.MaximumSqlRows is < 1) failures.Add("MaximumSqlRows must be positive.");
        if (options.MaximumSqlResultBytes < 1024) failures.Add("MaximumSqlResultBytes must be at least 1024.");
        if (options.MaximumImportColumns is < 1 or > 16_384) failures.Add("MaximumImportColumns must be between 1 and 16384.");
        if (options.MaximumCsvRecordBytes is < 256 or > 16 * 1024 * 1024)
            failures.Add("MaximumCsvRecordBytes must be between 256 bytes and 16 MiB.");
        if (options.MaximumCsvRecordBytes > options.MaximumUploadBytes)
            failures.Add("MaximumCsvRecordBytes cannot exceed MaximumUploadBytes.");
        if (options.MaximumUploadBytes < 1024) failures.Add("MaximumUploadBytes must be at least 1024.");
        if (options.MaximumExportRows < 1) failures.Add("MaximumExportRows must be positive.");
        if (options.MaximumExportBytes < 1024) failures.Add("MaximumExportBytes must be at least 1024.");
        if (options.MaximumExportCellBytes is < 256 or > 64 * 1024 * 1024)
            failures.Add("MaximumExportCellBytes must be between 256 bytes and 64 MiB.");
        if (options.MaximumExportCellBytes > options.MaximumExportBytes)
            failures.Add("MaximumExportCellBytes cannot exceed MaximumExportBytes.");

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(databaseId, typeof(MySqlProviderOptions), failures);
        }
    }
}
