namespace Autheris.Tests.Unit.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Federation.Subtrees;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

public sealed class WebSqlCoLocatedDataSourcesTests
{
    private static TableMetadata CreateTable(string domain, string schema, string table, string? dbName, string provider, string? group)
    {
        var id = new TableIdentifier(domain, schema, table);
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                TableName = table,
                SchemaName = schema,
                SourceName = domain,
                PhysicalDatabaseName = dbName,
                SourceType = provider,
                DataSourceType = DataSourceType.Sql,
                Visibility = DatasetVisibility.Full,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "foreign_id", DataType = "int" },
                new TableColumn { ColumnName = "val", DataType = "varchar" }
            ]
        };
    }

    [Fact]
    public void Grouping_ColocatedSourcesGroupedTogether_NonColocatedKeptSeparate()
    {
        var meta1 = CreateTable("src1", "dbo", "Orders", "DB1", "SqlServer", "grp1");
        var meta2 = CreateTable("src2", "dbo", "Invoices", "DB2", "SqlServer", "grp1");
        var meta3 = CreateTable("src3", "dbo", "Customers", "DB3", "SqlServer", "grp2");
        var meta4 = CreateTable("src4", "public", "Logs", "DB4", "PostgreSql", "grp1"); // PG not co-located

        var tables = new List<ResolvedSourceTable>
        {
            new(new TableAccessTarget(null, "dbo", "Orders", null, "dbo.Orders"), meta1.Identifier, meta1, "src1"),
            new(new TableAccessTarget(null, "dbo", "Invoices", null, "dbo.Invoices"), meta2.Identifier, meta2, "src2"),
            new(new TableAccessTarget(null, "dbo", "Customers", null, "dbo.Customers"), meta3.Identifier, meta3, "src3"),
            new(new TableAccessTarget(null, "public", "Logs", null, "public.Logs"), meta4.Identifier, meta4, "src4")
        };

        var connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["src1"] = new() { Provider = "SqlServer", CoLocationGroup = "grp1" },
            ["src2"] = new() { Provider = "SqlServer", CoLocationGroup = "grp1" },
            ["src3"] = new() { Provider = "SqlServer", CoLocationGroup = "grp2" },
            ["src4"] = new() { Provider = "PostgreSql", CoLocationGroup = "grp1" }
        };

        var options = new CrossSourceOptions
        {
            Enabled = true,
            CoLocation = new CoLocationOptions { Enabled = true }
        };

        var partitions = SubtreeGraphPartitioner.Partition(tables, connections, options);

        partitions.Count.ShouldBe(3);

        var pGrp1 = partitions.FirstOrDefault(p => p.CoLocationGroup == "grp1");
        pGrp1.ShouldNotBeNull();
        pGrp1.IsCoLocated.ShouldBeTrue();
        pGrp1.Tables.Count.ShouldBe(2);
        pGrp1.DistinctDatabases.ShouldBe(["DB1", "DB2"], ignoreOrder: true);
        pGrp1.IsMultiDatabase.ShouldBeTrue();

        var pGrp2 = partitions.FirstOrDefault(p => p.CoLocationGroup == "grp2");
        pGrp2.ShouldNotBeNull();
        pGrp2.IsCoLocated.ShouldBeTrue();
        pGrp2.Tables.Count.ShouldBe(1);

        var pPg = partitions.FirstOrDefault(p => p.GroupingKey == "source:src4");
        pPg.ShouldNotBeNull();
        pPg.IsCoLocated.ShouldBeFalse();
        pPg.Tables.Count.ShouldBe(1);
    }

    [Fact]
    public void Grouping_FeatureDisabled_FallsBackToSourceGrouping()
    {
        var meta1 = CreateTable("src1", "dbo", "Orders", "DB1", "SqlServer", "grp1");
        var meta2 = CreateTable("src2", "dbo", "Invoices", "DB2", "SqlServer", "grp1");

        var tables = new List<ResolvedSourceTable>
        {
            new(new TableAccessTarget(null, "dbo", "Orders", null, "dbo.Orders"), meta1.Identifier, meta1, "src1"),
            new(new TableAccessTarget(null, "dbo", "Invoices", null, "dbo.Invoices"), meta2.Identifier, meta2, "src2")
        };

        var connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["src1"] = new() { Provider = "SqlServer", CoLocationGroup = "grp1" },
            ["src2"] = new() { Provider = "SqlServer", CoLocationGroup = "grp1" }
        };

        var options = new CrossSourceOptions
        {
            Enabled = true,
            CoLocation = new CoLocationOptions { Enabled = false } // Disabled!
        };

        var partitions = SubtreeGraphPartitioner.Partition(tables, connections, options);

        partitions.Count.ShouldBe(2);
        partitions.All(p => !p.IsCoLocated).ShouldBeTrue();
        partitions.All(p => p.GroupingKey.StartsWith("source:")).ShouldBeTrue();
    }

    [Fact]
    public void SubtreeCompiler_MultiDatabaseSameGroup_EmitsThreePartNames()
    {
        var meta1 = CreateTable("sales", "dbo", "Orders", "DB_Sales", "SqlServer", "grp1");
        var meta2 = CreateTable("crm", "dbo", "Customers", "DB_CRM", "SqlServer", "grp1");

        var tables = new List<ResolvedSourceTable>
        {
            new(new TableAccessTarget("DB_Sales", "dbo", "Orders", null, "DB_Sales.dbo.Orders"), meta1.Identifier, meta1, "sales"),
            new(new TableAccessTarget("DB_CRM", "dbo", "Customers", null, "DB_CRM.dbo.Customers"), meta2.Identifier, meta2, "crm")
        };

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [meta1.Identifier] = TableAccessDecision.Allowed(meta1.Identifier, new Dictionary<string, ColumnAccessLevel>()),
            [meta2.Identifier] = TableAccessDecision.Allowed(meta2.Identifier, new Dictionary<string, ColumnAccessLevel>())
        };

        var (tree, _) = FastSqlEngine.Default.Parse("SELECT o.id, c.val FROM DB_Sales.dbo.Orders o JOIN DB_CRM.dbo.Customers c ON o.foreign_id = c.id".AsMemory());
        var statement = new SqlAstBuilder().BuildStatement(tree);

        var plan = SubtreeCompiler.Compile("grp1", tables, decisions, statement);

        plan.EmitThreePartNames.ShouldBeTrue();
        plan.Databases.ShouldBe(["DB_Sales", "DB_CRM"], ignoreOrder: true);
        plan.CompiledSql.ShouldContain("[DB_Sales].[dbo].[Orders]");
        plan.CompiledSql.ShouldContain("[DB_CRM].[dbo].[Customers]");
    }

    [Fact]
    public void SubtreeCompiler_SingleDatabaseSameGroup_EmitsTwoPartNames()
    {
        var meta1 = CreateTable("sales", "dbo", "Orders", "DB_Sales", "SqlServer", "grp1");
        var meta2 = CreateTable("sales", "dbo", "OrderLines", "DB_Sales", "SqlServer", "grp1");

        var tables = new List<ResolvedSourceTable>
        {
            new(new TableAccessTarget(null, "dbo", "Orders", null, "dbo.Orders"), meta1.Identifier, meta1, "sales"),
            new(new TableAccessTarget(null, "dbo", "OrderLines", null, "dbo.OrderLines"), meta2.Identifier, meta2, "sales")
        };

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [meta1.Identifier] = TableAccessDecision.Allowed(meta1.Identifier, new Dictionary<string, ColumnAccessLevel>()),
            [meta2.Identifier] = TableAccessDecision.Allowed(meta2.Identifier, new Dictionary<string, ColumnAccessLevel>())
        };

        var (tree, _) = FastSqlEngine.Default.Parse("SELECT o.id, l.val FROM dbo.Orders o JOIN dbo.OrderLines l ON o.id = l.foreign_id".AsMemory());
        var statement = new SqlAstBuilder().BuildStatement(tree);

        var plan = SubtreeCompiler.Compile("grp1", tables, decisions, statement);

        plan.EmitThreePartNames.ShouldBeFalse();
        plan.Databases.ShouldBe(["DB_Sales"]);
        plan.CompiledSql.ShouldContain("[dbo].[Orders]");
        plan.CompiledSql.ShouldContain("[dbo].[OrderLines]");
        plan.CompiledSql.ShouldNotContain("[DB_Sales]");
    }

    [Fact]
    public async Task QueryAnalysis_CoLocatedQuery_PopulatesCoLocatedGroupPlan()
    {
        var meta1 = CreateTable("sales", "dbo", "Orders", "DB_Sales", "SqlServer", "finance");
        var meta2 = CreateTable("crm", "dbo", "Customers", "DB_CRM", "SqlServer", "finance");

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([meta1, meta2]);

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new Consent { TableIdentifier = meta1.Identifier, Effect = ConsentEffect.Allow, GranteeSid = new Sid("user-1"), ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1) },
                new Consent { TableIdentifier = meta2.Identifier, Effect = ConsentEffect.Allow, GranteeSid = new Sid("user-1"), ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1) }
            ]);

        var gatewayOptions = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                CrossSource = new CrossSourceOptions
                {
                    Enabled = true,
                    CoLocation = new CoLocationOptions { Enabled = true }
                }
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new()
                {
                    ["sales"] = new() { Provider = "SqlServer", CoLocationGroup = "finance" },
                    ["crm"] = new() { Provider = "SqlServer", CoLocationGroup = "finance" }
                }
            }
        };

        var service = new QueryAnalysisService(
            metadataRepo,
            consentRepo,
            gatewayOptions: Options.Create(gatewayOptions));

        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-1")], "TestAuth"));

        var result = await service.AnalyzeQueriesAsync(
            [
                QueryAnalysisItem.FromSql("SELECT o.id, c.val FROM sales.dbo.Orders o JOIN crm.dbo.Customers c ON o.foreign_id = c.id")
            ],
            caller);

        result.ValidationReport.IsValid.ShouldBeTrue();
        result.Federation.ShouldNotBeNull();
        result.Federation.CoLocatedGroups.ShouldNotBeNull();
        result.Federation.CoLocatedGroups.Count.ShouldBe(1);

        var groupPlan = result.Federation.CoLocatedGroups[0];
        groupPlan.GroupName.ShouldBe("finance");
        groupPlan.SingleStatementExecuted.ShouldBeTrue();
        groupPlan.Databases.ShouldBe(["DB_Sales", "DB_CRM"], ignoreOrder: true);
        groupPlan.MemberTables.Count.ShouldBe(2);
    }

    [Fact]
    public async Task QueryAnalysis_FeatureDisabled_CoLocatedGroupsNull()
    {
        var meta1 = CreateTable("sales", "dbo", "Orders", "DB_Sales", "SqlServer", "finance");
        var meta2 = CreateTable("crm", "dbo", "Customers", "DB_CRM", "SqlServer", "finance");

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([meta1, meta2]);

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new Consent { TableIdentifier = meta1.Identifier, Effect = ConsentEffect.Allow, GranteeSid = new Sid("user-1"), ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1) },
                new Consent { TableIdentifier = meta2.Identifier, Effect = ConsentEffect.Allow, GranteeSid = new Sid("user-1"), ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1) }
            ]);

        var gatewayOptions = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                CrossSource = new CrossSourceOptions
                {
                    Enabled = true,
                    CoLocation = new CoLocationOptions { Enabled = false } // Disabled!
                }
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new()
                {
                    ["sales"] = new() { Provider = "SqlServer", CoLocationGroup = "finance" },
                    ["crm"] = new() { Provider = "SqlServer", CoLocationGroup = "finance" }
                }
            }
        };

        var service = new QueryAnalysisService(
            metadataRepo,
            consentRepo,
            gatewayOptions: Options.Create(gatewayOptions));

        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-1")], "TestAuth"));

        var result = await service.AnalyzeQueriesAsync(
            [
                QueryAnalysisItem.FromSql("SELECT o.id, c.val FROM sales.dbo.Orders o JOIN crm.dbo.Customers c ON o.foreign_id = c.id")
            ],
            caller);

        result.ValidationReport.IsValid.ShouldBeTrue();
        result.Federation?.CoLocatedGroups.ShouldBeNull();
    }
}
