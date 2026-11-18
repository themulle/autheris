using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Products;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

/// <summary>A write procedure exposed as a data product runs only with its own execute-write grant, on a wired transport, fully audited.</summary>
[Trait("Category", "Security")]
public sealed class ProductWriteTests
{
    private static readonly TenantId Tenant = ProductHarness.Tenant;
    private static readonly TableIdentifier ProductObject = ProductSource.ObjectOf(Tenant, "sales", "post_order");

    private const string Yaml = """
        name: post_order
        procedure: api.usp_PostOrder
        parameters:
          - name: customer_id
            type: int
            description: Customer number
          - name: note
            type: nvarchar(40)
            required: false
            description: Free text
        result_table: sales.orders
        outputs:
          - order_id
        """;

    private readonly InMemoryDataProductRepository _store = new();
    private readonly ITableMetadataRepository _tables = Substitute.For<ITableMetadataRepository>();
    private readonly IConsentRepository _consents = Substitute.For<IConsentRepository>();
    private readonly IConsentResolutionService _resolution = Substitute.For<IConsentResolutionService>();
    private readonly IProcedureInvoker _invoker = Substitute.For<IProcedureInvoker>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly InMemoryProcedureRegistry _registry = new();
    private readonly List<string> _order = [];
    private readonly List<AuditLogEntry> _entries = [];
    private DataProduct _product = null!;

    private static ClaimsPrincipal Caller(bool readOnly = false)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1003"), Autheris.Tests.Unit.TestSupport.DefaultTenantCaller.Claim], "test");
        if (readOnly)
        {
            TokenAccessScope.MarkReadOnly(identity);
        }

        return Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(new ClaimsPrincipal(identity));
    }

    private static Consent Grant(params WriteRule[] rules) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Tenant,
        TableIdentifier = ProductObject,
        GranteeType = GranteeType.User,
        GranteeSid = ProductHarness.Carol,
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(30),
        WriteRules = rules
    };

    private static WriteRule ExecuteWrite() => new(Guid.NewGuid(), WriteOperation.ExecuteWrite, [], WriteRowScope.AllRows);

    private async Task<GovernedProcedureExecutionService> CreateAsync(
        IReadOnlyList<Consent> consents, bool readGrant = false, Func<AuditLogEntry, Task>? onAudit = null)
    {
        var definition = Autheris.Tests.Unit.Procedures.TestProcedureYaml.Definition(Yaml) with { Mode = ProcedureMode.Write, ProductSchema = "sales" };
        _registry.Register(definition);
        _registry.MarkActive(definition.Name, new ProcedureValidationResult(true, [], ["order_id"], ["sales.orders"], new Dictionary<string, string>()));

        _product = new DataProduct(
            Guid.NewGuid(), Tenant, "sales", "post_order", DataProductKind.Procedure, "api.api.usp_PostOrder", "hash-1",
            [new TableIdentifier("api", "api", "usp_PostOrder")], [], ProductHarness.Admin, ProductCreatorAuthority.DataProductAdmin,
            DataProductStatus.Active, null, LoadSettings.Default, ProductLimits.Default, null, ProductHarness.Bob);
        await _store.CreateProductAsync(_product, new DataProductVersion(_product.Id, 1, "hash-1", _product.Definition, ProductHarness.Admin, _product.Authority, DateTimeOffset.UnixEpoch));

        _tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(call =>
            call.Arg<TableIdentifier>() == ProductObject
                ? new TableMetadata
                {
                    Identifier = ProductObject,
                    OwnerTenantId = Tenant.Value,
                    Table = new Table { Id = _product.Id, SourceName = ProductSource.Name, SchemaName = ProductObject.Schema, TableName = ProductObject.TableName, IsActive = true }
                }
                : null);
        _consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(consents.ToList());
        _resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(call => readGrant
                ? TableAccessDecision.Allowed(call.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel> { ["order_id"] = ColumnAccessLevel.Clear })
                : TableAccessDecision.Denied(call.ArgAt<TableIdentifier>(3), "no read grant"));
        _invoker.ExecuteWriteAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _order.Add("CALL");
                return Task.FromResult(new ProcedureWriteOutcome(3));
            });
        _audit.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var entry = call.Arg<AuditLogEntry>();
            if (onAudit is not null)
            {
                await onAudit(entry);
            }

            _entries.Add(entry);
            _order.Add(entry.EventType);
        });

        return new GovernedProcedureExecutionService(
            _registry, _invoker, Options.Create(new GatewayOptions()), _audit, _tables, _consents, _resolution,
            concurrencyGate: Gate(), productRepository: _store);
    }

    private static ITableReadConcurrencyGate Gate()
    {
        var gate = Substitute.For<ITableReadConcurrencyGate>();
        gate.TryEnter(default!, default).ReturnsForAnyArgs(Substitute.For<IDisposable>());
        return gate;
    }

    private static Dictionary<string, object?> Inputs() => new() { ["customer_id"] = 7 };

    private Task<GovernedProcedureResult> RunAsync(GovernedProcedureExecutionService service, ProcedureCallTransport transport = ProcedureCallTransport.Rest, ClaimsPrincipal? user = null, Dictionary<string, object?>? inputs = null)
    {
        using var scope = ProcedureCallScope.Enter(transport);
        return service.ExecuteAsync("post_order", inputs ?? Inputs(), user ?? Caller(), Tenant);
    }

    private Task NeverCalledAsync() => _invoker.DidNotReceiveWithAnyArgs().ExecuteWriteAsync(default!, default!, default!, default);

    [Fact]
    public async Task ExecuteWrite_requires_write_grant()
    {
        var denied = await CreateAsync([]);
        await Should.ThrowAsync<KeyNotFoundException>(() => RunAsync(denied));
        await NeverCalledAsync();

        var granted = await CreateAsync([Grant(ExecuteWrite())]);
        var result = await RunAsync(granted);

        result.RowCount.ShouldBe(3);
        result.Rows.ShouldBeEmpty("a write procedure returns no result set");
    }

    [Fact]
    public async Task Execute_grant_never_runs_write()
    {
        // a read grant on the product (and a write rule of another kind) never opens the write procedure
        var service = await CreateAsync([Grant(new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["order_id"], WriteRowScope.AllRows))], readGrant: true);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => RunAsync(service));

        ex.Message.ShouldBe(GovernedProcedureExecutionService.NotFoundMessage);
        await NeverCalledAsync();
    }

    [Fact]
    public async Task ExecuteWrite_grant_never_opens_the_read_decision()
    {
        var service = await CreateAsync([Grant(ExecuteWrite())]);
        var decision = await RunAsync(service);

        decision.ShouldNotBeNull();
        _resolution.ReceivedCalls().ShouldBeEmpty("the write decision never consults the read grant");
    }

    [Fact]
    public async Task Write_rejected_on_unwired_transport_and_readonly_token()
    {
        var service = await CreateAsync([Grant(ExecuteWrite())]);

        await Should.ThrowAsync<KeyNotFoundException>(() => RunAsync(service, ProcedureCallTransport.Unspecified));
        await Should.ThrowAsync<KeyNotFoundException>(() => RunAsync(service, ProcedureCallTransport.Rest, Caller(readOnly: true)));
        await Should.ThrowAsync<KeyNotFoundException>(() => RunAsync(service, ProcedureCallTransport.Mcp, Caller(readOnly: true)));
        await NeverCalledAsync();

        (await RunAsync(service, ProcedureCallTransport.Mcp)).RowCount.ShouldBe(3);
    }

    [Fact]
    public async Task Write_intent_audited_before_call_and_outcome_after()
    {
        var service = await CreateAsync([Grant(ExecuteWrite())]);

        await RunAsync(service, inputs: new Dictionary<string, object?> { ["customer_id"] = 7, ["note"] = "sensitive-note-value" });

        _order.ShouldBe(["PROCEDURE_WRITE_INTENT", "CALL", "PROCEDURE_WRITE_OUTCOME"]);
    }

    [Fact]
    public async Task Write_audit_carries_product_identity_and_never_values()
    {
        var service = await CreateAsync([Grant(ExecuteWrite())]);

        await RunAsync(service, inputs: new Dictionary<string, object?> { ["customer_id"] = 7, ["note"] = "sensitive-note-value" });

        var intent = _entries.Single(e => e.EventType == "PROCEDURE_WRITE_INTENT");
        intent.DetailsJson.ShouldContain(_product.Id.ToString());
        intent.DetailsJson.ShouldContain("hash-1");
        intent.DetailsJson.ShouldContain("customer_id");
        _entries.ShouldAllBe(e => !e.DetailsJson.Contains("sensitive-note-value") && !e.DetailsJson.Contains(":7"));
    }

    [Fact]
    public async Task Write_audit_failure_aborts_call()
    {
        var service = await CreateAsync([Grant(ExecuteWrite())],
            onAudit: entry => entry.EventType == "PROCEDURE_WRITE_INTENT" ? throw new InvalidOperationException("audit store down") : Task.CompletedTask);

        await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(service));

        await NeverCalledAsync();
    }

    [Fact]
    public async Task Write_parameters_typed_and_bounded()
    {
        var service = await CreateAsync([Grant(ExecuteWrite())]);

        await Should.ThrowAsync<ArgumentException>(() => RunAsync(service, inputs: new() { ["customer_id"] = 7, ["unknown"] = 1 }));
        await Should.ThrowAsync<ArgumentException>(() => RunAsync(service, inputs: new() { ["customer_id"] = "not-a-number" }));
        await Should.ThrowAsync<ArgumentException>(() => RunAsync(service, inputs: new() { ["customer_id"] = 7, ["note"] = new string('x', 5000) }));
        await NeverCalledAsync();
    }

    [Fact]
    public async Task Write_calls_are_rate_limited_per_product()
    {
        var service = await CreateAsync([Grant(ExecuteWrite())]);

        for (var i = 0; i < ProductWriteRateLimiter.MaxCallsPerWindow; i++)
        {
            await RunAsync(service);
        }

        await Should.ThrowAsync<GatewayThrottledException>(() => RunAsync(service));
    }

    [Fact]
    public async Task Definer_never_invokes_write_procedure()
    {
        await CreateAsync([]);
        var runner = new ScriptedRunner();
        var cluster = Substitute.For<Autheris.Application.State.IDistributedClusterStateProvider>();
        var loader = new ProductLoadService(_store, runner, new RecordingCacheStore(), new Autheris.Application.State.Caching.TableCache.TableCacheCatalog(), cluster,
            Options.Create(new Autheris.Domain.Options.TableCache.TableCacheOptions()), new RecordingAuditWriter());

        await Should.ThrowAsync<ProductLoadException>(() => loader.LoadAsync(_product));

        await NeverCalledAsync();
    }

    [Fact]
    public async Task A_write_product_is_never_routed_as_a_view_result()
    {
        await CreateAsync([]);
        var router = new ProductResultRouter(_store, null!, new Autheris.Application.State.Caching.TableCache.TableCacheCatalog());

        await Should.ThrowAsync<ProductLoadException>(() => router.RouteAsync(Tenant, ProductObject));
    }
}
