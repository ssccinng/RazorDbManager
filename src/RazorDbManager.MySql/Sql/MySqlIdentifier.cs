namespace RazorDbManager.MySql.Sql;

internal static class MySqlIdentifier
{
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        if (identifier.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("A MySQL identifier cannot contain a NUL character.", nameof(identifier));
        }

        return $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
    }

    public static string Qualify(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";
}
