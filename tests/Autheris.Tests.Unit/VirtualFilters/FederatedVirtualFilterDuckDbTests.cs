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
    public void CrossSourcePlanner_WithVirtualFilterPlan_DoesNotRewriteTheQuery_AndStagesNoKeyRelation()
    {
        // F-GOV-14 WI-6: the Virtual Filter is a gateway semi-join at staging; the former INNER JOIN against a preloaded
        // key relation (row multiplication, query shape bypasses) is gone.
        var sql = "SELECT id, name FROM web_crm.public.customers";
        var metadata = Engine.Analyze(sql.AsMemory());
        var webMeta = CreateWebTable("web_crm", "customers", ["id", "name"]);
        var resolvedTable = new ResolvedSourceTable(metadata.ReferencedTables[0], webMeta.Identifier, webMeta, "web_crm");

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [webMeta.Identifier] = TableAccessDecision.Allowed(webMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var plan = new CrossSourcePlanner(Engine).Plan(sql, metadata, [resolvedTable], decisions, new CrossSourceOptions { Enabled = true }, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User, Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant);

        plan.StagingRequests.Count.ShouldBe(1);
        plan.StagingNames.Count.ShouldBe(1);
        plan.GeneratedDuckDbSql.ShouldNotContain("JOIN");
    }
}
