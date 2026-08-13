using MySqlConnector;
using RazorDbManager.Core;
using RazorDbManager.MySql.Infrastructure;

namespace RazorDbManager.MySql.Transfer;

internal static class MySqlExportValueReader
{
    public static DbValue Read(
        MySqlDataReader reader,
        int ordinal,
        DbColumnMetadata column,
        long maximumCellBytes)
    {
        if (maximumCellBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCellBytes));
        if (!reader.IsDBNull(ordinal))
        {
            if (column.Type.Kind is DbDataKind.Binary or DbDataKind.Geometry)
            {
                EnsureWithinLimit(reader.GetBytes(ordinal, 0, null, 0, 0), maximumCellBytes, column.Name);
            }
            else if (reader.GetFieldType(ordinal) == typeof(string))
            {
                var characters = reader.GetChars(ordinal, 0, null, 0, 0);
                var inMemoryBytes = characters > long.MaxValue / sizeof(char)
                    ? long.MaxValue
                    : characters * sizeof(char);
                EnsureWithinLimit(inMemoryBytes, maximumCellBytes, column.Name);
            }
        }

        var value = MySqlValueConverter.Read(reader, ordinal, column, maximumCellBytes, out var truncated);
        if (truncated)
        {
            throw CellLimit(column.Name, maximumCellBytes);
        }

        return value;
    }

    internal static void EnsureWithinLimit(long valueBytes, long maximumCellBytes, string columnName)
    {
        if (valueBytes < 0) throw new ArgumentOutOfRangeException(nameof(valueBytes));
        if (maximumCellBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCellBytes));
        if (valueBytes > maximumCellBytes) throw CellLimit(columnName, maximumCellBytes);
    }

    private static RazorDbException CellLimit(string columnName, long maximumCellBytes) => new(
        RazorDbErrorCode.LimitExceeded,
        $"Export value in column '{columnName}' exceeds the {maximumCellBytes} byte cell limit.");
}
