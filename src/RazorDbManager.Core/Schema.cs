namespace RazorDbManager.Core;

/// <summary>Lists column types accepted by provider-neutral schema operations.</summary>
public enum SchemaDataType
{
    /// <summary>A Boolean-compatible value.</summary>
    Boolean,
    /// <summary>An 8-bit integer.</summary>
    Int8,
    /// <summary>A 16-bit integer.</summary>
    Int16,
    /// <summary>A 32-bit integer.</summary>
    Int32,
    /// <summary>A 64-bit integer.</summary>
    Int64,
    /// <summary>An exact decimal.</summary>
    Decimal,
    /// <summary>A single-precision floating-point number.</summary>
    Float,
    /// <summary>A double-precision floating-point number.</summary>
    Double,
    /// <summary>A fixed-width character string.</summary>
    Char,
    /// <summary>A variable-width character string.</summary>
    VarChar,
    /// <summary>A text large object.</summary>
    Text,
    /// <summary>A fixed-width byte string.</summary>
    Binary,
    /// <summary>A variable-width byte string.</summary>
    VarBinary,
    /// <summary>A binary large object.</summary>
    Blob,
    /// <summary>A date.</summary>
    Date,
    /// <summary>A time.</summary>
    Time,
    /// <summary>A date and time.</summary>
    DateTime,
    /// <summary>A timestamp.</summary>
    Timestamp,
    /// <summary>A JSON document.</summary>
    Json,
    /// <summary>An enum backed by declared string values.</summary>
    Enum,
    /// <summary>A set backed by declared string values.</summary>
    Set,
    /// <summary>A fixed-width bit string.</summary>
    Bit,
    /// <summary>A spatial value.</summary>
    Geometry,
    /// <summary>A globally unique identifier.</summary>
    Guid,
}

/// <summary>Defines a column type using validated, provider-neutral facets.</summary>
public sealed record ColumnTypeDefinition
{
    /// <summary>Gets the type family.</summary>
    public required SchemaDataType Type { get; init; }
    /// <summary>Gets whether an integer or decimal is unsigned.</summary>
    public bool IsUnsigned { get; init; }
    /// <summary>Gets character, binary, or bit length.</summary>
    public long? Length { get; init; }
    /// <summary>Gets decimal precision.</summary>
    public int? Precision { get; init; }
    /// <summary>Gets decimal scale.</summary>
    public int? Scale { get; init; }
    /// <summary>Gets enum or set members.</summary>
    public IReadOnlyList<string> AllowedValues { get; init; } = Array.Empty<string>();

    /// <summary>Validates facets without applying provider-specific limits.</summary>
    /// <returns>This definition.</returns>
    /// <exception cref="InvalidOperationException">The facet combination is invalid.</exception>
    public ColumnTypeDefinition Validate()
    {
        if (Length is <= 0 || Precision is <= 0 || Scale is < 0 || (Precision is not null && Scale > Precision))
        {
            throw new InvalidOperationException("Column type length, precision, or scale is invalid.");
        }

        bool supportsLength = Type is SchemaDataType.Char or SchemaDataType.VarChar
            or SchemaDataType.Binary or SchemaDataType.VarBinary or SchemaDataType.Bit;
        bool supportsPrecision = Type == SchemaDataType.Decimal;
        bool supportsUnsigned = Type is SchemaDataType.Int8 or SchemaDataType.Int16
            or SchemaDataType.Int32 or SchemaDataType.Int64 or SchemaDataType.Decimal;
        bool requiresValues = Type is SchemaDataType.Enum or SchemaDataType.Set;

        if ((!supportsLength && Length is not null)
            || (!supportsPrecision && (Precision is not null || Scale is not null))
            || (!supportsUnsigned && IsUnsigned)
            || (requiresValues && AllowedValues.Count == 0)
            || (!requiresValues && AllowedValues.Count != 0)
            || AllowedValues.Any(value => value is null)
            || AllowedValues.Distinct(StringComparer.Ordinal).Count() != AllowedValues.Count)
        {
            throw new InvalidOperationException("The selected type does not support the supplied facets.");
        }

        return this;
    }
}

/// <summary>Base type for safe, structured column defaults.</summary>
public abstract record ColumnDefault;

/// <summary>Specifies an explicit null default.</summary>
public sealed record NullColumnDefault : ColumnDefault;

/// <summary>Specifies a literal default that providers must parameterize or safely quote.</summary>
/// <param name="Value">The literal value.</param>
public sealed record LiteralColumnDefault(DbValue Value) : ColumnDefault;

/// <summary>Specifies the database current date and time as a default.</summary>
/// <param name="FractionalSecondsPrecision">Optional fractional-second precision.</param>
public sealed record CurrentTimestampColumnDefault(int? FractionalSecondsPrecision = null) : ColumnDefault;

/// <summary>Defines one column for a structured schema operation.</summary>
public sealed record ColumnDefinition
{
    /// <summary>Gets the column name.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the provider-neutral type.</summary>
    public required ColumnTypeDefinition Type { get; init; }
    /// <summary>Gets whether null is allowed.</summary>
    public bool IsNullable { get; init; } = true;
    /// <summary>Gets the structured default, or null when no default clause is emitted.</summary>
    public ColumnDefault? Default { get; init; }
    /// <summary>Gets whether the provider should generate incrementing numeric values.</summary>
    public bool IsAutoIncrement { get; init; }
    /// <summary>Gets an optional comment.</summary>
    public string? Comment { get; init; }
    /// <summary>Gets an optional provider-supported character set selected from provider metadata.</summary>
    public string? CharacterSet { get; init; }
    /// <summary>Gets an optional provider-supported collation selected from provider metadata.</summary>
    public string? Collation { get; init; }

    /// <summary>Validates the provider-neutral column definition.</summary>
    /// <returns>This definition.</returns>
    public ColumnDefinition Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("A column name is required.");
        }

        Type.Validate();
        if (IsAutoIncrement && Type.Type is not (SchemaDataType.Int8 or SchemaDataType.Int16
            or SchemaDataType.Int32 or SchemaDataType.Int64))
        {
            throw new InvalidOperationException("Auto increment requires an integer column.");
        }

        if (Default is LiteralColumnDefault literal && literal.Value.IsNull)
        {
            throw new InvalidOperationException("Use NullColumnDefault for a null default.");
        }

        if (Default is CurrentTimestampColumnDefault current
            && (current.FractionalSecondsPrecision is < 0 or > 9
                || Type.Type is not (SchemaDataType.DateTime or SchemaDataType.Timestamp)))
        {
            throw new InvalidOperationException("Current timestamp requires a temporal column and valid precision.");
        }

        return this;
    }
}

/// <summary>Defines a primary, unique, or ordinary index.</summary>
public sealed record IndexDefinition
{
    /// <summary>Gets the index name. Primary indexes may use an empty name.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Gets ordered index columns.</summary>
    public required IReadOnlyList<DbIndexColumn> Columns { get; init; }
    /// <summary>Gets whether values must be unique.</summary>
    public bool IsUnique { get; init; }
    /// <summary>Gets whether this is the table primary key.</summary>
    public bool IsPrimary { get; init; }

    /// <summary>Validates provider-neutral index invariants.</summary>
    /// <returns>This definition.</returns>
    public IndexDefinition Validate()
    {
        if ((!IsPrimary && string.IsNullOrWhiteSpace(Name)) || Columns.Count == 0
            || Columns.Any(column => string.IsNullOrWhiteSpace(column.Name))
            || Columns.Select(column => column.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Columns.Count
            || Columns.Any(column => column.PrefixLength is <= 0))
        {
            throw new InvalidOperationException("The index definition is invalid.");
        }

        if (IsPrimary && !IsUnique)
        {
            throw new InvalidOperationException("A primary index must be unique.");
        }

        return this;
    }
}

/// <summary>Defines a foreign key.</summary>
public sealed record ForeignKeyDefinition
{
    /// <summary>Gets the constraint name.</summary>
    public required string Name { get; init; }
    /// <summary>Gets ordered local columns.</summary>
    public required IReadOnlyList<string> Columns { get; init; }
    /// <summary>Gets the referenced table.</summary>
    public required DbObjectName ReferencedTable { get; init; }
    /// <summary>Gets ordered referenced columns.</summary>
    public required IReadOnlyList<string> ReferencedColumns { get; init; }
    /// <summary>Gets the action taken after deleting a referenced row.</summary>
    public DbReferentialAction OnDelete { get; init; }
    /// <summary>Gets the action taken after updating a referenced row.</summary>
    public DbReferentialAction OnUpdate { get; init; }

    /// <summary>Validates provider-neutral foreign-key invariants.</summary>
    /// <returns>This definition.</returns>
    public ForeignKeyDefinition Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Columns.Count == 0
            || Columns.Count != ReferencedColumns.Count
            || Columns.Any(string.IsNullOrWhiteSpace)
            || ReferencedColumns.Any(string.IsNullOrWhiteSpace)
            || Columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Columns.Count
            || ReferencedColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ReferencedColumns.Count)
        {
            throw new InvalidOperationException("The foreign-key definition is invalid.");
        }

        return this;
    }
}

/// <summary>Defines a new table.</summary>
public sealed record TableDefinition
{
    /// <summary>Gets the table name.</summary>
    public required DbObjectName Name { get; init; }
    /// <summary>Gets ordered columns.</summary>
    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }
    /// <summary>Gets indexes, including an optional primary index.</summary>
    public IReadOnlyList<IndexDefinition> Indexes { get; init; } = Array.Empty<IndexDefinition>();
    /// <summary>Gets foreign-key constraints.</summary>
    public IReadOnlyList<ForeignKeyDefinition> ForeignKeys { get; init; } = Array.Empty<ForeignKeyDefinition>();
    /// <summary>Gets an optional provider-supported engine selected from provider metadata.</summary>
    public string? Engine { get; init; }
    /// <summary>Gets an optional provider-supported character set selected from provider metadata.</summary>
    public string? CharacterSet { get; init; }
    /// <summary>Gets an optional provider-supported collation selected from provider metadata.</summary>
    public string? Collation { get; init; }
    /// <summary>Gets an optional comment.</summary>
    public string? Comment { get; init; }

    /// <summary>Validates internal references and provider-neutral table invariants.</summary>
    /// <returns>This definition.</returns>
    public TableDefinition Validate()
    {
        if (Columns.Count == 0
            || Columns.Select(column => column.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Columns.Count)
        {
            throw new InvalidOperationException("A table requires uniquely named columns.");
        }

        foreach (ColumnDefinition column in Columns)
        {
            column.Validate();
        }

        HashSet<string> columnNames = new(Columns.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);
        foreach (IndexDefinition index in Indexes)
        {
            index.Validate();
            if (index.Columns.Any(column => !columnNames.Contains(column.Name)))
            {
                throw new InvalidOperationException("An index references an unknown column.");
            }
        }

        if (Indexes.Count(index => index.IsPrimary) > 1
            || Indexes.Where(index => !index.IsPrimary)
                .Select(index => index.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != Indexes.Count(index => !index.IsPrimary))
        {
            throw new InvalidOperationException("Index names and primary-key definitions must be unique.");
        }

        foreach (ForeignKeyDefinition foreignKey in ForeignKeys)
        {
            foreignKey.Validate();
            if (foreignKey.Columns.Any(column => !columnNames.Contains(column)))
            {
                throw new InvalidOperationException("A foreign key references an unknown local column.");
            }
        }

        if (ForeignKeys.Select(key => key.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ForeignKeys.Count)
        {
            throw new InvalidOperationException("Foreign-key names must be unique.");
        }

        return this;
    }
}

/// <summary>Base type for one structured schema change.</summary>
public abstract record SchemaChange
{
    /// <summary>Gets whether this operation can irreversibly remove data.</summary>
    public virtual bool IsDestructive => false;
}

/// <summary>Creates a table.</summary>
/// <param name="Table">The complete table definition.</param>
public sealed record CreateTableChange(TableDefinition Table) : SchemaChange;

/// <summary>Renames a table.</summary>
/// <param name="Table">The current table name.</param>
/// <param name="NewName">The new unqualified name.</param>
public sealed record RenameTableChange(DbObjectName Table, string NewName) : SchemaChange;

/// <summary>Drops a table.</summary>
/// <param name="Table">The table to drop.</param>
public sealed record DropTableChange(DbObjectName Table) : SchemaChange
{
    /// <inheritdoc />
    public override bool IsDestructive => true;
}

/// <summary>Adds a column.</summary>
/// <param name="Table">The target table.</param>
/// <param name="Column">The new column.</param>
/// <param name="AfterColumn">An optional predecessor used for ordering.</param>
public sealed record AddColumnChange(DbObjectName Table, ColumnDefinition Column, string? AfterColumn = null) : SchemaChange;

/// <summary>Changes a column definition and optionally its name.</summary>
/// <param name="Table">The target table.</param>
/// <param name="ExistingColumn">The current column name.</param>
/// <param name="Column">The replacement definition.</param>
public sealed record AlterColumnChange(DbObjectName Table, string ExistingColumn, ColumnDefinition Column) : SchemaChange
{
    /// <inheritdoc />
    public override bool IsDestructive => true;
}

/// <summary>Drops a column and its data.</summary>
/// <param name="Table">The target table.</param>
/// <param name="Column">The column name.</param>
public sealed record DropColumnChange(DbObjectName Table, string Column) : SchemaChange
{
    /// <inheritdoc />
    public override bool IsDestructive => true;
}

/// <summary>Adds an index.</summary>
/// <param name="Table">The target table.</param>
/// <param name="Index">The index definition.</param>
public sealed record AddIndexChange(DbObjectName Table, IndexDefinition Index) : SchemaChange;

/// <summary>Drops an index or primary key.</summary>
/// <param name="Table">The target table.</param>
/// <param name="Index">The index name.</param>
/// <param name="IsPrimary">Whether the primary key is being dropped.</param>
public sealed record DropIndexChange(DbObjectName Table, string Index, bool IsPrimary = false) : SchemaChange
{
    /// <inheritdoc />
    public override bool IsDestructive => true;
}

/// <summary>Adds a foreign key.</summary>
/// <param name="Table">The target table.</param>
/// <param name="ForeignKey">The constraint definition.</param>
public sealed record AddForeignKeyChange(DbObjectName Table, ForeignKeyDefinition ForeignKey) : SchemaChange;

/// <summary>Drops a foreign key.</summary>
/// <param name="Table">The target table.</param>
/// <param name="ForeignKey">The constraint name.</param>
public sealed record DropForeignKeyChange(DbObjectName Table, string ForeignKey) : SchemaChange
{
    /// <inheritdoc />
    public override bool IsDestructive => true;
}

/// <summary>Requests a safe SQL preview for a structured schema change.</summary>
/// <param name="DatabaseId">The registration identifier.</param>
/// <param name="Change">The structured change.</param>
public sealed record SchemaChangeRequest(string DatabaseId, SchemaChange Change);

/// <summary>Contains generated DDL that must be presented before execution.</summary>
/// <param name="Statements">Provider-generated statements in execution order.</param>
/// <param name="IsDestructive">Whether data may be irreversibly removed.</param>
/// <param name="RequiredCapability">Capabilities required to execute the preview.</param>
/// <param name="SchemaFingerprint">The schema fingerprint against which SQL was generated.</param>
/// <param name="SqlHash">A SHA-256 digest binding confirmation to generated SQL.</param>
/// <param name="Warnings">Provider-safe warnings, including implicit-commit behavior.</param>
public sealed record DdlPreview(
    IReadOnlyList<string> Statements,
    bool IsDestructive,
    RazorDbCapability RequiredCapability,
    string SchemaFingerprint,
    string SqlHash,
    IReadOnlyList<string> Warnings);

/// <summary>Requests execution of a previously previewed schema change.</summary>
/// <param name="DatabaseId">The registration identifier.</param>
/// <param name="Change">The structured change.</param>
/// <param name="ExpectedSchemaFingerprint">The previewed schema fingerprint.</param>
/// <param name="ExpectedSqlHash">The previewed SQL hash.</param>
/// <param name="ConfirmationToken">A short-lived, actor-bound, single-use operation token.</param>
public sealed record DdlExecutionRequest(
    string DatabaseId,
    SchemaChange Change,
    string ExpectedSchemaFingerprint,
    string ExpectedSqlHash,
    string ConfirmationToken);

/// <summary>Reports completion of a DDL operation.</summary>
/// <param name="StatementsExecuted">The number of executed statements.</param>
/// <param name="SchemaFingerprint">The post-operation schema fingerprint.</param>
/// <param name="CompletedAt">The completion instant.</param>
public sealed record DdlExecutionResult(
    int StatementsExecuted,
    string SchemaFingerprint,
    DateTimeOffset CompletedAt);
