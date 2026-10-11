using System.Collections.Immutable;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Antlr4.Runtime.Misc;
using FsCheck;
using FsCheck.Fluent;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;

namespace TrinoSqlEngine.Tests.Properties;

/// <summary>One generated statement with its request context and the style it is rendered in.</summary>
internal sealed record StmtCase(Frag Frag, QueryGen.DmlKind? Dml, CaseCtx Ctx, RenderStyle Style, RenderStyle AltStyle)
{
    public string Sql => TokenRenderer.Render(Frag, Style);
    public string AltSql => TokenRenderer.Render(Frag, AltStyle);
    public StatementPermissions Permissions => Dml is null ? StatementPermissions.ReadOnly : (StatementPermissions)15;

    public static Gen<StmtCase> Gen() =>
        from ctx in CaseCtx.Gen()
        from isDml in FsCheck.Fluent.Gen.Elements(false, false, false, true)
        from read in QueryGen.Query()
        from dml in QueryGen.Dml(ctx.TenantValue)
        from s1 in StyleGen()
        from s2 in StyleGen()
        select isDml ? new StmtCase(dml.Frag, dml.Kind, ctx, s1, s2) : new StmtCase(read, null, ctx, s1, s2);

    private static Gen<RenderStyle> StyleGen() =>
        from k in FsCheck.Fluent.Gen.Choose(0, 2)
        from m in FsCheck.Fluent.Gen.Choose(0, 2)
        from seed in FsCheck.Fluent.Gen.Choose(0, 1_000_000)
        select new RenderStyle(k, m, seed);
}

/// <summary>
/// The independent oracles of the property gate. They look only at the emitted text and the bound parameters, never at compiler
/// internals, so they hold the compiler to the security invariants without sharing its code (plan 8.1, 9.3 G5).
/// </summary>
internal static class Oracles
{
    public const string FailClosed = "INV-1 fail-closed typing";
    public const string Coverage = "INV-2/3 every table reference carries the tenant and policy predicate";
    public const string NoLiteral = "INV-4/6 no literal, comment or ';' in the emitted text";
    public const string BoundValues = "INV-5 policy, tenant and user values are bound and round-trip exactly";
    public const string BindAccounting = "INV-4 marker/parameter accounting";
    public const string Dml = "INV-10 DML check option";
    public const string Determinism = "determinism";
    public const string Identity = "G3 format identity";
    public const string Limits = "INV-7 limit boundary";

    private static readonly Regex Leak = new(@"(LIT|POL|TNT|OTH)\d+_", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LiteralValue = new(@"^(LIT|OTH)\d+_", RegexOptions.Compiled);

    /// <summary>Exception types that count as a typed rejection. Anything else is an unhandled failure.</summary>
    public static bool IsTypedRejection(Exception e) => e is ParseCanceledException or SecurityException or AstBuildException or GovernedSqlException
        or DialectLimitExceededException;

    /// <summary>
    /// KD-1 (see <c>KnownDefectTests</c>): a parenthesized query primary makes <c>SqlAstBuilder</c> cast a <c>SelectStatement</c> to
    /// <c>QueryBody</c>. It fails closed, so the property gate counts it separately instead of letting it mask other findings.
    /// <c>KnownDefectTests.KD1_IsStillReproducible</c> fails when the defect is fixed, and the exemption must then be removed.
    /// </summary>
    internal static bool IsKnownDefectKd1(Exception e) =>
        e is InvalidCastException && e.Message.Contains("Nodes.SelectStatement", StringComparison.Ordinal) && e.Message.Contains("Nodes.QueryBody", StringComparison.Ordinal);

    // ---- compile and classify ----

    public static (CompiledSql? Sql, Exception? Error) TryCompile(CompileFn compile, string sql, CaseCtx ctx, StatementPermissions permissions)
    {
        try
        {
            return (compile(sql, ctx, permissions), null);
        }
        catch (Exception e)
        {
            return (null, e);
        }
    }

    /// <summary>Runs every oracle on one case; the outcome is Compiled, typed rejection or an unhandled failure.</summary>
    public static (string Sql, CaseCtx Ctx, IReadOnlyList<Violation> Violations, bool? Compiled, string? Rejection) CheckCase(StmtCase c, CompileFn compile)
    {
        var violations = new List<Violation>();
        string sql = c.Sql;
        var (compiled, error) = TryCompile(compile, sql, c.Ctx, c.Permissions);
        bool governedDialect = CaseCtx.Governed.Contains(c.Ctx.Dialect);

        if (error is not null)
        {
            if (IsKnownDefectKd1(error))
            {
                return (sql, c.Ctx, violations, false, "KD-1 InvalidCastException");
            }

            if (!IsTypedRejection(error))
            {
                violations.Add(new Violation(FailClosed, $"unhandled exception type {error.GetType().FullName}: {Short(error.Message)}"));
            }

            return (sql, c.Ctx, violations, false, error.GetType().Name);
        }

        CheckAccepted(c, compiled!, governedDialect, violations);
        return (sql, c.Ctx, violations, true, null);
    }

    private static void CheckAccepted(StmtCase c, CompiledSql compiled, bool governedDialect, List<Violation> violations)
    {
        if (!governedDialect)
        {
            violations.Add(new Violation(FailClosed, $"dialect {c.Ctx.Dialect} has no governed path but compiled"));
        }

        if (c.Frag.UnknownFunction)
        {
            violations.Add(new Violation(FailClosed, "a function outside the allowlist compiled"));
        }

        switch (c.Dml)
        {
            case QueryGen.DmlKind.DeleteUnfiltered:
                violations.Add(new Violation(Dml, "DELETE without WHERE compiled"));
                break;
            case QueryGen.DmlKind.InsertOtherTenant:
                violations.Add(new Violation(Dml, "INSERT with another tenant's value compiled"));
                break;
            case QueryGen.DmlKind.UpdateTenantColumn:
                violations.Add(new Violation(Dml, "UPDATE of the tenant column compiled"));
                break;
        }

        var expectedClass = c.Dml switch
        {
            null => SqlStatementClass.Select,
            QueryGen.DmlKind.Update or QueryGen.DmlKind.UpdateTenantColumn => SqlStatementClass.Update,
            QueryGen.DmlKind.Delete or QueryGen.DmlKind.DeleteUnfiltered => SqlStatementClass.Delete,
            QueryGen.DmlKind.Merge => SqlStatementClass.Merge,
            _ => SqlStatementClass.Insert
        };
        if (compiled.StatementClass != expectedClass)
        {
            violations.Add(new Violation(Dml, $"statement class {compiled.StatementClass}, expected {expectedClass}"));
        }

        CheckLiteralFree(compiled, c.Ctx.Dialect, compiled.StatementClass, violations);
        CheckBound(c, compiled, violations);
        CheckCoverage(c, compiled, violations);
    }

    // ---- text analysis ----

    internal sealed record QuotedSpan(int Start, int End, string Content);

    /// <summary>Marks every quoted identifier. Returns null when the quoting is unbalanced (the emitter lost control of a quote).</summary>
    internal static (string Masked, List<QuotedSpan> Spans)? Mask(string sql, TargetSqlDialect dialect)
    {
        var chars = sql.ToCharArray();
        var spans = new List<QuotedSpan>();
        for (int i = 0; i < sql.Length; i++)
        {
            char open = sql[i];
            char close = open switch
            {
                '"' => '"',
                '`' => '`',
                '[' when dialect == TargetSqlDialect.SqlServer => ']',
                _ => '\0'
            };
            if (close == '\0')
            {
                continue;
            }

            var content = new StringBuilder();
            int j = i + 1;
            bool closed = false;
            for (; j < sql.Length; j++)
            {
                if (sql[j] == close)
                {
                    if (j + 1 < sql.Length && sql[j + 1] == close)
                    {
                        content.Append(close);
                        j++;
                        continue;
                    }

                    closed = true;
                    break;
                }

                content.Append(sql[j]);
            }

            if (!closed)
            {
                return null;
            }

            for (int k = i; k <= j; k++)
            {
                chars[k] = '_';
            }

            spans.Add(new QuotedSpan(i, j + 1, content.ToString()));
            i = j;
        }

        return (new string(chars), spans);
    }

    private static void CheckLiteralFree(CompiledSql compiled, TargetSqlDialect dialect, SqlStatementClass statementClass, List<Violation> violations)
    {
        var masked = Mask(compiled.Sql, dialect);
        if (masked is null)
        {
            violations.Add(new Violation(NoLiteral, "unbalanced identifier quoting in the emitted text"));
            return;
        }

        string m = masked.Value.Masked;

        // SQL Server requires MERGE to be terminated by a semicolon: exactly one trailing ';' is the documented exception.
        if (statementClass == SqlStatementClass.Merge && dialect == TargetSqlDialect.SqlServer && m.EndsWith(';'))
        {
            m = m[..^1];
        }

        foreach (var token in new[] { "'", ";", "--", "/*", "*/", "\0" })
        {
            if (m.Contains(token, StringComparison.Ordinal))
            {
                violations.Add(new Violation(NoLiteral, $"emitted text contains '{token.Replace("\0", "\\0")}' outside a quoted identifier"));
            }
        }

        if (Leak.IsMatch(compiled.Sql))
        {
            violations.Add(new Violation(NoLiteral, "a generated user, policy or tenant value appears in the SQL text"));
        }
    }

    private static Regex MarkerRegex(string marker) => new("(?<![0-9A-Za-z_])" + Regex.Escape(marker) + "(?![0-9A-Za-z_])", RegexOptions.CultureInvariant);

    private static void CheckBound(StmtCase c, CompiledSql compiled, List<Violation> violations)
    {
        var markers = new HashSet<string>();
        foreach (var p in compiled.Parameters)
        {
            if (!markers.Add(p.Marker))
            {
                violations.Add(new Violation(BindAccounting, $"duplicate parameter marker {p.Marker}"));
            }

            if (!MarkerRegex(p.Marker).IsMatch(compiled.Sql))
            {
                violations.Add(new Violation(BindAccounting, $"parameter {p.Marker} ({p.Origin}) is not referenced by the SQL text"));
            }

            switch (p.Origin)
            {
                case ParameterOrigin.Tenant when !Equals(p.Value, c.Ctx.TenantValue):
                    violations.Add(new Violation(BoundValues, "the tenant parameter does not carry the caller's tenant value exactly"));
                    break;
                case ParameterOrigin.Policy when !Equals(p.Value, c.Ctx.PolicyValue):
                    violations.Add(new Violation(BoundValues, "the policy parameter does not carry the policy value exactly"));
                    break;
                case ParameterOrigin.QueryLiteral when p.Value is string s && LiteralValue.IsMatch(s) && !c.Frag.Literals.Contains(s):
                    violations.Add(new Violation(BoundValues, "a bound query literal differs from the literal the caller sent"));
                    break;
            }
        }

        string pattern = c.Ctx.Dialect switch
        {
            TargetSqlDialect.SqlServer => @"@p\d+",
            TargetSqlDialect.PostgreSql or TargetSqlDialect.DuckDb => @"\$\d+",
            _ => @":p\d+"
        };
        var masked = Mask(compiled.Sql, c.Ctx.Dialect);
        if (masked is not null)
        {
            foreach (Match m in Regex.Matches(masked.Value.Masked, pattern))
            {
                if (!markers.Contains(m.Value))
                {
                    violations.Add(new Violation(BindAccounting, $"marker {m.Value} in the text has no bound parameter"));
                }
            }
        }

        if (CaseCtx.Governed.Contains(c.Ctx.Dialect) && compiled.Parameters.Length > DialectCapabilityTable.Default.Get(c.Ctx.Dialect).MaxBindParameters)
        {
            violations.Add(new Violation(Limits, $"{compiled.Parameters.Length} parameters exceed the dialect maximum"));
        }
    }

    private static void CheckCoverage(StmtCase c, CompiledSql compiled, List<Violation> violations)
    {
        if (c.Dml == QueryGen.DmlKind.Merge)
        {
            if (!compiled.Parameters.Any(p => p.Origin == ParameterOrigin.Tenant))
            {
                violations.Add(new Violation(Coverage, "MERGE compiled without a tenant parameter"));
            }

            return;
        }

        var masked = Mask(compiled.Sql, c.Ctx.Dialect);
        if (masked is null)
        {
            return;
        }

        var (m, spans) = masked.Value;
        string raw = compiled.Sql;
        var tenantMarkers = compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Tenant).Select(p => p.Marker).ToArray();
        var policyMarkers = compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Policy).Select(p => p.Marker).ToArray();

        int occurrences = 0;
        int tenantOccurrences = 0;
        for (int i = 0; i + 1 < spans.Count; i++)
        {
            var schema = spans[i];
            var table = spans[i + 1];
            if (!schema.Content.Equals("dbo", StringComparison.OrdinalIgnoreCase) || m[schema.End..table.Start] != ".")
            {
                continue;
            }

            string name = table.Content.ToLowerInvariant();
            if (name is not ("orders" or "entitlements" or "lookup"))
            {
                continue;
            }

            string before = m[..schema.Start].TrimEnd();
            bool target = before.EndsWith("INTO", StringComparison.OrdinalIgnoreCase);
            if (target)
            {
                continue; // INSERT target: covered by the tenant value check below
            }

            if (c.Dml is null)
            {
                occurrences++;
            }

            if (name != "lookup")
            {
                tenantOccurrences++;
            }

            int scopeEnd = ScopeEnd(m, table.End);
            var conjuncts = WhereConjuncts(m, raw, table.End, scopeEnd, out bool topLevelOr, out bool hasWhere);
            if (!hasWhere)
            {
                violations.Add(new Violation(Coverage, $"reference to {name} has no WHERE clause in its secured scope"));
                continue;
            }

            if (topLevelOr)
            {
                violations.Add(new Violation(Coverage, $"reference to {name} has a top-level OR next to its predicate"));
            }

            if (name == "lookup")
            {
                if (!conjuncts.Any(j => Regex.IsMatch(j, @"\b1\s*=\s*0\b|\bFALSE\b", RegexOptions.IgnoreCase)))
                {
                    violations.Add(new Violation(Coverage, "a table without a tenant column is not denied"));
                }

                continue;
            }

            if (!conjuncts.Any(j => j.Contains("TenantId", StringComparison.OrdinalIgnoreCase) && tenantMarkers.Any(t => MarkerRegex(t).IsMatch(j))))
            {
                violations.Add(new Violation(Coverage, $"reference to {name} lacks a top-level tenant conjunct"));
            }

            if (name == "orders" && !conjuncts.Any(j => j.Contains("Region", StringComparison.OrdinalIgnoreCase) && policyMarkers.Any(t => MarkerRegex(t).IsMatch(j))))
            {
                violations.Add(new Violation(Coverage, "reference to orders lacks the policy conjunct"));
            }
        }

        // A dead CTE may legitimately disappear, so fewer references are fine; more than the query contains are not.
        if (c.Dml is null && occurrences > c.Frag.PhysicalRefs)
        {
            violations.Add(new Violation(Coverage, $"query has {c.Frag.PhysicalRefs} physical table references but the emitted text has {occurrences}"));
        }

        // A constant-false filter may fold a whole branch away, so the requirement follows the emitted references, not the input.
        if ((tenantOccurrences > 0 || c.Dml is not null) && tenantMarkers.Length == 0)
        {
            violations.Add(new Violation(Coverage, "an emitted reference to a tenant table has no tenant parameter"));
        }
    }

    /// <summary>End of the parenthesis scope that starts before <paramref name="from"/>: the first unmatched ')' or the end of the text.</summary>
    private static int ScopeEnd(string masked, int from)
    {
        int depth = 0;
        for (int i = from; i < masked.Length; i++)
        {
            if (masked[i] == '(')
            {
                depth++;
            }
            else if (masked[i] == ')' && --depth < 0)
            {
                return i;
            }
        }

        return masked.Length;
    }

    private static List<string> WhereConjuncts(string masked, string raw, int from, int to, out bool topLevelOr, out bool hasWhere)
    {
        topLevelOr = false;
        hasWhere = false;
        var result = new List<string>();
        int depth = 0;
        int whereAt = -1;
        for (int i = from; i < to && whereAt < 0; i++)
        {
            if (masked[i] == '(')
            {
                depth++;
            }
            else if (masked[i] == ')')
            {
                depth--;
            }
            else if (depth == 0 && IsWord(masked, i, "WHERE"))
            {
                whereAt = i + 5;
            }
        }

        if (whereAt < 0)
        {
            return result;
        }

        hasWhere = true;
        depth = 0;
        int start = whereAt;
        for (int i = whereAt; i <= to; i++)
        {
            if (i == to || depth == 0 && IsWord(masked, i, "AND"))
            {
                result.Add(raw[start..i]);
                start = i + 3;
                i += 2;
                continue;
            }

            if (masked[i] == '(')
            {
                depth++;
            }
            else if (masked[i] == ')')
            {
                depth--;
            }
            else if (depth == 0 && IsWord(masked, i, "OR"))
            {
                topLevelOr = true;
            }
        }

        return result;
    }

    private static bool IsWord(string s, int at, string word)
    {
        if (string.Compare(s, at, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        bool leftOk = at == 0 || !char.IsLetterOrDigit(s[at - 1]) && s[at - 1] != '_';
        int end = at + word.Length;
        bool rightOk = end >= s.Length || !char.IsLetterOrDigit(s[end]) && s[end] != '_';
        return leftOk && rightOk;
    }

    // ---- cross-compile oracles ----

    public static IReadOnlyList<Violation> CheckDeterminism(StmtCase c, CompileFn cold)
    {
        var violations = new List<Violation>();
        var (a, ea) = TryCompile(cold, c.Sql, c.Ctx, c.Permissions);
        var (b, eb) = TryCompile(cold, c.Sql, c.Ctx, c.Permissions);
        if ((ea is null) != (eb is null) || ea is not null && ea.GetType() != eb!.GetType())
        {
            violations.Add(new Violation(Determinism, $"outcome differs between two compiles ({ea?.GetType().Name ?? "ok"} vs {eb?.GetType().Name ?? "ok"})"));
        }
        else if (a is not null && !Same(a, b!))
        {
            violations.Add(new Violation(Determinism, "two compiles of the same input produce different SQL or parameters"));
        }

        return violations;
    }

    public static IReadOnlyList<Violation> CheckIdentity(StmtCase c, CompileFn compile)
    {
        var violations = new List<Violation>();
        var (a, ea) = TryCompile(compile, c.Sql, c.Ctx, c.Permissions);
        var (b, eb) = TryCompile(compile, c.AltSql, c.Ctx, c.Permissions);
        if ((ea is null) != (eb is null) || ea is not null && ea.GetType() != eb!.GetType())
        {
            violations.Add(new Violation(Identity, $"outcome depends on keyword case and whitespace ({ea?.GetType().Name ?? "ok"} vs {eb?.GetType().Name ?? "ok"})"));
        }
        else if (a is not null && !Same(a, b!))
        {
            violations.Add(new Violation(Identity, "the compiled SQL or parameters depend on keyword case and whitespace"));
        }

        return violations;
    }

    internal static bool Same(CompiledSql a, CompiledSql b) =>
        a.Sql == b.Sql && a.Dialect == b.Dialect && a.StatementClass == b.StatementClass &&
        a.Parameters.Length == b.Parameters.Length &&
        a.Parameters.Zip(b.Parameters).All(p => p.First.Marker == p.Second.Marker && p.First.Ordinal == p.Second.Ordinal &&
            Equals(p.First.Value, p.Second.Value) && p.First.Type == p.Second.Type && p.First.Origin == p.Second.Origin) &&
        a.AppliedPredicates.SequenceEqual(b.AppliedPredicates);

    private static string Short(string s) => s.Length <= 160 ? s : s[..160];
}
