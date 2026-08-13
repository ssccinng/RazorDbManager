using System.Text;

namespace RazorDbManager.MySql.Transfer;

internal static class MySqlDumpStatementGuard
{
    public static void EnsureAllowed(string statement, IReadOnlyCollection<string> allowedSchemas)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (allowedSchemas.Count == 0)
        {
            throw new InvalidOperationException("SQL restore has no allowed schema.");
        }

        var tokens = Tokenize(statement);
        RejectDatabaseScope(tokens);

        var allowed = new HashSet<string>(allowedSchemas, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index + 2 < tokens.Count; index++)
        {
            if (tokens[index].Kind != TokenKind.Identifier
                || tokens[index + 1].Kind != TokenKind.Dot
                || tokens[index + 2].Kind != TokenKind.Identifier)
            {
                continue;
            }

            var schema = tokens[index].Text;
            if (!allowed.Contains(schema))
            {
                throw new UnauthorizedAccessException(
                    $"SQL restore references schema '{schema}' outside the allowlist.");
            }

            // A third component is a column in MySQL's schema.table.column form,
            // not another schema qualifier.
            index += 2;
        }
    }

    private static void RejectDatabaseScope(IReadOnlyList<Token> tokens)
    {
        var identifiers = tokens.Where(token => token.Kind == TokenKind.Identifier).ToArray();
        if (identifiers.Length == 0) return;

        var verb = identifiers[0].Text;
        if (verb.Equals("USE", StringComparison.OrdinalIgnoreCase))
        {
            throw DatabaseScopeError();
        }

        if (!verb.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
            && !verb.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
            && !verb.Equals("DROP", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        for (var index = 1; index < identifiers.Length; index++)
        {
            var value = identifiers[index].Text;
            if (value.Equals("DATABASE", StringComparison.OrdinalIgnoreCase)
                || value.Equals("SCHEMA", StringComparison.OrdinalIgnoreCase))
            {
                throw DatabaseScopeError();
            }

            if (!IsDdlModifier(value)) break;
        }
    }

    private static bool IsDdlModifier(string value) =>
        value.Equals("OR", StringComparison.OrdinalIgnoreCase)
        || value.Equals("REPLACE", StringComparison.OrdinalIgnoreCase)
        || value.Equals("TEMPORARY", StringComparison.OrdinalIgnoreCase)
        || value.Equals("IF", StringComparison.OrdinalIgnoreCase)
        || value.Equals("NOT", StringComparison.OrdinalIgnoreCase)
        || value.Equals("EXISTS", StringComparison.OrdinalIgnoreCase);

    private static UnauthorizedAccessException DatabaseScopeError() =>
        new("SQL restore cannot select, create, alter, or drop databases.");

    private static IReadOnlyList<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        Scan(sql, 0, sql.Length, tokens);
        return tokens;
    }

    private static void Scan(string sql, int start, int end, ICollection<Token> tokens)
    {
        for (var index = start; index < end;)
        {
            var character = sql[index];
            var next = index + 1 < end ? sql[index + 1] : '\0';
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            if (character == '#'
                || character == '-' && next == '-' && IsDashComment(sql, index, end))
            {
                index = SkipLineComment(sql, index, end);
                continue;
            }

            if (character == '/' && next == '*')
            {
                var close = sql.IndexOf("*/", index + 2, end - index - 2, StringComparison.Ordinal);
                if (close < 0) throw new FormatException("The SQL statement ends inside a block comment.");
                var contentStart = ExecutableCommentContentStart(sql, index, close);
                if (contentStart >= 0)
                {
                    while (contentStart < close && char.IsDigit(sql[contentStart])) contentStart++;
                    Scan(sql, contentStart, close, tokens);
                }

                index = close + 2;
                continue;
            }

            if (character == '\'')
            {
                index = SkipQuoted(sql, index, end, character);
                tokens.Add(new Token(TokenKind.Other, string.Empty));
                continue;
            }

            if (character is '`' or '"')
            {
                var (value, nextIndex) = ReadQuotedIdentifier(sql, index, end, character);
                tokens.Add(new Token(TokenKind.Identifier, value));
                index = nextIndex;
                continue;
            }

            if (character == '.')
            {
                tokens.Add(new Token(TokenKind.Dot, "."));
                index++;
                continue;
            }

            if (IsIdentifierCharacter(character))
            {
                var identifierStart = index++;
                while (index < end && IsIdentifierCharacter(sql[index])) index++;
                var value = sql[identifierStart..index];
                tokens.Add(new Token(value.All(char.IsDigit) ? TokenKind.Other : TokenKind.Identifier, value));
                continue;
            }

            tokens.Add(new Token(TokenKind.Other, character.ToString()));
            index++;
        }
    }

    private static int ExecutableCommentContentStart(string sql, int start, int close)
    {
        if (start + 2 < close && sql[start + 2] == '!') return start + 3;
        if (start + 3 < close
            && sql[start + 2] is 'M' or 'm'
            && sql[start + 3] == '!')
        {
            return start + 4;
        }

        return -1;
    }

    private static (string Value, int NextIndex) ReadQuotedIdentifier(
        string sql,
        int start,
        int end,
        char quote)
    {
        var value = new StringBuilder();
        for (var index = start + 1; index < end; index++)
        {
            var character = sql[index];
            if (character == quote)
            {
                if (index + 1 < end && sql[index + 1] == quote)
                {
                    value.Append(quote);
                    index++;
                    continue;
                }

                return (value.ToString(), index + 1);
            }

            if (character == '\\' && index + 1 < end)
            {
                value.Append(sql[++index]);
                continue;
            }

            value.Append(character);
        }

        throw new FormatException("The SQL statement ends inside a quoted identifier.");
    }

    private static int SkipQuoted(string sql, int start, int end, char quote)
    {
        for (var index = start + 1; index < end; index++)
        {
            if (sql[index] == '\\' && index + 1 < end)
            {
                index++;
                continue;
            }

            if (sql[index] != quote) continue;
            if (index + 1 < end && sql[index + 1] == quote)
            {
                index++;
                continue;
            }

            return index + 1;
        }

        throw new FormatException("The SQL statement ends inside a string literal.");
    }

    private static int SkipLineComment(string sql, int index, int end)
    {
        while (index < end && sql[index] is not '\r' and not '\n') index++;
        return index;
    }

    private static bool IsDashComment(string sql, int index, int end) =>
        index + 2 >= end || char.IsWhiteSpace(sql[index + 2]);

    private static bool IsIdentifierCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '$' || value >= '\u0080';

    private enum TokenKind
    {
        Identifier,
        Dot,
        Other,
    }

    private readonly record struct Token(TokenKind Kind, string Text);
}
