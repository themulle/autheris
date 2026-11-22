using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Products;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

/// <summary>A procedure bound to a data product is governed on its product object in the guardrail and discovery, not on the tables behind its SQL.</summary>
[Trait("Category", "Security")]
public sealed class ProductProcedureTableSetTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly TableIdentifier Plain = new("sales", "dbo", "pub");

    private sealed class NoDemo : IDemoDataSwitch
    {
        public bool Enabled => false;
    }

    private static InMemoryProcedureRegistry Registry(string? productSchema)
    {
        var definition = new ProcedureDefinition(
            Name: "get_orders", Summary: "orders", ProcedureName: "usp_get_orders", Mode: ProcedureMode.Read, DataSource: "sales", Parameters: [],
            ContextBindings: [], RlsMode: ProcedureRlsMode.SessionContext, ResultTable: "dbo.pub", ClearedResultColumns: [], RequiredRoles: [], TimeoutSeconds: 30)
            with { ProductSchema = productSchema };
        var registry = new InMemoryProcedureRegistry();
        registry.Register(definition);
        registry.MarkActive(definition.Name, new ProcedureValidationResult(true, [], [], ["dbo.pub"], new Dictionary<string, string>()));
        return registry;
    }

    [Fact]
    public async Task Guardrail_decides_a_product_procedure_on_its_product_object()
    {
        var asked = new List<TableIdentifier>();
        var repo = global::Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.ServeCatalogReads(Substitute.For<ITableMetadataRepository>());
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var id = call.Arg<TableIdentifier>();
            asked.Add(id);
            // the product object is inactive, the plain base table is active: a denial proves the product object was the one checked
            return new TableMetadata
            {
                Identifier = id,
                Table = new Table { TableName = id.TableName, IsActive = !ProductSource.IsProductSource(id.Domain) },
                Columns = ProductSource.IsProductSource(id.Domain)
                    ? [new TableColumn { ColumnName = "secret", DataType = "varchar" }]
                    : [],
                ColumnClassifications = ProductSource.IsProductSource(id.Domain)
                    ? TableMetadataClassificationTestExtensions.PiiFor(["secret"])
                    : new Dictionary<string, IReadOnlyList<ColumnClassification>>()
            };
        });
        repo.StubAllTables(Array.Empty<TableMetadata>());
        var executor = Substitute.For<IMcpQueryExecutor>();
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("""{"rows":[]}"""));

        async Task<McpToolCallResult> CallAsync(string? productSchema) => await new AiDataGuardrailService(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new NoDemo()),
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            queryExecutor: executor,
            tableMetadataRepository: repo,
            procedureRegistry: Registry(productSchema),
            decisionPoint: Autheris.Tests.Unit.TestSupport.PdpTestDoubles.AllowAll())
            .ExecuteToolWithGuardrailAsync(
                new McpToolCallRequest(McpDatasetTools.CallProcedure, """{"procedure_name":"get_orders"}"""),
                new McpSessionContext("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: ["DataAnalyst"]));

        var plain = await CallAsync(null);
        plain.IsSuccess.ShouldBeTrue(plain.ErrorMessage);

        asked.Clear();
        var product = await CallAsync("sales");
        product.IsSuccess.ShouldBeFalse();
        product.ErrorMessage!.ShouldContain("not accessible");
        asked.ShouldContain(id => ProductSource.IsProductSource(id.Domain));
        asked.ShouldNotContain(Plain);
    }

    [Fact]
    public void Discovery_resolves_a_product_procedure_on_its_product_object()
    {
        var registry = Registry("sales");
        registry.TryGet("get_orders", out var registered).ShouldBeTrue();
        var productObject = ProductSource.ObjectOf(Tenant, "sales", "get_orders");
        TableMetadata Meta(TableIdentifier id) => new() { Identifier = id, Table = new Table { TableName = id.TableName, IsActive = true } };

        var withProduct = DiscoveryProcedureResolver.FilterEligible([registered!], [Meta(productObject)], Tenant);
        var baseOnly = DiscoveryProcedureResolver.FilterEligible([registered!], [Meta(Plain)], Tenant);
        var noTenant = DiscoveryProcedureResolver.FilterEligible([registered!], [Meta(productObject)], null);

        withProduct.ShouldHaveSingleItem().GovernedTables.Select(t => t.Identifier).ShouldBe([productObject]);
        baseOnly.ShouldBeEmpty("the base table is not the governed object of a product procedure");
        noTenant.ShouldBeEmpty("an unresolved tenant fails closed");
    }

    [Fact]
    public void Mcp_write_procedure_hint_is_destructive_unless_read_only()
    {
        McpToolAnnotations.For(McpDatasetTools.CallProcedure, readOnlySession: false, sqlDmlAllowed: false).ShouldBe(new McpToolHints(ReadOnly: false, Destructive: true));
        McpToolAnnotations.For(McpDatasetTools.CallProcedure, readOnlySession: true, sqlDmlAllowed: false).ReadOnly.ShouldBeTrue();
        var tool = new McpToolRegistry(Options.Create(new GatewayOptions()), new NoDemo()).FindTool(McpDatasetTools.CallProcedure).ShouldNotBeNull();
        tool.Description.ShouldContain("mode write");
        tool.Description.ShouldContain("read-only");
    }
}
