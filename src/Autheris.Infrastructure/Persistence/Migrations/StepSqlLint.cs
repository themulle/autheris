using System.Text;
using System.Text.RegularExpressions;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>
/// Lint of a governance migration step (issue #388, I388-SC-07, plan D-2 c). The SQL is normalised first (comments removed, string
/// literals emptied, quoted, bracketed and schema-qualified names reduced to the bare lower-case name), so quoting, comments and
/// qualification cannot hide a forbidden statement. Dynamic SQL is forbidden as a whole, so the lint cannot be bypassed.
/// A violation fails the step load, and with it startup (fail closed).
/// </summary>
internal static class StepSqlLint
{
    private static readonly (string Rule, Regex Pattern)[] Forbidden =
    [
        ("CONCURRENTLY", Re(@"\bconcurrently\b")),
        ("DISABLE TRIGGER", Re(@"\bdisable\s+trigger\b")),
        ("DROP TRIGGER", Re(@"\bdrop\s+trigger\b")),
        ("TRUNCATE", Re(@"\btruncate\b")),
        ("GRANT", Re(@"\bgrant\b")),
        ("REVOKE", Re(@"\brevoke\b")),
        ("CREATE/ALTER/DROP ROLE|USER|LOGIN", Re(@"\b(create|alter|drop)\s+(role|user|login)\b")),
        ("OWNER TO", Re(@"\bowner\s+to\b")),
        ("SET ROLE", Re(@"\bset\s+role\b")),
        ("CREATE EXTENSION", Re(@"\bcreate\s+extension\b")),
        ("COPY", Re(@"\bcopy\b")),
        ("xp_cmdshell", Re(@"\bxp_cmdshell\b")),
        ("ATTACH", Re(@"\battach\b")),
        ("PRAGMA writable_schema", Re(@"\bpragma\s+writable_schema\b")),
        ("history table access", Re(@"\bautheris_schema_migrations\b")),
        ("dynamic SQL (EXEC/EXECUTE)", Re(@"\bexec(ute)?\b")),
        ("dynamic SQL (sp_executesql)", Re(@"\bsp_executesql\b")),
        ("dynamic SQL (PREPARE)", Re(@"\bprepare\b")),
        ("dynamic SQL (DO)", Re(@"(^|;)\s*do\b")),
        ("transaction control", Re(@"(^|;)\s*(begin(\s+(transaction|tran|work|deferred|immediate|exclusive))?|commit|rollback|savepoint|release|end\s+transaction|start\s+transaction|set\s+transaction|set\s+implicit_transactions)\s*(;|$)"))
    ];

    private static readonly Regex AuditObject = Re(@"\b(audit_\w+|trg_audit\w*|autheris_audit\w*|fk_audit\w*)\b");
    private static readonly Regex AuditTable = Re(@"\baudit_log_entries\b");

    // The only forms allowed on the audit table (with a phase3 marker): DROP DEFAULT and an additive ADD COLUMN.
    private static readonly Regex AuditTableAllowed = Re(
        @"^alter\s+table\s+(if\s+exists\s+)?audit_log_entries\s+(alter\s+column\s+\w+\s+drop\s+default|drop\s+constraint\s+df_\w+|add\s+(?!constraint\b|foreign\b|primary\b|check\b|unique\b|index\b)[^;]+)$");

    private static readonly Regex SensitiveDml = Re(@"^(insert(\s+or\s+\w+)?\s+into|update|delete\s+from|merge\s+into|replace\s+into)\s+(audit_\w+|consent\w*)\b");
    // The one allowed dynamic form (SQL Server): the baseline default constraints are unnamed, so a step looks the generated name up and drops it with
    // EXEC(N'ALTER TABLE <t> DROP CONSTRAINT ' + QUOTENAME(@name)). The form is rewritten to the equivalent static statement before linting, so the audit
    // table rules (phase3 marker, DROP CONSTRAINT df_* only) still apply to it. Any other EXEC stays forbidden.
    private static readonly Regex DynamicDropConstraint = Re(
        @"(?:\bif\s+@\w+\s+is\s+not\s+null\s+)?\bexec\s*\(\s*N'alter\s+table\s+(?:dbo\.)?([A-Za-z_][A-Za-z0-9_]*)\s+drop\s+constraint\s+'\s*\+\s*quotename\s*\(\s*@\w+\s*\)\s*\)");
    private static readonly Regex Qualifier = new(@"\b[a-z_][a-z0-9_$]*\.(?=[a-z_])", RegexOptions.CultureInvariant);
    private static readonly Regex Blanks = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex MarkerId = new(@"^[A-Z0-9][A-Z0-9-]{2,40}$", RegexOptions.CultureInvariant);

    /// <summary>Validates the step text and returns the violations (empty when the step is acceptable).</summary>
    public static IReadOnlyList<string> Validate(string normalizedSql, string? guardSql, string? phase3Marker)
    {
        ArgumentNullException.ThrowIfNull(normalizedSql);
        var violations = new List<string>();
        var hasMarker = phase3Marker is not null;
        if (hasMarker && !MarkerId.IsMatch(phase3Marker!))
        {
            violations.Add("the phase3 marker id is malformed");
        }

        var statements = Normalize(DynamicDropConstraint.Replace(normalizedSql, "ALTER TABLE $1 DROP CONSTRAINT df_dynamic"));
        if (statements.Count == 0)
        {
            violations.Add("the step contains no statement");
        }

        var whole = string.Join(";", statements);
        foreach (var (rule, pattern) in Forbidden)
        {
            if (pattern.IsMatch(whole))
            {
                violations.Add("forbidden: " + rule);
            }
        }

        foreach (var statement in statements)
        {
            if (AuditObject.IsMatch(statement))
            {
                if (!hasMarker)
                {
                    violations.Add("a statement names an audit object without a '-- phase3:' marker");
                }

                if (AuditTable.IsMatch(statement) && !AuditTableAllowed.IsMatch(statement))
                {
                    violations.Add("only DROP DEFAULT and an additive ADD COLUMN are allowed on AUDIT_LOG_ENTRIES");
                }
            }
            else if (SensitiveDml.IsMatch(statement) && !hasMarker)
            {
                violations.Add("INSERT/UPDATE/DELETE on consent tables needs a '-- phase3:' marker");
            }
        }

        if (guardSql is not null)
        {
            var guard = Normalize(guardSql);
            if (guard.Count != 1 || !guard[0].StartsWith("select ", StringComparison.Ordinal))
            {
                violations.Add("the '-- guard:' must be a single SELECT");
            }
            else
            {
                foreach (var (rule, pattern) in Forbidden)
                {
                    if (pattern.IsMatch(guard[0]))
                    {
                        violations.Add("guard forbidden: " + rule);
                    }
                }
            }
        }

        return violations.Distinct().ToArray();
    }

    /// <summary>Normalises SQL to lower-case statements without comments, literal contents, quoting or schema qualifiers. A line <c>-- batch</c> ends a statement.</summary>
    internal static IReadOnlyList<string> Normalize(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                end = end < 0 ? sql.Length : end;
                sb.Append(sql.AsSpan(i + 2, end - i - 2).Trim().Equals("batch", StringComparison.OrdinalIgnoreCase) ? ';' : ' ');
                i = end;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                sb.Append(' ');
                i = end < 0 ? sql.Length : end + 2;
            }
            else if (c == '\'')
            {
                i = SkipQuoted(sql, i, '\'');
                sb.Append("''");
            }
            else if (c is '"' or '`')
            {
                var close = c;
                var start = i + 1;
                i = SkipQuoted(sql, i, close);
                sb.Append(sql.AsSpan(start, Math.Max(0, i - start - 1)));
            }
            else if (c == '[')
            {
                var end = sql.IndexOf(']', i + 1);
                end = end < 0 ? sql.Length - 1 : end;
                sb.Append(sql.AsSpan(i + 1, end - i - 1));
                i = end + 1;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        var text = Qualifier.Replace(Blanks.Replace(sb.ToString().ToLowerInvariant(), " "), string.Empty);
        return text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
    }

    private static int SkipQuoted(string sql, int start, char quote)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return sql.Length;
    }

    private static Regex Re(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
