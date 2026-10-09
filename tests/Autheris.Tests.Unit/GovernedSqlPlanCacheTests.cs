namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Antlr4.Runtime;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
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
using TrinoSqlEngine.Analysis;
using Xunit;

public sealed class GovernedSqlPlanCacheTests
{
    private const string Tenant = "tenant_a";

    private sealed class TrackingSqlEngine : ISqlEngine
    {
        private readonly ISqlEngine _inner;
        public int RewriteRlsCallCount { get; private set; }

        public TrackingSqlEngine(ISqlEngine inner) => _inner = inner;

        public string RewriteRls(ReadOnlyMemory<char> sql, RlsOptions? options = null)
        {
            RewriteRlsCallCount++;
            return _inner.RewriteRls(sql, options);
        }

        public string RewriteRls(ReadOnlyMemory<char> sql, RlsOptions? options, CancellationToken cancellationToken)
        {
            RewriteRlsCallCount++;
            return _inner.RewriteRls(sql, options, cancellationToken);
        }

        public string GenerateGovernedSql(ReadOnlyMemory<char> sql, RlsOptions? options = null)
            => _inner.GenerateGovernedSql(sql, options);

        public string GenerateGovernedSql(ReadOnlyMemory<char> sql, RlsOptions? options, CancellationToken cancellationToken)
            => _inner.GenerateGovernedSql(sql, options, cancellationToken);

        public string GenerateGovernedSql(string sql, RlsOptions? options = null)
            => _inner.GenerateGovernedSql(sql, options);

        public (SqlBaseParser.SingleStatementContext Tree, CommonTokenStream Tokens) Parse(
            ReadOnlyMemory<char> sql,
            SqlTokenSecurityOptions? tokenOptions = null,
            CancellationToken cancellationToken = default)
            => _inner.Parse(sql, tokenOptions, cancellationToken);

        public (SqlBaseParser.StandaloneExpressionContext Tree, CommonTokenStream Tokens) ParseExpression(
            ReadOnlyMemory<char> sql,
            SqlTokenSecurityOptions? tokenOptions = null,
            CancellationToken cancellationToken = default)
            => _inner.ParseExpression(sql, tokenOptions, cancellationToken);

        public SqlQueryMetadata Analyze(ReadOnlyMemory<char> sql)
            => _inner.Analyze(sql);

        public SqlQueryMetadata Analyze(
            ReadOnlyMemory<char> sql,
            SqlTokenSecurityOptions? tokenOptions,
            CancellationToken cancellationToken = default)
            => _inner.Analyze(sql, tokenOptions, cancellationToken);
    }

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-R4-USER"),
                new Claim("tenant_id", Tenant)
            ],
            "Test"));

    private static TableMetadata CreateTable(string tableName, string sourceType = "PostgreSQL") => new()
    {
        Identifier = new TableIdentifier("default", "public", tableName),
        Table = new Table { TableName = tableName, SchemaName = "public", SourceName = string.Empty, SourceType = sourceType },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "name", DataType = "varchar" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
        ]
    };

    private static ITableMetadataRepository CreateRepository(params TableMetadata[] tables)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<TableIdentifier>();
                foreach (var t in tables)
                {
                    if (t.Identifier.Equals(id))
                    {
                        return Task.FromResult<TableMetadata?>(t);
                    }
                }
                return Task.FromResult<TableMetadata?>(null);
            });
        return repo;
    }

    private static GovernedSqlExecutionService CreateService(
        ITableMetadataRepository repository,
        ISqlEngine? sqlEngine = null,
        ICompiledSqlQueryPlanCache? planCache = null)
    {
        var consentRepository = Substitute.For<IConsentRepository>();
        consentRepository.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var consentResolution = Substitute.For<IConsentResolutionService>();
        consentResolution.ResolveAccess(
                Arg.Any<Sid>(),
                Arg.Any<IReadOnlySet<Sid>>(),
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(),
                Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(
                ci.ArgAt<TableIdentifier>(3),
                new Dictionary<string, ColumnAccessLevel>(),
                null,
                hasUnconstrainedColumnAllow: true));

        var options = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, AllowDml = false }
        });

        return new GovernedSqlExecutionService(
            options: options,
            policyEnforcement: null,
            consentResolution: consentResolution,
            tableRepository: repository,
            connectionFactory: null,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consentRepository,
            secretProvider: null,
            sqlEngine: sqlEngine,
            planCache: planCache);
    }

    [Fact]
    public async Task GovernedSqlExecutionService_UsesPlanCache_OnRepeatedQueries()
    {
        var planCache = new CompiledSqlQueryPlanCache();
        var trackingEngine = new TrackingSqlEngine(new FastSqlEngine());
        var service = CreateService(CreateRepository(CreateTable("orders")), sqlEngine: trackingEngine, planCache: planCache);

        var user = CreateUser();
        var tenantId = new TenantId(Tenant);
        string query = "SELECT id, name FROM orders";

        // First call: compiles query and populates plan cache
        var result1 = await service.RewriteSqlAsync(query, user, tenantId);
        result1.ShouldContain("tenant_id = 'tenant_a'");
        trackingEngine.RewriteRlsCallCount.ShouldBe(1);

        // Second call: retrieves from plan cache directly (no re-compile)
        var result2 = await service.RewriteSqlAsync(query, user, tenantId);
        result2.ShouldBe(result1);
        trackingEngine.RewriteRlsCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GovernedSqlExecutionService_ZeroTrust_EnforcesTableDenialBeforeCacheLookup()
    {
        var planCache = new CompiledSqlQueryPlanCache();
        // Repository with no tables: table lookup will return null and throw table denied
        var service = CreateService(CreateRepository(), planCache: planCache);

        var user = CreateUser();
        var tenantId = new TenantId(Tenant);

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("SELECT id, name FROM orders", user, tenantId));
    }

    [Fact]
    public async Task GovernedSqlExecutionService_DifferentiatesCache_WhenColumnMaskingDiffers()
    {
        var planCache = new CompiledSqlQueryPlanCache();
        var trackingEngine = new TrackingSqlEngine(new FastSqlEngine());

        var orderTable = CreateTable("orders");
        var repo = CreateRepository(orderTable);

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var consentResolution = Substitute.For<IConsentResolutionService>();
        // Return clear for User A, but mask for User B
        consentResolution.ResolveAccess(
                Arg.Any<Sid>(),
                Arg.Any<IReadOnlySet<Sid>>(),
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(),
                Arg.Any<DatabaseDialect>())
            .Returns(ci =>
            {
                var sid = ci.ArgAt<Sid>(0);
                if (sid.Value == "S-1-5-21-USER-B")
                {
                    return TableAccessDecision.Allowed(
                        ci.ArgAt<TableIdentifier>(3),
                        new Dictionary<string, ColumnAccessLevel> { ["name"] = ColumnAccessLevel.Mask },
                        null,
                        hasUnconstrainedColumnAllow: false);
                }
                return TableAccessDecision.Allowed(
                    ci.ArgAt<TableIdentifier>(3),
                    new Dictionary<string, ColumnAccessLevel> { ["name"] = ColumnAccessLevel.Clear },
                    null,
                    hasUnconstrainedColumnAllow: true);
            });

        var options = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, AllowDml = false }
        });

        var service = new GovernedSqlExecutionService(
            options: options,
            policyEnforcement: null,
            consentResolution: consentResolution,
            tableRepository: repo,
            connectionFactory: null,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consentRepo,
            secretProvider: null,
            sqlEngine: trackingEngine,
            planCache: planCache);

        var userA = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-A"), new Claim("tenant_id", Tenant)], "Test"));
        var userB = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-B"), new Claim("tenant_id", Tenant)], "Test"));
        var tenantId = new TenantId(Tenant);
        string query = "SELECT id, name FROM orders";

        // User A compiles unmasked query
        var resultA = await service.RewriteSqlAsync(query, userA, tenantId);
        resultA.ShouldNotContain("'***'");
        trackingEngine.RewriteRlsCallCount.ShouldBe(1);

        // User B has mask policy: cache key must differ so User B gets a masked compilation and NOT User A's unmasked cached query!
        var resultB = await service.RewriteSqlAsync(query, userB, tenantId);
        resultB.ShouldContain("'***'");
        trackingEngine.RewriteRlsCallCount.ShouldBe(2);
    }

    [Fact]
    public void CompiledSqlQueryPlanCache_EvictsEntries_WhenExpired()
    {
        var cache = new CompiledSqlQueryPlanCache(TimeSpan.FromMilliseconds(50));
        var tenantId = new TenantId("t1");
        cache.SetCompiledSql(12345, DatabaseDialect.PostgreSql, tenantId, 999, "SELECT 1");

        cache.TryGetCompiledSql(12345, DatabaseDialect.PostgreSql, tenantId, 999, out var cached).ShouldBeTrue();
        cached.ShouldBe("SELECT 1");

        // Wait for TTL expiration
        Thread.Sleep(70);

        cache.TryGetCompiledSql(12345, DatabaseDialect.PostgreSql, tenantId, 999, out var expiredSql).ShouldBeFalse();
        expiredSql.ShouldBeNull();
    }

    [Fact]
    public void CompiledSqlQueryPlanCache_PolicyHash_DifferentiatesSubqueryStrategies()
    {
        var cache = new CompiledSqlQueryPlanCache();
        var rlsFilters = new Dictionary<string, string> { ["orders"] = "autheris_target.id = 1" };

        var hashExists = cache.ComputePolicyHash(rlsFilters, subqueryStrategy: RowFilterSubqueryStrategy.Exists);
        var hashInCorrelated = cache.ComputePolicyHash(rlsFilters, subqueryStrategy: RowFilterSubqueryStrategy.InCorrelated);
        var hashIn = cache.ComputePolicyHash(rlsFilters, subqueryStrategy: RowFilterSubqueryStrategy.In);

        hashExists.ShouldNotBe(hashInCorrelated);
        hashExists.ShouldNotBe(hashIn);
        hashInCorrelated.ShouldNotBe(hashIn);
    }
}
