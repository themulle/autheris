namespace Autheris.Tests.Unit.Sql.TimeZones;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Olap;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Application.Sql.TimeZones;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Application.Interfaces;
using TrinoSqlEngine.Analysis;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

/// <summary>REQ-SQL-130 (A-8, BC-130-4): the federated planner validates the conversion before it generates the DuckDB statement, on every entry.</summary>
public sealed class ConvertTimeZoneFederationTests
{
    private static readonly FastSqlEngine Engine = new();

    private sealed class FixedState(bool available) : ITimeZoneConversionState
    {
        public bool IsAvailable(TargetSqlDialect dialect) => available && dialect == TargetSqlDialect.DuckDb;
    }

    private static TableMetadata Table(string domain, string table, DataSourceType type, params string[] columns) => new()
    {
        Identifier = new TableIdentifier(domain, "public", table),
        Table = new Table { TableName = table, SchemaName = "public", SourceType = type == DataSourceType.Sql ? "PostgreSql" : "Http", DataSourceType = type, SourceName = domain, IsActive = true },
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList(),
        ColumnMaskingRules = new Dictionary<string, MaskingRule>()
    };

    private static (string Sql, Func<bool, ITimeZoneConversionState?, SqlStatement?, CrossSourcePlan> Plan) Setup(
        string select, ColumnAccessLevel createdAtAccess = ColumnAccessLevel.Clear)
    {
        string sql = select.Contains(" FROM ", StringComparison.Ordinal) ? select : $"{select} FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.shipment_id = s.id";
        var metadata = Engine.Analyze(sql.AsMemory());
        var orders = Table("crm", "orders", DataSourceType.Sql, "id", "shipment_id", "created_at", "label");
        var shipments = Table("logistics", "shipments", DataSourceType.HttpDeclarative, "id", "tracking");
        var tOrders = new ResolvedSourceTable(metadata.ReferencedTables[0], orders.Identifier, orders, "crm");
        var tShipments = new ResolvedSourceTable(metadata.ReferencedTables[1], shipments.Identifier, shipments, "logistics");
        var access = new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["shipment_id"] = ColumnAccessLevel.Clear, ["created_at"] = createdAtAccess, ["label"] = ColumnAccessLevel.Clear };
        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [orders.Identifier] = TableAccessDecision.Allowed(orders.Identifier, access, hasUnconstrainedColumnAllow: false),
            [shipments.Identifier] = TableAccessDecision.Allowed(shipments.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        return (sql, (_, state, statement) => new CrossSourcePlanner(Engine, state).Plan(
            sql, metadata, [tOrders, tShipments], decisions, new CrossSourceOptions { Enabled = true }, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant, null, statement));
    }

    private static SqlStatement Parse(string sql)
    {
        var (tree, _) = Engine.Parse(sql.AsMemory());
        return new SqlAstBuilder().BuildStatement(tree);
    }

    private static string Rejected(string select, ITimeZoneConversionState? state, bool supplied, ColumnAccessLevel access = ColumnAccessLevel.Clear)
    {
        var (sql, plan) = Setup(select, access);
        var ex = Should.Throw<SqlTimeZoneException>(() => plan(supplied, state, supplied ? Parse(sql) : null));
        ex.Message.ShouldBe(GovernedSqlErrorCodes.FixedMessage(ex.Code));
        return ex.Code;
    }

    private static readonly ITimeZoneConversionState Available = new FixedState(true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void T25_LiteralZonesWithACastTimestampPlanAndBindTheMappedZones(bool supplied)
    {
        var (sql, plan) = Setup("SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(o.created_at AS timestamp)) AS t");
        var planned = plan(supplied, Available, supplied ? Parse(sql) : null);

        planned.GeneratedDuckDbSql.ShouldContain("TIMEZONE($");
        planned.GeneratedDuckDbSql.ShouldNotContain("Berlin");
        planned.GeneratedDuckDbSql.ShouldNotContain("UTC");
        planned.Compiled.Parameters.Where(p => p.Value is string).Select(p => (string)p.Value!).Order().ToArray().ShouldBe(["Europe/Berlin", "UTC"]);
        planned.Compiled.Parameters.ShouldAllBe(p => !p.ZoneArgument);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Security")]
    public void ST_130_14_EveryEntryRejectsAColumnZoneABareColumnAParameterZoneAndAMaskedColumn(bool supplied)
    {
        Rejected("SELECT convert_timezone(o.label, 'UTC', CAST(o.created_at AS timestamp))", Available, supplied).ShouldBe(GovernedSqlErrorCodes.TimeZoneArgument);
        Rejected("SELECT convert_timezone('UTC', 'Europe/Berlin', o.created_at)", Available, supplied).ShouldBe(GovernedSqlErrorCodes.TimeZoneArgument);
        Rejected("SELECT convert_timezone(__param_zone, 'Europe/Berlin', CAST(o.created_at AS timestamp))", Available, supplied).ShouldBe(GovernedSqlErrorCodes.TimeZoneArgument);
        Rejected("SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(o.created_at AS timestamp))", Available, supplied, ColumnAccessLevel.Mask).ShouldBe(GovernedSqlErrorCodes.TimeZoneArgument);
        Rejected("SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(o.created_at AS timestamp))", Available, supplied, ColumnAccessLevel.Deny).ShouldBe(GovernedSqlErrorCodes.TimeZoneArgument);
        Rejected("SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(created_at AS timestamp))", Available, supplied, ColumnAccessLevel.Mask).ShouldBe(GovernedSqlErrorCodes.TimeZoneArgument);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Security")]
    public void ST_130_14_AMaskedColumnBehindACteAliasIsJudgedByItsName(bool supplied)
    {
        const string select = "WITH c AS (SELECT o.created_at AS created_at, o.label AS label FROM crm.public.orders o) " +
            "SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(c.created_at AS timestamp)) AS t FROM c JOIN logistics.v1.shipments s ON s.id = c.label";
        // the CTE alias belongs to no staged table: the column is judged by its name
        Rejected(select, Available, supplied, ColumnAccessLevel.Mask).ShouldBe(GovernedSqlErrorCodes.TimeZoneArgument);
        Rejected(select, Available, supplied, ColumnAccessLevel.Deny).ShouldBe(GovernedSqlErrorCodes.TimeZoneArgument);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Security")]
    public void ST_130_14_ACallInsideACteOrASubqueryIsValidated(bool supplied)
    {
        Rejected("SELECT x.t FROM (SELECT convert_timezone('Mars/Base', 'UTC', CAST(o.created_at AS timestamp)) AS t, o.label AS label FROM crm.public.orders o) x " +
                 "JOIN logistics.v1.shipments s ON s.id = x.label", Available, supplied).ShouldBe(GovernedSqlErrorCodes.TimeZoneInvalid);
        Rejected("WITH c AS (SELECT convert_timezone('Mars/Base', 'UTC', CAST(o.created_at AS timestamp)) AS t, o.label AS label FROM crm.public.orders o) " +
                 "SELECT c.t FROM c JOIN logistics.v1.shipments s ON s.id = c.label", Available, supplied).ShouldBe(GovernedSqlErrorCodes.TimeZoneInvalid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Security")]
    public void ST_130_14_AnUnavailableOrUnevaluatedStateIsRefusedStatically(bool supplied)
    {
        const string select = "SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(o.created_at AS timestamp))";
        Rejected(select, new FixedState(false), supplied).ShouldBe(GovernedSqlErrorCodes.TimeZoneUnavailable);
        Rejected(select, null, supplied).ShouldBe(GovernedSqlErrorCodes.TimeZoneUnavailable);
    }

    [Fact]
    public void T26_ThePlannerFunctionCheckAcceptsTheFunctionAndStillRejectsTheRelatedNames()
    {
        var (sql, plan) = Setup("SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(o.created_at AS timestamp))");
        Should.NotThrow(() => plan(false, Available, null));
        sql.ShouldNotBeNull();

        foreach (string related in new[] { "timezone('UTC', o.label)", "at_timezone(o.label, 'UTC')", "with_timezone(o.label, 'UTC')" })
        {
            var (_, relatedPlan) = Setup($"SELECT {related}");
            Should.Throw<WebSqlPolicyException>(() => relatedPlan(false, Available, null));
        }
    }

    [Fact]
    public void AStatementWithoutTheFunctionIsUnaffectedByTheState()
    {
        var (_, plan) = Setup("SELECT o.id");
        plan(false, null, null).GeneratedDuckDbSql.ShouldNotContain("TIMEZONE");
    }

    [Theory]
    [InlineData("SELECT o.created_at AT TIME ZONE 'UTC'")]
    [InlineData("SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(o.created_at AT TIME ZONE 'UTC' AS timestamp))")]
    [Trait("Category", "Security")]
    public void ST_130_12_TheOperatorFormIsRejectedOnEveryFederatedEntry(string select)
    {
        // Without a supplied statement the planner parses and builds (the builder refuses the operator); a supplied statement cannot be built at all.
        Should.Throw<AstBuildException>(() => Setup(select).Plan(false, Available, null));
        Should.Throw<AstBuildException>(() => Parse(Setup(select).Sql));
    }
}
