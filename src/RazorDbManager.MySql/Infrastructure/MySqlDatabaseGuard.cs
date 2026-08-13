using RazorDbManager.MySql.Configuration;

namespace RazorDbManager.MySql.Infrastructure;

internal sealed class MySqlDatabaseGuard(IReadOnlyCollection<string> allowedSchemas)
{
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema", "mysql", "performance_schema", "sys",
    };

    private readonly HashSet<string> _allowedSchemas = new(allowedSchemas, StringComparer.OrdinalIgnoreCase);

    public void EnsureAllowed(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (SystemSchemas.Contains(schema) || !_allowedSchemas.Contains(schema))
        {
            throw new UnauthorizedAccessException($"Schema '{schema}' is outside the provider allowlist.");
        }
    }

    public static IReadOnlyCollection<string> ResolveAllowedSchemas(
        MySqlProviderOptions options,
        string readerConnectionString)
    {
        if (options.AllowedSchemas.Count > 0)
        {
            return options.AllowedSchemas.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        var database = new MySqlConnector.MySqlConnectionStringBuilder(readerConnectionString).Database;
        return string.IsNullOrWhiteSpace(database)
            ? throw new InvalidOperationException("No allowed schema is configured.")
            : [database];
    }
}
