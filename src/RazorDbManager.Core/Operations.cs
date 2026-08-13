namespace RazorDbManager.Core;

/// <summary>Identifies a host-authenticated actor without depending on an ASP.NET identity type.</summary>
/// <param name="Id">The stable, non-empty subject identifier.</param>
/// <param name="DisplayName">An optional display name.</param>
/// <param name="AuthenticationTime">The instant at which the current authentication was established, when known.</param>
public sealed record RazorDbActor(string Id, string? DisplayName = null, DateTimeOffset? AuthenticationTime = null)
{
    /// <summary>Gets a validated copy of this actor.</summary>
    /// <returns>This actor.</returns>
    /// <exception cref="InvalidOperationException">The subject identifier is empty.</exception>
    public RazorDbActor Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new InvalidOperationException("An actor id is required.");
        }

        return this;
    }
}

/// <summary>Classifies a user-visible database operation for authorization and auditing.</summary>
public enum RazorDbOperation
{
    /// <summary>Browse schemas and object metadata.</summary>
    BrowseMetadata,
    /// <summary>Query table data.</summary>
    ReadRows,
    /// <summary>Insert a row.</summary>
    InsertRow,
    /// <summary>Update a row.</summary>
    UpdateRow,
    /// <summary>Delete a row.</summary>
    DeleteRow,
    /// <summary>Delete a bounded set of rows atomically.</summary>
    DeleteRows,
    /// <summary>Preview a structured schema change.</summary>
    PreviewSchema,
    /// <summary>Execute a structured schema change.</summary>
    ExecuteSchema,
    /// <summary>Execute arbitrary SQL.</summary>
    ExecuteSql,
    /// <summary>Import database content.</summary>
    Import,
    /// <summary>Export database content.</summary>
    Export,
    /// <summary>Download binary content.</summary>
    DownloadBinary,
    /// <summary>Cancel an owned background transfer.</summary>
    CancelJob,
}

/// <summary>Provides an optional schema and object target for authorization and audit records.</summary>
/// <param name="Schema">The schema name, when applicable.</param>
/// <param name="Object">The object name, when applicable.</param>
/// <param name="Subresource">A column, constraint, artifact, or other safe identifier.</param>
public sealed record RazorDbResource(string? Schema = null, string? Object = null, string? Subresource = null)
{
    /// <summary>Creates a resource from a schema-qualified object name.</summary>
    /// <param name="name">The object name.</param>
    /// <returns>A resource.</returns>
    public static RazorDbResource FromObject(DbObjectName name) => new(name.Schema, name.Name);
}

/// <summary>Associates an operation with its actor, registration, required capability, and resource.</summary>
/// <param name="Actor">The authenticated actor.</param>
/// <param name="Registration">The target registration.</param>
/// <param name="Operation">The requested operation.</param>
/// <param name="RequiredCapability">The capability required by that operation.</param>
/// <param name="Resource">The optional target resource.</param>
public sealed record RazorDbAuthorizationContext(
    RazorDbActor Actor,
    DatabaseRegistration Registration,
    RazorDbOperation Operation,
    RazorDbCapability RequiredCapability,
    RazorDbResource? Resource = null);

/// <summary>Reports a resource-level authorization decision.</summary>
/// <param name="IsAllowed">Whether the operation is allowed.</param>
/// <param name="ReasonCode">A non-sensitive stable denial code.</param>
public sealed record RazorDbAuthorizationResult(bool IsAllowed, string? ReasonCode = null)
{
    /// <summary>Gets the standard successful decision.</summary>
    public static RazorDbAuthorizationResult Allowed { get; } = new(true);

    /// <summary>Creates a denied decision.</summary>
    /// <param name="reasonCode">A stable, non-sensitive reason code.</param>
    /// <returns>A denied decision.</returns>
    public static RazorDbAuthorizationResult Denied(string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return new RazorDbAuthorizationResult(false, reasonCode);
    }
}

/// <summary>Supplies an additional host-defined, resource-level authorization boundary.</summary>
public interface IRazorDbManagerAuthorizer
{
    /// <summary>Authorizes one operation after host policy and capability checks.</summary>
    ValueTask<RazorDbAuthorizationResult> AuthorizeAsync(
        RazorDbAuthorizationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Requests recent-session verification for a high-risk operation.</summary>
/// <param name="Actor">The authenticated actor.</param>
/// <param name="Registration">The target registration.</param>
/// <param name="Operation">The high-risk operation.</param>
/// <param name="Resource">The optional target resource.</param>
public sealed record RazorDbSessionValidationContext(
    RazorDbActor Actor,
    DatabaseRegistration Registration,
    RazorDbOperation Operation,
    RazorDbResource? Resource = null);

/// <summary>Reports whether the current session is sufficiently recent for a high-risk operation.</summary>
/// <param name="IsValid">Whether execution may proceed.</param>
/// <param name="ValidUntil">The instant after which another validation is required.</param>
/// <param name="ReasonCode">A non-sensitive stable failure code.</param>
public sealed record RazorDbSessionValidationResult(
    bool IsValid,
    DateTimeOffset? ValidUntil = null,
    string? ReasonCode = null);

/// <summary>Validates recent authentication for high-risk operations.</summary>
public interface IRazorDbSessionValidator
{
    /// <summary>Validates one actor's current session.</summary>
    ValueTask<RazorDbSessionValidationResult> ValidateAsync(
        RazorDbSessionValidationContext context,
        CancellationToken cancellationToken = default);
}
