namespace Autheris.Tests.Unit.Security;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql.Errors;
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
using Xunit;

/// <summary>
/// SEC-PG-15 (#307, GAP-016): a federated query that misses a required filter column answers 400
/// REQUIRED_FILTER_COLUMN_MISSING (typed exception) instead of a generic 403. The requirement itself is not relaxed.
/// </summary>
[Trait("Category", "Security")]
public sealed class FederatedRequiredFilterTypedExceptionTests
{
    private static readonly FastSqlEngine Engine = new();

    private static TableMetadata Orders(bool group) => new()
    {
        Identifier = new TableIdentifier("crm", "public", "orders"),
        Table = new Table
        {
            TableName = "orders",
            SchemaName = "public",
            SourceType = "PostgreSql",
            DataSourceType = DataSourceType.Sql,
            SourceName = "crm",
            IsActive = true,
            RequiredFilterColumns = group ? default : ["region"],
            RequiredFilterColumnGroups = group ? [["region"]] : default
        },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "region", DataType = "varchar" }]
    };

    private static TableMetadata Shipments() => new()
    {
        Identifier = new TableIdentifier("logistics", "v1", "shipments"),
        Table = new Table
        {
            TableName = "shipments",
            SchemaName = "v1",
            SourceType = "Http",
            DataSourceType = DataSourceType.HttpDeclarative,
            SourceName = "logistics",
            IsActive = true
        },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    [Fact]
    public void CrossSourcePlanner_RequiredFilterMissing_ThrowsTypedException_SEC_PG_15()
    {
        var sql = "SELECT o.id FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.id = s.id";
        var metadata = Engine.Analyze(sql.AsMemory());
        var o = Orders(group: false);
        var s = Shipments();
        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], o.Identifier, o, "crm");
        var tShip = new ResolvedSourceTable(metadata.ReferencedTables[1], s.Identifier, s, "logistics");
        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [o.Identifier] = TableAccessDecision.Allowed(o.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            [s.Identifier] = TableAccessDecision.Allowed(s.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var ex = Should.Throw<TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException>(() =>
            new CrossSourcePlanner(Engine).Plan(sql, metadata, [tOrders, tShip], decisions, new CrossSourceOptions { Enabled = true },
                Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant));

        ex.Message.ShouldNotContain("region");
        SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
        mapped.Status.ShouldBe(400);
        mapped.Code.ShouldBe("REQUIRED_FILTER_COLUMN_MISSING");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FederatedStaging_RequiredFilterMissing_ThrowsTypedException_SEC_PG_15(bool group)
    {
        var service = new FederatedStagingService(
            Substitute.For<IAutherisConnectorRegistry>(),
            Substitute.For<IColumnMaskingProvider>(),
            Substitute.For<IAuditLogRepository>(),
            Options.Create(new GatewayOptions { WebSql = new WebSqlOptions() }),
            NullLogger<FederatedStagingService>.Instance);
        var meta = Orders(group);
        var request = new StagingTableRequest(
            new TrinoSqlEngine.Analysis.TableAccessTarget("crm", "public", "orders", null, "crm.public.orders"),
            meta,
            TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            "stg_orders",
            ["id"]);
        var budget = new FederationBudget(5, 1000, 5000, 32 * 1024 * 1024, 128 * 1024 * 1024, 30, 4);

        var ex = await Should.ThrowAsync<TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException>(async () =>
            await service.StageAsync([request], new ClaimsPrincipal(new ClaimsIdentity("t")), new TenantId("t1"), budget, CancellationToken.None));

        ex.Message.ShouldNotContain("region");
    }
}
