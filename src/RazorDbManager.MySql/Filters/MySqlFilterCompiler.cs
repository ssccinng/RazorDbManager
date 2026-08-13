using RazorDbManager.MySql.Sql;

namespace RazorDbManager.MySql.Filters;

internal sealed class MySqlFilterCompiler
{
    public MySqlCompiledFilter Compile(MySqlFilter? filter, IEnumerable<string> allowedColumns)
    {
        ArgumentNullException.ThrowIfNull(allowedColumns);

        if (filter is null)
        {
            return new MySqlCompiledFilter(string.Empty, []);
        }

        var context = new CompileContext(allowedColumns);
        var sql = CompileCore(filter, context);
        return new MySqlCompiledFilter(sql, context.Parameters);
    }

    private static string CompileCore(MySqlFilter filter, CompileContext context) => filter switch
    {
        MySqlComparisonFilter comparison => CompileComparison(comparison, context),
        MySqlNullFilter nullFilter => $"{context.Column(nullFilter.Column)} IS {(nullFilter.IsNull ? string.Empty : "NOT ")}NULL",
        MySqlInFilter inFilter => CompileIn(inFilter, context),
        MySqlAndFilter andFilter => CompileGroup(andFilter.Filters, "AND", context),
        MySqlOrFilter orFilter => CompileGroup(orFilter.Filters, "OR", context),
        MySqlNotFilter notFilter => $"NOT ({CompileCore(notFilter.Filter, context)})",
        _ => throw new NotSupportedException($"Unsupported filter type '{filter.GetType().Name}'."),
    };

    private static string CompileComparison(MySqlComparisonFilter filter, CompileContext context)
    {
        var column = context.Column(filter.Column);
        if (filter.Value is null)
        {
            return filter.Operator switch
            {
                MySqlComparisonOperator.Equal => $"{column} IS NULL",
                MySqlComparisonOperator.NotEqual => $"{column} IS NOT NULL",
                _ => throw new ArgumentException("Only equality operators can compare with NULL.", nameof(filter)),
            };
        }

        var op = filter.Operator switch
        {
            MySqlComparisonOperator.Equal => "=",
            MySqlComparisonOperator.NotEqual => "<>",
            MySqlComparisonOperator.LessThan => "<",
            MySqlComparisonOperator.LessThanOrEqual => "<=",
            MySqlComparisonOperator.GreaterThan => ">",
            MySqlComparisonOperator.GreaterThanOrEqual => ">=",
            MySqlComparisonOperator.Like => "LIKE",
            MySqlComparisonOperator.NotLike => "NOT LIKE",
            _ => throw new ArgumentOutOfRangeException(nameof(filter)),
        };

        return $"{column} {op} {context.Parameter(filter.Value)}";
    }

    private static string CompileIn(MySqlInFilter filter, CompileContext context)
    {
        var column = context.Column(filter.Column);
        if (filter.Values.Count == 0)
        {
            return filter.Negated ? "1 = 1" : "0 = 1";
        }

        var nonNullValues = filter.Values.Where(value => value is not null).ToArray();
        var containsNull = nonNullValues.Length != filter.Values.Count;
        var parts = new List<string>(2);

        if (nonNullValues.Length > 0)
        {
            var parameters = string.Join(", ", nonNullValues.Select(context.Parameter));
            parts.Add($"{column} {(filter.Negated ? "NOT " : string.Empty)}IN ({parameters})");
        }

        if (containsNull)
        {
            parts.Add($"{column} IS {(filter.Negated ? "NOT " : string.Empty)}NULL");
        }

        var joiner = filter.Negated ? " AND " : " OR ";
        return parts.Count == 1 ? parts[0] : $"({string.Join(joiner, parts)})";
    }

    private static string CompileGroup(IReadOnlyList<MySqlFilter> filters, string joiner, CompileContext context)
    {
        if (filters.Count == 0)
        {
            return joiner == "AND" ? "1 = 1" : "0 = 1";
        }

        return $"({string.Join($" {joiner} ", filters.Select(filter => CompileCore(filter, context)))})";
    }

    private sealed class CompileContext(IEnumerable<string> allowedColumns)
    {
        private readonly HashSet<string> _allowedColumns = new(allowedColumns, StringComparer.OrdinalIgnoreCase);
        private readonly List<MySqlParameterValue> _parameters = [];

        public IReadOnlyList<MySqlParameterValue> Parameters => _parameters;

        public string Column(string name)
        {
            if (!_allowedColumns.Contains(name))
            {
                throw new ArgumentException($"Column '{name}' is not present in the current table metadata.", nameof(name));
            }

            return MySqlIdentifier.Quote(name);
        }

        public string Parameter(object? value)
        {
            var name = $"@p{_parameters.Count}";
            _parameters.Add(new MySqlParameterValue(name, value));
            return name;
        }
    }
}
