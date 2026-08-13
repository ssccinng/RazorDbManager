using RazorDbManager.Core;

namespace RazorDbManager.MySql.Configuration;

public sealed class MySqlProviderOptions
{
    /// <summary>Gets or sets the read connection-string name.</summary>
    public string ConnectionStringName { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional row-mutation connection-string name.</summary>
    public string? WriterConnectionStringName { get; set; }

    /// <summary>Gets or sets the dedicated schema connection-string name.</summary>
    public string? SchemaConnectionStringName { get; set; }

    /// <summary>Gets or sets the dedicated arbitrary SQL connection-string name.</summary>
    public string? SqlConsoleConnectionStringName { get; set; }

    /// <summary>Gets or sets the immutable capability ceiling.</summary>
    public RazorDbCapability EnabledCapabilities { get; set; } = RazorDbCapabilitySets.ReadOnly;

    /// <summary>Gets the schemas visible to this registration.</summary>
    public IList<string> AllowedSchemas { get; } = [];

    /// <summary>Gets or sets whether a missing high-risk credential may explicitly fall back to a shared credential.</summary>
    public bool AllowSharedHighRiskCredential { get; set; }

    /// <summary>Gets or sets whether TLS requirements may be relaxed in a Development host only.</summary>
    public bool AllowInsecureDevelopmentConnection { get; set; }

    /// <summary>Gets or sets whether SQL restore is explicitly enabled with Import and ExecuteSql capabilities.</summary>
    public bool EnableSqlRestore { get; set; }

    /// <summary>Gets or sets metadata cache duration in seconds.</summary>
    public int MetadataCacheSeconds { get; set; } = 30;

    /// <summary>Gets or sets the default browser page size.</summary>
    public int DefaultPageSize { get; set; } = 100;

    /// <summary>Gets or sets the maximum browser page size.</summary>
    public int MaximumPageSize { get; set; } = 500;

    /// <summary>Gets or sets the maximum interactive response size.</summary>
    public int MaximumResponseBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Gets or sets the maximum inline cell preview size.</summary>
    public int MaximumCellPreviewBytes { get; set; } = 64 * 1024;

    /// <summary>Gets or sets the maximum size of one streamed binary or geometry download.</summary>
    public long MaximumBinaryDownloadBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>Gets or sets the SQL console timeout.</summary>
    public int SqlCommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets the maximum SQL input size.</summary>
    public int MaximumSqlTextBytes { get; set; } = 1024 * 1024;

    /// <summary>Gets or sets the maximum number of statements accepted in one SQL-console batch.</summary>
    public int MaximumSqlStatements { get; set; } = 100;

    /// <summary>Gets or sets the maximum SQL result row count.</summary>
    public int MaximumSqlRows { get; set; } = 1_000;

    /// <summary>Gets or sets the maximum SQL result size.</summary>
    public int MaximumSqlResultBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Gets or sets the maximum CSV column count.</summary>
    public int MaximumImportColumns { get; set; } = 1_024;

    /// <summary>Gets or sets the maximum raw byte size of one logical CSV record.</summary>
    public int MaximumCsvRecordBytes { get; set; } = 1024 * 1024;

    /// <summary>Gets or sets the maximum upload size.</summary>
    public long MaximumUploadBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>Gets or sets the maximum export row count.</summary>
    public long MaximumExportRows { get; set; } = 100_000;

    /// <summary>Gets or sets the maximum export size.</summary>
    public long MaximumExportBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Gets or sets the largest single value materialized while streaming an export.</summary>
    public int MaximumExportCellBytes { get; set; } = 16 * 1024 * 1024;
}

internal enum MySqlCredentialSlot
{
    Reader,
    Writer,
    Schema,
    SqlConsole,
}
