using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

namespace Autheris.Benchmarks;

/// <summary>
/// G8 performance gate (plan 9.3): the governed AST compiler compiles a 200-query corpus. The legacy token-stream rewriter was deleted
/// with the cutover (X1); its allocation and Gen2 numbers are frozen in <c>benchmarks/baselines/sql-compiler-legacy.json</c>, which
/// <c>SqlGate.BenchCheck</c> uses as the reference when a result has no legacy sample. The corpus has three size buckets (0.5 KB, 4 KB and 64 KB). One operation compiles one query; the queries of a
/// bucket are cycled, so the reported time and allocation are per-query averages over the bucket.
/// <list type="bullet">
/// <item><description><see cref="Ast"/>: <c>Compile</c> on a fresh engine per operation, i.e. always a plan cache miss (the work the frozen legacy numbers include).</description></item>
/// <item><description><see cref="AstCached"/>: <c>Compile</c> on a warm engine (cache hit, informational, not gated).</description></item>
/// </list>
/// <c>SqlGate.BenchCheck</c> evaluates the exported JSON. Run: <c>dotnet run -c Release --project benchmarks/Autheris.Benchmarks -- --filter "*SqlCompiler*" --job short --exporters json --artifacts benchmarks/results</c>.
/// </summary>
[MemoryDiagnoser]
public class SqlCompilerBenchmarks
{
    private const string TenantValue = "acme";

    // 200 queries: 100 of about 0.5 KB, 70 of about 4 KB, 30 of about 64 KB.
    private static readonly IReadOnlyDictionary<string, (int Count, int TargetBytes)> BucketShape = new Dictionary<string, (int, int)>
    {
        ["0.5KB"] = (100, 512),
        ["4KB"] = (70, 4 * 1024),
        ["64KB"] = (30, 64 * 1024)
    };

    private string[] _queries = [];
    private int _next;
    private FastSqlEngine _warmEngine = null!;
    private CompileRequest _astRequest = null!;

    [Params("0.5KB", "4KB", "64KB")]
    public string Bucket { get; set; } = "0.5KB";

    [GlobalSetup]
    public void Setup()
    {
        var (count, target) = BucketShape[Bucket];
        _queries = BuildBucket(count, target);
        _warmEngine = new FastSqlEngine();
        _astRequest = AstRequest();

        // Fail loudly if the corpus is not accepted by the compiler: a benchmark of rejected statements would measure the error path.
        foreach (string sql in _queries)
        {
            _warmEngine.Compile(sql.AsMemory(), _astRequest, CancellationToken.None);
        }
    }

    [Benchmark]
    public CompiledSql Ast() => new FastSqlEngine().Compile(NextQuery().AsMemory(), _astRequest, CancellationToken.None);

    [Benchmark]
    public CompiledSql AstCached() => _warmEngine.Compile(NextQuery().AsMemory(), _astRequest, CancellationToken.None);

    private string NextQuery()
    {
        string sql = _queries[_next];
        _next = (_next + 1) % _queries.Length;
        return sql;
    }

    /// <summary>
    /// Deterministic corpus. Every query is a join over the two secured tables with a flat projection (no literal binds, so the 64 KB
    /// statements stay far below the bind budget) padded to the target size, with a different shape per index.
    /// </summary>
    internal static string[] BuildBucket(int count, int targetBytes)
    {
        var queries = new string[count];
        for (int i = 0; i < count; i++)
        {
            var sb = new StringBuilder(targetBytes + 256);
            sb.Append("SELECT o.id, o.status, o.amount AS total");
            int column = 0;
            while (sb.Length < targetBytes - 220)
            {
                sb.Append(CultureInfo.InvariantCulture, $", o.amount + e.orderid * {column % 7 + 1} AS c{column}");
                column++;
            }

            sb.Append(" FROM orders o JOIN entitlements e ON e.orderid = o.id");
            sb.Append((i % 3) switch
            {
                0 => " WHERE o.status = 'open'",
                1 => " WHERE o.amount > 10 AND e.orderid < 1000",
                _ => " WHERE o.status IN ('open', 'closed')"
            });

            queries[i] = sb.ToString();
        }

        return queries;
    }

    private sealed class NoPolicy : IPolicyPredicateProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => false;
        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;
    }

    private sealed class NoMasks : IColumnMaskProvider
    {
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException();
    }

    private static CompileRequest AstRequest()
    {
        var catalog = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(new TableIdentity("dbo", "Orders"), ImmutableArray.Create(
                new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("Status", "nvarchar(20)"), new CatalogColumn("Amount", "decimal(18,2)")), "TenantId", 1),
            new TableCatalogEntry(new TableIdentity("dbo", "Entitlements"), ImmutableArray.Create(
                new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("OrderId", "int")), "TenantId", 1)
        }, "dbo");

        return new CompileRequest
        {
            TargetDialect = TargetSqlDialect.SqlServer,
            TokenGuards = SqlTokenSecurityOptions.ForDialect(TargetSqlDialect.SqlServer),
            Policy = new GovernancePolicy
            {
                RowFilters = new NoPolicy(),
                Masks = new NoMasks(),
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", TenantValue, SqlParameterType.String)
            }
        };
    }
}
