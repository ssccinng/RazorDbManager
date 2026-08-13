using MySqlConnector;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;

namespace RazorDbManager.MySql.Infrastructure;

internal sealed class MySqlCredentialSource(
    DatabaseRegistration registration,
    IRazorDbCredentialProvider credentialProvider,
    MySqlCredentialValidator validator)
{
    private readonly object _schemaLock = new();
    private string[]? _effectiveAllowedSchemas;

    public async ValueTask<string> GetConnectionStringAsync(MySqlCredentialSlot slot, CancellationToken cancellationToken = default)
    {
        if (slot == MySqlCredentialSlot.Reader)
        {
            var readerConnectionString = await ResolveAsync(MySqlCredentialSlot.Reader, cancellationToken).ConfigureAwait(false);
            var establishedSchemas = Volatile.Read(ref _effectiveAllowedSchemas);
            if (establishedSchemas is not null)
            {
                _ = validator.Validate(readerConnectionString, MySqlCredentialSlot.Reader, establishedSchemas);
                return readerConnectionString;
            }

            var readerBuilder = validator.Validate(readerConnectionString, MySqlCredentialSlot.Reader);
            var candidateSchemas = validator.ResolveAllowedSchemas(readerBuilder).ToArray();
            lock (_schemaLock)
            {
                _effectiveAllowedSchemas ??= candidateSchemas;
                establishedSchemas = _effectiveAllowedSchemas;
            }

            // A concurrent first read may have established a different schema set.
            _ = validator.Validate(readerConnectionString, MySqlCredentialSlot.Reader, establishedSchemas);
            return readerConnectionString;
        }

        var effectiveSchemas = await GetEffectiveSchemasAsync(cancellationToken).ConfigureAwait(false);
        var connectionString = await ResolveAsync(slot, cancellationToken).ConfigureAwait(false);
        _ = validator.Validate(connectionString, slot, effectiveSchemas);
        return connectionString;
    }

    private async ValueTask<string> ResolveAsync(MySqlCredentialSlot slot, CancellationToken cancellationToken)
    {
        var purpose = slot switch
        {
            MySqlCredentialSlot.Reader => RazorDbCredentialPurpose.Reader,
            MySqlCredentialSlot.Writer => RazorDbCredentialPurpose.Writer,
            MySqlCredentialSlot.Schema => RazorDbCredentialPurpose.Schema,
            MySqlCredentialSlot.SqlConsole => RazorDbCredentialPurpose.SqlConsole,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };
        return (await credentialProvider.GetCredentialAsync(registration, purpose, cancellationToken).ConfigureAwait(false)).ConnectionString;
    }

    private async ValueTask<IReadOnlyCollection<string>> GetEffectiveSchemasAsync(CancellationToken cancellationToken)
    {
        var effectiveSchemas = Volatile.Read(ref _effectiveAllowedSchemas);
        if (effectiveSchemas is not null) return effectiveSchemas;
        _ = await GetConnectionStringAsync(MySqlCredentialSlot.Reader, cancellationToken).ConfigureAwait(false);
        return Volatile.Read(ref _effectiveAllowedSchemas)
            ?? throw new InvalidOperationException("The reader credential did not establish an allowed schema set.");
    }

    public async ValueTask<MySqlDataSource> CreateDataSourceAsync(MySqlCredentialSlot slot, CancellationToken cancellationToken = default)
    {
        var connectionString = await GetConnectionStringAsync(slot, cancellationToken).ConfigureAwait(false);
        var connectionBuilder = new MySqlConnectionStringBuilder(connectionString);
        connectionBuilder.AllowZeroDateTime = true;
        connectionBuilder.ConvertZeroDateTime = false;
        connectionBuilder.UseAffectedRows = false;
        connectionBuilder.AllowLoadLocalInfile = false;
        connectionBuilder.PersistSecurityInfo = false;
        connectionBuilder.AllowUserVariables = slot == MySqlCredentialSlot.SqlConsole;
        var builder = new MySqlDataSourceBuilder(connectionBuilder.ConnectionString);
        return builder.Build();
    }
}
