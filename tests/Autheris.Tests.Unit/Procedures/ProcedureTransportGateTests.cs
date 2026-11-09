using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Procedures;

/// <summary>SEC-PROC-02: one transport gate call over every source of a procedure call, before any database access.</summary>
[Collection(TransportGateFallbackCollection.Name)]
public sealed class ProcedureTransportGateTests
{
    private const string Header = """
        name: get_orders
        procedure: api.usp_GetOrders
        parameters:
          - name: customer_id
            type: int
            description: Customer number
        result_table: sales.orders
        """;

    private static readonly TenantId Tenant = new("tenant-a");

    private sealed class RecordingGate(params string[] denied) : IDataSourceTransportGate
    {
        public List<string?> Sources { get; } = [];
        public int Calls { get; private set; }
        public DataSourceTransportFlags Ingress => DataSourceTransportFlags.Rest;
        public IReadOnlyList<string> CheckedSources => [];
        public Task EnsureAsync(string? n, CancellationToken ct = default) => throw new InvalidOperationException("single-source form must not be used");
        public Task EnsureAllAsync(IEnumerable<string?> n, CancellationToken ct = default) => throw new InvalidOperationException("not catalog-known");
        public Task EnsureCatalogSourceAsync(string? n, CancellationToken ct = default) => throw new InvalidOperationException("single-source form must not be used");
        public bool IsAllowed(string? n) => true;

        public Task EnsureCatalogSourcesAsync(IEnumerable<string?> names, CancellationToken ct = default)
        {
            Calls++;
            var list = names.ToList();
            Sources.AddRange(list);
            if (list.Any(s => string.IsNullOrWhiteSpace(s) || denied.Contains(s, StringComparer.OrdinalIgnoreCase)))
            {
                throw new TransportNotAllowedException(list.FirstOrDefault(), DataSourceTransportFlags.None);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Rig
    {
        public IProcedureInvoker Invoker { get; } = Substitute.For<IProcedureInvoker>();
        public IProcedureRowScopeResolver RowScope { get; } = Substitute.For<IProcedureRowScopeResolver>();
        public InMemoryProcedureRegistry Registry { get; } = new();
        public ITableMetadataRepository Tables { get; } = Substitute.For<ITableMetadataRepository>();
        public IConsentRepository Consents { get; } = Substitute.For<IConsentRepository>();
        public IAuditLogRepository Audit { get; } = Substitute.For<IAuditLogRepository>();

        public GovernedProcedureExecutionService Create(IDataSourceTransportGate? gate, string? definitionSource, string tableSource, string tableDomain = "sales")
        {
            var registry = Registry;
            var def = TestProcedureYaml.Definition(Header, "x", false, 60) with { DataSource = definitionSource };
            registry.Register(def);
            registry.MarkActive(def.Name, new ProcedureValidationResult(true, [], ["order_id"], ["sales.orders"], new Dictionary<string, string>()));

            var tables = Tables;
            tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(ci => new TableMetadata
            {
                Identifier = new TableIdentifier(tableDomain, ci.Arg<TableIdentifier>().Schema, ci.Arg<TableIdentifier>().TableName),
                Table = new Table { SourceName = tableSource },
                Columns = [new TableColumn { ColumnName = "order_id" }]
            });
            var consents = Consents;
            consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
                .Returns(new List<Consent>());
            var resolution = Substitute.For<IConsentResolutionService>();
            resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
                .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["order_id"] = ColumnAccessLevel.Clear }));
            Invoker.ExecuteReadAsync(Arg.Any<ProcedureDefinition>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<ProcedureSecurityContext>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new RawProcedureResult(["order_id"], [new object?[] { 1 }], false));
            var audit = Audit;
            audit.RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

            return new GovernedProcedureExecutionService(
                registry, Invoker, Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), audit, tables, consents, resolution,
                Substitute.For<IColumnMaskingProvider>(),
                policyEnforcement: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance,
                rowScope: RowScope,
                concurrencyGate: PermissiveGate(),
                transportGate: gate);
        }

        public Task<GovernedProcedureResult> Run(GovernedProcedureExecutionService svc) =>
            svc.ExecuteAsync("get_orders", new Dictionary<string, object?> { ["customer_id"] = 7 }, Caller(), Tenant);

        public async Task AssertDeniedBeforeDatabaseAsync(GovernedProcedureExecutionService svc)
        {
            await Should.ThrowAsync<TransportNotAllowedException>(() => Run(svc));
            await Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default, default);
            RowScope.ReceivedCalls().ShouldBeEmpty();
        }
    }

    private static ITableReadConcurrencyGate PermissiveGate()
    {
        var g = Substitute.For<ITableReadConcurrencyGate>();
        g.TryEnter(Arg.Any<string>(), Arg.Any<int>()).Returns(_ => Substitute.For<IDisposable>());
        return g;
    }

    private static ClaimsPrincipal Caller() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1001"), DefaultTenantCaller.Claim], "test"));

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("definition_connection")]
    [InlineData("definition_data_source")]
    [InlineData("table_source")]
    [InlineData("empty_table_source_uses_domain")]
    public Task Execute_SourceNotAllowed_DeniedBeforeInvoke(string sourceKind) => AssertSourceDeniedBeforeInvokeAsync(sourceKind);

    /// <summary>Shared with the advisory regression theory.</summary>
    internal static async Task AssertSourceDeniedBeforeInvokeAsync(string sourceKind)
    {
        var rig = new Rig();
        var (gate, svc) = sourceKind switch
        {
            "definition_connection" => Build(rig, "procedures", null, "src-ok"),
            "definition_data_source" => Build(rig, "ds-denied", "ds-denied", "src-ok"),
            "table_source" => Build(rig, "src-denied", "ds-ok", "src-denied"),
            _ => Build(rig, "dom-denied", "ds-ok", string.Empty, "dom-denied")
        };

        await rig.AssertDeniedBeforeDatabaseAsync(svc);
        gate.Calls.ShouldBe(1);
    }

    private static (RecordingGate Gate, GovernedProcedureExecutionService Svc) Build(Rig rig, string denied, string? defSource, string tableSource, string domain = "sales")
    {
        var gate = new RecordingGate(denied);
        return (gate, rig.Create(gate, defSource, tableSource, domain));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Execute_NullGate_FailsClosed()
    {
        // The unit project installs an allow-all fallback; a service built without a gate must resolve to the production
        // deny-all default. Restore that default for this test only (the collection runs without parallel tests).
        var previous = TransportGateFallback.Current;
        TransportGateFallback.Current = DenyAllTransportGate.Instance;
        try
        {
            var rig = new Rig();
            var svc = rig.Create(null, "ds-ok", "src-ok");

            await rig.AssertDeniedBeforeDatabaseAsync(svc);
        }
        finally
        {
            TransportGateFallback.Current = previous;
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Rest_TransportDenied_Returns403_FixedText()
    {
        var rig = new Rig();
        var svc = rig.Create(new RecordingGate("src-denied"), "ds-ok", "src-denied");
        var http = new DefaultHttpContext { User = Caller() };
        http.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        http.Response.Body = new MemoryStream();
        http.Request.Method = "POST";
        http.Request.Body = new MemoryStream("""{"customer_id":7}"""u8.ToArray());
        http.Request.ContentLength = 17;

        await Autheris.Api.Endpoints.ProcedureEndpointRoutes.HandlePost("get_orders", http, svc, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        http.Response.StatusCode.ShouldBe(403);
        http.Response.Body.Position = 0;
        var text = await new StreamReader(http.Response.Body).ReadToEndAsync();
        text.ShouldContain("Access to the data source is not allowed over this transport.");
        text.ShouldNotContain("src-denied");
        await rig.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default, default);
        rig.RowScope.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Mcp_TransportDenied_ReturnsTransportNotAllowed()
    {
        var rig = new Rig();
        var svc = rig.Create(new RecordingGate("src-denied"), "ds-ok", "src-denied");
        var handler = new Autheris.Application.Mcp.Services.McpToolExecutionHandler(
            Substitute.For<Autheris.Application.Sql.Interfaces.IGovernedSqlExecutionService>(),
            Substitute.For<Autheris.Application.DataSources.Interfaces.IGovernedDataQueryService>(),
            Substitute.For<Autheris.Application.Catalog.Interfaces.ICatalogDiscoveryService>(),
            Substitute.For<Autheris.Application.Mcp.Interfaces.IApiDispatcherService>(),
            Substitute.For<Autheris.Application.Lineage.ILineageGraphStore>(),
            rig.Tables,
            procedureCatalog: new global::Autheris.Application.Procedures.Services.ProcedureCatalog(rig.Registry, svc),
            procedureExecutionService: svc);
        var tool = Autheris.Application.Mcp.Services.McpDatasetTools.Definitions().Single(t => t.Name == Autheris.Application.Mcp.Services.McpDatasetTools.CallProcedure);
        var session = new McpSessionContext("s", "u", Tenant.Value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var result = await handler.ExecuteToolAsync(tool, """{"procedure_name":"get_orders","arguments":{"customer_id":7}}""", session, CancellationToken.None);

        result.ShouldContain(TransportNotAllowedException.Code);
        result.ShouldNotContain("src-denied");
        await rig.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default, default);
        rig.RowScope.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpDescribe_TransportDenied_NotDescribed()
    {
        var rig = new Rig();
        var svc = rig.Create(new RecordingGate("src-denied"), "ds-ok", "src-denied");
        var catalog = new Autheris.Application.Mcp.Services.McpDatasetCatalog(
            rig.Tables, rig.Consents, Substitute.For<Autheris.Application.Common.Interfaces.IGatewayExecutionService>(), rig.Audit,
            procedureCatalog: new global::Autheris.Application.Procedures.Services.ProcedureCatalog(rig.Registry, svc), transportGate: new RecordingGate("src-denied"));

        await Should.ThrowAsync<TransportNotAllowedException>(() => catalog.DescribeDatasetAsync(Caller(), "procedure:get_orders", null));

        await rig.Invoker.DidNotReceiveWithAnyArgs().ExecuteReadAsync(default!, default!, default!, default, default);
        rig.RowScope.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public Task Execute_EmptySourceName_StillGated() => AssertEmptySourceNameStillGatedAsync();

    internal static async Task AssertEmptySourceNameStillGatedAsync()
    {
        var gate = new RecordingGate();
        var rig = new Rig();

        await rig.Run(rig.Create(gate, "ds-ok", tableSource: "", tableDomain: "dom-x"));

        gate.Calls.ShouldBe(1);
        gate.Sources.ShouldBe(["ds-ok", "dom-x"], ignoreOrder: true);
    }

    [Fact]
    public async Task Execute_AllSourcesAllowed_Invokes()
    {
        var gate = new RecordingGate();
        var rig = new Rig();

        var result = await rig.Run(rig.Create(gate, "ds-ok", "src-ok"));

        result.ShouldNotBeNull();
        gate.Calls.ShouldBe(1);
        gate.Sources.ShouldBe(["ds-ok", "src-ok"], ignoreOrder: true);
        await rig.Invoker.ReceivedWithAnyArgs(1).ExecuteReadAsync(default!, default!, default!, default, default);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TransportGateFallbackCollection
{
    public const string Name = "TransportGateFallback";

    [Fact]
    [Trait("Category", "Security")]
    public async Task Executor_VisibilityDecisions_CoverExactlyTheGovernedTables_SEC_PROC_09()
    {
        var rig = new Rig();
        var svc = rig.Create(new RecordingGate(), "ds-ok", "ds-ok");
        rig.Registry.TryGet("get_orders", out var registered).ShouldBeTrue();

        var visibility = await svc.EvaluateVisibilityAsync(registered!, Caller(), Tenant, null, CancellationToken.None);

        visibility.Visible.ShouldBeTrue();
        visibility.Decisions.Keys.Order(StringComparer.OrdinalIgnoreCase).ShouldBe(registered!.GovernedTableKeys().Order(StringComparer.OrdinalIgnoreCase));
    }
}
