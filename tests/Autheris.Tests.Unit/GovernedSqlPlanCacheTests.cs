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
            auditLogRepository: null,
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
}
