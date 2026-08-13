using Microsoft.Extensions.Hosting;
using MySqlConnector;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;

namespace RazorDbManager.MySql.Infrastructure;

internal sealed class MySqlCredentialValidator(
    DatabaseRegistration registration,
    MySqlProviderOptions options,
    IHostEnvironment environment)
{
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema", "mysql", "performance_schema", "sys",
    };

    public MySqlConnectionStringBuilder Validate(
        string connectionString,
        MySqlCredentialSlot slot,
        IReadOnlyCollection<string>? effectiveAllowedSchemas = null)
    {
        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"The {SlotName(slot)} credential for database '{registration.Id}' is not a valid MySQL connection string.", exception);
        }

        if (builder.PersistSecurityInfo)
            throw Unsafe(slot, "PersistSecurityInfo must be false.");
        if (builder.AllowLoadLocalInfile)
            throw Unsafe(slot, "AllowLoadLocalInfile must be false.");
        if (builder.SslMode != MySqlSslMode.VerifyFull
            && !(environment.IsDevelopment() && options.AllowInsecureDevelopmentConnection))
            throw Unsafe(slot, "SslMode must be VerifyFull outside an explicitly relaxed Development environment.");

        var allowedSchemas = effectiveAllowedSchemas ?? ResolveAllowedSchemas(builder);
        if (allowedSchemas.Count == 0)
            throw Unsafe(slot, "A default Database or explicit AllowedSchemas entry is required.");
        if (allowedSchemas.Any(schema => SystemSchemas.Contains(schema)))
            throw Unsafe(slot, "System schemas cannot be managed.");
        if (!string.IsNullOrWhiteSpace(builder.Database)
            && !allowedSchemas.Contains(builder.Database, StringComparer.OrdinalIgnoreCase))
            throw Unsafe(slot, "The credential Database must be included in AllowedSchemas.");
        if (slot == MySqlCredentialSlot.SqlConsole && string.IsNullOrWhiteSpace(builder.Database))
            throw Unsafe(slot, "The SQL-console credential must select an allowed Database.");

        return builder;
    }

    public IReadOnlyCollection<string> ResolveAllowedSchemas(MySqlConnectionStringBuilder reader)
    {
        string[] schemas = options.AllowedSchemas.Count > 0
            ? options.AllowedSchemas.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : string.IsNullOrWhiteSpace(reader.Database) ? [] : [reader.Database];
        if (schemas.Length == 0)
            throw Unsafe(MySqlCredentialSlot.Reader, "A default Database or explicit AllowedSchemas entry is required.");
        return schemas;
    }

    private InvalidOperationException Unsafe(MySqlCredentialSlot slot, string message) =>
        new($"The {SlotName(slot)} credential for database '{registration.Id}' is unsafe: {message}");

    private static string SlotName(MySqlCredentialSlot slot) => slot switch
    {
        MySqlCredentialSlot.Reader => "reader",
        MySqlCredentialSlot.Writer => "writer",
        MySqlCredentialSlot.Schema => "schema",
        MySqlCredentialSlot.SqlConsole => "SQL-console",
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };
}
