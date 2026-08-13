using Microsoft.Extensions.Configuration;
using RazorDbManager.Core;

namespace RazorDbManager;

internal sealed class ConfigurationCredentialProvider(IConfiguration configuration) : IRazorDbCredentialProvider
{
    public ValueTask<RazorDbCredential> GetCredentialAsync(DatabaseRegistration registration, RazorDbCredentialPurpose purpose, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = purpose switch
        {
            RazorDbCredentialPurpose.Reader => registration.ConnectionStringName,
            RazorDbCredentialPurpose.Writer => registration.WriterConnectionStringName ?? registration.ConnectionStringName,
            RazorDbCredentialPurpose.Schema => registration.SchemaConnectionStringName
                ?? (registration.AllowSharedHighRiskCredential ? registration.ConnectionStringName : throw Missing(purpose)),
            RazorDbCredentialPurpose.SqlConsole => registration.SqlConsoleConnectionStringName
                ?? (registration.AllowSharedHighRiskCredential ? registration.ConnectionStringName : throw Missing(purpose)),
            RazorDbCredentialPurpose.Transfer => registration.WriterConnectionStringName ?? registration.ConnectionStringName,
            _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
        };
        string? value = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"Connection string '{name}' is not configured.");
        return ValueTask.FromResult(new RazorDbCredential(value));
    }

    private static InvalidOperationException Missing(RazorDbCredentialPurpose purpose) =>
        new($"A dedicated {purpose} credential is required unless AllowSharedHighRiskCredential is explicitly enabled.");
}
