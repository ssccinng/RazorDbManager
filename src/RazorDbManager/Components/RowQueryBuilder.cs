using RazorDbManager.Core;

namespace RazorDbManager.Components;

internal enum FilterDraftOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Contains,
    StartsWith,
    EndsWith,
    IsNull,
    IsNotNull,
}

internal sealed class FilterDraft
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Column { get; set; } = string.Empty;
    public FilterDraftOperator Operator { get; set; } = FilterDraftOperator.Contains;
    public string Value { get; set; } = string.Empty;
}

internal static class RowQueryBuilder
{
    public static FilterExpression? BuildQuickFilter(
        DbTableMetadata table,
        string? columnName,
        string? value)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (!string.IsNullOrWhiteSpace(columnName))
        {
            DbColumnMetadata column = table.Columns.FirstOrDefault(item =>
                    item.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                ?? throw new RazorDbException(RazorDbErrorCode.Validation, "The quick search references an unknown column.");
            FilterDraftOperator filterOperator = IsTextSearchable(column.Type.Kind)
                ? FilterDraftOperator.Contains
                : FilterDraftOperator.Equal;
            return new ComparisonFilter(
                column.Name,
                Comparison(filterOperator, column.Type.Kind),
                Parse(column.Type.Kind, value));
        }

        ComparisonFilter[] terms = table.Columns
            .Where(column => IsTextSearchable(column.Type.Kind))
            .Take(40)
            .Select(column => new ComparisonFilter(
                column.Name,
                DbComparisonOperator.Contains,
                Parse(column.Type.Kind, value)))
            .ToArray();
        return terms.Length switch
        {
            0 => throw new RazorDbException(RazorDbErrorCode.Unsupported, "This table has no text-searchable columns."),
            1 => terms[0],
            _ => new LogicalFilter(DbLogicalOperator.Or, terms),
        };
    }

    public static FilterExpression? CombineWithAnd(FilterExpression? first, FilterExpression? second) =>
        (first, second) switch
        {
            (null, null) => null,
            (not null, null) => first,
            (null, not null) => second,
            _ => new LogicalFilter(DbLogicalOperator.And, [first!, second!]),
        };

    public static FilterDraftOperator DefaultOperator(DbDataKind kind) =>
        IsTextSearchable(kind) ? FilterDraftOperator.Contains : FilterDraftOperator.Equal;

    public static FilterExpression BuildIdentityFilter(IReadOnlyCollection<DbRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count is < 1 or > RazorDbBatchLimits.MaximumRows)
        {
            throw new RazorDbException(RazorDbErrorCode.LimitExceeded, "The selected row count is outside the batch limit.");
        }

        RowIdentity[] identities = rows.Select(row => row.Identity
            ?? throw new RazorDbException(RazorDbErrorCode.Validation, "A selected row has no safe identity.")).ToArray();
        string[] keyColumns = identities[0].Values.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (identities.Any(identity => !identity.Values.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(keyColumns, StringComparer.OrdinalIgnoreCase)))
        {
            throw new RazorDbException(RazorDbErrorCode.Validation, "Selected row identities do not use the same key columns.");
        }

        if (keyColumns.Length == 1)
        {
            string column = keyColumns[0];
            return new InFilter(column, identities.Select(identity => identity.Values[column]).ToArray());
        }

        int maximumRows = Math.Max(1, 98 / (keyColumns.Length + 1));
        if (identities.Length > maximumRows)
        {
            throw new RazorDbException(RazorDbErrorCode.LimitExceeded, "The composite-key selection is too large to export safely.");
        }
        FilterExpression[] terms = identities.Select(identity => (FilterExpression)new LogicalFilter(
            DbLogicalOperator.And,
            keyColumns.Select(column => (FilterExpression)new ComparisonFilter(
                column,
                DbComparisonOperator.Equal,
                identity.Values[column])).ToArray())).ToArray();
        return terms.Length == 1 ? terms[0] : new LogicalFilter(DbLogicalOperator.Or, terms);
    }

    public static FilterExpression? BuildFilter(
        DbTableMetadata table,
        IReadOnlyCollection<FilterDraft> drafts,
        DbLogicalOperator logicalOperator)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(drafts);

        Dictionary<string, DbColumnMetadata> columns = table.Columns.ToDictionary(
            column => column.Name,
            StringComparer.OrdinalIgnoreCase);
        List<FilterExpression> terms = [];
        foreach (FilterDraft draft in drafts)
        {
            if (string.IsNullOrWhiteSpace(draft.Column)) continue;
            if (!columns.TryGetValue(draft.Column, out DbColumnMetadata? column))
            {
                throw new RazorDbException(RazorDbErrorCode.Validation, "A filter references an unknown column.");
            }

            if (draft.Operator is FilterDraftOperator.IsNull or FilterDraftOperator.IsNotNull)
            {
                terms.Add(new NullFilter(column.Name, draft.Operator == FilterDraftOperator.IsNull));
                continue;
            }

            if (string.IsNullOrWhiteSpace(draft.Value)) continue;
            terms.Add(new ComparisonFilter(
                column.Name,
                Comparison(draft.Operator, column.Type.Kind),
                Parse(column.Type.Kind, draft.Value)));
        }

        return terms.Count switch
        {
            0 => null,
            1 => terms[0],
            _ => new LogicalFilter(logicalOperator, terms),
        };
    }

    public static IReadOnlyList<FilterDraftOperator> Operators(DbDataKind kind)
    {
        FilterDraftOperator[] comparison =
        [
            FilterDraftOperator.Equal,
            FilterDraftOperator.NotEqual,
            FilterDraftOperator.LessThan,
            FilterDraftOperator.LessThanOrEqual,
            FilterDraftOperator.GreaterThan,
            FilterDraftOperator.GreaterThanOrEqual,
            FilterDraftOperator.IsNull,
            FilterDraftOperator.IsNotNull,
        ];
        return kind is DbDataKind.Text or DbDataKind.Json or DbDataKind.Enum or DbDataKind.Set
            ?
            [
                FilterDraftOperator.Contains,
                FilterDraftOperator.Equal,
                FilterDraftOperator.NotEqual,
                FilterDraftOperator.StartsWith,
                FilterDraftOperator.EndsWith,
                FilterDraftOperator.LessThan,
                FilterDraftOperator.LessThanOrEqual,
                FilterDraftOperator.GreaterThan,
                FilterDraftOperator.GreaterThanOrEqual,
                FilterDraftOperator.IsNull,
                FilterDraftOperator.IsNotNull,
            ]
            : comparison;
    }

    public static bool RequiresValue(FilterDraftOperator value) =>
        value is not (FilterDraftOperator.IsNull or FilterDraftOperator.IsNotNull);

    private static bool IsTextSearchable(DbDataKind kind) =>
        kind is DbDataKind.Text or DbDataKind.Json or DbDataKind.Enum or DbDataKind.Set;

    private static DbComparisonOperator Comparison(FilterDraftOperator value, DbDataKind kind)
    {
        DbComparisonOperator result = value switch
        {
            FilterDraftOperator.Equal => DbComparisonOperator.Equal,
            FilterDraftOperator.NotEqual => DbComparisonOperator.NotEqual,
            FilterDraftOperator.LessThan => DbComparisonOperator.LessThan,
            FilterDraftOperator.LessThanOrEqual => DbComparisonOperator.LessThanOrEqual,
            FilterDraftOperator.GreaterThan => DbComparisonOperator.GreaterThan,
            FilterDraftOperator.GreaterThanOrEqual => DbComparisonOperator.GreaterThanOrEqual,
            FilterDraftOperator.Contains => DbComparisonOperator.Contains,
            FilterDraftOperator.StartsWith => DbComparisonOperator.StartsWith,
            FilterDraftOperator.EndsWith => DbComparisonOperator.EndsWith,
            _ => throw new RazorDbException(RazorDbErrorCode.Validation, "The selected filter operator requires no value."),
        };
        if (result is DbComparisonOperator.Contains or DbComparisonOperator.StartsWith or DbComparisonOperator.EndsWith
            && kind is not (DbDataKind.Text or DbDataKind.Json or DbDataKind.Enum or DbDataKind.Set))
        {
            throw new RazorDbException(RazorDbErrorCode.Validation, "Text matching is not available for this column type.");
        }
        return result;
    }

    private static DbValue Parse(DbDataKind kind, string value) => kind switch
    {
        DbDataKind.SignedInteger => DbValue.FromText(DbValueKind.SignedInteger, value),
        DbDataKind.UnsignedInteger => DbValue.FromText(DbValueKind.UnsignedInteger, value),
        DbDataKind.Decimal => DbValue.FromText(DbValueKind.Decimal, value),
        DbDataKind.FloatingPoint => DbValue.FromText(DbValueKind.FloatingPoint, value),
        DbDataKind.Boolean => DbValue.FromText(DbValueKind.Boolean, value),
        DbDataKind.Date => DbValue.FromText(DbValueKind.Date, value),
        DbDataKind.Time => DbValue.FromText(DbValueKind.Time, value),
        DbDataKind.DateTime => DbValue.FromText(DbValueKind.DateTime, value),
        DbDataKind.Timestamp => DbValue.FromText(DbValueKind.Timestamp, value),
        DbDataKind.Json => DbValue.FromText(DbValueKind.Json, value),
        DbDataKind.Enum => DbValue.FromText(DbValueKind.Enum, value),
        DbDataKind.Set => DbValue.FromText(DbValueKind.Set, value),
        DbDataKind.BitString => DbValue.FromText(DbValueKind.BitString, value),
        DbDataKind.Guid => DbValue.FromText(DbValueKind.Guid, value),
        DbDataKind.ProviderSpecific => DbValue.FromText(DbValueKind.ProviderSpecific, value),
        _ => DbValue.FromString(value),
    };
}
