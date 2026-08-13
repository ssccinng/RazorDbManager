using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using RazorDbManager.Core;

namespace RazorDbManager;

internal sealed record ProtectedRowExportQuery(string Payload, string PlaintextHash);

internal sealed class RowExportQueryProtector(IDataProtectionProvider dataProtectionProvider)
{
    internal const string PayloadParameter = "rowQueryProtected";
    internal const string HashParameter = "rowQueryHash";
    internal const string ClearedParameter = "rowQueryState";
    internal const string LegacyPlaintextParameter = "rowQuery";
    internal const string ProtectorPurpose = "RazorDbManager.RowExportQuery.v1";

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);

    public ProtectedRowExportQuery Protect(RowExportQuery query)
    {
        string plaintext = RowExportQueryCodec.Serialize(query);
        return new ProtectedRowExportQuery(
            _protector.Protect(plaintext),
            ComputeHash(plaintext));
    }

    public RowExportQuery Unprotect(string? payload, string? expectedPlaintextHash)
    {
        if (string.IsNullOrWhiteSpace(payload) || !IsSha256(expectedPlaintextHash)) throw Invalid();
        try
        {
            string plaintext = _protector.Unprotect(payload);
            string actualHash = ComputeHash(plaintext);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualHash),
                    Encoding.ASCII.GetBytes(expectedPlaintextHash!)))
            {
                throw Invalid();
            }

            return RowExportQueryCodec.Deserialize(plaintext);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException
            or RazorDbException or System.Text.Json.JsonException or ArgumentException)
        {
            throw new RazorDbException(
                RazorDbErrorCode.Forbidden,
                "The protected export row selection is invalid or no longer readable.",
                exception);
        }
    }

    public void AddProtectedParameters(IDictionary<string, string> parameters, RowExportQuery query)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ProtectedRowExportQuery protectedQuery = Protect(query);
        parameters[PayloadParameter] = protectedQuery.Payload;
        parameters[HashParameter] = protectedQuery.PlaintextHash;
    }

    public static IReadOnlyDictionary<string, string> TerminalParameters(
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Dictionary<string, string> sanitized = new(parameters, StringComparer.Ordinal);
        bool containedRowQuery = sanitized.Remove(PayloadParameter);
        containedRowQuery |= sanitized.Remove(LegacyPlaintextParameter);
        sanitized.Remove("authorizationToken");
        if (containedRowQuery) sanitized[ClearedParameter] = "cleared";
        return sanitized;
    }

    private static string ComputeHash(string plaintext) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character)) return false;
        }
        return true;
    }

    private static RazorDbException Invalid() => new(
        RazorDbErrorCode.Forbidden,
        "The protected export row selection is invalid or no longer readable.");
}
