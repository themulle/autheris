namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

public sealed class AstCompilerBindLimitTests
{
    private static readonly TableIdentity Orders = new("main", "Orders");

    private static FastSqlEngine UnbudgetedEngine() => new()
    {
        ParseTimeout = TimeSpan.Zero,
        ParseConcurrencyLimiter = new SemaphoreSlim(1, 1)
    };

    private static CompileRequest Request(string tenant)
    {
        var catalog = new InMemoryTableCatalog(
            [
                new TableCatalogEntry(
                    Orders,
                    [new CatalogColumn("Id", "INTEGER"), new CatalogColumn("TenantId", "VARCHAR")],
                    "TenantId",
                    1)
            ],
            "main");
        var policies = new NoOpPolicies();
        return new()
        {
            TargetDialect = TargetSqlDialect.DuckDb,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = policies,
                Masks = policies,
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
            }
        };
    }

    [Fact]
    public void BindLimit_OverTheTable_IsATypedRejection()
    {
        const int limit = 50;
        var compiler = new GovernedSqlCompiler(UnbudgetedEngine(), new ReducedBindLimit(limit), new CompiledSqlTemplateCache());
        string Sql(int values) => "SELECT id FROM orders WHERE id IN (" + string.Join(",", Enumerable.Range(1, values)) + ")";
        var request = Request("acme") with { CompileTimeout = CompileLimits.MaxCompileTimeout };

        compiler.Compile(Sql(10).AsMemory(), request, CancellationToken.None).Parameters.Length.ShouldBeLessThanOrEqualTo(limit);

        var ex = Should.Throw<SqlLimitExceededException>(() => compiler.Compile(Sql(limit + 1).AsMemory(), request, CancellationToken.None));
        ex.Kind.ShouldBe(SqlLimitKind.BindParameters);
        ex.Maximum.ShouldBe(limit);
    }

    [Fact]
    public void BindLimit_AValueListBelowTheProductionLimit_Compiles()
    {
        string sql = "SELECT id FROM orders WHERE id IN (" + string.Join(",", Enumerable.Range(1, 2200)) + ")";
        var request = Request("acme") with { CompileTimeout = CompileLimits.MaxCompileTimeout };

        UnbudgetedEngine().Compile(sql.AsMemory(), request, CancellationToken.None).Parameters.Length.ShouldBeGreaterThan(2200);
    }

    private sealed class NoOpPolicies : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => false;
        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) => default!;
    }

    private sealed class ReducedBindLimit(int maxBindParameters) : IDialectCapabilityProvider
    {
        public DialectCapabilities Get(TargetSqlDialect dialect) =>
            DialectCapabilityTable.Default.Get(dialect) with { MaxBindParameters = maxBindParameters };
    }
}
