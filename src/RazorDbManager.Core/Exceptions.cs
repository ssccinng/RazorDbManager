namespace RazorDbManager.Core;

/// <summary>Classifies safe failures crossing a provider boundary.</summary>
public enum RazorDbErrorCode
{
    /// <summary>The request is invalid.</summary>
    Validation,
    /// <summary>The actor lacks access.</summary>
    Forbidden,
    /// <summary>The registration or database object does not exist.</summary>
    NotFound,
    /// <summary>A row or schema changed concurrently.</summary>
    Conflict,
    /// <summary>A configured resource limit was exceeded.</summary>
    LimitExceeded,
    /// <summary>The provider cannot perform the requested operation.</summary>
    Unsupported,
    /// <summary>Authentication to the database failed.</summary>
    DatabaseAuthentication,
    /// <summary>The database was unavailable.</summary>
    DatabaseUnavailable,
    /// <summary>The database rejected the operation.</summary>
    DatabaseRejected,
    /// <summary>The operation was cancelled.</summary>
    Cancelled,
    /// <summary>An unexpected provider failure occurred.</summary>
    ProviderFailure,
}

/// <summary>A provider-neutral exception whose message is safe to present to an authorized actor.</summary>
public class RazorDbException : Exception
{
    /// <summary>Initializes a safe provider-neutral failure.</summary>
    /// <param name="code">The stable error classification.</param>
    /// <param name="message">A sanitized message containing no credentials, SQL parameters, or row values.</param>
    /// <param name="innerException">The optional internal cause, which must not be serialized to a client.</param>
    public RazorDbException(RazorDbErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Gets the stable error classification.</summary>
    public RazorDbErrorCode Code { get; }
}
