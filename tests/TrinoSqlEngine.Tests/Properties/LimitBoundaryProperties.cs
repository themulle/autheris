using System.Text;
using FsCheck;
using FsCheck.Fluent;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using Xunit;
using Xunit.Abstractions;

namespace TrinoSqlEngine.Tests.Properties;

/// <summary>
/// G5-f / INV-7: the bind-parameter limit of every governed dialect is exact. A statement that needs exactly the dialect maximum
/// compiles; one more value is rejected with the typed <see cref="SqlLimitExceededException"/> before any text is returned.
/// The shape (chunk size of the IN lists, literal kind) is generated; the boundary itself is computed from the capability table,
/// not hard-coded, so a changed limit cannot silently move the boundary out of the test.
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class LimitBoundaryProperties(ITestOutputHelper output)
{
    /// <summary>Compile function of the limit properties: the query-length guard is raised so that the bind limit is the binding one.</summary>
    internal static CompileFn Production { get; } = (sql, ctx, permissions) =>
        // The compile budget is raised to its maximum so that a loaded CI machine cannot turn a boundary probe of 65,535 values into a
        // CompileTime rejection (a flaky security test would fail the gate, M-5).
        new FastSqlEngine { MaxQueryLength = int.MaxValue }.Compile(
            sql.AsMemory(), ctx.Request(permissions) with { CompileTimeout = CompileLimits.MaxCompileTimeout }, CancellationToken.None);

    /// <summary>
    /// <paramref name="ChunkChoice"/> is the generated preference; the effective chunk keeps the OR chain below the engine's AST
    /// nesting guard (64) and an IN list below the dialect's IN-list limit.
    /// </summary>
    internal sealed record Shape(TargetSqlDialect Dialect, int ChunkChoice, bool Strings)
    {
        public int ChunkSize
        {
            get
            {
                var caps = DialectCapabilityTable.Default.Get(Dialect);
                int min = (caps.MaxBindParameters + 49) / 50;
                return Math.Clamp(ChunkChoice, min, caps.MaxInListItems ?? int.MaxValue);
            }
        }
    }

    private static Gen<Shape> ShapeGen() =>
        from d in Gen.Elements(CaseCtx.Governed)
        from chunk in Gen.Elements(1, 100, 700, 999, 1000, 5000, 40_000)
        from strings in Gen.Elements(false, true)
        select new Shape(d, chunk, strings);

    private static string Query(Shape shape, int literals)
    {
        var sb = new StringBuilder("SELECT id FROM orders WHERE ");
        for (int i = 0; i < literals; i += shape.ChunkSize)
        {
            if (i > 0)
            {
                sb.Append(" OR ");
            }

            sb.Append(shape.Strings ? "status IN (" : "id IN (");
            int end = Math.Min(literals, i + shape.ChunkSize);
            for (int j = i; j < end; j++)
            {
                sb.Append(j > i ? "," : string.Empty).Append(shape.Strings ? $"'v{j}'" : (j + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            sb.Append(')');
        }

        return sb.ToString();
    }

    private static int FixedParameters(Shape shape, CaseCtx ctx, CompileFn compile)
    {
        var one = compile(Query(shape, 1), ctx, StatementPermissions.ReadOnly);
        return one.Parameters.Length - 1;
    }

    private static string Describe(Exception? e) =>
        e is SqlLimitExceededException l ? $"{l.Kind} requested {l.Requested} maximum {l.Maximum}" : e?.GetType().Name ?? "none";

    internal static IEnumerable<Violation> Check(Shape shape, CompileFn compile)
    {
        var ctx = new CaseCtx(shape.Dialect, true, "TNT1_acme", "POL1_eu");
        int max = DialectCapabilityTable.Default.Get(shape.Dialect).MaxBindParameters;
        int fixedCount = FixedParameters(shape, ctx, compile);

        var (atMax, errorAtMax) = Oracles.TryCompile(compile, Query(shape, max - fixedCount), ctx, StatementPermissions.ReadOnly);
        if (atMax is null)
        {
            yield return new Violation(Oracles.Limits, $"{shape.Dialect}: a statement with exactly {max} parameters was rejected ({Describe(errorAtMax)})");
        }
        else if (atMax.Parameters.Length != max)
        {
            yield return new Violation(Oracles.Limits, $"{shape.Dialect}: expected {max} parameters, got {atMax.Parameters.Length}");
        }

        var (overMax, errorOverMax) = Oracles.TryCompile(compile, Query(shape, max - fixedCount + 1), ctx, StatementPermissions.ReadOnly);
        if (overMax is not null)
        {
            yield return new Violation(Oracles.Limits, $"{shape.Dialect}: a statement with {max + 1} parameters compiled");
        }
        else if (errorOverMax is not SqlLimitExceededException { Kind: SqlLimitKind.BindParameters } limit || limit.Maximum != max || limit.Requested != max + 1)
        {
            yield return new Violation(Oracles.Limits, $"{shape.Dialect}: max+1 was not rejected with the typed BindParameters limit ({Describe(errorOverMax)})");
        }
    }

    [Fact]
    public void BindLimit_IsExact_ForEveryGovernedDialect()
    {
        var failures = new List<string>();
        foreach (var seed in GateProfile.AllSeeds())
        {
            // Three shapes per seed: bounded, because one boundary probe compiles up to 65,536 values.
            foreach (var shape in GateProfile.Sample(ShapeGen(), seed, 3))
            {
                foreach (var v in Check(shape, Production))
                {
                    failures.Add($"seed={seed} {shape}: {v.Message}");
                }
            }
        }

        // Every governed dialect is probed at least once with a fixed shape, independent of the generator.
        foreach (var dialect in CaseCtx.Governed)
        {
            foreach (var v in Check(new Shape(dialect, 1000, false), Production))
            {
                failures.Add($"fixed {dialect}: {v.Message}");
            }
        }

        output.WriteLine($"failures={failures.Count}");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(10)));
    }

    [Fact]
    public void OracleInList_IsExact_At1000()
    {
        var ctx = new CaseCtx(TargetSqlDialect.Oracle, true, "TNT1_acme", "POL1_eu");
        string Sql(int n) => "SELECT id FROM orders WHERE id IN (" + string.Join(",", Enumerable.Range(1, n)) + ")";
        Production(Sql(1000), ctx, StatementPermissions.ReadOnly);
        var ex = Assert.Throws<SqlLimitExceededException>(() => Production(Sql(1001), ctx, StatementPermissions.ReadOnly));
        Assert.Equal(SqlLimitKind.InListItems, ex.Kind);
    }
}
