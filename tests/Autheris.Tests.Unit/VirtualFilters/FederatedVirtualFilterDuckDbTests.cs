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

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [webMeta.Identifier] = TableAccessDecision.Allowed(webMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
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
            webMeta.Identifier,
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
}
