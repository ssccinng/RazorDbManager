using RazorDbManager.Core;
using RazorDbManager.MySql.Data;
using RazorDbManager.MySql.Health;
using RazorDbManager.MySql.Metadata;
using RazorDbManager.MySql.Schema;
using RazorDbManager.MySql.Sql;

namespace RazorDbManager.MySql;

internal sealed class MySqlRazorDbProvider(
    DatabaseRegistration registration,
    MySqlHealthProbe health,
    MySqlMetadataService metadata,
    MySqlDataService data,
    MySqlSchemaService schema,
    MySqlSqlService sql,
    IRazorDbTransferProvider transfer) : IRazorDbProvider,
    IRazorDbMetadataProvider,
    IRazorDbDataProvider,
    IRazorDbSchemaProvider,
    IRazorDbSqlProvider,
    IRazorDbTransferProvider,
    IRazorDbProviderHealthProbe
{
    public const string ProviderId = "mysql";
    public string ProviderName => ProviderId;
    public DatabaseRegistration Registration => registration;
    public IRazorDbMetadataProvider Metadata => this;
    public IRazorDbDataProvider Data => this;
    public IRazorDbSchemaProvider Schema => this;
    public IRazorDbSqlProvider Sql => this;
    public IRazorDbTransferProvider Transfer => this;

    public ValueTask<RazorDbProviderHealthReport> CheckHealthAsync(
        CancellationToken cancellationToken = default) =>
        health.CheckHealthAsync(cancellationToken);

    public ValueTask<DatabaseMetadata> GetDatabaseAsync(MetadataRequest request, CancellationToken cancellationToken = default)
    {
        EnsureDatabase(request.DatabaseId);
        EnsureCapability(RazorDbCapability.BrowseMetadata);
        return new(metadata.GetDatabaseAsync(request.Refresh, cancellationToken));
    }

    public ValueTask<DbTableMetadata> GetTableAsync(DbObjectName table, bool refresh = false, CancellationToken cancellationToken = default)
    {
        EnsureCapability(RazorDbCapability.BrowseMetadata);
        return new(metadata.GetTableAsync(table, refresh, cancellationToken));
    }

    public ValueTask<RowPage> QueryRowsAsync(RowQueryRequest request, CancellationToken cancellationToken = default)
    {
        EnsureDatabase(request.DatabaseId);
        EnsureCapability(RazorDbCapability.ReadRows);
        return new(data.QueryAsync(request, cancellationToken));
    }

    public ValueTask<RowMutationResult> InsertRowAsync(InsertRowRequest request, CancellationToken cancellationToken = default)
    {
        EnsureDatabase(request.DatabaseId);
        EnsureCapability(RazorDbCapability.InsertRows);
        return new(data.InsertAsync(request, cancellationToken));
    }

    public ValueTask<RowMutationResult> UpdateRowAsync(UpdateRowRequest request, CancellationToken cancellationToken = default)
    {
        EnsureDatabase(request.DatabaseId);
        EnsureCapability(RazorDbCapability.UpdateRows);
        return new(data.UpdateAsync(request, cancellationToken));
    }

    public ValueTask<RowMutationResult> DeleteRowAsync(DeleteRowRequest request, CancellationToken cancellationToken = default)
    {
        EnsureDatabase(request.DatabaseId);
        EnsureCapability(RazorDbCapability.DeleteRows);
        return new(data.DeleteAsync(request, cancellationToken));
    }

    public ValueTask<BatchRowMutationResult> DeleteRowsAsync(
        DeleteRowsRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase(request.DatabaseId);
        EnsureCapability(RazorDbCapability.DeleteRows);
        return new(data.DeleteManyAsync(request, cancellationToken));
    }

    public ValueTask<IRazorDbBinaryReadSession> OpenBinaryAsync(
        BinaryCellRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase(request.DatabaseId);
        EnsureCapability(RazorDbCapability.DownloadBinary);
        return new(data.OpenBinaryAsync(request, cancellationToken));
    }

    public ValueTask<DdlPreview> PreviewAsync(SchemaChangeRequest request, CancellationToken cancellationToken = default)
    {
        EnsureDatabase(request.DatabaseId);
        EnsureCapability(RazorDbCapability.ModifySchema);
        if (request.Change.IsDestructive) EnsureCapability(RazorDbCapability.DestructiveSchema);
        return new(schema.PreviewAsync(request, cancellationToken));
    }

    public ValueTask<DdlExecutionResult> ExecuteAsync(DdlExecutionRequest request, CancellationToken cancellationToken = default)
    {
        EnsureDatabase(request.DatabaseId);
        EnsureCapability(RazorDbCapability.ModifySchema);
        if (request.Change.IsDestructive) EnsureCapability(RazorDbCapability.DestructiveSchema);
        return new(schema.ExecuteAsync(request, cancellationToken));
    }

    public ValueTask<SqlExecutionResult> ExecuteAsync(SqlExecutionRequest request, CancellationToken cancellationToken = default)
    {
        EnsureDatabase(request.DatabaseId);
        EnsureCapability(RazorDbCapability.ExecuteSql);
        return new(sql.ExecuteAsync(request, cancellationToken));
    }

    public ValueTask<TransferResult> ImportAsync(
        ImportRequest request,
        Stream source,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase(request.DatabaseId);
        EnsureCapability(RazorDbCapability.Import);
        if (request.Format == TransferFormat.Sql)
        {
            EnsureCapability(RazorDbCapability.ExecuteSql);
        }

        return transfer.ImportAsync(request, source, progress, cancellationToken);
    }

    public ValueTask<TransferResult> ExportAsync(
        ExportRequest request,
        Stream destination,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase(request.DatabaseId);
        EnsureCapability(RazorDbCapability.Export);
        return transfer.ExportAsync(request, destination, progress, cancellationToken);
    }

    private void EnsureDatabase(string databaseId)
    {
        if (!registration.Id.Equals(databaseId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Database registration '{databaseId}' was not found.");
    }

    private void EnsureCapability(RazorDbCapability capability)
    {
        if (!registration.EnabledCapabilities.Includes(capability))
            throw new RazorDbException(RazorDbErrorCode.Forbidden, "The database registration does not enable this operation.");
    }
}
