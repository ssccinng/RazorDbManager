using RazorDbManager.Core;

namespace RazorDbManager;

/// <summary>
/// Revalidates a queued operation immediately before a background worker executes it.
/// </summary>
/// <remarks>
/// Host implementations should consult current account state and current Access or HighRisk
/// policy equivalents. The default implementation rechecks the resource authorizer and, for
/// high-risk work, the session validator.
/// </remarks>
public interface IRazorDbBackgroundAuthorizer
{
    /// <summary>Revalidates one actor, operation, capability, and resource.</summary>
    /// <param name="context">The current operation authorization context.</param>
    /// <param name="highRisk">Whether current high-risk authorization is required.</param>
    /// <param name="cancellationToken">Cancels the authorization check.</param>
    /// <returns>The current authorization decision.</returns>
    ValueTask<RazorDbAuthorizationResult> AuthorizeAsync(
        RazorDbAuthorizationContext context,
        bool highRisk,
        CancellationToken cancellationToken = default);
}

internal sealed class DefaultRazorDbBackgroundAuthorizer(
    IRazorDbManagerAuthorizer resourceAuthorizer,
    IRazorDbSessionValidator sessionValidator) : IRazorDbBackgroundAuthorizer
{
    public async ValueTask<RazorDbAuthorizationResult> AuthorizeAsync(
        RazorDbAuthorizationContext context,
        bool highRisk,
        CancellationToken cancellationToken = default)
    {
        RazorDbAuthorizationResult resource = await resourceAuthorizer.AuthorizeAsync(context, cancellationToken);
        if (!resource.IsAllowed || !highRisk) return resource;
        RazorDbSessionValidationResult session = await sessionValidator.ValidateAsync(
            new RazorDbSessionValidationContext(context.Actor, context.Registration, context.Operation, context.Resource),
            cancellationToken);
        return session.IsValid
            ? RazorDbAuthorizationResult.Allowed
            : RazorDbAuthorizationResult.Denied(session.ReasonCode ?? "background-high-risk-denied");
    }
}
