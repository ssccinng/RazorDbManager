namespace RazorDbManager.Core;

/// <summary>Requests creation of a server-side temporary artifact.</summary>
/// <param name="DatabaseId">The target registration.</param>
/// <param name="ActorId">The owning actor.</param>
/// <param name="FileName">A display-only file name.</param>
/// <param name="ContentType">The media type.</param>
/// <param name="ExpiresAt">The expiration instant.</param>
/// <param name="SourceResources">The resources whose data is contained in an export artifact.</param>
public sealed record RazorDbArtifactCreateRequest(
    string DatabaseId,
    string ActorId,
    string FileName,
    string ContentType,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<RazorDbResource>? SourceResources = null);

/// <summary>Describes a temporary import or export artifact.</summary>
/// <param name="Id">The opaque artifact identifier.</param>
/// <param name="DatabaseId">The target registration.</param>
/// <param name="ActorId">The owning actor.</param>
/// <param name="FileName">The display-only file name.</param>
/// <param name="ContentType">The media type.</param>
/// <param name="Length">The completed length in bytes.</param>
/// <param name="CreatedAt">The creation instant.</param>
/// <param name="ExpiresAt">The expiration instant.</param>
/// <param name="Sha256">The completed content digest.</param>
/// <param name="SourceResources">The resources whose data is contained in an export artifact.</param>
public sealed record RazorDbArtifactDescriptor(
    string Id,
    string DatabaseId,
    string ActorId,
    string FileName,
    string ContentType,
    long? Length,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? Sha256 = null,
    IReadOnlyList<RazorDbResource>? SourceResources = null);

/// <summary>Pairs a caller-owned writable stream with an incomplete artifact.</summary>
/// <param name="Descriptor">The incomplete descriptor.</param>
/// <param name="Content">The writable content stream, which the caller must dispose.</param>
public sealed record RazorDbArtifactWriteSession(RazorDbArtifactDescriptor Descriptor, Stream Content);

/// <summary>Pairs a caller-owned readable stream with a completed artifact.</summary>
/// <param name="Descriptor">The completed descriptor.</param>
/// <param name="Content">The readable content stream, which the caller must dispose.</param>
public sealed record RazorDbArtifactReadSession(RazorDbArtifactDescriptor Descriptor, Stream Content);

/// <summary>Stores temporary transfer artifacts independently of a local-file implementation.</summary>
public interface IRazorDbArtifactStore
{
    /// <summary>Creates an incomplete artifact and opens its caller-owned output stream.</summary>
    ValueTask<RazorDbArtifactWriteSession> CreateWriteAsync(
        RazorDbArtifactCreateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Marks an artifact complete after its stream has been flushed and disposed.</summary>
    ValueTask<RazorDbArtifactDescriptor> CompleteWriteAsync(
        string artifactId,
        long length,
        string sha256,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a completed artifact for reading, or returns null when unavailable.</summary>
    ValueTask<RazorDbArtifactReadSession?> OpenReadAsync(
        string artifactId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an artifact and its content. The operation is idempotent.</summary>
    ValueTask DeleteAsync(string artifactId, CancellationToken cancellationToken = default);

    /// <summary>Deletes expired and abandoned artifacts.</summary>
    ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}
