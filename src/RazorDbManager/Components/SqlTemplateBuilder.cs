using System.Text;
using RazorDbManager.Core;

namespace RazorDbManager.Components;

internal enum SqlTemplateKind
{
    Select,
    Where,
    Insert,
    Update,
    Delete,
}

internal static class SqlTemplateBuilder
{
    public static string Build(DbTableMetadata table, SqlTemplateKind kind)
    {
        ArgumentNullException.ThrowIfNull(table);
        string target = $"{Quote(table.Name.Schema)}.{Quote(table.Name.Name)}";
        DbColumnMetadata? editable = table.Columns.FirstOrDefault(column =>
            !column.IsGenerated && !column.IsAutoIncrement && column.Type.Kind is not (DbDataKind.Binary or DbDataKind.Geometry));
        return kind switch
        {
            SqlTemplateKind.Select => $"SELECT *\nFROM {target}\nLIMIT 100;",
            SqlTemplateKind.Where => $"SELECT *\nFROM {target}\nWHERE 1 = 1\nLIMIT 100;",
            SqlTemplateKind.Insert when editable is not null =>
                $"INSERT INTO {target} ({Quote(editable.Name)})\nVALUES (NULL);",
            SqlTemplateKind.Update when editable is not null =>
                $"UPDATE {target}\nSET {Quote(editable.Name)} = {Quote(editable.Name)}\nWHERE 1 = 0;",
            SqlTemplateKind.Delete => $"DELETE FROM {target}\nWHERE 1 = 0;",
            _ => throw new RazorDbException(RazorDbErrorCode.Unsupported, "The table has no column suitable for this SQL template."),
        };
    }

    private static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
    }
}
