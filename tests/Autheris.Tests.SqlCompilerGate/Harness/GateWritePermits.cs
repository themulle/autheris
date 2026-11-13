namespace Autheris.Tests.SqlCompilerGate.Harness;

using System.Text;
using System.Text.RegularExpressions;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Test-only default write permit of the SQL compiler gate (WP-SIMPL-45 PR 3 CI repair, R-1). The engine fails closed without a
/// permit, so the gate builds one per statement: the operation comes from the corpus statement class, every catalog column of the
/// target that has no mask is writable and no column is
/// write-protected. The target is found lexically (comments and string literals are ignored). When no unambiguous target is found
/// the permit has an empty writable set, never all columns (SEC-CIFIX-8). Lives in the test assembly only (SEC-CIFIX-1/-2).
/// </summary>
public static partial class GateWritePermits
{
    private const string NamePart = "(?:\"(?:[^\"]|\"\")*\"|\\[[^\\]]*\\]|`[^`]*`|[A-Za-z_][A-Za-z0-9_$#]*)";

    [GeneratedRegex(@"\b(?<kw>INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO)\s+(?<name>" + NamePart + @"(?:\s*\.\s*" + NamePart + @")*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetPattern();

    [GeneratedRegex(NamePart, RegexOptions.CultureInvariant)]
    private static partial Regex PartPattern();

    /// <summary>Null for the select class. The operation is the corpus class (MERGE maps to Update so the engine reaches its MERGE rejection).</summary>
    public static DmlWritePermit? For(string statementClass, string sql, GovernancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        DmlWriteOperation? operation = statementClass switch
        {
            "insert" => DmlWriteOperation.Insert,
            "update" or "merge" => DmlWriteOperation.Update,
            "delete" => DmlWriteOperation.Delete,
            _ => null
        };
        if (operation is null) return null;

        var empty = new DmlWritePermit(operation.Value, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var stripped = Strip(sql);
        if (stripped is null) return empty;
        var matches = TargetPattern().Matches(stripped).Cast<Match>().ToList();
        var merges = matches.Where(m => Kind(m).StartsWith("MERGE", StringComparison.Ordinal)).ToList();
        var candidates = merges.Count > 0 ? merges : matches;
        if (candidates.Count != 1) return empty;   // none or ambiguous: fail closed

        var parts = PartPattern().Matches(candidates[0].Groups["name"].Value).Select(m => Unquote(m.Value)).ToArray();
        var entry = policy.Catalog.Resolve(new SqlQualifiedName(parts));
        if (entry is null) return empty;

        var writable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in entry.Columns)
        {
            if (!policy.Masks.HasMask(entry.Identity, column.Name)) writable.Add(column.Name);
        }

        return new DmlWritePermit(operation.Value, writable, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static string Kind(Match m) => Regex.Replace(m.Groups["kw"].Value.ToUpperInvariant(), @"\s+", " ");

    private static string Unquote(string part) => part[0] switch
    {
        '"' => part[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal),
        '[' or '`' => part[1..^1],
        _ => part
    };

    /// <summary>Removes comments and string literals; keeps quoted identifiers. Null when a comment, literal or identifier is unterminated.</summary>
    private static string? Strip(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end;
                sb.Append(' ');
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return null;
                i = end + 2;
                sb.Append(' ');
            }
            else if (c == '\'')
            {
                int j = i + 1;
                while (true)
                {
                    if (j >= sql.Length) return null;
                    if (sql[j] == '\'')
                    {
                        if (j + 1 < sql.Length && sql[j + 1] == '\'') { j += 2; continue; }
                        break;
                    }

                    j++;
                }

                i = j + 1;
                sb.Append(" '' ");
            }
            else if (c is '"' or '`' or '[')
            {
                char close = c == '[' ? ']' : c;
                int j = i + 1;
                while (true)
                {
                    if (j >= sql.Length) return null;
                    if (sql[j] == close)
                    {
                        if (close == '"' && j + 1 < sql.Length && sql[j + 1] == '"') { j += 2; continue; }
                        break;
                    }

                    j++;
                }

                sb.Append(sql, i, j + 1 - i);
                i = j + 1;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }
}
