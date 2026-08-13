namespace RazorDbManager.Core;

/// <summary>Validates provider-neutral request trees before a provider translates them to SQL.</summary>
public static class RazorDbValidationExtensions
{
    /// <summary>Validates a filter against current table metadata and complexity limits.</summary>
    /// <param name="filter">The structured filter.</param>
    /// <param name="table">Current table metadata.</param>
    /// <param name="maximumDepth">The maximum nested logical depth.</param>
    /// <param name="maximumTerms">The maximum total filter-node count.</param>
    /// <returns>The validated filter.</returns>
    /// <exception cref="InvalidOperationException">The tree is invalid or references an unknown column.</exception>
    public static FilterExpression ValidateAgainst(
        this FilterExpression filter,
        DbTableMetadata table,
        int maximumDepth = 16,
        int maximumTerms = 100)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(table);
        if (maximumDepth <= 0 || maximumTerms <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDepth), "Filter limits must be positive.");
        }

        HashSet<string> columns = new(table.Columns.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);
        int nodeCount = 0;
        ValidateFilter(filter, columns, depth: 1, maximumDepth, maximumTerms, ref nodeCount);
        return filter;
    }

    /// <summary>Validates sort columns against current table metadata.</summary>
    /// <param name="sorts">The requested sorts.</param>
    /// <param name="table">Current table metadata.</param>
    /// <returns>The validated sort list.</returns>
    /// <exception cref="InvalidOperationException">A sort references an unknown or duplicate column.</exception>
    public static IReadOnlyList<DbSort> ValidateAgainst(
        this IReadOnlyList<DbSort> sorts,
        DbTableMetadata table)
    {
        ArgumentNullException.ThrowIfNull(sorts);
        ArgumentNullException.ThrowIfNull(table);
        HashSet<string> columns = new(table.Columns.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);
        if (sorts.Any(sort => string.IsNullOrWhiteSpace(sort.Column) || !columns.Contains(sort.Column))
            || sorts.Select(sort => sort.Column).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sorts.Count)
        {
            throw new InvalidOperationException("A sort references an unknown or duplicate column.");
        }

        return sorts;
    }

    private static void ValidateFilter(
        FilterExpression filter,
        HashSet<string> columns,
        int depth,
        int maximumDepth,
        int maximumTerms,
        ref int nodeCount)
    {
        nodeCount++;
        if (depth > maximumDepth || nodeCount > maximumTerms)
        {
            throw new InvalidOperationException("The filter exceeds configured complexity limits.");
        }

        switch (filter)
        {
            case ComparisonFilter comparison:
                ValidateColumn(comparison.Column, columns);
                if (comparison.Value is null || comparison.Value.IsNull)
                {
                    throw new InvalidOperationException("Use NullFilter for null comparisons.");
                }

                break;
            case NullFilter nullFilter:
                ValidateColumn(nullFilter.Column, columns);
                break;
            case InFilter inFilter:
                ValidateColumn(inFilter.Column, columns);
                if (inFilter.Values.Count == 0 || inFilter.Values.Any(value => value is null || value.IsNull))
                {
                    throw new InvalidOperationException("An IN filter requires non-null values.");
                }

                break;
            case LogicalFilter logical:
                if (logical.Terms.Count < 2 || logical.Terms.Any(term => term is null))
                {
                    throw new InvalidOperationException("A logical filter requires at least two terms.");
                }

                foreach (FilterExpression term in logical.Terms)
                {
                    ValidateFilter(term, columns, depth + 1, maximumDepth, maximumTerms, ref nodeCount);
                }

                break;
            default:
                throw new InvalidOperationException("The filter type is not supported.");
        }
    }

    private static void ValidateColumn(string column, HashSet<string> columns)
    {
        if (string.IsNullOrWhiteSpace(column) || !columns.Contains(column))
        {
            throw new InvalidOperationException("A filter references an unknown column.");
        }
    }
}
