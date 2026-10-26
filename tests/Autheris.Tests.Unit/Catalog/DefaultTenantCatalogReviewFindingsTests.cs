namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OData;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Phase 5 review of WP-DT-3 (DT-SC-08): the catalog owner's widening is physical metadata only.</summary>
[Trait("Category", "Security")]
public sealed class DefaultTenantCatalogReviewFindingsTests
{
    private static readonly TenantId D = new("tenant-d");
    private static readonly TenantId A = new("tenant-a");

    private static TableMetadata Virtual(TenantId tenant, string name = "m")
    {
        var id = SemanticVirtualTables.IdentifierFor(tenant, name);
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = id.Domain,
                SchemaName = id.Schema,
                TableName = name,
                DocumentationSource = SemanticVirtualTables.SourceMarker,
                IsActive = true,
                DataSourceType = DataSourceType.Sql
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "varchar" }]
        };
    }

    private static ClaimsPrincipal OwnerAdmin() => Wp0.User("S-D-GA", null, ["GovernanceAdmin"]);

    // ---- finding 1: DatasetCatalogAdminService ----

    private static DatasetCatalogAdminService AdminService(ITableMetadataRepository tables) => new(
        tables, Substitute.For<IDataOwnershipRepository>(), Substitute.For<IAuditWriter>(),
        new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = D.Value } }),
        TimeProvider.System);

    [Fact]
    public async Task DT_SC_08_Default_Tenant_GA_Cannot_Administer_Foreign_Virtual_Table_Settings()
    {
        var foreign = Virtual(A).Identifier;
        var own = Virtual(D).Identifier;
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetActiveTableIdentifierAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => (TableIdentifier?)ci.Arg<TableIdentifier>());
        tables.GetDatasetCatalogSettingsAsync(Arg.Any<TableIdentifier>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new DatasetCatalogSettings([], DatasetVisibility.Hidden));
        tables.SetDatasetVisibilityAsync(Arg.Any<TableIdentifier>(), Arg.Any<DatasetVisibility>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new DatasetCatalogChange(ci.Arg<TableIdentifier>(),
                new DatasetCatalogSettings([], DatasetVisibility.Hidden), new DatasetCatalogSettings([], ci.Arg<DatasetVisibility>())));
        var svc = AdminService(tables);
        var edit = new DatasetCatalogEdit(null, null, null, "Full");

        (await svc.ApplyAsync(OwnerAdmin(), foreign.ToString(), edit, "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.NotFound);
        await tables.DidNotReceiveWithAnyArgs().SetDatasetVisibilityAsync(default, default, default, default);
        (await svc.ApplyAsync(OwnerAdmin(), own.ToString(), edit, "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.Applied);
        // The global administrator is not confined.
        var clusterAdmin = Wp0.User("S-CA", null, ["ClusterAdmin"]);
        (await svc.ApplyAsync(clusterAdmin, foreign.ToString(), edit, "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.Applied);
    }

    // ---- finding 2: golden queries / guardrail ----

    [Fact]
    public async Task DT_SC_08_Default_Tenant_GA_Golden_Queries_Exclude_Foreign_Virtual_Tables()
    {
        var foreignTable = Virtual(A);
        var physical = Wp0.Table("sales", "orders");
        var metadata = Wp0.Tables(foreignTable, physical);
        var golden = new GoldenQueryService(Options.Create(new GatewayOptions()), NullLogger<GoldenQueryService>.Instance);
        golden.RegisterGoldenQuery(new GoldenQuery("g-foreign", foreignTable.Identifier.Domain, foreignTable.Identifier.TableName, "t", "d", "q"));
        golden.RegisterGoldenQuery(new GoldenQuery("g-orders", "sales", "orders", "t", "d", "q"));
        var registry = Substitute.For<IMcpToolRegistry>();
        registry.FindTool("get_golden_queries").Returns(new McpToolDefinition("get_golden_queries", "x", "{}", string.Empty, null));
        var options = new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = D.Value } };
        var guardrail = new AiDataGuardrailService(
            registry, Options.Create(options), NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            tableMetadataRepository: metadata, goldenQueryService: golden, consentRepository: Wp0.Consents());
        var session = new McpSessionContext("s", "sp", D.Value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "S-D-GA", ["GovernanceAdmin"], []);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest("get_golden_queries", "{}", "s"), session);

        result.IsSuccess.ShouldBeTrue();
        result.ContentJson.ShouldContain("g-orders");
        result.ContentJson.ShouldNotContain("g-foreign");
    }

    // ---- finding 3: OData candidates ----

    [Fact]
    public async Task DT_SC_08_OData_Owner_Candidates_Exclude_Foreign_Virtual_Tables()
    {
        var ownVirtual = Virtual(D, "own_model");
        var foreignVirtual = Virtual(A, "foreign_model");
        var tables = new[] { Wp0.Table("sales", "orders"), ownVirtual, foreignVirtual };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(tables));
        var exec = Substitute.For<IGatewayExecutionService>();
        exec.CheckTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableMetadata>(), false, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(TableAccessDecision.Allowed(
                ci.Arg<TableMetadata>().Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)));
        var resolver = new TenantResolver(() => new TenancyOptions { DefaultTenantId = D.Value }, null, NullLogger<TenantResolver>.Instance);
        var handler = new ODataHandler(repo, exec, NullLogger<ODataHandler>.Instance, tenantResolver: resolver);

        var csdl = await handler.GetMetadataCsdlAsync(OwnerAdmin());

        var ns = XNamespace.Get("http://docs.oasis-open.org/odata/ns/edm");
        var names = XDocument.Parse(csdl).Descendants(ns + "EntityType").Select(e => e.Attribute("Name")!.Value).ToArray();
        names.ShouldContain(n => n.Contains("orders", StringComparison.OrdinalIgnoreCase));
        names.ShouldContain(n => n.Contains("own_model", StringComparison.OrdinalIgnoreCase));
        names.ShouldNotContain(n => n.Contains("foreign_model", StringComparison.OrdinalIgnoreCase));
    }

    // ---- finding 7: 403 hint distinguishes an invalid claim ----

    [Fact]
    public void DT_SC_08_Datasource_Denial_Names_Invalid_Claim_Not_The_Missing_Option()
    {
        var invalid = Wp0.User("S-GA", "bad tenant!!", ["GovernanceAdmin"]);
        var claimless = Wp0.User("S-GA", null, ["GovernanceAdmin"]);

        CallerScopeResolver.DatasourceListingDenial(invalid, CallerScope.None).ShouldContain("TENANT_CLAIM_INVALID");
        CallerScopeResolver.DatasourceListingDenial(invalid, CallerScope.None).ShouldNotContain("DefaultTenantId");
        CallerScopeResolver.DatasourceListingDenial(claimless, CallerScope.None).ShouldContain("DefaultTenantId");
    }
}
