using Microsoft.Extensions.Configuration;
using RazorDbManager.Core;

namespace RazorDbManager.MySql.Infrastructure;

internal sealed class ConfigurationRazorDbCredentialProvider(IConfiguration configuration) : IRazorDbCredentialProvider
{
    public ValueTask<RazorDbCredential> GetCredentialAsync(
        DatabaseRegistration registration,
        RazorDbCredentialPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = purpose switch
        {
            RazorDbCredentialPurpose.Reader => registration.ConnectionStringName,
            RazorDbCredentialPurpose.Writer => registration.WriterConnectionStringName ?? registration.ConnectionStringName,
            RazorDbCredentialPurpose.Schema => registration.SchemaConnectionStringName
                ?? (registration.AllowSharedHighRiskCredential ? registration.ConnectionStringName : null),
            RazorDbCredentialPurpose.SqlConsole => registration.SqlConsoleConnectionStringName
                ?? (registration.AllowSharedHighRiskCredential ? registration.ConnectionStringName : null),
            RazorDbCredentialPurpose.Transfer => registration.WriterConnectionStringName ?? registration.ConnectionStringName,
            _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
        } ?? throw new InvalidOperationException($"No credential is configured for purpose '{purpose}'.");
        var connectionString = configuration.GetConnectionString(name)
            ?? throw new InvalidOperationException($"Connection string '{name}' was not found.");
        return ValueTask.FromResult(new RazorDbCredential(connectionString));
    }
}
