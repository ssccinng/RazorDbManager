using RazorDbManager.Core;

namespace RazorDbManager.MySql.Health;

internal sealed record MySqlGrantAnalysis(
    RazorDbCapability Capabilities,
    IReadOnlyList<string> DiagnosticCodes);

internal static class MySqlGrantAnalyzer
{
    private const string GrantPrefix = "GRANT ";
    private const string AllPrivileges = "ALL PRIVILEGES";
    private const string All = "ALL";

    public static MySqlGrantAnalysis Analyze(
        IEnumerable<string> grantStatements,
        IReadOnlyCollection<string> allowedSchemas)
    {
        ArgumentNullException.ThrowIfNull(grantStatements);
        ArgumentNullException.ThrowIfNull(allowedSchemas);
        if (allowedSchemas.Count == 0)
            throw new ArgumentException("At least one allowed schema is required.", nameof(allowedSchemas));

        var globalPrivileges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var schemaPrivileges = allowedSchemas
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                schema => schema,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
        var statementCount = 0;

        foreach (string rawStatement in grantStatements)
        {
            statementCount++;
            string statement = rawStatement.Trim().TrimEnd(';').TrimEnd();
            if (!statement.StartsWith(GrantPrefix, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add("grants-statement-unparsed");
                continue;
            }

            int onIndex = FindKeyword(statement, " ON ", GrantPrefix.Length);
            if (onIndex < 0)
            {
                // MySQL and MariaDB render role assignments as GRANT ... TO ... without an ON scope.
                diagnostics.Add("grants-role-unresolved");
                continue;
            }

            int toIndex = FindKeyword(statement, " TO ", onIndex + 4);
            if (toIndex < 0)
            {
                diagnostics.Add("grants-statement-unparsed");
                continue;
            }

            string privilegeText = statement[GrantPrefix.Length..onIndex].Trim();
            string scopeText = statement[(onIndex + 4)..toIndex].Trim();
            if (!TryParseScope(scopeText, out string? schema, out bool isGlobal, out bool isWholeSchema))
            {
                diagnostics.Add("grants-statement-unparsed");
                continue;
            }

            if (!isGlobal && !isWholeSchema)
            {
                diagnostics.Add("grants-partial-scope-ignored");
                continue;
            }

            HashSet<string>? target = isGlobal
                ? globalPrivileges
                : schema is not null && schemaPrivileges.TryGetValue(schema, out HashSet<string>? privileges)
                    ? privileges
                    : null;
            if (target is null)
            {
                // SHOW GRANTS can include unrelated schemas. They are intentionally irrelevant to this registration.
                continue;
            }

            foreach (string privilege in SplitPrivileges(privilegeText))
            {
                if (privilege.Contains('('))
                {
                    diagnostics.Add("grants-column-scope-ignored");
                    continue;
                }

                target.Add(NormalizePrivilege(privilege));
            }
        }

        if (statementCount == 0)
            diagnostics.Add("grants-empty");

        RazorDbCapability? commonCapabilities = null;
        foreach (HashSet<string> scopedPrivileges in schemaPrivileges.Values)
        {
            var effectivePrivileges = new HashSet<string>(globalPrivileges, StringComparer.OrdinalIgnoreCase);
            effectivePrivileges.UnionWith(scopedPrivileges);
            RazorDbCapability capabilities = MapCapabilities(effectivePrivileges);
            commonCapabilities = commonCapabilities is null
                ? capabilities
                : commonCapabilities.Value & capabilities;
        }

        return new MySqlGrantAnalysis(
            commonCapabilities ?? RazorDbCapability.None,
            diagnostics.Order(StringComparer.Ordinal).ToArray());
    }

    private static RazorDbCapability MapCapabilities(IReadOnlySet<string> privileges)
    {
        bool all = privileges.Contains(AllPrivileges) || privileges.Contains(All);
        bool Has(string privilege) => all || privileges.Contains(privilege);

        RazorDbCapability result = RazorDbCapability.None;
        if (Has("SELECT"))
        {
            result |= RazorDbCapability.BrowseMetadata
                | RazorDbCapability.ReadRows
                | RazorDbCapability.Export
                | RazorDbCapability.DownloadBinary;
        }

        if (Has("INSERT")) result |= RazorDbCapability.InsertRows | RazorDbCapability.Import;
        if (Has("UPDATE")) result |= RazorDbCapability.UpdateRows;
        if (Has("DELETE")) result |= RazorDbCapability.DeleteRows;

        bool canModifySchema = Has("CREATE")
            && Has("ALTER")
            && Has("INDEX")
            && Has("REFERENCES");
        if (canModifySchema) result |= RazorDbCapability.ModifySchema;
        if (canModifySchema && Has("DROP")) result |= RazorDbCapability.DestructiveSchema;

        // Arbitrary SQL cannot be inferred from a partial privilege list. ALL is the only conservative signal.
        if (all) result |= RazorDbCapability.ExecuteSql;
        return result;
    }

    private static IEnumerable<string> SplitPrivileges(string value)
    {
        int start = 0;
        int parentheses = 0;
        char quote = '\0';
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (quote != '\0')
            {
                if (current == quote)
                {
                    if (index + 1 < value.Length && value[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                continue;
            }

            if (current is '`' or '\'' or '"') quote = current;
            else if (current == '(') parentheses++;
            else if (current == ')' && parentheses > 0) parentheses--;
            else if (current == ',' && parentheses == 0)
            {
                yield return value[start..index].Trim();
                start = index + 1;
            }
        }

        yield return value[start..].Trim();
    }

    private static string NormalizePrivilege(string value)
    {
        string normalized = string.Join(' ', value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.ToUpperInvariant();
    }

    private static bool TryParseScope(
        string value,
        out string? schema,
        out bool isGlobal,
        out bool isWholeSchema)
    {
        schema = null;
        isGlobal = false;
        isWholeSchema = false;
        int separator = FindScopeSeparator(value);
        if (separator <= 0 || separator == value.Length - 1) return false;

        string schemaToken = UnquoteIdentifier(value[..separator].Trim());
        string objectToken = UnquoteIdentifier(value[(separator + 1)..].Trim());
        if (schemaToken.Length == 0 || objectToken.Length == 0) return false;

        isGlobal = schemaToken == "*" && objectToken == "*";
        isWholeSchema = !isGlobal && objectToken == "*";
        schema = isGlobal ? null : schemaToken;
        return true;
    }

    private static int FindScopeSeparator(string value)
    {
        char quote = '\0';
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (quote != '\0')
            {
                if (current == quote)
                {
                    if (index + 1 < value.Length && value[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                continue;
            }

            if (current is '`' or '\'' or '"') quote = current;
            else if (current == '.') return index;
        }

        return -1;
    }

    private static string UnquoteIdentifier(string value)
    {
        if (value.Length < 2) return value;
        char quote = value[0];
        if (quote is not ('`' or '\'' or '"') || value[^1] != quote) return value;
        return value[1..^1].Replace(new string(quote, 2), quote.ToString(), StringComparison.Ordinal);
    }

    private static int FindKeyword(string value, string keyword, int start)
    {
        char quote = '\0';
        for (int index = start; index <= value.Length - keyword.Length; index++)
        {
            char current = value[index];
            if (quote != '\0')
            {
                if (current == quote)
                {
                    if (index + 1 < value.Length && value[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                continue;
            }

            if (current is '`' or '\'' or '"')
            {
                quote = current;
                continue;
            }

            if (value.AsSpan(index).StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }
}
