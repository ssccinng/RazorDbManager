namespace RazorDbManager.MySql.Filters;

internal abstract record MySqlFilter;

internal enum MySqlComparisonOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Like,
    NotLike,
}

internal sealed record MySqlComparisonFilter(
    string Column,
    MySqlComparisonOperator Operator,
    object? Value) : MySqlFilter;

internal sealed record MySqlNullFilter(string Column, bool IsNull = true) : MySqlFilter;

internal sealed record MySqlInFilter(string Column, IReadOnlyList<object?> Values, bool Negated = false) : MySqlFilter;

internal sealed record MySqlAndFilter(IReadOnlyList<MySqlFilter> Filters) : MySqlFilter;

internal sealed record MySqlOrFilter(IReadOnlyList<MySqlFilter> Filters) : MySqlFilter;

internal sealed record MySqlNotFilter(MySqlFilter Filter) : MySqlFilter;

internal sealed record MySqlCompiledFilter(string Sql, IReadOnlyList<MySqlParameterValue> Parameters);

internal sealed record MySqlParameterValue(string Name, object? Value);
