namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using Xunit;

public sealed class CrossSourcePlannerTests
{
    private static readonly FastSqlEngine Engine = new();

    private static TableMetadata CreateTable(string domain, string table, DataSourceType dsType, IReadOnlyList<string> columns, Dictionary<string, MaskingRule>? masking = null)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, "public", table),
            Table = new Table
            {
                TableName = table,
                SchemaName = "public",
                SourceType = dsType == DataSourceType.Sql ? "PostgreSql" : "Http",
                DataSourceType = dsType,
                SourceName = domain,
                IsActive = true
            },
            Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList(),
            ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>()
        };
    }

    [Fact]
    public void Plan_AssignsDeterministicStagingNames_AndEliminatesCatalogPrefixes()
    {
        var sql = "SELECT o.id, s.tracking FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.shipment_id = s.id";
        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "shipment_id", "secret_note"]);
        var shipMeta = CreateTable("logistics", "shipments", DataSourceType.HttpDeclarative, ["id", "tracking", "internal_status"]);

        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "crm");
        var tShipments = new ResolvedSourceTable(metadata.ReferencedTables[1], shipMeta.Identifier, shipMeta, "logistics");

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [orderMeta.Identifier] = TableAccessDecision.Allowed(orderMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            [shipMeta.Identifier] = TableAccessDecision.Allowed(shipMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var planner = new CrossSourcePlanner(Engine);
        var plan = planner.Plan(sql, metadata, [tOrders, tShipments], decisions, new CrossSourceOptions { Enabled = true });

        plan.StagingNames.ShouldBe(["s0", "s1"]);
        plan.StagingRequests.Count.ShouldBe(2);
        plan.StagingRequests[0].StagingName.ShouldBe("s0");
        plan.StagingRequests[1].StagingName.ShouldBe("s1");

        // Staged projection must be minimal
        plan.StagingRequests[0].Projection.ShouldContain("id");
        plan.StagingRequests[0].Projection.ShouldContain("shipment_id");
        plan.StagingRequests[0].Projection.ShouldNotContain("secret_note"); // unreferenced!

        // DuckDB SQL must reference s0 and s1 with aliases o and s, without crm or logistics catalog names
        plan.GeneratedDuckDbSql.ShouldContain("\"s0\" AS \"o\"");
        plan.GeneratedDuckDbSql.ShouldContain("\"s1\" AS \"s\"");
        plan.GeneratedDuckDbSql.ShouldNotContain("crm.public");
        plan.GeneratedDuckDbSql.ShouldNotContain("logistics.v1");
    }

    [Fact]
    public void FederatedPlan_BindsParameters()
    {
        var sql = "SELECT o.id, s.tracking FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.shipment_id = s.id WHERE o.id = 42 AND s.tracking = 'abc'' OR 1=1 --'";
        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "shipment_id"]);
        var shipMeta = CreateTable("logistics", "shipments", DataSourceType.HttpDeclarative, ["id", "tracking"]);
        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "crm");
        var tShipments = new ResolvedSourceTable(metadata.ReferencedTables[1], shipMeta.Identifier, shipMeta, "logistics");
        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [orderMeta.Identifier] = TableAccessDecision.Allowed(orderMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            [shipMeta.Identifier] = TableAccessDecision.Allowed(shipMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var plan = new CrossSourcePlanner(Engine).Plan(sql, metadata, [tOrders, tShipments], decisions, new CrossSourceOptions { Enabled = true });

        plan.Compiled.Dialect.ShouldBe(TrinoSqlEngine.TargetSqlDialect.DuckDb);
        plan.Compiled.Parameters.Length.ShouldBe(2);
        plan.Compiled.Parameters.Select(p => p.Value).ShouldContain("abc' OR 1=1 --");
        plan.Compiled.Parameters.ShouldAllBe(p => p.Marker.StartsWith('$'));
        plan.GeneratedDuckDbSql.ShouldContain("$1");
        plan.GeneratedDuckDbSql.ShouldNotContain("abc");
        plan.GeneratedDuckDbSql.ShouldNotContain("42");
    }

    [Fact]
    public void Plan_SEC_FILTER_01_Violation_ThrowsWebSqlPolicyException()
    {
        var sql = "SELECT o.id, s.tracking FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.shipment_id = s.id WHERE o.secret_note = 'classified'";
        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "shipment_id", "secret_note"],
            new Dictionary<string, MaskingRule> { ["secret_note"] = new MaskingRule { RuleType = "REDACT" } });
        var shipMeta = CreateTable("logistics", "shipments", DataSourceType.HttpDeclarative, ["id", "tracking"]);

        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "crm");
        var tShipments = new ResolvedSourceTable(metadata.ReferencedTables[1], shipMeta.Identifier, shipMeta, "logistics");

        var colAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["shipment_id"] = ColumnAccessLevel.Clear,
            ["secret_note"] = ColumnAccessLevel.Mask
        };

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [orderMeta.Identifier] = TableAccessDecision.Allowed(orderMeta.Identifier, colAccess, hasUnconstrainedColumnAllow: false),
            [shipMeta.Identifier] = TableAccessDecision.Allowed(shipMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var planner = new CrossSourcePlanner(Engine);

        var ex = Should.Throw<WebSqlPolicyException>(() =>
            planner.Plan(sql, metadata, [tOrders, tShipments], decisions, new CrossSourceOptions { Enabled = true }));

        ex.Message.ShouldContain("Security Policy Violation: Column 'secret_note'");
    }

    [Fact]
    public void Plan_SEC_JOIN_01_Violation_ThrowsWebSqlPolicyException()
    {
        var sql = "SELECT o.id, s.tracking FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.secret_note = s.id";
        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("crm", "orders", DataSourceType.Sql, ["id", "secret_note"],
            new Dictionary<string, MaskingRule> { ["secret_note"] = new MaskingRule { RuleType = "REDACT" } });
        var shipMeta = CreateTable("logistics", "shipments", DataSourceType.HttpDeclarative, ["id", "tracking"]);

        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "crm");
        var tShipments = new ResolvedSourceTable(metadata.ReferencedTables[1], shipMeta.Identifier, shipMeta, "logistics");

        var colAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["secret_note"] = ColumnAccessLevel.Mask
        };

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [orderMeta.Identifier] = TableAccessDecision.Allowed(orderMeta.Identifier, colAccess, hasUnconstrainedColumnAllow: false),
            [shipMeta.Identifier] = TableAccessDecision.Allowed(shipMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var planner = new CrossSourcePlanner(Engine);

        var ex = Should.Throw<WebSqlPolicyException>(() =>
            planner.Plan(sql, metadata, [tOrders, tShipments], decisions, new CrossSourceOptions { Enabled = true }));

        ex.Message.ShouldContain("relational JOIN predicate");
    }

    [Fact]
    public void Plan_CrossJoin_WhenNotAllowed_ThrowsWebSqlPolicyException()
    {
        var sql = "SELECT o.id, s.tracking FROM crm.public.orders o CROSS JOIN logistics.v1.shipments s";
        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("crm", "orders", DataSourceType.Sql, ["id"]);
        var shipMeta = CreateTable("logistics", "shipments", DataSourceType.HttpDeclarative, ["tracking"]);

        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "crm");
        var tShipments = new ResolvedSourceTable(metadata.ReferencedTables[1], shipMeta.Identifier, shipMeta, "logistics");

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [orderMeta.Identifier] = TableAccessDecision.Allowed(orderMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            [shipMeta.Identifier] = TableAccessDecision.Allowed(shipMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var planner = new CrossSourcePlanner(Engine);

        var ex = Should.Throw<WebSqlPolicyException>(() =>
            planner.Plan(sql, metadata, [tOrders, tShipments], decisions, new CrossSourceOptions { Enabled = true, AllowNonEquiJoins = false }));

        ex.Message.ShouldContain("CROSS JOIN");
    }

    [Fact]
    public void Plan_ProhibitedDuckDbFunction_ThrowsWebSqlPolicyException()
    {
        var sql = "SELECT o.id, read_csv('secret.csv') FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.id = s.id";
        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("crm", "orders", DataSourceType.Sql, ["id"]);
        var shipMeta = CreateTable("logistics", "shipments", DataSourceType.HttpDeclarative, ["id"]);

        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "crm");
        var tShipments = new ResolvedSourceTable(metadata.ReferencedTables[1], shipMeta.Identifier, shipMeta, "logistics");

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [orderMeta.Identifier] = TableAccessDecision.Allowed(orderMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            [shipMeta.Identifier] = TableAccessDecision.Allowed(shipMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var planner = new CrossSourcePlanner(Engine);

        var ex = Should.Throw<WebSqlPolicyException>(() =>
            planner.Plan(sql, metadata, [tOrders, tShipments], decisions, new CrossSourceOptions { Enabled = true }));

        ex.Message.ShouldContain("read_csv");
    }
    [Fact]
    public void Plan_DecisionMissingForOneTable_ThrowsEnforcementIncomplete_AndProducesNoStagingRequest()
    {
        // ST-3 (SEC-1): a table without its own decision is denied, never read with a default "allowed" decision.
        var sql = "SELECT o.id, s.tracking FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.id = s.id";
        var metadata = Engine.Analyze(sql.AsMemory());

        var orderMeta = CreateTable("crm", "orders", DataSourceType.Sql, ["id"]);
        var shipMeta = CreateTable("logistics", "shipments", DataSourceType.HttpDeclarative, ["id", "tracking"]);

        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orderMeta.Identifier, orderMeta, "crm");
        var tShipments = new ResolvedSourceTable(metadata.ReferencedTables[1], shipMeta.Identifier, shipMeta, "logistics");

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [orderMeta.Identifier] = TableAccessDecision.Allowed(orderMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var planner = new CrossSourcePlanner(Engine);
        CrossSourcePlan? plan = null;

        var ex = Should.Throw<GatewaySecurityException>(() =>
            plan = planner.Plan(sql, metadata, [tOrders, tShipments], decisions, new CrossSourceOptions { Enabled = true }));

        ex.ErrorCode.ShouldBe("VF_ENFORCEMENT_INCOMPLETE");
        plan.ShouldBeNull();
    }

    [Fact]
    public void Plan_SameSchemaAndTableInTwoSources_EachStagingRequestCarriesItsOwnDecision()
    {
        // ST-1 (SEC-1): decisions are keyed by the source-qualified name; erp.public.customers must not replace the
        // decision (and its Virtual Filter) of crm_api.public.customers.
        var sql = "SELECT c.id, e.credit FROM crm_api.public.customers c JOIN erp.public.customers e ON c.id = e.id";
        var metadata = Engine.Analyze(sql.AsMemory());

        var crmMeta = CreateTable("crm_api", "customers", DataSourceType.HttpDeclarative, ["id", "status"]);
        var erpMeta = CreateTable("erp", "customers", DataSourceType.Sql, ["id", "credit"]);

        var crmDecision = TableAccessDecision.Allowed(crmMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            .WithMandatoryPredicate("P_vf_customers(id)", ["vf_customers"]);
        var erpDecision = TableAccessDecision.Allowed(erpMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        foreach (var ordered in new[] { (crmMeta, erpMeta), (erpMeta, crmMeta) })
        {
            var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
            {
                [ordered.Item1.Identifier] = ordered.Item1 == crmMeta ? crmDecision : erpDecision,
                [ordered.Item2.Identifier] = ordered.Item2 == crmMeta ? crmDecision : erpDecision
            };

            var plan = new CrossSourcePlanner(Engine).Plan(
                sql,
                metadata,
                [
                    new ResolvedSourceTable(metadata.ReferencedTables[0], crmMeta.Identifier, crmMeta, "crm_api"),
                    new ResolvedSourceTable(metadata.ReferencedTables[1], erpMeta.Identifier, erpMeta, "erp")
                ],
                decisions,
                new CrossSourceOptions { Enabled = true });

            var crmRequest = plan.StagingRequests.Single(r => r.Metadata.Identifier == crmMeta.Identifier);
            crmRequest.Decision.ShouldBeSameAs(crmDecision);
            crmRequest.Decision.AppliedVirtualFilters.ShouldNotBeNull().Count.ShouldBeGreaterThan(0);
            plan.StagingRequests.Single(r => r.Metadata.Identifier == erpMeta.Identifier).Decision.ShouldBeSameAs(erpDecision);
        }
    }
}
