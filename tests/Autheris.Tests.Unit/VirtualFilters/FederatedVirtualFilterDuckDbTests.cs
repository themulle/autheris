namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using Xunit;

public sealed class FederatedVirtualFilterDuckDbTests
{
    private static readonly FastSqlEngine Engine = new();

    private static TableMetadata CreateWebTable(string domain, string table, IReadOnlyList<string> columns)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, "public", table),
            Table = new Table
            {
                TableName = table,
                SchemaName = "public",
                SourceType = "HttpDeclarative",
                DataSourceType = DataSourceType.HttpDeclarative,
                SourceName = domain,
                IsActive = true
            },
            Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList()
        };
    }

    [Fact]
    public void CrossSourcePlanner_WithVirtualFilterJoinSpec_InjectsJoinAndStagingRequest()
    {
        var sql = "SELECT id, name FROM web_crm.public.customers";
        var metadata = Engine.Analyze(sql.AsMemory());
        var webMeta = CreateWebTable("web_crm", "customers", ["id", "name"]);
        var resolvedTable = new ResolvedSourceTable(metadata.ReferencedTables[0], webMeta.Identifier, webMeta, "web_crm");

        var decisions = new Dictionary<string, TableAccessDecision>
        {
            [webMeta.Identifier.ToQualifiedName()] = TableAccessDecision.Allowed(webMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var filter = new VirtualFilter
        {
            TenantId = new TenantId("tenant-alpha"),
            Name = "vf_allowed",
            Source = "mssql_corp",
            KeyColumns = new[] { "client.customer_id" }
        };

        var binding = new FilterBinding
        {
            FilterName = "vf_allowed",
            TargetPattern = "^web_crm\\.public\\.customers$",
            ColumnMap = new Dictionary<string, string> { ["customer_id"] = "id" }
        };

        var vfJoinSpec = new VirtualFilterJoinSpec(
            webMeta.Identifier.ToQualifiedName(),
            filter,
            binding,
            new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["customer_id"] = "47" }
            });

        var planner = new CrossSourcePlanner(Engine);
        var plan = planner.Plan(
            sql,
            metadata,
            [resolvedTable],
            decisions,
            new CrossSourceOptions { Enabled = true },
            virtualFilterJoins: new[] { vfJoinSpec });

        // There should be 2 staging requests: web_crm.public.customers (s0) and virtual filter (s1)
        plan.StagingRequests.Count.ShouldBe(2);
        var vfStaging = plan.StagingRequests.FirstOrDefault(r => r.PreloadedRows != null);
        vfStaging.ShouldNotBeNull();
        vfStaging.PreloadedRows.ShouldNotBeNull();
        vfStaging.PreloadedRows.Count.ShouldBe(1);

        // Generated DuckDB SQL must contain INNER JOIN
        plan.GeneratedDuckDbSql.ShouldContain("JOIN");
        plan.GeneratedDuckDbSql.ShouldContain("customer_id");
    }

    [Fact]
    public async Task FederatedExecution_ShortCircuitMiss_ReturnsZeroRows_WithoutStaging()
    {
        var sql = "SELECT id, name FROM web_crm.public.customers WHERE id = 999";
        var tenantId = new TenantId("tenant-alpha");
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1")
        }));

        var webMeta = CreateWebTable("web_crm", "customers", ["id", "name"]);
        var metadata = Engine.Analyze(sql.AsMemory());

        var router = Substitute.For<ICrossSourceQueryRouter>();
        router.RouteAsync(Arg.Any<SqlQueryMetadata>(), Arg.Any<string?>(), Arg.Any<TenantId>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new QueryRoutingDecision(
                QuerySourceClass.CrossSource,
                new[] { new ResolvedSourceTable(metadata.ReferencedTables[0], webMeta.Identifier, webMeta, "web_crm") }));

        var stagingService = Substitute.For<IFederatedStagingService>();
        var duckDbEngine = Substitute.For<IDuckDbOlapEngine>();
        var planner = Substitute.For<ICrossSourcePlanner>();
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var options = Options.Create(new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true },
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                CrossSource = new CrossSourceOptions { Enabled = true }
            }
        });

        var shortCircuitEvaluator = Substitute.For<IVirtualFilterShortCircuitEvaluator>();
        shortCircuitEvaluator.EvaluateAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(ShortCircuitEvaluationResult.Miss(
                new VirtualFilter { Name = "vf_test", Source = "mssql", KeyColumns = new[] { "client.customer_id" } },
                new FilterBinding { FilterName = "vf_test", TargetPattern = ".*" },
                "ID not allowed"));

        var service = new FederatedDuckDbExecutionService(
            options,
            auditRepo,
            router,
            planner,
            stagingService,
            duckDbEngine,
            sqlEngine: Engine,
            shortCircuitEvaluator: shortCircuitEvaluator);

        var request = new GovernedSqlQueryRequest(sql);
        var result = await service.ExecuteQueryBufferedAsync(request, user, tenantId);

        // Staging service must never be called!
        await stagingService.DidNotReceiveWithAnyArgs().StageAsync(default!, default!, default!, default!, default);
        result.Rows.ShouldBeEmpty();
    }
}
