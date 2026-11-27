using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Products;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

/// <summary>A read procedure exposed as a data product: the product decision replaces the consent lookup on the governed table set.</summary>
[Trait("Category", "Security")]
public sealed partial class ProductExposureTests
{
    private static readonly TenantId Tenant = ProductHarness.Tenant;
    private static readonly TableIdentifier ProductObject = ProductSource.ObjectOf(Tenant, "sales", "get_orders");

    private const string Yaml = """
        name: get_orders
        procedure: api.usp_GetOrders
        parameters:
          - name: customer_id
            type: int
            description: Customer number
        result_table: sales.orders
        outputs:
          - order_id
          - email
          - note
        cleared_columns:
          - note
        """;

    private readonly InMemoryDataProductRepository _store = new();
    private readonly ITableMetadataRepository _tables = Substitute.For<ITableMetadataRepository>();
    private readonly IConsentRepository _consents = Substitute.For<IConsentRepository>();
    private readonly IConsentResolutionService _resolution = Substitute.For<IConsentResolutionService>();
    private readonly IProcedureInvoker _invoker = Substitute.For<IProcedureInvoker>();
    private readonly IColumnMaskingProvider _masking = Substitute.For<IColumnMaskingProvider>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly InMemoryProcedureRegistry _registry = new();
    private readonly List<AuditLogEntry> _entries = [];

    private static ClaimsPrincipal Caller() => Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(new ClaimsPrincipal(
        new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1003"), Autheris.Tests.Unit.TestSupport.DefaultTenantCaller.Claim], "test")));

    private async Task<GovernedProcedureExecutionService> CreateAsync(
        Dictionary<string, ColumnAccessLevel>? grant,
        string? rowFilter = null,
        string? productSchema = "sales",
        DataProductStatus status = DataProductStatus.Active)
    {
        var definition = TestProcedureYaml(Yaml) with { ProductSchema = productSchema };
        _registry.Register(definition);
        _registry.MarkActive(definition.Name, new ProcedureValidationResult(true, [], ["order_id", "email", "note", "extra"], ["sales.orders"], new Dictionary<string, string>()));

        var product = new DataProduct(
            Guid.NewGuid(), Tenant, "sales", "get_orders", DataProductKind.Procedure, "api.api.usp_GetOrders", "hash",
            [new TableIdentifier("api", "api", "usp_GetOrders")], [], ProductHarness.Admin, ProductCreatorAuthority.DataProductAdmin,
            status, null, LoadSettings.Default, ProductLimits.Default, null, ProductHarness.Bob);
        await _store.CreateProductAsync(product, new DataProductVersion(product.Id, 1, "hash", product.Definition, ProductHarness.Admin, product.Authority, DateTimeOffset.UnixEpoch));

        _tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(call =>
            call.Arg<TableIdentifier>() == ProductObject
                ? new TableMetadata
                {
                    Identifier = ProductObject,
                    OwnerTenantId = Tenant.Value,
                    Table = new Table { Id = product.Id, SourceName = ProductSource.Name, SchemaName = ProductObject.Schema, TableName = ProductObject.TableName, IsActive = true }
                }
                : null);
        _consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Consent>());
        _resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(call => grant is null
                ? TableAccessDecision.Denied(call.ArgAt<TableIdentifier>(3), "no product grant")
                : TableAccessDecision.Allowed(call.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(grant, StringComparer.OrdinalIgnoreCase), rowFilterSql: rowFilter));
        _masking.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>()).Returns("***");
        _invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RawProcedureResult(
                ["order_id", "email", "note", "extra"],
                [new object?[] { 1, "a@b.c", "n1", "secret" }, new object?[] { 2, "d@e.f", "n2", "secret" }],
                false));
        _audit.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(_entries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        return new GovernedProcedureExecutionService(
            _registry, _invoker, Options.Create(new GatewayOptions()), _audit, _tables, _consents, _resolution, _masking,
            concurrencyGate: Gate(), productRepository: _store);
    }

    private static ITableReadConcurrencyGate Gate()
    {
        return Autheris.Tests.Unit.TestDoubles.GrantAllReadConcurrencyGate.Instance;
    }

    private static ProcedureDefinition TestProcedureYaml(string yaml) => Autheris.Tests.Unit.Procedures.TestProcedureYaml.Definition(yaml);

    private static Dictionary<string, object?> Inputs() => new() { ["customer_id"] = 7 };

    private static Dictionary<string, ColumnAccessLevel> ClearGrant() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["order_id"] = ColumnAccessLevel.Clear,
        ["email"] = ColumnAccessLevel.Clear,
        ["note"] = ColumnAccessLevel.Clear
    };

    [Fact]
    public async Task ProcedureProduct_UndeclaredOutputColumn_IsNotExposed()
    {
        var service = await CreateAsync(ClearGrant());

        var result = await service.ExecuteAsync("get_orders", Inputs(), Caller(), Tenant);

        result.Columns.ShouldNotContain("extra");
        result.Rows.ShouldAllBe(r => !r.ContainsKey("extra"));
        result.Columns.ShouldBe(["order_id", "email", "note"], ignoreOrder: true);
    }

    [Fact]
    public async Task ProcedureProduct_NonClearEligibleColumn_IsMasked()
    {
        var service = await CreateAsync(ClearGrant());

        var result = await service.ExecuteAsync("get_orders", Inputs(), Caller(), Tenant);

        // a clear rule on the grant never lifts a column the procedure did not declare clear-eligible
        result.Rows[0]["order_id"].ShouldBe("***");
        result.Rows[0]["email"].ShouldBe("***");
        result.Rows[0]["note"].ShouldBe("n1");
        result.HasMaskedValues.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ProcedureProduct_NoColumnReturned_HasNoMaskedValues()
    {
        var grant = ClearGrant();
        grant["order_id"] = ColumnAccessLevel.Deny;
        grant["email"] = ColumnAccessLevel.Deny;
        grant["note"] = ColumnAccessLevel.Deny;
        var service = await CreateAsync(grant);

        var result = await service.ExecuteAsync("get_orders", Inputs(), Caller(), Tenant);

        result.HasMaskedValues.ShouldBeFalse();
    }

    [Fact]
    public async Task ProcedureProduct_ClearEligibleColumn_NeedsClearRuleOnTheGrant()
    {
        var grant = ClearGrant();
        grant["note"] = ColumnAccessLevel.Mask;
        var service = await CreateAsync(grant);

        var result = await service.ExecuteAsync("get_orders", Inputs(), Caller(), Tenant);

        result.Rows[0]["note"].ShouldBe("***");
    }

    [Fact]
    public async Task ProcedureProduct_DeniedColumn_IsRemoved()
    {
        var grant = ClearGrant();
        grant["email"] = ColumnAccessLevel.Deny;
        var service = await CreateAsync(grant);

        var result = await service.ExecuteAsync("get_orders", Inputs(), Caller(), Tenant);

        result.Columns.ShouldNotContain("email");
    }

    [Fact]
    public async Task ProcedureProduct_RowPolicyPresent_FailsClosed404()
    {
        var service = await CreateAsync(ClearGrant(), rowFilter: "region = 'EU'");

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => service.ExecuteAsync("get_orders", Inputs(), Caller(), Tenant));

        ex.Message.ShouldBe(GovernedProcedureExecutionService.NotFoundMessage);
        await _invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task ProcedureProduct_CallerWithoutProductGrant_NotFound()
    {
        var service = await CreateAsync(grant: null);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => service.ExecuteAsync("get_orders", Inputs(), Caller(), Tenant));

        ex.Message.ShouldBe(GovernedProcedureExecutionService.NotFoundMessage);
        await _invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default, default);
        _entries.ShouldContain(e => e.EventType == "PROCEDURE_DENIED");
    }

    [Fact]
    public async Task ProcedureProduct_BaseTableConsentIsNotUsed()
    {
        // the governed table set (sales.orders) has no grant at all; only the product object does
        var service = await CreateAsync(ClearGrant());

        var result = await service.ExecuteAsync("get_orders", Inputs(), Caller(), Tenant);

        result.RowCount.ShouldBe(2);
        _resolution.ReceivedCalls().Select(c => c.GetArguments()[3]).Cast<TableIdentifier>().ShouldAllBe(t => t == ProductObject);
    }

    [Fact]
    public async Task ProcedureProduct_RetiredProduct_NotFound()
    {
        var service = await CreateAsync(ClearGrant(), status: DataProductStatus.Retired);

        await Should.ThrowAsync<KeyNotFoundException>(() => service.ExecuteAsync("get_orders", Inputs(), Caller(), Tenant));
    }

    [Fact]
    public async Task ProcedureProduct_WithoutProductStore_FailsClosed()
    {
        var service = await CreateAsync(ClearGrant());
        var withoutStore = new GovernedProcedureExecutionService(
            _registry, _invoker, Options.Create(new GatewayOptions()), _audit, _tables, _consents, _resolution, _masking, concurrencyGate: Gate());

        await Should.ThrowAsync<KeyNotFoundException>(() => withoutStore.ExecuteAsync("get_orders", Inputs(), Caller(), Tenant));
        (await service.ExecuteAsync("get_orders", Inputs(), Caller(), Tenant)).RowCount.ShouldBe(2);
    }

    [Fact]
    public async Task ProcedureProduct_OtherTenantsProduct_NotFound()
    {
        var service = await CreateAsync(ClearGrant());

        await Should.ThrowAsync<KeyNotFoundException>(() => service.ExecuteAsync("get_orders", Inputs(), Caller(), new TenantId("tenant-b")));
    }

    [Fact]
    public async Task Procedure_product_clear_only_with_cleared_column_and_clear_rule()
    {
        var withClear = await (await CreateAsync(ClearGrant())).ExecuteAsync("get_orders", Inputs(), Caller(), Tenant);
        var mask = ClearGrant();
        mask["note"] = ColumnAccessLevel.Mask;
        var withMask = await (await CreateAsync(mask)).ExecuteAsync("get_orders", Inputs(), Caller(), Tenant);

        withClear.Rows[0]["note"].ShouldBe("n1");
        withClear.Rows[0]["order_id"].ShouldBe("***", "a column not declared clear-eligible stays masked even with a clear rule");
        withMask.Rows[0]["note"].ShouldBe("***");
    }

    [Fact]
    public async Task Procedure_product_row_policy_returns_uniform_404()
    {
        var blocked = await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await (await CreateAsync(ClearGrant(), rowFilter: "region = 'EU'")).ExecuteAsync("get_orders", Inputs(), Caller(), Tenant));
        var unknown = await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await (await CreateAsync(ClearGrant())).ExecuteAsync("no_such_procedure", Inputs(), Caller(), Tenant));

        blocked.Message.ShouldBe(unknown.Message);
    }

    [Fact]
    public async Task Unknown_foreign_hidden_product_same_404()
    {
        var hidden = await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await (await CreateAsync(grant: null)).ExecuteAsync("get_orders", Inputs(), Caller(), Tenant));
        var foreign = await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await (await CreateAsync(ClearGrant())).ExecuteAsync("get_orders", Inputs(), Caller(), new TenantId("tenant-b")));
        var unknown = await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await (await CreateAsync(ClearGrant())).ExecuteAsync("no_such_procedure", Inputs(), Caller(), Tenant));

        hidden.Message.ShouldBe(unknown.Message);
        foreign.Message.ShouldBe(unknown.Message);
    }
}
