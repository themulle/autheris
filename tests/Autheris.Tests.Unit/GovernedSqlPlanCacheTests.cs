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
        ISqlEngine? sqlEngine = null)
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
            options: options, executionGuard: GovernedTestSupport.Guard,
            policyEnforcement: null,
            consentResolution: consentResolution,
            tableRepository: repository, policyFactory: global::Autheris.Tests.Unit.GovernedTestSupport.PolicyFactory(repository), typedPolicies: global::Autheris.Tests.Unit.GovernedTestSupport.TypedPolicies(),
            connectionFactory: null,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consentRepository,
            secretProvider: null,
            sqlEngine: sqlEngine);
    }

    // L-16: the Application string cache is gone. The TTL and policy-hash tests of CompiledSqlQueryPlanCache tested deleted behaviour;
    // its bound and collision properties live in Sql/CompiledSqlTemplateCacheTests.
    // X1: GovernedSqlExecutionService_UsesPlanCache_OnRepeatedQueries and ..._DifferentiatesCache_WhenColumnMaskingDiffers tested the
    // Application string cache, which the governed pipeline no longer uses. The same properties are asserted against the engine's
    // value-free template cache in GovernedPipelineCachePoisoningTests (M-8).

    [Fact]
    public async Task GovernedSqlExecutionService_ZeroTrust_EnforcesTableDenialBeforeCompile()
    {
        // Repository with no tables: table lookup will return null and throw table denied
        var service = CreateService(CreateRepository());

        var user = CreateUser();
        var tenantId = new TenantId(Tenant);

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("SELECT id, name FROM orders", user, tenantId));
    }
}
