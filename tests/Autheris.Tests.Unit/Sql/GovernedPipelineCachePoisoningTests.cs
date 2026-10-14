namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// M-8 (SEC-ADG-01, WP-X1): the engine's value-free template cache is consulted by every governed compile. A cache hit rebinds the
/// values of the current request and revalidates the table dependencies; it never serves another principal's, tenant's or catalog
/// version's compilation. All tests share one engine instance, so each compile can hit what the previous one inserted.
/// </summary>
public sealed class GovernedPipelineCachePoisoningTests
{
    private const string Tenant = "acme";
    private static readonly TableIdentifier OrdersId = new("default", "public", "orders");

    private readonly FastSqlEngine _engine = new();

    private static TableMetadata Orders(params string[] columns) => new()
    {
        Identifier = OrdersId,
        Table = new Table { TableName = "orders", SchemaName = "public", SourceType = "PostgreSQL" },
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = c == "id" ? "int" : "varchar" }).ToList()
    };

    private static ClaimsPrincipal User(string sid) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, sid), new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Writer")], "Test"));

    private static Consent RegionConsent(string sid, string region, TableMetadata table) => new()
    {
        TableIdentifier = table.Identifier,
        TenantId = new TenantId(Tenant),
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(sid),
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        RowFilters = [new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = $"\"{region}\"" }],
        ColumnRules = table.Columns.Select(c => new ConsentColumnRule { ColumnName = c.ColumnName, AccessLevel = ColumnAccessLevel.Clear }).ToList()
    };

    private GovernedSqlExecutionService CreateService(
        Func<TableMetadata> table, Func<IReadOnlyList<Consent>>? consents = null, bool allowDml = false, ISqlEngine? engine = null)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(OrdersId.Equals(ci.Arg<TableIdentifier>()) ? table() : null));
        var consentRepository = Substitute.For<IConsentRepository>();
        consentRepository.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var subjects = ci.Arg<IEnumerable<Sid>>().ToHashSet();
                return Task.FromResult<IReadOnlyList<Consent>>((consents?.Invoke() ?? []).Where(c => c.GranteeSid is { } g && subjects.Contains(g)).ToList());
            });
        IConsentResolutionService resolution;
        if (consents is null)
        {
            resolution = Substitute.For<IConsentResolutionService>();
            resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
                .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true));
        }
        else
        {
            resolution = new ConsentResolutionService();
        }

        return global::Autheris.Tests.Unit.TestDoubles.GovernedSqlExecutionServiceFactory.Create(
            Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, AllowDml = allowDml, DmlWriterRoles = ["Writer"], DefaultMaxRows = 100, MaxAllowedRows = 500 } }), global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, executionGuard: GovernedTestSupport.Guard,
            consentResolution: resolution,
            tableRepository: repo, policyFactory: GovernedTestSupport.PolicyFactory(repo), typedPolicies: GovernedTestSupport.TypedPolicies(),
            sqlEngine: engine ?? _engine,
            logger: NullLogger<GovernedSqlExecutionService>.Instance, consentRepository: consentRepository, policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance);
    }

    [Fact]
    public async Task CachePoisoning_TwoPrincipalsWithDifferentConsent_GetTheirOwnBoundValues()
    {
        var table = Orders("id", "region", "tenant_id");
        var service = CreateService(() => table, () => [RegionConsent("S-A", "EU", table), RegionConsent("S-B", "US", table)]);
        const string Sql = "SELECT id FROM orders";

        var a1 = await service.CompileAsync(Sql, User("S-A"), new TenantId(Tenant));
        var b = await service.CompileAsync(Sql, User("S-B"), new TenantId(Tenant));   // same text, same shape, other consent values
        var a2 = await service.CompileAsync(Sql, User("S-A"), new TenantId(Tenant));  // and back: a hit that must rebind

        _engine.CompileCache.Stats.Misses.ShouldBe(1);   // only the first compile built a template
        _engine.CompileCache.Stats.Hits.ShouldBe(2);     // the other principal and the way back were served from it, and rebound their own values
        a1.ValuesOf(ParameterOrigin.Policy).ShouldBe(["EU"]);
        b.ValuesOf(ParameterOrigin.Policy).ShouldBe(["US"]);
        a2.ValuesOf(ParameterOrigin.Policy).ShouldBe(["EU"]);
        a1.Sql.ShouldBe(b.Sql);   // one value-free template: the text does not carry the consent value, so the values alone differ
    }

    [Fact]
    public async Task CachePoisoning_TenantCaseVariants_AreBoundExactly()
    {
        var table = Orders("id", "tenant_id");
        var service = CreateService(() => table);
        const string Sql = "SELECT id FROM orders";

        var lower = await service.CompileAsync(Sql, User("S-A"), new TenantId("acme"));
        var upper = await service.CompileAsync(Sql, User("S-A"), new TenantId("ACME"));
        var lowerAgain = await service.CompileAsync(Sql, User("S-A"), new TenantId("acme"));

        _engine.CompileCache.Stats.Misses.ShouldBe(1);
        _engine.CompileCache.Stats.Hits.ShouldBe(2);                  // the variants share one template; the tenant is bound per request
        lower.ValuesOf(ParameterOrigin.Tenant).ShouldBe(["acme"]);
        upper.ValuesOf(ParameterOrigin.Tenant).ShouldBe(["ACME"]);   // 'ACME' never receives the cached 'acme'
        lowerAgain.ValuesOf(ParameterOrigin.Tenant).ShouldBe(["acme"]);
    }

    [Fact]
    public async Task CachePoisoning_CatalogVersionChange_IsNotServedFromTheOldTemplate()
    {
        var current = Orders("id", "tenant_id");
        var service = CreateService(() => current);
        const string Sql = "SELECT * FROM orders";

        var before = await service.CompileAsync(Sql, User("S-A"), new TenantId(Tenant));
        var control = await service.CompileAsync(Sql, User("S-A"), new TenantId(Tenant));
        _engine.CompileCache.Stats.Hits.ShouldBe(1);                  // control: the unchanged catalog is a hit
        control.Sql.ShouldBe(before.Sql);
        long misses = _engine.CompileCache.Stats.Misses;
        current = Orders("id", "extra_column", "tenant_id");          // a governed column was added
        var after = await service.CompileAsync(Sql, User("S-A"), new TenantId(Tenant));
        _engine.CompileCache.Stats.Misses.ShouldBe(misses + 1);       // a miss: the changed column set is a different dependency
        _engine.CompileCache.Stats.Hits.ShouldBe(1);
        current = Orders("id", "tenant_id");                          // and removed again
        var back = await service.CompileAsync(Sql, User("S-A"), new TenantId(Tenant));

        before.Sql.ShouldNotContain("extra_column");
        after.Sql.ShouldContain("extra_column");                      // the table fingerprint (column set) is part of the dependency
        back.Sql.ShouldNotContain("extra_column");
    }

    [Fact]
    public async Task CachePoisoning_CancelledCompile_IsNotCached()
    {
        var table = Orders("id", "tenant_id");
        var engine = _engine;   // the cancelled compile is the first thing that could touch this engine's cache
        var service = CreateService(() => table, engine: engine);
        const string Sql = "SELECT id FROM orders";
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => service.CompileAsync(Sql, User("S-A"), new TenantId(Tenant), cancelled.Token));
        var afterCancel = await service.CompileAsync(Sql, User("S-A"), new TenantId(Tenant));
        var reference = await CreateService(() => table, engine: new FastSqlEngine()).CompileAsync(Sql, User("S-A"), new TenantId(Tenant));

        engine.CompileCache.Stats.Hits.ShouldBe(0);   // the cancelled compile left no template: the compile after it had to build its own
        afterCancel.Sql.ShouldBe(reference.Sql);
        afterCancel.Parameters.Select(p => (p.Origin, p.Value)).ShouldBe(reference.Parameters.Select(p => (p.Origin, p.Value)));
    }

    [Fact]
    public async Task CachePoisoning_WriterThenReader_ReaderStillCannotRunDml()
    {
        // The statement permissions are part of the cache key: a DELETE compiled for a writer must not be served to a request that
        // does not allow DML. (The governed pipeline rejects a reader before the compile; this proves the engine on its own.)
        var table = Orders("id", "tenant_id");
        var service = CreateService(() => table, allowDml: true);
        const string Sql = "DELETE FROM orders WHERE id = 1";

        var writer = await service.CompileAsync(Sql, User("S-W"), new TenantId(Tenant));
        writer.StatementClass.ShouldBe(SqlStatementClass.Delete);

        var policy = await new GovernancePolicyFactory(new StubCatalogProvider(table), Substitute.For<Autheris.Application.Interfaces.ITableMetadataRepository>(), Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions())).CreateAsync(
            new GovernedPolicyInput("default", new TenantId(Tenant),
                new Dictionary<TableIdentity, TableAccessDecision>
                {
                    [new TableIdentity("public", "orders")] = TableAccessDecision.Allowed(OrdersId, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true)
                },
                new HashSet<TableIdentity> { new("public", "orders") }, GovernedSubqueryStrategy.Correlated)
            { Metadata = new Dictionary<TableIdentity, TableMetadata> { [new TableIdentity("public", "orders")] = table } },
            CancellationToken.None);
        var readOnly = CompileRequestFactory.Create(policy, TargetSqlDialect.PostgreSql, new WebSqlOptions(), isDml: false, 100, new HashSet<string>());

        long hits = _engine.CompileCache.Stats.Hits;
        var ex = Should.Throw<SqlCompileNotSupportedException>(() => _engine.Compile(Sql.AsMemory(), readOnly, CancellationToken.None));
        ex.Reason.ShouldBe(SqlCompileNotSupportedReason.StatementClass);
        _engine.CompileCache.Stats.Hits.ShouldBe(hits);   // not served from the writer's template
    }

    private sealed class StubCatalogProvider(TableMetadata table) : IGovernedCatalogProvider
    {
        public ValueTask<ITableCatalog> GetCatalogAsync(string dataSourceName, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct) =>
            ValueTask.FromResult<ITableCatalog>(new InMemoryTableCatalog(
                [new TableCatalogEntry(new TableIdentity("public", "orders"), table.Columns.Select(c => new CatalogColumn(c.ColumnName, c.DataType)).ToImmutableArray(), "tenant_id", 1)], "public"));
    }
}
