using RazorDbManager.Core;

namespace RazorDbManager.Sample;

/// <summary>Development-only queued-operation authorization for the fixed sample identity.</summary>
internal sealed class SampleBackgroundAuthorizer : IRazorDbBackgroundAuthorizer
{
    public ValueTask<RazorDbAuthorizationResult> AuthorizeAsync(
        RazorDbAuthorizationContext context,
        bool highRisk,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool isSampleAdministrator = string.Equals(context.Actor.Id, "sample-admin", StringComparison.Ordinal);
        return ValueTask.FromResult(isSampleAdministrator
            ? RazorDbAuthorizationResult.Allowed
            : RazorDbAuthorizationResult.Denied("sample-identity-revoked"));
    }
}

/// <summary>Development-only recent-session validation for the fixed sample identity.</summary>
internal sealed class SampleSessionValidator : IRazorDbSessionValidator
{
    public ValueTask<RazorDbSessionValidationResult> ValidateAsync(
        RazorDbSessionValidationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool isSampleAdministrator = string.Equals(context.Actor.Id, "sample-admin", StringComparison.Ordinal);
        return ValueTask.FromResult(isSampleAdministrator
            ? new RazorDbSessionValidationResult(true, DateTimeOffset.UtcNow.AddMinutes(5))
            : new RazorDbSessionValidationResult(false, ReasonCode: "sample-session-invalid"));
    }
}
