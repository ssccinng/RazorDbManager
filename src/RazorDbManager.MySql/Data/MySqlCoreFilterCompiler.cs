using RazorDbManager.Core;
using RazorDbManager.MySql.Infrastructure;
using RazorDbManager.MySql.Sql;

namespace RazorDbManager.MySql.Data;

internal sealed record CompiledSql(string Text, IReadOnlyList<(string Name, object Value)> Parameters);

internal static class MySqlCoreFilterCompiler
{
    public static CompiledSql Compile(FilterExpression? filter, IReadOnlyCollection<string> allowedColumns)
    {
        if (filter is null) return new CompiledSql(string.Empty, []);
        var context = new Context(allowedColumns);
        return new CompiledSql(CompileCore(filter, context), context.Parameters);
    }

    private static string CompileCore(FilterExpression filter, Context context) => filter switch
    {
        ComparisonFilter comparison => Comparison(comparison, context),
        NullFilter nullFilter => $"{context.Column(nullFilter.Column)} IS {(nullFilter.IsNull ? string.Empty : "NOT ")}NULL",
        InFilter inFilter => In(inFilter, context),
        LogicalFilter logical => Logical(logical, context),
        _ => throw new NotSupportedException($"Filter '{filter.GetType().Name}' is not supported by MySQL."),
    };

    private static string Comparison(ComparisonFilter filter, Context context)
    {
        var column = context.Column(filter.Column);
        if (filter.Value.IsNull)
        {
            return filter.Operator switch
            {
                DbComparisonOperator.Equal => $"{column} IS NULL",
                DbComparisonOperator.NotEqual => $"{column} IS NOT NULL",
                _ => throw new ArgumentException("NULL can only be used with equality comparisons.", nameof(filter)),
            };
        }

        var op = filter.Operator switch
        {
            DbComparisonOperator.Equal => "=",
            DbComparisonOperator.NotEqual => "<>",
            DbComparisonOperator.LessThan => "<",
            DbComparisonOperator.LessThanOrEqual => "<=",
            DbComparisonOperator.GreaterThan => ">",
            DbComparisonOperator.GreaterThanOrEqual => ">=",
            DbComparisonOperator.Contains or DbComparisonOperator.StartsWith or DbComparisonOperator.EndsWith => "LIKE",
            _ => throw new ArgumentOutOfRangeException(nameof(filter)),
        };
        var value = filter.Operator switch
        {
            DbComparisonOperator.Contains => DbValue.FromString($"%{EscapeLike(filter.Value.Text!)}%"),
            DbComparisonOperator.StartsWith => DbValue.FromString($"{EscapeLike(filter.Value.Text!)}%"),
            DbComparisonOperator.EndsWith => DbValue.FromString($"%{EscapeLike(filter.Value.Text!)}"),
            _ => filter.Value,
        };
        return $"{column} {op} {context.Parameter(value)}{(op == "LIKE" ? " ESCAPE '\\\\'" : string.Empty)}";
    }

    private static string In(InFilter filter, Context context)
    {
        if (filter.Values.Count == 0) throw new ArgumentException("IN filters must contain values.", nameof(filter));
        var column = context.Column(filter.Column);
        var nonNull = filter.Values.Where(value => !value.IsNull).ToArray();
        var containsNull = nonNull.Length != filter.Values.Count;
        var parts = new List<string>();
        if (nonNull.Length > 0)
        {
            parts.Add($"{column} {(filter.Negated ? "NOT " : string.Empty)}IN ({string.Join(", ", nonNull.Select(context.Parameter))})");
        }

        if (containsNull) parts.Add($"{column} IS {(filter.Negated ? "NOT " : string.Empty)}NULL");
        return parts.Count == 1 ? parts[0] : $"({string.Join(filter.Negated ? " AND " : " OR ", parts)})";
    }

    private static string Logical(LogicalFilter filter, Context context)
    {
        if (filter.Terms.Count < 2) throw new ArgumentException("Logical filters require at least two terms.", nameof(filter));
        var join = filter.Operator == DbLogicalOperator.And ? " AND " : " OR ";
        return $"({string.Join(join, filter.Terms.Select(term => CompileCore(term, context)))})";
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private sealed class Context(IReadOnlyCollection<string> allowedColumns)
    {
        private readonly HashSet<string> _allowed = new(allowedColumns, StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Name, object Value)> _parameters = [];
        public IReadOnlyList<(string Name, object Value)> Parameters => _parameters;

        public string Column(string name) => _allowed.Contains(name)
            ? MySqlIdentifier.Quote(name)
            : throw new ArgumentException($"Unknown column '{name}'.", nameof(name));

        public string Parameter(DbValue value)
        {
            var name = $"@p{_parameters.Count}";
            _parameters.Add((name, MySqlValueConverter.ToParameter(value)));
            return name;
        }
    }
}
