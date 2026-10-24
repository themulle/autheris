namespace Autheris.Tests.Unit.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using Xunit;

/// <summary>#327 / SEC-RG-15: a join chain whose tables share a bare name across sources is matched on the qualified identity.</summary>
public sealed class CrossSourcePlannerSubtreeTests
{
    private static readonly FastSqlEngine Engine = new();

    private static TableMetadata Table(string domain, string schema, string table, DatabaseDialect dialect, params string[] columns) => new()
    {
        Identifier = new TableIdentifier(domain, schema, table),
        Table = new Table
        {
            TableName = table,
            SchemaName = schema,
            SourceType = dialect.ToString(),
            DataSourceType = DataSourceType.Sql,
            SourceName = domain,
            IsActive = true
        },
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList(),
        ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
    };

    private static (List<ResolvedSourceTable> Tables, Dictionary<TableIdentifier, TableAccessDecision> Decisions, SqlQueryMetadata Metadata) Setup(
        string sql, bool withMsDecision = true)
    {
        var metadata = Engine.Analyze(sql.AsMemory());
        var pgSales = Table("pg", "public", "sales", DatabaseDialect.PostgreSql, "id", "region_id");
        var pgRegions = Table("pg", "public", "regions", DatabaseDialect.PostgreSql, "id", "name");
        var msSales = Table("ms", "dbo", "sales", DatabaseDialect.SqlServer, "id", "region_id");
        var byName = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["pg.public.sales"] = pgSales,
            ["pg.public.regions"] = pgRegions,
            ["ms.dbo.sales"] = msSales
        };
        var tables = metadata.ReferencedTables
            .Select(t => new ResolvedSourceTable(t, byName[t.FullName].Identifier, byName[t.FullName], byName[t.FullName].Identifier.Domain))
            .ToList();
        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>();
        foreach (var t in tables)
        {
            if (!withMsDecision && t.Metadata.Identifier.Domain == "ms") continue;
            decisions[t.Metadata.Identifier] = TableAccessDecision.Allowed(
                t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        }

        return (tables, decisions, metadata);
    }

    private static CrossSourcePlan Plan(string sql, bool withMsDecision = true)
    {
        var (tables, decisions, metadata) = Setup(sql, withMsDecision);
        return new CrossSourcePlanner(Engine).Plan(sql, metadata, tables, decisions, new CrossSourceOptions { Enabled = true },
            Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant);
    }

    private const string ChainAfirst =
        "SELECT a.id, r.name FROM pg.public.sales a JOIN pg.public.regions r ON a.region_id = r.id JOIN ms.dbo.sales b ON a.id = b.id";

    private const string ChainBfirst =
        "SELECT a.id, r.name FROM ms.dbo.sales b JOIN pg.public.sales a ON a.id = b.id JOIN pg.public.regions r ON a.region_id = r.id";

    [Fact]
    public void SameNameTableOtherSource_NotMatchedBySubtree()
    {
        var plan = Plan(ChainAfirst);

        // The pg subtree {a, r} is one staging; the ms table b stays its own staging and keeps its ON clause.
        plan.StagingRequests.Count.ShouldBe(2);
        plan.GeneratedDuckDbSql.ShouldContain("JOIN");
        plan.GeneratedDuckDbSql.ShouldContain("\"b\".\"id\"");
    }

    [Fact]
    public void ChainOrderIndependent_SamePlanShape()
    {
        var first = Plan(ChainAfirst);
        var second = Plan(ChainBfirst);

        second.StagingRequests.Count.ShouldBe(first.StagingRequests.Count);
        second.GeneratedDuckDbSql.ShouldContain("\"b\".\"id\"");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void JoinChainSameNameDifferentSource_DistinctAndPolicyChecked()
    {
        // The ms table has the same bare name as the pg table of the subtree; it still needs its own decision (fail closed).
        Should.Throw<GatewaySecurityException>(() => Plan(ChainAfirst, withMsDecision: false));
        Should.Throw<GatewaySecurityException>(() => Plan(ChainBfirst, withMsDecision: false));
    }

    [Trait("Category", "Security")]
    [Fact]
    public void ChainWithUnknownSource_UniformDenial()
    {
        // A table that is not among the resolved tables is never guessed (same denial type as any unresolved table).
        var (tables, decisions, metadata) = Setup(ChainAfirst);
        var unresolved = tables.Where(t => t.Metadata.Identifier.Domain != "ms").ToList();

        Should.Throw<Exception>(() => new CrossSourcePlanner(Engine).Plan(ChainAfirst, metadata, unresolved, decisions,
            new CrossSourceOptions { Enabled = true },
            Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant))
            .ShouldBeAssignableTo<System.Security.SecurityException>();
    }
}
