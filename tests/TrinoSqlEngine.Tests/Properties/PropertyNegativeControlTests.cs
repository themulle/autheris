using System.Collections.Immutable;
using System.Text.RegularExpressions;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

namespace TrinoSqlEngine.Tests.Properties;

/// <summary>
/// Negative controls of the property gate. A property that cannot fail proves nothing, so each oracle is run against a
/// deliberately broken compiler and must report a violation under the same pinned seeds. The broken compilers wrap the real one
/// and damage its output the way a regression in the named stage would (plan 16.5 M-3). The real compiler passes the same
/// oracles in <see cref="CompilerInvariantPropertyTests"/>.
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
[Trait("Category", "Metamorphic")]
[Trait("Category", "NightlyGate")]
public class PropertyNegativeControlTests
{
    private const int Cases = 2_000;
    private static readonly ulong[] Seeds = [GateProfile.PinnedSeeds[0], GateProfile.PinnedSeeds[1]];

    private static CompileFn Break(Func<string, CaseCtx, CompiledSql, CompiledSql> damage) =>
        (sql, ctx, permissions) => damage(sql, ctx, ProductionCompiler.Compile(sql, ctx, permissions));

    private static RunResult RunOracles(CompileFn broken) =>
        PropertyRunner.Run(StmtCase.Gen(), Cases, c => Oracles.CheckCase(c, broken), Seeds);

    private static void MustViolate(RunResult result, string oracle)
    {
        Assert.True(result.ViolatedOracles.Contains(oracle),
            $"oracle '{oracle}' did not fail on the broken compiler; it reported: [{string.Join("; ", result.ViolatedOracles)}] over {result.Compiled} compiled cases");
    }

    private static string TenantMarker(CompiledSql c) => c.Parameters.FirstOrDefault(p => p.Origin == ParameterOrigin.Tenant).Marker;

    private static string Inline(string text, string marker, string value) =>
        Regex.Replace(text, "(?<![0-9A-Za-z_])" + Regex.Escape(marker) + "(?![0-9A-Za-z_])", "'" + value.Replace("'", "''") + "'");

    [Fact]
    public void DroppedTenantPredicate_IsDetected()
    {
        // Mutant: the tenant comparison is replaced by NULL in every scope.
        var broken = Break((_, _, c) => TenantMarker(c) is { Length: > 0 } m
            ? c with { Sql = Regex.Replace(c.Sql, "(?<![0-9A-Za-z_])" + Regex.Escape(m) + "(?![0-9A-Za-z_])", "NULL") }
            : c);
        MustViolate(RunOracles(broken), Oracles.Coverage);
    }

    [Fact]
    public void OnlyTheFirstReferenceSecured_IsDetected()
    {
        // Mutant: the injector secures the first table reference and forgets the later ones (a join, a subquery, a CTE body).
        var broken = Break((_, ctx, c) =>
        {
            if (TenantMarker(c) is not { Length: > 0 } m || Oracles.Mask(c.Sql, ctx.Dialect) is not { } masked)
            {
                return c;
            }

            var spans = masked.Spans.Where((s, i) => i + 1 < masked.Spans.Count && s.Content.Equals("dbo", StringComparison.OrdinalIgnoreCase)).ToList();
            if (spans.Count < 2)
            {
                return c;
            }

            string head = c.Sql[..spans[1].Start];
            string tail = c.Sql[spans[1].Start..];
            return c with { Sql = head + Regex.Replace(tail, "(?<![0-9A-Za-z_])" + Regex.Escape(m) + "(?![0-9A-Za-z_])", "NULL") };
        });
        MustViolate(RunOracles(broken), Oracles.Coverage);
    }

    [Fact]
    public void UserPredicateEscapingTheTenantConjunct_IsDetected()
    {
        // Mutant: the conjunction of the injected predicates is joined with OR.
        var broken = Break((_, _, c) => c with { Sql = c.Sql.Replace(") AND (", ") OR (", StringComparison.Ordinal) });
        MustViolate(RunOracles(broken), Oracles.Coverage);
    }

    [Fact]
    public void TableWithoutTenantColumnNotDenied_IsDetected()
    {
        var broken = Break((_, _, c) => c with { Sql = c.Sql.Replace("1 = 0", "1 = 1", StringComparison.Ordinal) });
        MustViolate(RunOracles(broken), Oracles.Coverage);
    }

    [Fact]
    public void InlinedQueryLiteral_IsDetected()
    {
        // Mutant: the first string literal is written into the SQL text instead of being bound.
        var broken = Break((_, _, c) =>
        {
            var p = c.Parameters.FirstOrDefault(x => x.Origin == ParameterOrigin.QueryLiteral && x.Value is string);
            return p.Marker is null ? c : c with { Sql = Inline(c.Sql, p.Marker, (string)p.Value!) };
        });
        MustViolate(RunOracles(broken), Oracles.NoLiteral);
    }

    [Fact]
    public void InlinedPolicyValue_IsDetected()
    {
        var broken = Break((_, _, c) =>
        {
            var p = c.Parameters.FirstOrDefault(x => x.Origin == ParameterOrigin.Policy);
            return p.Marker is null ? c : c with { Sql = Inline(c.Sql, p.Marker, (string)p.Value!) };
        });
        MustViolate(RunOracles(broken), Oracles.NoLiteral);
    }

    [Fact]
    public void InlinedTenantValue_IsDetected()
    {
        var broken = Break((_, _, c) => TenantMarker(c) is { Length: > 0 } m ? c with { Sql = Inline(c.Sql, m, c.Parameters.First(p => p.Marker == m).Value!.ToString()!) } : c);
        MustViolate(RunOracles(broken), Oracles.NoLiteral);
    }

    [Fact]
    public void CommentInjectedIntoTheText_IsDetected()
    {
        var broken = Break((_, _, c) => c with { Sql = c.Sql + " -- trailing" });
        MustViolate(RunOracles(broken), Oracles.NoLiteral);
    }

    [Fact]
    public void TruncatedPolicyValue_IsDetected()
    {
        var broken = Break((_, _, c) => c with
        {
            Parameters = c.Parameters.Select(p => p.Origin == ParameterOrigin.Policy ? p with { Value = ((string)p.Value!)[..^1] } : p).ToImmutableArray()
        });
        MustViolate(RunOracles(broken), Oracles.BoundValues);
    }

    [Fact]
    public void WrongTenantValueBound_IsDetected()
    {
        var broken = Break((_, _, c) => c with
        {
            Parameters = c.Parameters.Select(p => p.Origin == ParameterOrigin.Tenant ? p with { Value = "someone-else" } : p).ToImmutableArray()
        });
        MustViolate(RunOracles(broken), Oracles.BoundValues);
    }

    [Fact]
    public void CorruptedQueryLiteral_IsDetected()
    {
        var broken = Break((_, _, c) => c with
        {
            Parameters = c.Parameters.Select(p => p.Origin == ParameterOrigin.QueryLiteral && p.Value is string s ? p with { Value = s + "x" } : p).ToImmutableArray()
        });
        MustViolate(RunOracles(broken), Oracles.BoundValues);
    }

    [Fact]
    public void OrphanParameter_IsDetected()
    {
        var broken = Break((_, _, c) => c with { Parameters = c.Parameters.Add(c.Parameters[0] with { Marker = "@@orphan", Name = "orphan", Ordinal = 99 }) });
        MustViolate(RunOracles(broken), Oracles.BindAccounting);
    }

    [Fact]
    public void UnknownFunctionAccepted_IsDetected()
    {
        // Mutant: the function allowlist is bypassed by renaming denied functions to an allowed one before compiling.
        CompileFn broken = (sql, ctx, permissions) =>
        {
            foreach (var name in new[] { "pg_read_file", "xp_cmdshell", "load_extension", "sleep_ms", "utl_http_request" })
            {
                sql = sql.Replace(name, "lower", StringComparison.OrdinalIgnoreCase);
            }

            return ProductionCompiler.Compile(sql, ctx, permissions);
        };
        MustViolate(RunOracles(broken), Oracles.FailClosed);
    }

    [Fact]
    public void UnhandledExceptionType_IsDetected()
    {
        CompileFn broken = (sql, ctx, permissions) =>
            sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase) ? throw new InvalidOperationException("unexpected") : ProductionCompiler.Compile(sql, ctx, permissions);
        MustViolate(RunOracles(broken), Oracles.FailClosed);
    }

    [Fact]
    public void UngovernedDialectAccepted_IsDetected()
    {
        // Mutant: an unsupported dialect is silently compiled as PostgreSQL.
        CompileFn broken = (sql, ctx, permissions) =>
            ProductionCompiler.Compile(sql, CaseCtx.Governed.Contains(ctx.Dialect) ? ctx : ctx with { Dialect = TargetSqlDialect.PostgreSql }, permissions);
        MustViolate(RunOracles(broken), Oracles.FailClosed);
    }

    [Fact]
    public void DmlGuardsSwallowed_AreDetected()
    {
        // Mutant: a typed DML rejection (unfiltered DELETE, foreign tenant INSERT, tenant column UPDATE) is turned into success.
        CompileFn broken = (sql, ctx, permissions) =>
        {
            try
            {
                return ProductionCompiler.Compile(sql, ctx, permissions);
            }
            catch (System.Security.SecurityException) when (permissions != StatementPermissions.ReadOnly)
            {
                var cls = sql.TrimStart().Split(' ', '\n', '\t', '\r')[0].ToUpperInvariant() switch
                {
                    "DELETE" => SqlStatementClass.Delete,
                    "UPDATE" => SqlStatementClass.Update,
                    "MERGE" => SqlStatementClass.Merge,
                    _ => SqlStatementClass.Insert
                };
                return new CompiledSql("SELECT 1", ImmutableArray<BoundParameter>.Empty, ctx.Dialect, cls, ImmutableArray<SecurityPredicateId>.Empty, CompilerInfo.Version);
            }
        };
        MustViolate(RunOracles(broken), Oracles.Dml);
    }

    [Fact]
    public void NondeterministicOutput_IsDetected()
    {
        int calls = 0;
        CompileFn broken = (sql, ctx, permissions) => ProductionCompiler.CompileCold(sql, ctx, permissions) is var c && Interlocked.Increment(ref calls) % 2 == 0 ? c with { Sql = c.Sql + " " } : c;
        var result = PropertyRunner.Run(StmtCase.Gen(), 500, c => (c.Sql, c.Ctx, Oracles.CheckDeterminism(c, broken), null, null), Seeds);
        MustViolate(result, Oracles.Determinism);
    }

    [Fact]
    public void FormattingDependentOutput_IsDetected()
    {
        CompileFn broken = (sql, ctx, permissions) =>
        {
            var c = ProductionCompiler.Compile(sql, ctx, permissions);
            return sql.Contains('\t') ? c with { Sql = c.Sql + " " } : c;
        };
        var result = PropertyRunner.Run(StmtCase.Gen(), 500, c => (c.Sql, c.Ctx, Oracles.CheckIdentity(c, broken), null, null), Seeds);
        MustViolate(result, Oracles.Identity);
    }

    [Theory]
    [InlineData("DISTINCT ", "")]
    [InlineData(" AND ", " OR ")]
    [InlineData("NOT ", "")]
    [InlineData(" LEFT JOIN ", " INNER JOIN ")]
    [InlineData(" UNION ALL ", " UNION ")]
    public void RendererThatChangesTheTree_IsDetectedByTheRoundTrip(string find, string replace)
    {
        // Mutant: the printer drops or changes one construct, as a builder that loses structure would.
        int detected = 0;
        PropertyRunner.Run(StmtCase.Gen(), 1_500, c =>
        {
            var (violations, _, _) = RoundTrip.Check(c, s => TrinoSourceRenderer.Render(s).Replace(find, replace, StringComparison.Ordinal));
            detected += violations.Count;
            return (c.Sql, c.Ctx, violations, null, null);
        }, Seeds);
        Assert.True(detected > 0, $"the round trip did not notice the change '{find}' -> '{replace}'");
    }

    [Fact]
    public void BindLimitNotEnforced_IsDetected()
    {
        // Mutant: the typed limit rejection is swallowed and an empty statement returned.
        CompileFn broken = (sql, ctx, permissions) =>
        {
            try
            {
                return LimitBoundaryProperties.Production(sql, ctx, permissions);
            }
            catch (SqlLimitExceededException)
            {
                return new CompiledSql("SELECT 1", ImmutableArray<BoundParameter>.Empty, ctx.Dialect, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, CompilerInfo.Version);
            }
        };
        var violations = LimitBoundaryProperties.Check(new LimitBoundaryProperties.Shape(TargetSqlDialect.SqlServer, 100, false), broken).ToList();
        Assert.Contains(violations, v => v.Oracle == Oracles.Limits);
    }

    [Fact]
    public void BindLimitOffByOne_IsDetected()
    {
        // Control: the real compiler passes the exact boundary check...
        Assert.Empty(LimitBoundaryProperties.Check(new LimitBoundaryProperties.Shape(TargetSqlDialect.SqlServer, 100, false), LimitBoundaryProperties.Production));

        // ...and a compiler whose limit is one too high (max + 1 compiles) fails it.
        CompileFn lenient = (sql, ctx, permissions) =>
        {
            try
            {
                return LimitBoundaryProperties.Production(sql, ctx, permissions);
            }
            catch (SqlLimitExceededException e) when (e.Requested == e.Maximum + 1)
            {
                return new CompiledSql("SELECT 1", ImmutableArray<BoundParameter>.Empty, ctx.Dialect, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, CompilerInfo.Version);
            }
        };
        Assert.NotEmpty(LimitBoundaryProperties.Check(new LimitBoundaryProperties.Shape(TargetSqlDialect.SqlServer, 100, false), lenient));
    }
}
