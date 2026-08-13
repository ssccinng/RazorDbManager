using System.Text;
using RazorDbManager.Core;

namespace RazorDbManager.Components;

internal static class SqlResultCsvExporter
{
    private const string NullToken = "\\N";

    internal static string Build(SqlStatementResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Kind != SqlStatementResultKind.ResultSet)
            throw new ArgumentException("Only SQL result sets can be exported.", nameof(result));

        StringBuilder csv = new();
        WriteRecord(csv, result.Columns.Select(column => EncodeText(column.Name)));
        foreach (IReadOnlyList<DbValue> row in result.Rows)
        {
            if (row.Count != result.Columns.Count)
            {
                throw new ArgumentException(
                    "Every SQL result row must contain exactly one value for each column.",
                    nameof(result));
            }

            WriteRecord(csv, row.Select(FormatValue));
        }

        return csv.ToString();
    }

    private static string FormatValue(DbValue value)
    {
        if (value.IsNull) return NullToken;
        if (value.Kind is DbValueKind.Binary or DbValueKind.Geometry)
            return EncodeText(Convert.ToBase64String(value.Binary.Span));

        string text = value.Text ?? string.Empty;
        return value.Kind is DbValueKind.String
            or DbValueKind.Json
            or DbValueKind.Enum
            or DbValueKind.Set
            or DbValueKind.ProviderSpecific
            ? EncodeText(text)
            : text;
    }

    private static string EncodeText(string text)
    {
        if (text.StartsWith('\\')) text = $"\\{text}";
        if (text.StartsWith('\'')) return $"'{text}";
        return StartsSpreadsheetFormula(text) ? $"'{text}" : text;
    }

    private static bool StartsSpreadsheetFormula(string text)
    {
        int index = 0;
        while (index < text.Length && text[index] == ' ') index++;
        return index < text.Length && text[index] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n';
    }

    private static void WriteRecord(StringBuilder csv, IEnumerable<string> fields)
    {
        bool first = true;
        foreach (string field in fields)
        {
            if (!first) csv.Append(',');
            first = false;
            WriteField(csv, field);
        }

        csv.Append("\r\n");
    }

    private static void WriteField(StringBuilder csv, string field)
    {
        bool quote = field.IndexOfAny([',', '"', '\r', '\n']) >= 0
            || field.StartsWith(' ')
            || field.EndsWith(' ');
        if (!quote)
        {
            csv.Append(field);
            return;
        }

        csv.Append('"');
        csv.Append(field.Replace("\"", "\"\"", StringComparison.Ordinal));
        csv.Append('"');
    }
}
