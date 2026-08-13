namespace RazorDbManager.Core;

/// <summary>Describes an independently grantable database-management capability.</summary>
[Flags]
public enum RazorDbCapability : ulong
{
    /// <summary>No database operations are allowed.</summary>
    None = 0,
    /// <summary>Read schemas, tables, columns, keys, and related metadata.</summary>
    BrowseMetadata = 1UL << 0,
    /// <summary>Read table data.</summary>
    ReadRows = 1UL << 1,
    /// <summary>Insert table rows.</summary>
    InsertRows = 1UL << 2,
    /// <summary>Update table rows.</summary>
    UpdateRows = 1UL << 3,
    /// <summary>Delete table rows.</summary>
    DeleteRows = 1UL << 4,
    /// <summary>Create and alter schema objects using structured operations.</summary>
    ModifySchema = 1UL << 5,
    /// <summary>Perform destructive schema operations such as dropping a table or column.</summary>
    DestructiveSchema = 1UL << 6,
    /// <summary>Execute arbitrary SQL using the SQL-console credential.</summary>
    ExecuteSql = 1UL << 7,
    /// <summary>Import data or SQL scripts.</summary>
    Import = 1UL << 8,
    /// <summary>Export data or SQL dumps.</summary>
    Export = 1UL << 9,
    /// <summary>Download binary cell values through a protected endpoint.</summary>
    DownloadBinary = 1UL << 10,

    /// <summary>Compatibility alias for <see cref="BrowseMetadata"/>.</summary>
    Metadata = BrowseMetadata,
    /// <summary>Compatibility alias for <see cref="ReadRows"/>.</summary>
    ReadData = ReadRows,
    /// <summary>Compatibility alias for <see cref="ModifySchema"/>.</summary>
    Schema = ModifySchema,
}

/// <summary>Provides supported, named capability combinations.</summary>
public static class RazorDbCapabilitySets
{
    /// <summary>Capabilities required to browse metadata and data without changing either.</summary>
    public const RazorDbCapability ReadOnly = RazorDbCapability.BrowseMetadata | RazorDbCapability.ReadRows;

    /// <summary>Capabilities required for the standard row-data editor.</summary>
    public const RazorDbCapability DataEditor = ReadOnly
        | RazorDbCapability.InsertRows
        | RazorDbCapability.UpdateRows
        | RazorDbCapability.DeleteRows;

    /// <summary>Every capability understood by this version of the core library.</summary>
    public const RazorDbCapability All = DataEditor
        | RazorDbCapability.ModifySchema
        | RazorDbCapability.DestructiveSchema
        | RazorDbCapability.ExecuteSql
        | RazorDbCapability.Import
        | RazorDbCapability.Export
        | RazorDbCapability.DownloadBinary;

    /// <summary>Returns whether <paramref name="granted"/> contains every requested capability.</summary>
    /// <param name="granted">The capability ceiling.</param>
    /// <param name="requested">The capabilities required by an operation.</param>
    /// <returns><see langword="true"/> when all requested flags are granted.</returns>
    public static bool Includes(this RazorDbCapability granted, RazorDbCapability requested) =>
        (granted & requested) == requested;
}

/// <summary>Configures provider-neutral RazorDbManager behavior.</summary>
public sealed class RazorDbRuntimeOptions
{
    /// <summary>Gets or sets the registration used by the built-in manager page.</summary>
    public string? DefaultDatabaseId { get; set; }

    /// <summary>Gets or sets the resource limits applied when a registration has no override.</summary>
    public RazorDbResourceLimits ResourceLimits { get; set; } = new();

    /// <summary>Gets or sets the duration for which provider metadata may be cached.</summary>
    public TimeSpan MetadataCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Validates provider-neutral runtime settings.</summary>
    /// <returns>This instance.</returns>
    /// <exception cref="InvalidOperationException">A runtime setting is invalid.</exception>
    public RazorDbRuntimeOptions Validate()
    {
        if (DefaultDatabaseId is not null && string.IsNullOrWhiteSpace(DefaultDatabaseId))
        {
            throw new InvalidOperationException("The default database id cannot be blank.");
        }

        if (MetadataCacheDuration < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Metadata cache duration cannot be negative.");
        }

        ResourceLimits.Validate();
        return this;
    }
}

/// <summary>Defines bounded defaults for interactive and transfer operations.</summary>
public sealed record RazorDbResourceLimits
{
    /// <summary>Gets the default number of rows returned by the table browser.</summary>
    public int DefaultPageSize { get; init; } = 100;
    /// <summary>Gets the largest page size accepted from a caller.</summary>
    public int MaximumPageSize { get; init; } = 500;
    /// <summary>Gets the largest interactive response in bytes.</summary>
    public long MaximumResponseBytes { get; init; } = 8L * 1024 * 1024;
    /// <summary>Gets the largest inline cell preview in bytes.</summary>
    public int MaximumCellPreviewBytes { get; init; } = 64 * 1024;
    /// <summary>Gets the largest binary value that may be downloaded.</summary>
    public long MaximumBinaryDownloadBytes { get; init; } = 25L * 1024 * 1024;
    /// <summary>Gets the largest SQL-console input in characters.</summary>
    public int MaximumSqlCharacters { get; init; } = 1024 * 1024;
    /// <summary>Gets the default SQL-console timeout.</summary>
    public TimeSpan SqlTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Gets the largest SQL-console result row count.</summary>
    public int MaximumSqlRows { get; init; } = 1_000;
    /// <summary>Gets the largest SQL-console result in bytes.</summary>
    public long MaximumSqlResultBytes { get; init; } = 10L * 1024 * 1024;
    /// <summary>Gets the largest accepted upload in bytes.</summary>
    public long MaximumUploadBytes { get; init; } = 100L * 1024 * 1024;
    /// <summary>Gets the largest CSV record in bytes.</summary>
    public int MaximumCsvRecordBytes { get; init; } = 1024 * 1024;
    /// <summary>Gets the largest CSV column count.</summary>
    public int MaximumCsvColumns { get; init; } = 1_024;
    /// <summary>Gets the largest export row count.</summary>
    public long MaximumExportRows { get; init; } = 100_000;
    /// <summary>Gets the largest export artifact in bytes.</summary>
    public long MaximumExportBytes { get; init; } = 256L * 1024 * 1024;
    /// <summary>Gets the maximum duration of an export operation.</summary>
    public TimeSpan ExportTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Validates that every configured limit is positive and internally consistent.</summary>
    /// <returns>This instance.</returns>
    /// <exception cref="InvalidOperationException">A limit is invalid.</exception>
    public RazorDbResourceLimits Validate()
    {
        if (DefaultPageSize <= 0 || MaximumPageSize < DefaultPageSize)
        {
            throw new InvalidOperationException("Page-size limits are invalid.");
        }

        if (MaximumResponseBytes <= 0 || MaximumCellPreviewBytes <= 0 || MaximumBinaryDownloadBytes <= 0
            || MaximumSqlCharacters <= 0 || SqlTimeout <= TimeSpan.Zero || MaximumSqlRows <= 0
            || MaximumSqlResultBytes <= 0 || MaximumUploadBytes <= 0 || MaximumCsvRecordBytes <= 0
            || MaximumCsvColumns <= 0 || MaximumExportRows <= 0 || MaximumExportBytes <= 0
            || ExportTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Resource limits must be positive.");
        }

        return this;
    }
}

/// <summary>Registers one logical database and its immutable capability ceiling.</summary>
public sealed record DatabaseRegistration
{
    /// <summary>Gets the stable application-level registration identifier.</summary>
    public required string Id { get; init; }
    /// <summary>Gets the provider identifier used to select a provider factory.</summary>
    public required string ProviderName { get; init; }
    /// <summary>Gets an optional user-facing name.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Gets the configuration key for the read credential.</summary>
    public required string ConnectionStringName { get; init; }
    /// <summary>Gets the optional configuration key for row mutations.</summary>
    public string? WriterConnectionStringName { get; init; }
    /// <summary>Gets the optional configuration key for schema changes.</summary>
    public string? SchemaConnectionStringName { get; init; }
    /// <summary>Gets the optional configuration key for arbitrary SQL.</summary>
    public string? SqlConsoleConnectionStringName { get; init; }
    /// <summary>Gets the maximum capabilities this registration can grant.</summary>
    public RazorDbCapability EnabledCapabilities { get; init; } = RazorDbCapabilitySets.ReadOnly;
    /// <summary>Gets schemas that may be accessed. An empty list requires the provider to infer one default schema.</summary>
    public IReadOnlyList<string> AllowedSchemas { get; init; } = Array.Empty<string>();
    /// <summary>Gets whether the read credential may explicitly serve a missing high-risk credential.</summary>
    public bool AllowSharedHighRiskCredential { get; init; }
    /// <summary>Gets optional per-database resource limits.</summary>
    public RazorDbResourceLimits? ResourceLimits { get; init; }

    /// <summary>Validates invariant, security-sensitive registration settings.</summary>
    /// <returns>This registration.</returns>
    /// <exception cref="InvalidOperationException">The registration is incomplete or contradictory.</exception>
    public DatabaseRegistration Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(ProviderName)
            || string.IsNullOrWhiteSpace(ConnectionStringName))
        {
            throw new InvalidOperationException("Database id, provider name, and connection-string name are required.");
        }

        if ((EnabledCapabilities & ~RazorDbCapabilitySets.All) != 0)
        {
            throw new InvalidOperationException("The registration contains unknown capabilities.");
        }

        if (EnabledCapabilities.Includes(RazorDbCapability.DestructiveSchema)
            && !EnabledCapabilities.Includes(RazorDbCapability.ModifySchema))
        {
            throw new InvalidOperationException("Destructive schema access requires schema access.");
        }

        if (!AllowSharedHighRiskCredential
            && EnabledCapabilities.Includes(RazorDbCapability.ModifySchema)
            && string.IsNullOrWhiteSpace(SchemaConnectionStringName))
        {
            throw new InvalidOperationException(
                "Schema access requires a separate schema credential unless shared high-risk credentials are explicitly allowed.");
        }

        if (!AllowSharedHighRiskCredential
            && EnabledCapabilities.Includes(RazorDbCapability.ExecuteSql)
            && string.IsNullOrWhiteSpace(SqlConsoleConnectionStringName))
        {
            throw new InvalidOperationException(
                "SQL console access requires a separate credential unless shared high-risk credentials are explicitly allowed.");
        }

        if (AllowedSchemas.Any(string.IsNullOrWhiteSpace)
            || AllowedSchemas.Distinct(StringComparer.OrdinalIgnoreCase).Count() != AllowedSchemas.Count)
        {
            throw new InvalidOperationException("Allowed schemas must be non-empty and unique.");
        }

        ResourceLimits?.Validate();
        return this;
    }
}
