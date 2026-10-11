using System.Collections.Frozen;
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
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Properties;

/// <summary>Request-level generator input: dialect, token guards and the three hostile values (tenant, policy) of one compile.</summary>
internal sealed record CaseCtx(TargetSqlDialect Dialect, bool StrictGuards, string TenantValue, string PolicyValue)
{
    public static readonly TargetSqlDialect[] Governed =
        [TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql, TargetSqlDialect.DuckDb, TargetSqlDialect.Databricks, TargetSqlDialect.Oracle];

    public static Gen<CaseCtx> Gen() =>
        from dialect in FsCheck.Fluent.Gen.Frequency(
            (10, FsCheck.Fluent.Gen.Elements(Governed)),
            (1, FsCheck.Fluent.Gen.Elements(TargetSqlDialect.Sqlite, TargetSqlDialect.Ansi, TargetSqlDialect.Snowflake)))
        from strict in FsCheck.Fluent.Gen.Elements(true, true, false)
        from tn in FsCheck.Fluent.Gen.Choose(0, 99999)
        from th in FsCheck.Fluent.Gen.Choose(0, QueryGen.Hostile.Length - 1)
        from pn in FsCheck.Fluent.Gen.Choose(0, 99999)
        from ph in FsCheck.Fluent.Gen.Choose(0, QueryGen.Hostile.Length - 1)
        // Oracle treats an empty tenant string as NULL, so the tenant value is never empty (a unique prefix guarantees it).
        select new CaseCtx(dialect, strict, $"TNT{tn}_" + QueryGen.Hostile[th], $"POL{pn}_" + QueryGen.Hostile[ph]);

    public CompileRequest Request(StatementPermissions statements = StatementPermissions.ReadOnly)
    {
        var rowFilters = new DictPolicyProvider();
        rowFilters.Predicates[Orders] = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
                BinaryOperator.Equal,
                new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__pol_region"] = new(PolicyValue, SqlParameterType.String) });
        rowFilters.NoPolicy.Add(Entitlements);

        return new CompileRequest
        {
            TargetDialect = Dialect,
            TokenGuards = StrictGuards ? SqlTokenSecurityOptions.Strict : SqlTokenSecurityOptions.None,
            AllowExperimentalDialect = true,
            Statements = statements,
            Policy = new GovernancePolicy
            {
                RowFilters = rowFilters,
                Masks = new DictMaskProvider(),
                Catalog = Catalog(),
                Tenant = new TenantBinding("__autheris_tenant", TenantValue, SqlParameterType.String)
            }
        };
    }
}

/// <summary>The compile function under test. The negative controls pass broken implementations through the same oracles.</summary>
internal delegate CompiledSql CompileFn(string sql, CaseCtx ctx, StatementPermissions statements);

internal static class ProductionCompiler
{
    private static readonly FastSqlEngine SharedEngine = new();

    public static CompiledSql Compile(string sql, CaseCtx ctx, StatementPermissions statements) =>
        SharedEngine.Compile(sql.AsMemory(), ctx.Request(statements), CancellationToken.None);

    /// <summary>A fresh engine: no plan-cache entry can hide a nondeterministic pipeline.</summary>
    public static CompiledSql CompileCold(string sql, CaseCtx ctx, StatementPermissions statements) =>
        new FastSqlEngine().Compile(sql.AsMemory(), ctx.Request(statements), CancellationToken.None);
}

internal sealed record Violation(string Oracle, string Message);

internal sealed record Failure(ulong Seed, int Index, string Sql, CaseCtx Ctx, IReadOnlyList<Violation> Violations)
{
    public override string ToString() =>
        $"seed={Seed} index={Index} dialect={Ctx.Dialect} strict={Ctx.StrictGuards}\n  sql: {Truncate(Sql)}\n" +
        string.Join("\n", Violations.Select(v => $"  [{v.Oracle}] {v.Message}"));

    private static string Truncate(string s) => s.Length <= 600 ? s : s[..600] + $"... ({s.Length} chars)";
}

internal sealed class RunResult
{
    public int Cases { get; set; }
    public int Compiled { get; set; }
    public int Rejected { get; set; }
    public List<Failure> Failures { get; } = [];
    public Dictionary<string, int> RejectionTypes { get; } = [];

    public IEnumerable<string> ViolatedOracles => Failures.SelectMany(f => f.Violations).Select(v => v.Oracle).Distinct();

    /// <summary>Throws with the seed, the case index and every violation. Never retries (M-5).</summary>
    public void ThrowIfFailed(string property)
    {
        if (Failures.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Property '{property}' failed in {Failures.Count} of {Cases} cases (profile {GateProfile.Name}). Replay with SQLGATE_SEED=<seed> for the random seed; pinned seeds are fixed.");
        foreach (var f in Failures.Take(5))
        {
            sb.AppendLine(f.ToString());
        }

        throw new Xunit.Sdk.XunitException(sb.ToString());
    }
}

/// <summary>Runs the generated cases of every seed against an oracle. No retry, no shrinking: the failure carries seed and index.</summary>
internal static class PropertyRunner
{
    public static RunResult Run<T>(
        Gen<T> gen,
        int totalCases,
        Func<T, (string Sql, CaseCtx Ctx, IReadOnlyList<Violation> Violations, bool? Compiled, string? Rejection)> check,
        IEnumerable<ulong>? seeds = null,
        int maxSize = 8,
        int maxFailures = 25)
    {
        var result = new RunResult();
        int perSeed = GateProfile.CasesPerSeed(totalCases);
        foreach (var seed in seeds ?? GateProfile.AllSeeds())
        {
            int index = 0;
            foreach (var item in GateProfile.Sample(gen, seed, perSeed, maxSize))
            {
                var (sql, ctx, violations, compiled, rejection) = check(item);
                result.Cases++;
                if (compiled == true)
                {
                    result.Compiled++;
                }
                else if (compiled == false)
                {
                    result.Rejected++;
                    if (rejection is not null)
                    {
                        result.RejectionTypes[rejection] = result.RejectionTypes.GetValueOrDefault(rejection) + 1;
                    }
                }

                if (violations.Count > 0 && result.Failures.Count < maxFailures)
                {
                    result.Failures.Add(new Failure(seed, index, sql, ctx, violations));
                }

                index++;
            }
        }

        return result;
    }
}
