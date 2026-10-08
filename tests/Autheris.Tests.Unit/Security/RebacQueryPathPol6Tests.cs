namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
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
using Xunit;

/// <summary>
/// POL-6: with Rebac.EnforceOnQueryPaths the query paths (table queries/OData/GraphQL tree, WebSQL) require the
/// ReBAC relation can_query; without it they keep their previous behaviour and do not consult ReBAC.
/// </summary>
public sealed class RebacQueryPathPol6Tests
{
    private const string Tenant = "tenant_a";
    private static readonly TableIdentifier Table = new("default", "public", "orders");

    private static ClaimsPrincipal CreateUser() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-REBAC-USER"),
                new Claim("tenant_id", Tenant)
            ],
            "Test"));

    private static TableMetadata CreateMetadata() => new()
    {
        Identifier = Table,
        Table = new Table { TableName = "orders", SchemaName = "public", SourceType = "PostgreSQL" },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
        ]
    };

    private static GatewayOptions CreateOptions(bool enforceOnQueryPaths) => new()
    {
        Rebac = new RebacOptions { Enabled = true, EnforceOnQueryPaths = enforceOnQueryPaths },
        WebSql = new WebSqlOptions { Enabled = true, DefaultMaxRows = 100, MaxAllowedRows = 500 }
    };

    private static IRebacEvaluator CreateEvaluator(bool allowed)
    {
        var evaluator = Substitute.For<IRebacEvaluator>();
        evaluator.IsEnabled.Returns(true);
        evaluator.CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<RebacCheckResult>(allowed ? RebacCheckResult.Permitted : RebacCheckResult.Denied));
        return evaluator;
    }

    private static ITableMetadataRepository CreateRepository()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(Table.Equals(ci.Arg<TableIdentifier>()) ? CreateMetadata() : null));
        return repo;
    }

    private static GatewayExecutionService CreateGatewayService(GatewayOptions options, IRebacEvaluator? evaluator)
    {
        var cache = Substitute.For<IConsentCacheService>();
        cache.GetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        return new GatewayExecutionService(
            CreateRepository(),
            Substitute.For<IConsentRepository>(),
            Substitute.For<IAuditLogRepository>(),
            Substitute.For<IConsentResolutionService>(),
            cache,
            Substitute.For<IColumnMaskingProvider>(),
            options: Options.Create(options),
            rebacEvaluator: evaluator);
    }

    private static GovernedSqlExecutionService CreateWebSqlService(GatewayOptions options, IRebacEvaluator? evaluator)
    {
        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true));

        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        return new GovernedSqlExecutionService(
            Options.Create(options),
            consentResolution: resolution,
            tableRepository: CreateRepository(),
            environment: env,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents,
            rebacEvaluator: evaluator);
    }

    [Fact]
    public async Task TableQueryPath_Enforced_WithoutRelation_IsDenied()
    {
        var service = CreateGatewayService(CreateOptions(enforceOnQueryPaths: true), CreateEvaluator(allowed: false));

        var access = await service.ResolveTableAccessAsync(CreateUser(), Table, null, null);

        access.Decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task TableQueryPath_Enforced_WithRelation_IsAllowed()
    {
        var evaluator = CreateEvaluator(allowed: true);
        var service = CreateGatewayService(CreateOptions(enforceOnQueryPaths: true), evaluator);

        var access = await service.ResolveTableAccessAsync(CreateUser(), Table, null, null);

        access.Decision.IsAllowed.ShouldBeTrue();
        await evaluator.Received(1).CheckAsync(
            Arg.Is<RebacCheckRequest>(r => r.Relation == "can_query" && r.Object == "table:default.public.orders" && r.TenantId == Tenant),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TableQueryPath_Enforced_WithoutEvaluator_FailsClosed()
    {
        var service = CreateGatewayService(CreateOptions(enforceOnQueryPaths: true), evaluator: null);

        var access = await service.ResolveTableAccessAsync(CreateUser(), Table, null, null);

        access.Decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task TableQueryPath_NotEnforced_DoesNotConsultRebac()
    {
        var evaluator = CreateEvaluator(allowed: false);
        var service = CreateGatewayService(CreateOptions(enforceOnQueryPaths: false), evaluator);

        var access = await service.ResolveTableAccessAsync(CreateUser(), Table, null, null);

        access.Decision.IsAllowed.ShouldBeTrue();
        await evaluator.DidNotReceive().CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WebSql_Enforced_WithoutRelation_IsDenied()
    {
        var service = CreateWebSqlService(CreateOptions(enforceOnQueryPaths: true), CreateEvaluator(allowed: false));

        await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.RewriteSqlAsync("SELECT id FROM orders", CreateUser(), new TenantId(Tenant)));
    }

    [Fact]
    public async Task WebSql_Enforced_WithRelation_IsAllowed()
    {
        var service = CreateWebSqlService(CreateOptions(enforceOnQueryPaths: true), CreateEvaluator(allowed: true));

        var sql = await service.RewriteSqlAsync("SELECT id FROM orders", CreateUser(), new TenantId(Tenant));

        sql.ShouldContain("tenant_id = 'tenant_a'");
    }
}
