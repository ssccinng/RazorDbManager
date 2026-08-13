namespace RazorDbManager.Core;

/// <summary>Contains all security-relevant values bound to a short-lived operation token.</summary>
/// <param name="ActorId">The stable actor identifier.</param>
/// <param name="DatabaseId">The registration identifier.</param>
/// <param name="Operation">The confirmed operation.</param>
/// <param name="Resource">The optional target resource.</param>
/// <param name="SchemaFingerprint">The previewed schema fingerprint.</param>
/// <param name="PayloadHash">The previewed SQL or request hash.</param>
public sealed record RazorDbOperationTokenContext(
    string ActorId,
    string DatabaseId,
    RazorDbOperation Operation,
    RazorDbResource? Resource,
    string SchemaFingerprint,
    string PayloadHash);

/// <summary>Represents an opaque operation token and its expiration.</summary>
/// <param name="Value">The opaque token value.</param>
/// <param name="ExpiresAt">The expiration instant.</param>
public sealed record RazorDbOperationToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>Reports one attempt to consume an operation token.</summary>
/// <param name="IsValid">Whether the token matched, was unexpired, and was consumed exactly once.</param>
/// <param name="ReasonCode">A stable, non-sensitive rejection code.</param>
public sealed record RazorDbOperationTokenResult(bool IsValid, string? ReasonCode = null);

/// <summary>Issues and atomically consumes actor-bound, single-use operation tokens.</summary>
public interface IRazorDbOperationTokenStore
{
    /// <summary>Issues a token bound to the complete operation context.</summary>
    ValueTask<RazorDbOperationToken> IssueAsync(
        RazorDbOperationTokenContext context,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically validates and consumes a token, preventing replay.</summary>
    ValueTask<RazorDbOperationTokenResult> ConsumeAsync(
        string token,
        RazorDbOperationTokenContext expectedContext,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
