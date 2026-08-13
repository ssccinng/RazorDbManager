namespace RazorDbManager.Core;

/// <summary>Requests insertion of one row.</summary>
/// <param name="DatabaseId">The registration identifier.</param>
/// <param name="Table">The target table.</param>
/// <param name="Values">Edited values by metadata-validated column name.</param>
/// <param name="ExpectedSchemaFingerprint">The schema fingerprint displayed to the actor.</param>
public sealed record InsertRowRequest(
    string DatabaseId,
    DbObjectName Table,
    IReadOnlyDictionary<string, EditValue> Values,
    string ExpectedSchemaFingerprint);

/// <summary>Requests an optimistic update of exactly one safely identified row.</summary>
/// <param name="DatabaseId">The registration identifier.</param>
/// <param name="Table">The target table.</param>
/// <param name="Identity">The primary or safe unique identity read with the row.</param>
/// <param name="OriginalValues">Original values used to detect concurrent changes.</param>
/// <param name="Values">Changed values by metadata-validated column name.</param>
/// <param name="ExpectedSchemaFingerprint">The schema fingerprint displayed to the actor.</param>
public sealed record UpdateRowRequest(
    string DatabaseId,
    DbObjectName Table,
    RowIdentity Identity,
    IReadOnlyDictionary<string, DbValue> OriginalValues,
    IReadOnlyDictionary<string, EditValue> Values,
    string ExpectedSchemaFingerprint);

/// <summary>Requests optimistic deletion of exactly one safely identified row.</summary>
/// <param name="DatabaseId">The registration identifier.</param>
/// <param name="Table">The target table.</param>
/// <param name="Identity">The primary or safe unique identity read with the row.</param>
/// <param name="OriginalValues">Original values used to detect concurrent changes.</param>
/// <param name="ExpectedSchemaFingerprint">The schema fingerprint displayed to the actor.</param>
public sealed record DeleteRowRequest(
    string DatabaseId,
    DbObjectName Table,
    RowIdentity Identity,
    IReadOnlyDictionary<string, DbValue> OriginalValues,
    string ExpectedSchemaFingerprint);

/// <summary>Identifies one row and its original values for an optimistic batch deletion.</summary>
/// <param name="Identity">The primary or safe unique identity read with the row.</param>
/// <param name="OriginalValues">Original values used to detect concurrent changes.</param>
public sealed record DeleteRowTarget(
    RowIdentity Identity,
    IReadOnlyDictionary<string, DbValue> OriginalValues);

/// <summary>Requests an atomic optimistic deletion of a bounded set of rows.</summary>
/// <param name="DatabaseId">The registration identifier.</param>
/// <param name="Table">The target table.</param>
/// <param name="Rows">The rows and original snapshots selected by the actor.</param>
/// <param name="ExpectedSchemaFingerprint">The schema fingerprint displayed to the actor.</param>
public sealed record DeleteRowsRequest(
    string DatabaseId,
    DbObjectName Table,
    IReadOnlyList<DeleteRowTarget> Rows,
    string ExpectedSchemaFingerprint);

/// <summary>Defines the provider-neutral safety ceiling for one interactive batch.</summary>
public static class RazorDbBatchLimits
{
    /// <summary>Maximum number of rows accepted by one interactive batch mutation.</summary>
    public const int MaximumRows = 100;
}

/// <summary>Classifies the outcome of a one-row mutation.</summary>
public enum RowMutationStatus
{
    /// <summary>Exactly one row was changed.</summary>
    Succeeded,
    /// <summary>The row or schema changed since it was read.</summary>
    Conflict,
    /// <summary>The identified row no longer exists.</summary>
    NotFound,
}

/// <summary>Reports the outcome of an insert, update, or delete operation.</summary>
/// <param name="Status">The mutation status.</param>
/// <param name="AffectedRows">The provider-reported affected row count.</param>
/// <param name="Identity">The resulting row identity when available.</param>
/// <param name="SchemaFingerprint">The current schema fingerprint.</param>
/// <param name="Message">An optional safe, user-facing explanation.</param>
public sealed record RowMutationResult(
    RowMutationStatus Status,
    int AffectedRows,
    RowIdentity? Identity,
    string SchemaFingerprint,
    string? Message = null);

/// <summary>Reports the atomic outcome of a bounded row batch mutation.</summary>
/// <param name="Status">The mutation status.</param>
/// <param name="RequestedRows">The number of requested rows.</param>
/// <param name="AffectedRows">The committed affected row count. A rolled-back batch reports zero.</param>
/// <param name="SchemaFingerprint">The current schema fingerprint.</param>
/// <param name="ConflictIndex">The zero-based conflicting row index, when known.</param>
/// <param name="Message">An optional safe, user-facing explanation.</param>
public sealed record BatchRowMutationResult(
    RowMutationStatus Status,
    int RequestedRows,
    int AffectedRows,
    string SchemaFingerprint,
    int? ConflictIndex = null,
    string? Message = null);
