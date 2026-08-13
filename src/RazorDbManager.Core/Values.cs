using System.Globalization;
using System.Security.Cryptography;

namespace RazorDbManager.Core;

/// <summary>Classifies the canonical representation stored by <see cref="DbValue"/>.</summary>
public enum DbValueKind
{
    /// <summary>A database null.</summary>
    Null,
    /// <summary>Text.</summary>
    String,
    /// <summary>A signed integer encoded as invariant text.</summary>
    SignedInteger,
    /// <summary>An unsigned integer encoded as invariant text.</summary>
    UnsignedInteger,
    /// <summary>An exact decimal encoded as invariant text without precision loss.</summary>
    Decimal,
    /// <summary>A floating-point number encoded as invariant round-trip text.</summary>
    FloatingPoint,
    /// <summary>A Boolean encoded as true or false.</summary>
    Boolean,
    /// <summary>A date encoded as provider-preserving text, including non-standard dates.</summary>
    Date,
    /// <summary>A time encoded as provider-preserving text.</summary>
    Time,
    /// <summary>A date-time encoded as provider-preserving text, including zero dates.</summary>
    DateTime,
    /// <summary>A timestamp encoded as provider-preserving text.</summary>
    Timestamp,
    /// <summary>A GUID encoded as text.</summary>
    Guid,
    /// <summary>A JSON document encoded as text.</summary>
    Json,
    /// <summary>An enum member encoded as text.</summary>
    Enum,
    /// <summary>A set encoded as provider-preserving text.</summary>
    Set,
    /// <summary>A bit string encoded as text.</summary>
    BitString,
    /// <summary>A binary value.</summary>
    Binary,
    /// <summary>A geometry value in provider-defined binary form.</summary>
    Geometry,
    /// <summary>A provider-specific value encoded as text.</summary>
    ProviderSpecific,
}

/// <summary>Stores one immutable database value without coercing precision-sensitive data.</summary>
public sealed class DbValue : IEquatable<DbValue>
{
    private readonly byte[]? _binary;

    private DbValue(DbValueKind kind, string? text, byte[]? binary)
    {
        Kind = kind;
        Text = text;
        _binary = binary;
    }

    /// <summary>Gets the singleton database-null value.</summary>
    public static DbValue Null { get; } = new(DbValueKind.Null, null, null);

    /// <summary>Gets the representation kind.</summary>
    public DbValueKind Kind { get; }

    /// <summary>Gets canonical text for non-binary, non-null values.</summary>
    public string? Text { get; }

    /// <summary>Gets a read-only view over binary content, or an empty value for non-binary kinds.</summary>
    public ReadOnlyMemory<byte> Binary => _binary ?? ReadOnlyMemory<byte>.Empty;

    /// <summary>Gets whether this value represents database null.</summary>
    public bool IsNull => Kind == DbValueKind.Null;

    /// <summary>Creates a text-backed value of the specified kind.</summary>
    /// <param name="kind">A non-null, non-binary representation kind.</param>
    /// <param name="text">Canonical, provider-preserving text.</param>
    /// <returns>An immutable value.</returns>
    public static DbValue FromText(DbValueKind kind, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (kind is DbValueKind.Null or DbValueKind.Binary or DbValueKind.Geometry)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "The selected kind is not text-backed.");
        }

        return new DbValue(kind, text, null);
    }

    /// <summary>Creates a string value.</summary>
    /// <param name="value">The string.</param>
    /// <returns>An immutable value.</returns>
    public static DbValue FromString(string value) => FromText(DbValueKind.String, value);

    /// <summary>Creates a signed integer value.</summary>
    /// <param name="value">The integer.</param>
    /// <returns>An immutable value.</returns>
    public static DbValue FromSignedInteger(long value) =>
        FromText(DbValueKind.SignedInteger, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Creates an unsigned integer value.</summary>
    /// <param name="value">The integer.</param>
    /// <returns>An immutable value.</returns>
    public static DbValue FromUnsignedInteger(ulong value) =>
        FromText(DbValueKind.UnsignedInteger, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Creates an exact decimal from invariant text, preserving provider precision.</summary>
    /// <param name="value">An invariant decimal representation.</param>
    /// <returns>An immutable value.</returns>
    public static DbValue FromDecimal(string value) => FromText(DbValueKind.Decimal, value);

    /// <summary>Creates a floating-point value.</summary>
    /// <param name="value">The floating-point number.</param>
    /// <returns>An immutable value.</returns>
    public static DbValue FromFloatingPoint(double value) =>
        FromText(DbValueKind.FloatingPoint, value.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>Creates a Boolean value.</summary>
    /// <param name="value">The Boolean.</param>
    /// <returns>An immutable value.</returns>
    public static DbValue FromBoolean(bool value) =>
        FromText(DbValueKind.Boolean, value ? "true" : "false");

    /// <summary>Creates a binary or geometry value by defensively copying its bytes.</summary>
    /// <param name="value">The binary bytes.</param>
    /// <param name="kind">Either <see cref="DbValueKind.Binary"/> or <see cref="DbValueKind.Geometry"/>.</param>
    /// <returns>An immutable value.</returns>
    public static DbValue FromBinary(ReadOnlySpan<byte> value, DbValueKind kind = DbValueKind.Binary)
    {
        if (kind is not (DbValueKind.Binary or DbValueKind.Geometry))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "The selected kind is not binary-backed.");
        }

        return new DbValue(kind, null, value.ToArray());
    }

    /// <summary>Computes a stable SHA-256 hexadecimal digest of the canonical representation.</summary>
    /// <returns>A lowercase hexadecimal digest suitable for concurrency and audit metadata.</returns>
    public string ComputeHash()
    {
        byte[] bytes;
        if (_binary is null)
        {
            bytes = System.Text.Encoding.UTF8.GetBytes($"{(int)Kind}:{Text}");
        }
        else
        {
            bytes = new byte[_binary.Length + 1];
            bytes[0] = checked((byte)Kind);
            _binary.CopyTo(bytes, 1);
        }

        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <inheritdoc />
    public bool Equals(DbValue? other)
    {
        if (other is null || Kind != other.Kind || !string.Equals(Text, other.Text, StringComparison.Ordinal))
        {
            return false;
        }

        return _binary is null
            ? other._binary is null
            : other._binary is not null && _binary.AsSpan().SequenceEqual(other._binary);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DbValue);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Kind);
        hash.Add(Text, StringComparer.Ordinal);
        if (_binary is not null)
        {
            foreach (byte item in _binary)
            {
                hash.Add(item);
            }
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        DbValueKind.Null => "NULL",
        DbValueKind.Binary or DbValueKind.Geometry => $"<{Kind}: {_binary!.Length} bytes>",
        _ => Text!,
    };
}

/// <summary>Classifies how an edited field participates in an insert or update.</summary>
public enum EditValueKind
{
    /// <summary>Do not include the column in generated SQL.</summary>
    Omitted,
    /// <summary>Write database null.</summary>
    Null,
    /// <summary>Use the database default keyword.</summary>
    Default,
    /// <summary>Write an explicitly supplied value.</summary>
    Value,
}

/// <summary>Represents the four distinct states of an edited database field.</summary>
public readonly record struct EditValue
{
    private EditValue(EditValueKind kind, DbValue? value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>Gets a field that is absent from generated SQL.</summary>
    public static EditValue Omitted { get; } = new(EditValueKind.Omitted, null);
    /// <summary>Gets an explicit database-null field.</summary>
    public static EditValue Null { get; } = new(EditValueKind.Null, null);
    /// <summary>Gets a field that uses the database default keyword.</summary>
    public static EditValue Default { get; } = new(EditValueKind.Default, null);
    /// <summary>Gets the edit state.</summary>
    public EditValueKind Kind { get; }
    /// <summary>Gets the explicit value when <see cref="Kind"/> is <see cref="EditValueKind.Value"/>.</summary>
    public DbValue? Value { get; }

    /// <summary>Creates an explicit, non-null edited value.</summary>
    /// <param name="value">The value. Use <see cref="Null"/> for database null.</param>
    /// <returns>An explicit edit value.</returns>
    public static EditValue FromValue(DbValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IsNull)
        {
            throw new ArgumentException("Use EditValue.Null for database null.", nameof(value));
        }

        return new EditValue(EditValueKind.Value, value);
    }
}
