namespace RazorDbManager.Core;

/// <summary>Describes whether a provider can currently serve database-manager requests.</summary>
public enum RazorDbHealthStatus
{
    /// <summary>The provider's required connection and diagnostics succeeded.</summary>
    Ready,
    /// <summary>The provider is unavailable or one or more diagnostics could not be completed reliably.</summary>
    Degraded,
}

/// <summary>Describes the operational significance of a provider health diagnostic.</summary>
public enum RazorDbHealthDiagnosticSeverity
{
    /// <summary>The diagnostic records an expected, healthy condition.</summary>
    Information,
    /// <summary>The diagnostic identifies reduced availability, security posture, or diagnostic confidence.</summary>
    Warning,
}

/// <summary>Identifies one structured, non-sensitive provider health observation.</summary>
/// <param name="Code">A stable, non-sensitive machine-readable code.</param>
/// <param name="Severity">The significance of the observation.</param>
public sealed record RazorDbHealthDiagnostic(
    string Code,
    RazorDbHealthDiagnosticSeverity Severity);

/// <summary>Contains a point-in-time provider readiness and capability diagnostic.</summary>
/// <param name="Status">The provider readiness state.</param>
/// <param name="ProductName">The sanitized database product name, when available.</param>
/// <param name="ProductVersion">The sanitized database product version, when available.</param>
/// <param name="CurrentDatabase">The selected database reported by the reader connection, when available.</param>
/// <param name="DiagnosticCapabilities">
/// Capabilities inferred from database grants for diagnostics only. These values never authorize an operation.
/// </param>
/// <param name="Diagnostics">Structured observations that contain no credentials, SQL, parameter values, or exception text.</param>
public sealed record RazorDbProviderHealthReport(
    RazorDbHealthStatus Status,
    string? ProductName,
    string? ProductVersion,
    string? CurrentDatabase,
    RazorDbCapability DiagnosticCapabilities,
    IReadOnlyList<RazorDbHealthDiagnostic> Diagnostics);

/// <summary>
/// Optionally exposes a live, provider-specific readiness probe without extending the required provider contract.
/// </summary>
public interface IRazorDbProviderHealthProbe
{
    /// <summary>Runs a live, bounded provider readiness check.</summary>
    /// <param name="cancellationToken">Cancels the check and any underlying database command.</param>
    /// <returns>A sanitized readiness report.</returns>
    ValueTask<RazorDbProviderHealthReport> CheckHealthAsync(
        CancellationToken cancellationToken = default);
}
