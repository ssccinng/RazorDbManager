using System.Text;
using System.Runtime.CompilerServices;

namespace RazorDbManager.MySql.Sql;

internal sealed record MySqlScriptStatement(string Text, int StartLine);

internal static class MySqlScriptTokenizer
{
    private enum State
    {
        Normal,
        SingleQuoted,
        DoubleQuoted,
        BacktickQuoted,
        LineComment,
        BlockComment,
    }

    public static IReadOnlyList<MySqlScriptStatement> Tokenize(string script, int maximumStatements = 10_000)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (maximumStatements <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumStatements));
        }

        var statements = new List<MySqlScriptStatement>();
        var current = new StringBuilder();
        var delimiter = ";";
        var state = State.Normal;
        var line = 1;
        var statementLine = 1;
        var atLineStart = true;

        for (var index = 0; index < script.Length;)
        {
            if (state == State.Normal && atLineStart && IsOnlyWhitespace(current)
                && TryReadDelimiterDirective(script, index, out var directiveLength, out var newDelimiter))
            {
                delimiter = newDelimiter;
                current.Clear();
                for (var offset = 0; offset < directiveLength; offset++)
                {
                    if (script[index + offset] == '\n')
                    {
                        line++;
                    }
                }

                index += directiveLength;
                statementLine = line;
                atLineStart = true;
                continue;
            }

            if (state == State.Normal && StartsWith(script, index, delimiter))
            {
                Emit(current, statementLine, statements, maximumStatements);
                index += delimiter.Length;
                statementLine = line;
                atLineStart = false;
                continue;
            }

            var character = script[index];
            var next = index + 1 < script.Length ? script[index + 1] : '\0';
            current.Append(character);

            switch (state)
            {
                case State.Normal when character == '\'':
                    state = State.SingleQuoted;
                    break;
                case State.Normal when character == '"':
                    state = State.DoubleQuoted;
                    break;
                case State.Normal when character == '`':
                    state = State.BacktickQuoted;
                    break;
                case State.Normal when character == '#':
                    state = State.LineComment;
                    break;
                case State.Normal when character == '-' && next == '-' && IsDashComment(script, index):
                    current.Append(next);
                    index++;
                    state = State.LineComment;
                    break;
                case State.Normal when character == '/' && next == '*':
                    current.Append(next);
                    index++;
                    state = State.BlockComment;
                    break;
                case State.SingleQuoted when character == '\\' && next != '\0':
                case State.DoubleQuoted when character == '\\' && next != '\0':
                    current.Append(next);
                    index++;
                    break;
                case State.SingleQuoted when character == '\'' && next == '\'':
                case State.DoubleQuoted when character == '"' && next == '"':
                case State.BacktickQuoted when character == '`' && next == '`':
                    current.Append(next);
                    index++;
                    break;
                case State.SingleQuoted when character == '\'':
                case State.DoubleQuoted when character == '"':
                case State.BacktickQuoted when character == '`':
                    state = State.Normal;
                    break;
                case State.LineComment when character is '\r' or '\n':
                    state = State.Normal;
                    break;
                case State.BlockComment when character == '*' && next == '/':
                    current.Append(next);
                    index++;
                    state = State.Normal;
                    break;
            }

            if (character == '\n')
            {
                line++;
                atLineStart = true;
                if (IsOnlyWhitespace(current))
                {
                    statementLine = line;
                }
            }
            else if (!char.IsWhiteSpace(character))
            {
                atLineStart = false;
            }

            index++;
        }

        if (state is State.SingleQuoted or State.DoubleQuoted or State.BacktickQuoted or State.BlockComment)
        {
            throw new FormatException("The SQL script ends inside a quoted value, identifier, or block comment.");
        }

        Emit(current, statementLine, statements, maximumStatements);
        return statements;
    }

    public static async IAsyncEnumerable<MySqlScriptStatement> TokenizeAsync(
        TextReader reader,
        int maximumStatementCharacters,
        int maximumStatements = 10_000,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maximumStatementCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumStatementCharacters));
        if (maximumStatements <= 0) throw new ArgumentOutOfRangeException(nameof(maximumStatements));

        var current = new StringBuilder(Math.Min(maximumStatementCharacters, 64 * 1024));
        var delimiter = ";";
        var state = State.Normal;
        var lineNumber = 1;
        var statementLine = 1;
        var statementCount = 0;

        await foreach (var line in ReadLinesAsync(reader, maximumStatementCharacters, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state == State.Normal && IsOnlyComment(current.ToString())
                && TryParseDelimiterLine(line, out var newDelimiter))
            {
                delimiter = newDelimiter;
                current.Clear();
                statementLine = ++lineNumber;
                continue;
            }

            var text = line + "\n";
            for (var index = 0; index < text.Length; index++)
            {
                if (state == State.Normal && StartsWith(text, index, delimiter))
                {
                    var statement = TakeStatement(current, statementLine);
                    if (statement is not null)
                    {
                        if (++statementCount > maximumStatements)
                            throw new InvalidOperationException($"The script exceeds the {maximumStatements} statement limit.");
                        yield return statement;
                    }

                    index += delimiter.Length - 1;
                    statementLine = lineNumber;
                    continue;
                }

                var character = text[index];
                var next = index + 1 < text.Length ? text[index + 1] : '\0';
                current.Append(character);
                if (current.Length > maximumStatementCharacters)
                    throw new InvalidOperationException($"A SQL statement exceeds the {maximumStatementCharacters} character limit.");

                switch (state)
                {
                    case State.Normal when character == '\'': state = State.SingleQuoted; break;
                    case State.Normal when character == '"': state = State.DoubleQuoted; break;
                    case State.Normal when character == '`': state = State.BacktickQuoted; break;
                    case State.Normal when character == '#': state = State.LineComment; break;
                    case State.Normal when character == '-' && next == '-' && IsDashComment(text, index):
                        current.Append(next); index++; state = State.LineComment; break;
                    case State.Normal when character == '/' && next == '*':
                        current.Append(next); index++; state = State.BlockComment; break;
                    case State.SingleQuoted when character == '\\' && next != '\0':
                    case State.DoubleQuoted when character == '\\' && next != '\0':
                        current.Append(next); index++; break;
                    case State.SingleQuoted when character == '\'' && next == '\'':
                    case State.DoubleQuoted when character == '"' && next == '"':
                    case State.BacktickQuoted when character == '`' && next == '`':
                        current.Append(next); index++; break;
                    case State.SingleQuoted when character == '\'':
                    case State.DoubleQuoted when character == '"':
                    case State.BacktickQuoted when character == '`': state = State.Normal; break;
                    case State.LineComment when character is '\r' or '\n': state = State.Normal; break;
                    case State.BlockComment when character == '*' && next == '/':
                        current.Append(next); index++; state = State.Normal; break;
                }
            }

            lineNumber++;
        }

        if (state is State.SingleQuoted or State.DoubleQuoted or State.BacktickQuoted or State.BlockComment)
            throw new FormatException("The SQL script ends inside a quoted value, identifier, or block comment.");

        var finalStatement = TakeStatement(current, statementLine);
        if (finalStatement is not null)
        {
            if (++statementCount > maximumStatements)
                throw new InvalidOperationException($"The script exceeds the {maximumStatements} statement limit.");
            yield return finalStatement;
        }
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        TextReader reader,
        int maximumLineCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new char[16 * 1024];
        var line = new StringBuilder(Math.Min(maximumLineCharacters, buffer.Length));
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    yield return line.ToString();
                    line.Clear();
                    continue;
                }

                line.Append(character);
                if (line.Length > maximumLineCharacters)
                    throw new InvalidOperationException($"A SQL input line exceeds the {maximumLineCharacters} character limit.");
            }
        }

        if (line.Length > 0) yield return line.ToString();
    }

    private static bool TryParseDelimiterLine(string line, out string delimiter)
    {
        delimiter = string.Empty;
        var trimmed = line.Trim();
        const string keyword = "DELIMITER";
        if (!trimmed.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
            || trimmed.Length == keyword.Length
            || !char.IsWhiteSpace(trimmed[keyword.Length])) return false;

        delimiter = trimmed[keyword.Length..].Trim();
        if (delimiter.Length is 0 or > 32 || delimiter.Any(char.IsWhiteSpace))
            throw new FormatException("DELIMITER must be a non-empty token of at most 32 characters without whitespace.");
        return true;
    }

    private static MySqlScriptStatement? TakeStatement(StringBuilder current, int startLine)
    {
        var value = current.ToString().Trim();
        current.Clear();
        return value.Length == 0 || IsOnlyComment(value) ? null : new MySqlScriptStatement(value, startLine);
    }

    private static bool TryReadDelimiterDirective(
        string script,
        int start,
        out int length,
        out string delimiter)
    {
        length = 0;
        delimiter = string.Empty;
        var index = start;
        while (index < script.Length && script[index] is ' ' or '\t')
        {
            index++;
        }

        const string keyword = "DELIMITER";
        if (!StartsWith(script, index, keyword, StringComparison.OrdinalIgnoreCase)
            || index + keyword.Length >= script.Length
            || !char.IsWhiteSpace(script[index + keyword.Length]))
        {
            return false;
        }

        var valueStart = index + keyword.Length;
        while (valueStart < script.Length && script[valueStart] is ' ' or '\t')
        {
            valueStart++;
        }

        var lineEnd = valueStart;
        while (lineEnd < script.Length && script[lineEnd] is not '\r' and not '\n')
        {
            lineEnd++;
        }

        delimiter = script[valueStart..lineEnd].Trim();
        if (delimiter.Length is 0 or > 32 || delimiter.Any(char.IsWhiteSpace))
        {
            throw new FormatException("DELIMITER must be a non-empty token of at most 32 characters without whitespace.");
        }

        if (lineEnd < script.Length && script[lineEnd] == '\r')
        {
            lineEnd++;
        }

        if (lineEnd < script.Length && script[lineEnd] == '\n')
        {
            lineEnd++;
        }

        length = lineEnd - start;
        return true;
    }

    private static void Emit(
        StringBuilder current,
        int startLine,
        ICollection<MySqlScriptStatement> statements,
        int maximumStatements)
    {
        var text = current.ToString().Trim();
        current.Clear();
        if (text.Length == 0 || IsOnlyComment(text))
        {
            return;
        }

        if (statements.Count >= maximumStatements)
        {
            throw new InvalidOperationException($"The script exceeds the {maximumStatements} statement limit.");
        }

        statements.Add(new MySqlScriptStatement(text, startLine));
    }

    private static bool IsOnlyWhitespace(StringBuilder builder)
    {
        foreach (var chunk in builder.GetChunks())
        {
            foreach (var character in chunk.Span)
            {
                if (!char.IsWhiteSpace(character))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsOnlyComment(string text)
    {
        var state = State.Normal;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var next = index + 1 < text.Length ? text[index + 1] : '\0';

            switch (state)
            {
                case State.Normal when char.IsWhiteSpace(character):
                    continue;
                case State.Normal when character == '#':
                    state = State.LineComment;
                    continue;
                case State.Normal when character == '-' && next == '-' && IsDashComment(text, index):
                    state = State.LineComment;
                    index++;
                    continue;
                case State.Normal when character == '/' && next == '*' && index + 2 < text.Length && text[index + 2] != '!':
                    state = State.BlockComment;
                    index++;
                    continue;
                case State.LineComment when character is '\r' or '\n':
                    state = State.Normal;
                    continue;
                case State.LineComment:
                    continue;
                case State.BlockComment when character == '*' && next == '/':
                    state = State.Normal;
                    index++;
                    continue;
                case State.BlockComment:
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool IsDashComment(string script, int index) =>
        index + 2 >= script.Length || char.IsWhiteSpace(script[index + 2]);

    private static bool StartsWith(
        string source,
        int index,
        string value,
        StringComparison comparison = StringComparison.Ordinal) =>
        index <= source.Length - value.Length
        && source.AsSpan(index, value.Length).Equals(value.AsSpan(), comparison);
}
