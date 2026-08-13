namespace RazorDbManager.Core;

/// <summary>Classifies the least-privilege credential required by an operation.</summary>
public enum RazorDbCredentialPurpose
{
    /// <summary>Metadata and row queries.</summary>
    Reader,
    /// <summary>Row inserts, updates, and deletes.</summary>
    Writer,
    /// <summary>Structured schema operations.</summary>
    Schema,
    /// <summary>Arbitrary SQL console operations.</summary>
    SqlConsole,
    /// <summary>Import and export operations.</summary>
    Transfer,
}

/// <summary>Wraps a sensitive provider connection value resolved only on the server.</summary>
public sealed class RazorDbCredential
{
    /// <summary>Initializes a credential.</summary>
    /// <param name="connectionString">The sensitive provider connection string.</param>
    public RazorDbCredential(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ConnectionString = connectionString;
    }

    /// <summary>Gets the sensitive connection string. It must never be logged, serialized, or exposed to a client.</summary>
    public string ConnectionString { get; }

    /// <inheritdoc />
    public override string ToString() => "[RazorDb credential]";
}

/// <summary>Resolves server-side credentials independently of a particular configuration system.</summary>
public interface IRazorDbCredentialProvider
{
    /// <summary>Resolves the least-privilege credential for an operation.</summary>
    /// <param name="registration">The target registration.</param>
    /// <param name="purpose">The credential purpose.</param>
    /// <param name="cancellationToken">Cancels asynchronous secret resolution.</param>
    /// <returns>The sensitive credential.</returns>
    ValueTask<RazorDbCredential> GetCredentialAsync(
        DatabaseRegistration registration,
        RazorDbCredentialPurpose purpose,
        CancellationToken cancellationToken = default);
}
