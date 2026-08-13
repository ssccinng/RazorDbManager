using RazorDbManager.Core;

namespace RazorDbManager.MySql.Data;

internal static class MySqlKeysetPagination
{
    public static RowCursor? CreateNextCursor(
        DbTableMetadata table,
        IReadOnlyList<DbSort> sorts,
        IReadOnlyList<DbRow> rows,
        bool lastRowSortValuesComplete)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(sorts);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0
            || table.RowIdentityKey is null
            || sorts.Count == 0
            || !lastRowSortValuesComplete)
        {
            return null;
        }

        var ordinals = table.Columns.Select((column, index) => (column.Name, index))
            .ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        return new RowCursor(sorts.Select(sort => rows[^1].Values[ordinals[sort.Column]]).ToArray());
    }
}
