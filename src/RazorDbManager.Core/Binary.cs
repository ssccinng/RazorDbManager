namespace RazorDbManager.Core;

/// <summary>Identifies one binary or geometry cell using a current safe row identity.</summary>
/// <param name="DatabaseId">The logical database registration.</param>
/// <param name="Table">The schema-qualified source table.</param>
/// <param name="Column">The binary or geometry column.</param>
/// <param name="Identity">A primary or safe unique row identity.</param>
public sealed record BinaryCellRequest(
    string DatabaseId,
    DbObjectName Table,
    string Column,
    RowIdentity Identity)
{
    /// <summary>Validates provider-neutral request invariants.</summary>
    /// <returns>This request.</returns>
    public BinaryCellRequest Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DatabaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Column);
        ArgumentNullException.ThrowIfNull(Identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(Identity.KeyName);
        if (Identity.Values is null || Identity.Values.Count is < 1 or > 64)
        {
            throw new ArgumentException("A row identity must contain between 1 and 64 values.", nameof(Identity));
        }

        if (Identity.Values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || pair.Value.IsNull))
        {
            throw new ArgumentException("Row identity columns and values must be non-empty and non-null.", nameof(Identity));
        }

        return this;
    }
}

/// <summary>Describes a bounded binary value without materializing its contents.</summary>
/// <param name="Length">The exact byte length.</param>
/// <param name="ContentType">The safe response media type.</param>
/// <param name="FileName">The suggested download file name.</param>
/// <param name="Kind">Whether the value is binary or geometry data.</param>
public sealed record BinaryCellDescriptor(
    long Length,
    string ContentType,
    string FileName,
    DbDataKind Kind);

/// <summary>
/// Owns the provider reader, connection, and related resources for one bounded binary download.
/// The caller must dispose the session after copying or abandoning the response.
/// </summary>
public interface IRazorDbBinaryReadSession : IAsyncDisposable
{
    /// <summary>Gets non-sensitive response metadata.</summary>
    BinaryCellDescriptor Descriptor { get; }

    /// <summary>Copies the value directly to a caller-owned destination without materializing it.</summary>
    ValueTask CopyToAsync(Stream destination, CancellationToken cancellationToken = default);
}
