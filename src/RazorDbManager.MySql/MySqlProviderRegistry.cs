using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;
using RazorDbManager.MySql.Data;
using RazorDbManager.MySql.Health;
using RazorDbManager.MySql.Infrastructure;
using RazorDbManager.MySql.Metadata;
using RazorDbManager.MySql.Schema;
using RazorDbManager.MySql.Sql;
using RazorDbManager.MySql.Transfer;

namespace RazorDbManager.MySql;

internal sealed record MySqlRegistrationDescriptor(DatabaseRegistration Registration, MySqlProviderOptions Options);

internal sealed class MySqlProviderRegistry(
    IEnumerable<MySqlRegistrationDescriptor> descriptors,
    IHostEnvironment environment,
    IRazorDbCredentialProvider credentialProvider) : IRazorDbProviderRegistry
{
    private readonly IReadOnlyDictionary<string, MySqlRegistrationDescriptor> _registrations = Build(descriptors);
    private readonly ConcurrentDictionary<string, Lazy<Task<IRazorDbProvider>>> _providers =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<DatabaseRegistration> Registrations => _registrations.Values.Select(value => value.Registration).ToArray();

    public DatabaseRegistration GetRequiredRegistration(string databaseId) =>
        _registrations.TryGetValue(databaseId, out var registration)
            ? registration.Registration
            : throw new KeyNotFoundException($"Database registration '{databaseId}' was not found.");

    public async ValueTask<IRazorDbProvider> GetProviderAsync(string databaseId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registrations.TryGetValue(databaseId, out var descriptor))
            throw new KeyNotFoundException($"Database registration '{databaseId}' was not found.");

        var provider = _providers.GetOrAdd(databaseId, _ => new Lazy<Task<IRazorDbProvider>>(
            () => CreateProviderAsync(databaseId, descriptor),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await provider.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (provider.IsValueCreated && provider.Value.IsFaulted)
            {
                _providers.TryRemove(new KeyValuePair<string, Lazy<Task<IRazorDbProvider>>>(databaseId, provider));
            }

            throw;
        }
    }

    private async Task<IRazorDbProvider> CreateProviderAsync(
        string databaseId,
        MySqlRegistrationDescriptor descriptor)
    {
        var validator = new MySqlProviderOptionsValidator();
        validator.Validate(databaseId, descriptor.Options);
        var credentialValidator = new MySqlCredentialValidator(descriptor.Registration, descriptor.Options, environment);
        var credentials = new MySqlCredentialSource(descriptor.Registration, credentialProvider, credentialValidator);
        var readerConnectionString = await credentials.GetConnectionStringAsync(MySqlCredentialSlot.Reader).ConfigureAwait(false);
        var schemas = MySqlDatabaseGuard.ResolveAllowedSchemas(descriptor.Options, readerConnectionString);
        var guard = new MySqlDatabaseGuard(schemas);
        var health = new MySqlHealthProbe(descriptor.Registration, credentials, schemas);
        var metadata = new MySqlMetadataService(databaseId, descriptor.Registration, descriptor.Options, credentials, guard);
        var data = new MySqlDataService(descriptor.Options, credentials, guard, metadata);
        var ddlGenerator = new MySqlDdlGenerator();
        var schema = new MySqlSchemaService(credentials, guard, metadata, ddlGenerator);
        var sql = new MySqlSqlService(descriptor.Options, credentials);
        var dump = new MySqlSqlDumpService(descriptor.Options, credentials, guard, metadata, schemas);
        var transfer = new MySqlTransferService(descriptor.Options, credentials, guard, metadata, dump);
        return new MySqlRazorDbProvider(
            descriptor.Registration, health, metadata, data, schema, sql, transfer);
    }

    private static IReadOnlyDictionary<string, MySqlRegistrationDescriptor> Build(IEnumerable<MySqlRegistrationDescriptor> descriptors)
    {
        var result = new Dictionary<string, MySqlRegistrationDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            descriptor.Registration.Validate();
            if (!result.TryAdd(descriptor.Registration.Id, descriptor))
                throw new InvalidOperationException($"Database id '{descriptor.Registration.Id}' is registered more than once.");
        }
        return result;
    }
}
