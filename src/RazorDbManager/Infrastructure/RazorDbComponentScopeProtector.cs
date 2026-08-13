using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace RazorDbManager;

internal readonly record struct RazorDbComponentScopeValidation(
    bool IsValid,
    bool IsReadOnly,
    string ReasonCode)
{
    public static RazorDbComponentScopeValidation Invalid(string reasonCode) => new(false, true, reasonCode);
}

internal sealed class RazorDbComponentScopeProtector
{
    internal const string FormFieldName = "componentScope";
    internal static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(4);

    private readonly ITimeLimitedDataProtector _protector;

    public RazorDbComponentScopeProtector(IDataProtectionProvider dataProtection)
    {
        _protector = dataProtection
            .CreateProtector("RazorDbManager.ComponentScope.v1")
            .ToTimeLimitedDataProtector();
    }

    public string Protect(string actorId, string databaseId, bool readOnly) =>
        Protect(actorId, databaseId, readOnly, TokenLifetime);

    internal string Protect(string actorId, string databaseId, bool readOnly, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (actorId.Length > 512) throw new ArgumentException("The actor identifier is too long.", nameof(actorId));
        if (databaseId.Length > 128) throw new ArgumentException("The database identifier is too long.", nameof(databaseId));

        string payload = JsonSerializer.Serialize(new ComponentScopePayload(1, actorId, databaseId, readOnly));
        return _protector.Protect(payload, lifetime);
    }

    public RazorDbComponentScopeValidation Validate(string? token, string actorId, string databaseId)
    {
        if (string.IsNullOrWhiteSpace(token))
            return RazorDbComponentScopeValidation.Invalid("component-scope-missing");
        if (token.Length > 4_096)
            return RazorDbComponentScopeValidation.Invalid("component-scope-invalid");

        try
        {
            ComponentScopePayload? payload = JsonSerializer.Deserialize<ComponentScopePayload>(_protector.Unprotect(token));
            if (payload is null || payload.Version != 1
                || string.IsNullOrWhiteSpace(payload.ActorId)
                || string.IsNullOrWhiteSpace(payload.DatabaseId))
                return RazorDbComponentScopeValidation.Invalid("component-scope-invalid");
            if (!string.Equals(payload.ActorId, actorId, StringComparison.Ordinal))
                return RazorDbComponentScopeValidation.Invalid("component-scope-actor");
            if (!string.Equals(payload.DatabaseId, databaseId, StringComparison.OrdinalIgnoreCase))
                return RazorDbComponentScopeValidation.Invalid("component-scope-database");
            return new RazorDbComponentScopeValidation(true, payload.ReadOnly, "component-scope-valid");
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return RazorDbComponentScopeValidation.Invalid("component-scope-invalid");
        }
    }

    private sealed record ComponentScopePayload(int Version, string ActorId, string DatabaseId, bool ReadOnly);
}
