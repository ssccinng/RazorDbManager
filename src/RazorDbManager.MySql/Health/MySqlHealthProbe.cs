using MySqlConnector;
using RazorDbManager.Core;
using RazorDbManager.MySql.Configuration;
using RazorDbManager.MySql.Infrastructure;

namespace RazorDbManager.MySql.Health;

internal sealed class MySqlHealthProbe(
    DatabaseRegistration registration,
    MySqlCredentialSource credentials,
    IReadOnlyCollection<string> allowedSchemas)
{
    private const int DiagnosticCommandTimeoutSeconds = 5;
    private static readonly RazorDbCapability ReaderCapabilities = RazorDbCapability.BrowseMetadata
        | RazorDbCapability.ReadRows
        | RazorDbCapability.Export
        | RazorDbCapability.DownloadBinary;
    private static readonly RazorDbCapability ExcessiveReaderCapabilities = RazorDbCapability.InsertRows
        | RazorDbCapability.UpdateRows
        | RazorDbCapability.DeleteRows
        | RazorDbCapability.ModifySchema
        | RazorDbCapability.DestructiveSchema
        | RazorDbCapability.ExecuteSql
        | RazorDbCapability.Import;

    public async ValueTask<RazorDbProviderHealthReport> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<RazorDbHealthDiagnostic>();
        try
        {
            await using MySqlDataSource dataSource = await credentials
                .CreateDataSourceAsync(MySqlCredentialSlot.Reader, cancellationToken)
                .ConfigureAwait(false);
            var connectionOptions = new MySqlConnectionStringBuilder(dataSource.ConnectionString);
            bool tlsVerifyFull = connectionOptions.SslMode == MySqlSslMode.VerifyFull;
            diagnostics.Add(new RazorDbHealthDiagnostic(
                tlsVerifyFull ? "tls-verify-full" : "tls-development-relaxed",
                tlsVerifyFull
                    ? RazorDbHealthDiagnosticSeverity.Information
                    : RazorDbHealthDiagnosticSeverity.Warning));

            await using MySqlConnection connection = await dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            (string? currentDatabase, string version) = await ReadIdentityAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            diagnostics.Add(new RazorDbHealthDiagnostic(
                "reader-connection-ready",
                RazorDbHealthDiagnosticSeverity.Information));

            string? safeCurrentDatabase = null;
            if (currentDatabase is null)
            {
                diagnostics.Add(new RazorDbHealthDiagnostic(
                    "reader-database-unselected",
                    RazorDbHealthDiagnosticSeverity.Information));
            }
            else
            {
                safeCurrentDatabase = allowedSchemas.FirstOrDefault(schema =>
                    schema.Equals(currentDatabase, StringComparison.OrdinalIgnoreCase));
                if (safeCurrentDatabase is null)
                {
                    diagnostics.Add(new RazorDbHealthDiagnostic(
                        "reader-database-outside-allowlist",
                        RazorDbHealthDiagnosticSeverity.Warning));
                }
            }

            RazorDbCapability diagnosticCapabilities = RazorDbCapability.None;
            try
            {
                IReadOnlyList<string> grants = await ReadGrantsAsync(connection, cancellationToken)
                    .ConfigureAwait(false);
                MySqlGrantAnalysis analysis = MySqlGrantAnalyzer.Analyze(grants, allowedSchemas);
                diagnosticCapabilities = analysis.Capabilities;
                foreach (string code in analysis.DiagnosticCodes)
                {
                    diagnostics.Add(new RazorDbHealthDiagnostic(
                        code,
                        RazorDbHealthDiagnosticSeverity.Warning));
                }

                RazorDbCapability expectedReaderCapabilities = registration.EnabledCapabilities & ReaderCapabilities;
                if (!diagnosticCapabilities.Includes(expectedReaderCapabilities))
                {
                    diagnostics.Add(new RazorDbHealthDiagnostic(
                        "grants-reader-capabilities-incomplete",
                        RazorDbHealthDiagnosticSeverity.Warning));
                }
                if (HasExcessiveReaderCapabilities(diagnosticCapabilities))
                {
                    diagnostics.Add(new RazorDbHealthDiagnostic(
                        "grants-reader-excessive",
                        RazorDbHealthDiagnosticSeverity.Warning));
                }
                else if (analysis.DiagnosticCodes.Count == 0
                    && diagnosticCapabilities.Includes(expectedReaderCapabilities))
                {
                    diagnostics.Add(new RazorDbHealthDiagnostic(
                        "grants-discovery-ready",
                        RazorDbHealthDiagnosticSeverity.Information));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (MySqlException)
            {
                diagnostics.Add(new RazorDbHealthDiagnostic(
                    "grants-discovery-unavailable",
                    RazorDbHealthDiagnosticSeverity.Warning));
            }

            string productName = version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase)
                ? "MariaDB"
                : "MySQL";
            return new RazorDbProviderHealthReport(
                Status(diagnostics),
                productName,
                SanitizeVersion(version),
                safeCurrentDatabase,
                diagnosticCapabilities,
                diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MySqlException)
        {
            return Unavailable("reader-connection-failed", diagnostics);
        }
        catch (TimeoutException)
        {
            return Unavailable("reader-connection-timeout", diagnostics);
        }
        catch (InvalidOperationException)
        {
            return Unavailable("reader-probe-invalid", diagnostics);
        }
    }

    private static async Task<(string? CurrentDatabase, string Version)> ReadIdentityAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1, DATABASE(), VERSION()";
        command.CommandTimeout = DiagnosticCommandTimeoutSeconds;
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetInt32(0) != 1
            || reader.IsDBNull(2))
        {
            throw new InvalidOperationException("The reader identity probe returned an invalid result.");
        }

        string? currentDatabase = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (currentDatabase, reader.GetString(2));
    }

    private static async Task<IReadOnlyList<string>> ReadGrantsAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SHOW GRANTS";
        command.CommandTimeout = DiagnosticCommandTimeoutSeconds;
        var grants = new List<string>();
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0)) grants.Add(reader.GetString(0));
        }

        return grants;
    }

    private static RazorDbProviderHealthReport Unavailable(
        string code,
        List<RazorDbHealthDiagnostic> diagnostics)
    {
        diagnostics.Add(new RazorDbHealthDiagnostic(code, RazorDbHealthDiagnosticSeverity.Warning));
        return new RazorDbProviderHealthReport(
            RazorDbHealthStatus.Degraded,
            null,
            null,
            null,
            RazorDbCapability.None,
            diagnostics);
    }

    private static RazorDbHealthStatus Status(IReadOnlyCollection<RazorDbHealthDiagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == RazorDbHealthDiagnosticSeverity.Warning)
            ? RazorDbHealthStatus.Degraded
            : RazorDbHealthStatus.Ready;

    internal static bool HasExcessiveReaderCapabilities(RazorDbCapability capabilities) =>
        (capabilities & ExcessiveReaderCapabilities) != RazorDbCapability.None;

    private static string SanitizeVersion(string value)
    {
        const int maximumLength = 128;
        Span<char> buffer = stackalloc char[Math.Min(value.Length, maximumLength)];
        int written = 0;
        foreach (char character in value)
        {
            if (written == buffer.Length) break;
            buffer[written++] = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.' or '-' or '_' or '+'
                    ? character
                    : '-';
        }

        return new string(buffer[..written]);
    }
}
