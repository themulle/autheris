using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

namespace Autheris.Benchmarks;

/// <summary>
/// NFR-2 gate for the governed AST compile path (CR-ADG-12): a cache hit (template rebind plus per-table dependency validation)
/// and a cache miss (parse, build, simplify, inject, verify, emit). The thresholds are generous regression guards, not targets;
/// run with <c>dotnet run -c Release -- compile</c>.
/// </summary>
public static class CompilePathBenchmark
{
    private const string Sql = "SELECT o.id, o.status, sum(o.amount) AS total FROM orders o JOIN entitlements e ON e.orderid = o.id WHERE o.status = 'open' GROUP BY o.id, o.status ORDER BY total DESC";
    private const double MaxHitMicroseconds = 250;
    private const double MaxMissMilliseconds = 10;

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

    private static CompileRequest Request(string tenant)
    {
        var orders = new TableIdentity("dbo", "Orders");
        var entitlements = new TableIdentity("dbo", "Entitlements");
        var catalog = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(orders, ImmutableArray.Create(
                new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("Status", "nvarchar(20)"), new CatalogColumn("Amount", "decimal(18,2)")), "TenantId", 1),
            new TableCatalogEntry(entitlements, ImmutableArray.Create(
                new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("OrderId", "int")), "TenantId", 1)
        }, "dbo");

        return new CompileRequest
        {
            TargetDialect = TargetSqlDialect.SqlServer,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = new NoPolicy(),
                Masks = new NoMasks(),
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
            }
        };
    }

    public static void Run()
    {
        Console.WriteLine("--- [Benchmark] Governed AST compile path (CR-ADG-12, NFR-2) ---");
        var engine = new FastSqlEngine();
        var request = Request("acme");

        // Warm-up: first compile fills the cache, the rest are hits.
        for (int i = 0; i < 2_000; i++) engine.Compile(Sql.AsMemory(), request, CancellationToken.None);

        const int hits = 50_000;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < hits; i++) engine.Compile(Sql.AsMemory(), request, CancellationToken.None);
        double hitMicroseconds = clock.Elapsed.TotalMilliseconds * 1000 / hits;

        const int misses = 500;
        clock.Restart();
        for (int i = 0; i < misses; i++)
        {
            // A distinct statement text per iteration is a distinct cache key: every compile is a miss.
            engine.Compile(($"SELECT id FROM orders WHERE id = {i}").AsMemory(), request, CancellationToken.None);
        }

        double missMilliseconds = clock.Elapsed.TotalMilliseconds / misses;

        Console.WriteLine($"cache hit : {hitMicroseconds:F1} us per compile (limit {MaxHitMicroseconds} us)");
        Console.WriteLine($"cache miss: {missMilliseconds:F2} ms per compile (limit {MaxMissMilliseconds} ms)");
        if (hitMicroseconds > MaxHitMicroseconds || missMilliseconds > MaxMissMilliseconds)
        {
            Console.Error.WriteLine("The governed compile path exceeds its NFR-2 regression limit.");
            Environment.ExitCode = 1;
        }
    }
}
