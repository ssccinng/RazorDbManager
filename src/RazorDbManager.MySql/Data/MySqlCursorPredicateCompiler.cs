using RazorDbManager.Core;
using RazorDbManager.MySql.Infrastructure;
using RazorDbManager.MySql.Sql;

namespace RazorDbManager.MySql.Data;

internal static class MySqlCursorPredicateCompiler
{
    public static CompiledSql Compile(
        RowCursor cursor,
        IReadOnlyList<DbSort> sorts,
        IReadOnlyCollection<string> allowedColumns)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(sorts);
        ArgumentNullException.ThrowIfNull(allowedColumns);

        if (sorts.Count == 0 || cursor.Values.Count != sorts.Count)
        {
            throw new ArgumentException("Cursor value count does not match the effective sort.", nameof(cursor));
        }

        var allowed = new HashSet<string>(allowedColumns, StringComparer.OrdinalIgnoreCase);
        foreach (DbSort sort in sorts)
        {
            if (!allowed.Contains(sort.Column))
            {
                throw new ArgumentException("Cursor references an unknown sort column.", nameof(cursor));
            }
        }

        var parameters = new List<(string Name, object Value)>();
        var parameterNames = new string?[cursor.Values.Count];
        for (var index = 0; index < cursor.Values.Count; index++)
        {
            DbValue value = cursor.Values[index];
            if (value.IsNull) continue;

            string name = $"@cursor{parameters.Count}";
            parameterNames[index] = name;
            parameters.Add((name, MySqlValueConverter.ToParameter(value)));
        }

        var terms = new List<string>(sorts.Count);
        for (var index = 0; index < sorts.Count; index++)
        {
            var parts = new List<string>(index + 1);
            for (var prefix = 0; prefix < index; prefix++)
            {
                parts.Add(Equality(sorts[prefix], cursor.Values[prefix], parameterNames[prefix]));
            }

            parts.Add(StrictlyAfter(sorts[index], cursor.Values[index], parameterNames[index]));
            terms.Add($"({string.Join(" AND ", parts)})");
        }

        return new CompiledSql($"({string.Join(" OR ", terms)})", parameters);
    }

    private static string Equality(DbSort sort, DbValue value, string? parameterName)
    {
        string column = MySqlIdentifier.Quote(sort.Column);
        return value.IsNull ? $"{column} IS NULL" : $"{column} = {parameterName}";
    }

    private static string StrictlyAfter(DbSort sort, DbValue value, string? parameterName)
    {
        string column = MySqlIdentifier.Quote(sort.Column);
        if (sort.Direction == DbSortDirection.Ascending)
        {
            // MySQL sorts NULL before non-NULL values in ascending order.
            return value.IsNull ? $"{column} IS NOT NULL" : $"{column} > {parameterName}";
        }

        // MySQL sorts NULL after non-NULL values in descending order. Once the
        // cursor itself is NULL, only later tie-break columns can advance it.
        return value.IsNull
            ? "1 = 0"
            : $"({column} < {parameterName} OR {column} IS NULL)";
    }
}
